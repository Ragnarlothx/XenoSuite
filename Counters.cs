using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XenoSuite
{
    /// Kernel-level counters Task Manager / Resource Monitor show but the base sampler didn't:
    /// context switches, system calls, run-queue length, DPC / interrupt time, effective CPU clock,
    /// standby / modified / free page lists, hard faults, and disk latency + queue depth.
    /// Each counter is a single-instance PerformanceCounter (~0.05 ms per read); one that fails is dropped
    /// on its own without taking the rest down.
    public sealed class SysCounters
    {
        sealed class C
        {
            public string Key, Cat, Name, Inst; public double Scale = 1; public PerformanceCounter Pc; public bool Dead;
            public bool Bg => Cat == "System"; public double Last = double.NaN;
        }
        readonly C[] list =
        {
            new() { Key = "ctx",      Cat = "System", Name = "Context Switches/sec" },
            new() { Key = "sysc",     Cat = "System", Name = "System Calls/sec" },
            new() { Key = "queue",    Cat = "System", Name = "Processor Queue Length" },
            new() { Key = "intr",     Cat = "Processor Information", Name = "Interrupts/sec", Inst = "_Total" },
            new() { Key = "dpc",      Cat = "Processor Information", Name = "% DPC Time", Inst = "_Total" },
            new() { Key = "irq",      Cat = "Processor Information", Name = "% Interrupt Time", Inst = "_Total" },
            new() { Key = "perf",     Cat = "Processor Information", Name = "% Processor Performance", Inst = "_Total" },
            new() { Key = "util",     Cat = "Processor Information", Name = "% Processor Utility", Inst = "_Total" },
            new() { Key = "freq",     Cat = "Processor Information", Name = "Processor Frequency", Inst = "_Total" },
            new() { Key = "sbN",      Cat = "Memory", Name = "Standby Cache Normal Priority Bytes" },
            new() { Key = "sbR",      Cat = "Memory", Name = "Standby Cache Reserve Bytes" },
            new() { Key = "sbC",      Cat = "Memory", Name = "Standby Cache Core Bytes" },
            new() { Key = "modified", Cat = "Memory", Name = "Modified Page List Bytes" },
            new() { Key = "free",     Cat = "Memory", Name = "Free & Zero Page List Bytes" },
            new() { Key = "faults",   Cat = "Memory", Name = "Page Faults/sec" },
            new() { Key = "hard",     Cat = "Memory", Name = "Page Reads/sec" },
            new() { Key = "pagesIn",  Cat = "Memory", Name = "Pages Input/sec" },
            new() { Key = "pagesOut", Cat = "Memory", Name = "Pages Output/sec" },
            new() { Key = "dlr",      Cat = "PhysicalDisk", Name = "Avg. Disk sec/Read", Inst = "_Total", Scale = 1000 },
            new() { Key = "dlw",      Cat = "PhysicalDisk", Name = "Avg. Disk sec/Write", Inst = "_Total", Scale = 1000 },
            new() { Key = "dq",       Cat = "PhysicalDisk", Name = "Current Disk Queue Length", Inst = "_Total" },
            new() { Key = "diops",    Cat = "PhysicalDisk", Name = "Disk Transfers/sec", Inst = "_Total" },
        };
        bool opened;
        public volatile bool Active;
        // The "System" object counters cost ~3.5 ms each to read (Windows assembles the whole object), so those three
        // are read once a second on a background thread; the per-tick read just copies the last value.
        void BgLoop()
        {
            while (true)
            {
                if (Active)
                    foreach (var c in list)
                    {
                        if (!c.Bg || c.Dead) continue;
                        try { c.Last = c.Pc.NextValue() * c.Scale; } catch { c.Dead = true; }
                    }
                Thread.Sleep(1000);
            }
        }

        void Open()
        {
            opened = true;
            foreach (var c in list)
            {
                try { c.Pc = c.Inst == null ? new PerformanceCounter(c.Cat, c.Name, true) : new PerformanceCounter(c.Cat, c.Name, c.Inst, true); c.Pc.NextValue(); }
                catch (Exception ex) { c.Dead = true; Log.Write("counter", $"{c.Cat}\\{c.Name}: {ex.Message}"); }
            }
            new Thread(BgLoop) { IsBackground = true, Name = "XenoSuite.Counters", Priority = ThreadPriority.BelowNormal }.Start();
        }

        public void Write(Utf8JsonWriter w)
        {
            if (!opened) Open();
            w.WriteStartObject();
            double sb = 0; bool anySb = false;
            foreach (var c in list)
            {
                if (c.Dead) continue;
                double v;
                if (c.Bg) { v = c.Last; if (double.IsNaN(v)) continue; }
                else try { v = c.Pc.NextValue() * c.Scale; }
                catch { c.Dead = true; continue; }
                if (c.Key.StartsWith("sb")) { sb += v; anySb = true; continue; }
                w.WriteNumber(c.Key, Math.Round(v, v < 10 ? 3 : 1));
            }
            if (anySb) w.WriteNumber("standby", Math.Round(sb));
            w.WriteEndObject();
        }
    }

    /// Latency monitor: pings the default gateway, a public resolver and any user-defined hosts (LAN boxes,
    /// the rack, a game server) every 2 s on its own thread. Only runs while the Monitor tab is visible.
    public sealed class Pinger
    {
        public volatile bool Active;
        public Func<string[]> Gateways = () => Array.Empty<string>();
        static string File_ => Path.Combine(MainWindow.AppData, "ping-targets.json");

        sealed class Tgt
        {
            public string Host, Label; public bool Auto;
            public readonly Queue<double> Hist = new();   // ms, -1 = lost
            public double Last = -1; public int Seq; public string Addr;
        }
        readonly object lk = new();
        List<Tgt> targets = new();
        string[] custom;
        Thread th;

        public Pinger()
        {
            try { custom = File.Exists(File_) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(File_)) : null; } catch { }
            custom ??= new[] { "1.1.1.1" };
        }

        public string[] Custom => custom;
        public void SetCustom(string[] hosts)
        {
            custom = hosts.Select(h => h?.Trim()).Where(h => !string.IsNullOrEmpty(h)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            try { File.WriteAllText(File_, JsonSerializer.Serialize(custom)); } catch { }
        }

        public void Start()
        {
            if (th != null) return;
            th = new Thread(Loop) { IsBackground = true, Name = "XenoSuite.Pinger", Priority = ThreadPriority.BelowNormal };
            th.Start();
        }

        void Loop()
        {
            while (true)
            {
                if (Active)
                {
                    try { Round(); } catch (Exception ex) { Log.Write("ping", ex.Message); }
                }
                Thread.Sleep(2000);
            }
        }

        void Round()
        {
            var want = new List<(string host, string label, bool auto)>();
            foreach (var g in Gateways().Take(2)) want.Add((g, "Gateway", true));
            foreach (var c in custom) want.Add((c, c, false));
            lock (lk)
            {
                var old = targets.ToDictionary(t => t.Host, StringComparer.OrdinalIgnoreCase);
                targets = want.GroupBy(w => w.host, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .Select(w => old.TryGetValue(w.host, out var t) ? t : new Tgt { Host = w.host, Label = w.label, Auto = w.auto }).ToList();
            }
            Tgt[] snap; lock (lk) snap = targets.ToArray();
            var tasks = snap.Select(async t =>
            {
                double ms = -1; string addr = null;
                try
                {
                    using var p = new Ping();
                    var r = await p.SendPingAsync(t.Host, 1000);
                    if (r.Status == IPStatus.Success) { ms = r.RoundtripTime; addr = r.Address?.ToString(); }
                }
                catch { }
                lock (lk)
                {
                    t.Last = ms; t.Seq++; if (addr != null) t.Addr = addr;
                    t.Hist.Enqueue(ms); while (t.Hist.Count > 60) t.Hist.Dequeue();
                }
            }).ToArray();
            Task.WaitAll(tasks, 3000);
        }

        /// Tiny per-tick payload: last result + rolling stats; the UI keeps its own graph history keyed by seq.
        public void Write(Utf8JsonWriter w)
        {
            w.WriteStartArray();
            lock (lk)
                foreach (var t in targets)
                {
                    var ok = t.Hist.Where(x => x >= 0).ToArray();
                    w.WriteStartObject();
                    w.WriteString("host", t.Host); w.WriteString("label", t.Label); w.WriteBoolean("auto", t.Auto);
                    if (t.Addr != null && t.Addr != t.Host) w.WriteString("addr", t.Addr);
                    w.WriteNumber("seq", t.Seq); w.WriteNumber("ms", t.Last);
                    w.WriteNumber("n", t.Hist.Count);
                    w.WriteNumber("loss", t.Hist.Count == 0 ? 0 : Math.Round(100.0 * (t.Hist.Count - ok.Length) / t.Hist.Count, 1));
                    if (ok.Length > 0)
                    {
                        w.WriteNumber("avg", Math.Round(ok.Average(), 1)); w.WriteNumber("min", ok.Min()); w.WriteNumber("max", ok.Max());
                        double jit = 0; for (int i = 1; i < ok.Length; i++) jit += Math.Abs(ok[i] - ok[i - 1]);
                        w.WriteNumber("jitter", ok.Length > 1 ? Math.Round(jit / (ok.Length - 1), 1) : 0);
                    }
                    w.WriteEndObject();
                }
            w.WriteEndArray();
        }
    }
}
