using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace XenoSuite
{
    /// LibreHardwareMonitor wrapper — every temperature, clock, voltage, fan, power, load and SMART sensor it can find
    /// (CPU via PawnIO, NVIDIA via NVAPI, AMD via ADL, SuperIO, NVMe/SATA, RAM, PSU, NICs).
    ///
    /// Perf notes (v1.1):
    ///  - Hardware is polled on its OWN thread. Measured on the RTX 3080 box, LHM's NVIDIA update alone costs ~96 ms,
    ///    which used to sit inside every tick. The tick now just copies the latest snapshot (microseconds).
    ///  - The sensor list (ids, names, types, owning hardware) is static, so it's only sent to the UI when it changes
    ///    ("meta"). Every tick then carries just a flat number array [v, min, max, v, min, max, …] (~90% smaller).
    ///  - Slow hardware (Storage SMART, PSU, battery, coolers) is refreshed at most once per second even when the
    ///    sample interval is 250/500 ms; CPU / GPU / RAM / SuperIO follow the sample interval.
    public sealed class HwSensors : IDisposable
    {
        Computer pc;
        public bool Ok { get; private set; }
        public string Error { get; private set; }
        readonly object updLock = new();    // held while LHM hardware is being updated / walked
        readonly object snapLock = new();   // guards the snapshot the tick thread reads

        const int SlowMs = 1000;
        long lastSlow;
        bool metaWanted = true;
        int sentVersion = -1;
        Thread th;

        // live sensor set (sensor thread only)
        ISensor[] sens = Array.Empty<ISensor>();
        // snapshot (snapLock)
        string[] ids = Array.Empty<string>(), hwIds = Array.Empty<string>(), types = Array.Empty<string>(), names = Array.Empty<string>();
        int[] idx = Array.Empty<int>();
        double?[] vals = Array.Empty<double?>(), mins = Array.Empty<double?>(), maxs = Array.Empty<double?>();
        (string id, string name, string type, string parent)[] hwMeta = Array.Empty<(string, string, string, string)>();
        public int MetaVersion { get; private set; }
        public long Updates { get; private set; }

        /// Rolling per-hardware update cost (ms), for the profiler readout.
        public readonly ConcurrentDictionary<string, double> UpdateMs = new();
        public double LastCycleMs { get; private set; }

        public void Open()
        {
            try
            {
                pc = new Computer
                {
                    IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true,
                    IsControllerEnabled = true, IsStorageEnabled = true, IsNetworkEnabled = false, IsPsuEnabled = true, IsBatteryEnabled = true,
                };
                pc.Open();
                Ok = true;
                Log.Write("lhm", "opened: " + string.Join(" | ", All().Select(h => $"{h.HardwareType}={h.Name} ({h.Sensors.Length} sensors)")));
            }
            catch (Exception ex) { Error = ex.Message; Log.Write("lhm", ex.ToString()); }
        }

        /// Starts the polling thread. It runs only while isActive() and paces itself to interval().
        public void Start(Func<bool> isActive, Func<int> interval)
        {
            if (!Ok || th != null) return;
            th = new Thread(() =>
            {
                while (true)
                {
                    var sw = Stopwatch.StartNew();
                    if (isActive()) { try { Update(); } catch (Exception ex) { Log.Write("lhm.update", ex.Message); } }
                    Thread.Sleep(Math.Max(50, interval() - (int)sw.ElapsedMilliseconds));
                }
            }) { IsBackground = true, Name = "XenoSuite.Sensors", Priority = ThreadPriority.AboveNormal };
            th.Start();
        }

        IEnumerable<IHardware> All()
        {
            foreach (var h in pc.Hardware)
            {
                yield return h;
                foreach (var s in h.SubHardware) yield return s;
            }
        }

        static bool IsSlow(HardwareType t) => t is HardwareType.Storage or HardwareType.Psu or HardwareType.Battery or HardwareType.EmbeddedController or HardwareType.Cooler;

        /// One polling pass (normally called by the sensor thread).
        public void Update()
        {
            if (!Ok) return;
            lock (updLock)
            {
                var cycle = Stopwatch.StartNew();
                long now = Environment.TickCount64;
                bool doSlow = now - lastSlow >= SlowMs - 50;
                if (doSlow) lastSlow = now;
                foreach (var h in pc.Hardware)
                {
                    if (!doSlow && IsSlow(h.HardwareType)) continue;
                    long t0 = Stopwatch.GetTimestamp();
                    try { h.Update(); foreach (var s in h.SubHardware) s.Update(); } catch { }
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    UpdateMs.AddOrUpdate(h.HardwareType + ":" + h.Name, ms, (_, old) => old * 0.8 + ms * 0.2);
                }
                bool changed = SensorSetChanged();
                lock (snapLock)
                {
                    if (changed) RebuildMeta();
                    for (int i = 0; i < sens.Length; i++) { var s = sens[i]; vals[i] = R(s.Value); mins[i] = R(s.Min); maxs[i] = R(s.Max); }
                }
                Updates++;
                LastCycleMs = cycle.Elapsed.TotalMilliseconds;
            }
        }

        bool SensorSetChanged()
        {
            int i = 0;
            foreach (var h in All())
                foreach (var s in h.Sensors)
                {
                    if (s.Value == null && s.Min == null) continue;
                    if (i >= sens.Length || !ReferenceEquals(sens[i], s)) return true;
                    i++;
                }
            return i != sens.Length;
        }

        void RebuildMeta()
        {
            var ls = new List<ISensor>(sens.Length + 16); var hw = new List<string>(sens.Length + 16);
            foreach (var h in All())
            {
                var hid = h.Identifier.ToString();
                foreach (var s in h.Sensors) { if (s.Value == null && s.Min == null) continue; ls.Add(s); hw.Add(hid); }
            }
            sens = ls.ToArray(); hwIds = hw.ToArray();
            ids = sens.Select(s => s.Identifier.ToString()).ToArray();
            types = sens.Select(s => s.SensorType.ToString()).ToArray();
            names = sens.Select(s => s.Name).ToArray();
            idx = sens.Select(s => s.Index).ToArray();
            vals = new double?[sens.Length]; mins = new double?[sens.Length]; maxs = new double?[sens.Length];
            hwMeta = All().Select(h => (h.Identifier.ToString(), h.Name, h.HardwareType.ToString(), h.Parent?.Identifier.ToString())).ToArray();
            MetaVersion++;
        }

        static double? R(float? f) => f == null || float.IsNaN(f.Value) || float.IsInfinity(f.Value) ? null : Math.Round((double)f.Value, 3);

        public void ForceMeta() { lock (snapLock) metaWanted = true; }

        /// Writes {"m":ver, "meta":{…}?, "v":[v,min,max,…]} from the latest snapshot, or null before the first pass.
        public void Write(Utf8JsonWriter w)
        {
            lock (snapLock)
            {
                if (!Ok || MetaVersion == 0) { w.WriteNullValue(); return; }
                w.WriteStartObject();
                w.WriteNumber("m", MetaVersion);
                if (metaWanted || sentVersion != MetaVersion)
                {
                    metaWanted = false; sentVersion = MetaVersion;
                    w.WriteStartObject("meta");
                    w.WriteStartArray("hw");
                    foreach (var h in hwMeta)
                    {
                        w.WriteStartArray();
                        w.WriteStringValue(h.id); w.WriteStringValue(h.name); w.WriteStringValue(h.type);
                        if (h.parent != null) w.WriteStringValue(h.parent); else w.WriteNullValue();
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    w.WriteStartArray("s");
                    for (int i = 0; i < ids.Length; i++)
                    {
                        w.WriteStartArray();
                        w.WriteStringValue(ids[i]); w.WriteStringValue(hwIds[i]); w.WriteStringValue(types[i]); w.WriteStringValue(names[i]); w.WriteNumberValue(idx[i]);
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteStartArray("v");
                for (int i = 0; i < vals.Length; i++) { Num(w, vals[i]); Num(w, mins[i]); Num(w, maxs[i]); }
                w.WriteEndArray();
                w.WriteEndObject();
            }
        }

        static void Num(Utf8JsonWriter w, double? v) { if (v == null) w.WriteNullValue(); else w.WriteNumberValue(v.Value); }

        /// For the CSV logger: (ids, "name (type) id" labels, current values).
        public (string[] ids, string[] labels, double?[] vals) Table()
        {
            lock (snapLock) return (ids, names.Select((n, i) => $"{n} ({types[i]}) {ids[i]}").ToArray(), (double?[])vals.Clone());
        }

        public string ProfileText() =>
            $"sensor pass {LastCycleMs:0.0}ms: " + string.Join(" | ", UpdateMs.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:0.0}ms"));

        public void ResetMinMax()
        {
            if (!Ok) return;
            lock (updLock) foreach (var h in All()) foreach (var s in h.Sensors) { s.ResetMin(); s.ResetMax(); }
        }

        public string Report() { lock (updLock) return Ok ? pc.GetReport() : Error; }

        public void Dispose() { try { lock (updLock) pc?.Close(); } catch { } }
    }
}
