using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XenoSuite
{
    /// Fast, allocation-light system sampling: CPU (total + per logical core), memory, network, drives and the
    /// full process list — all from native calls, no WMI, so it can run at sub-second intervals.
    ///
    /// Perf notes (v1.1):
    ///  - Process rows are written straight into the tick's JSON writer (no object[] boxing, ~6k fewer allocs/tick)
    ///    and process names are cached per process instance instead of re-marshalled every tick.
    ///  - Network adapter list + static info (MAC/IPs/gateway/DNS) is cached and refreshed every 5 s or on an
    ///    address-change event; per tick only the byte counters are read.
    ///  - Drive list (DriveInfo.IsReady can stall for seconds on a dead network share) and the GPU-per-process
    ///    perf counters (a heavy category read) refresh on background tasks, never inside the tick.
    public sealed class Sampler
    {
        public readonly int Cores = Environment.ProcessorCount;
        IntPtr procBuf = IntPtr.Zero; int procBufLen = 0;
        long[] prevIdle, prevTotal, prevKernel;
        long lastTick;
        public readonly GpuProc GpuProc = new();
        PerformanceCounter dRead, dWrite, dIdle; bool diskBroken;

        /// System-wide disk throughput (fallback when LHM storage sensors aren't available).
        public object Disk()
        {
            if (diskBroken) return null;
            try
            {
                dRead ??= new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                dWrite ??= new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                dIdle ??= new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total");
                return new { r = Math.Round(dRead.NextValue()), w = Math.Round(dWrite.NextValue()), act = Math.Round(100 - Math.Min(100, dIdle.NextValue()), 1) };
            }
            catch (Exception ex) { diskBroken = true; Log.Write("disk", ex.Message); return null; }
        }

        // ---------- CPU per core ----------
        IntPtr cpuBuf = IntPtr.Zero;
        public double LastCpuTotal;
        public object Cpu()
        {
            const int SPPI = 8; int sz = 48 * Cores;
            if (cpuBuf == IntPtr.Zero) cpuBuf = Marshal.AllocHGlobal(sz);
            var buf = cpuBuf;
            if (Native.NtQuerySystemInformation(SPPI, buf, sz, out _) != 0) return null;
            var cores = new double[Cores]; double tb = 0, tt = 0, tk = 0;
            prevIdle ??= new long[Cores]; prevTotal ??= new long[Cores]; prevKernel ??= new long[Cores];
            for (int i = 0; i < Cores; i++)
            {
                long idle = Marshal.ReadInt64(buf, i * 48), kern = Marshal.ReadInt64(buf, i * 48 + 8), user = Marshal.ReadInt64(buf, i * 48 + 16);
                long total = kern + user;
                long dT = total - prevTotal[i], dI = idle - prevIdle[i], dK = (kern - idle) - prevKernel[i];
                cores[i] = dT > 0 ? Math.Round(Math.Clamp(100.0 * (dT - dI) / dT, 0, 100), 1) : 0;
                tb += dT - dI; tt += dT; tk += Math.Max(0, dK);
                prevIdle[i] = idle; prevTotal[i] = total; prevKernel[i] = kern - idle;
            }
            LastCpuTotal = tt > 0 ? Math.Round(100 * tb / tt, 1) : 0;
            return new { total = LastCpuTotal, kernel = tt > 0 ? Math.Round(100 * tk / tt, 1) : 0, cores };
        }

        // ---------- memory ----------
        public ulong LastMemUsed;
        public object Mem()
        {
            var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            Native.GlobalMemoryStatusEx(ref m);
            Native.GetPerformanceInfo(out var p, (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>());
            ulong pg = (ulong)p.PageSize;
            LastMemUsed = m.ullTotalPhys - m.ullAvailPhys;
            return new
            {
                total = m.ullTotalPhys, avail = m.ullAvailPhys, used = LastMemUsed, load = m.dwMemoryLoad,
                commit = (ulong)p.CommitTotal * pg, commitLimit = (ulong)p.CommitLimit * pg, commitPeak = (ulong)p.CommitPeak * pg,
                cache = (ulong)p.SystemCache * pg, paged = (ulong)p.KernelPaged * pg, nonpaged = (ulong)p.KernelNonpaged * pg,
                handles = p.HandleCount, procs = p.ProcessCount, threads = p.ThreadCount,
                pageTotal = m.ullTotalPageFile, pageAvail = m.ullAvailPageFile,
            };
        }

        // ---------- network ----------
        sealed class Nic
        {
            public NetworkInterface Ni; public string Id, Name, Desc, Type, Mac; public long Speed;
            public string[] Ips, Gw, Dns; public long PrevRx = -1, PrevTx = -1;
        }
        Nic[] nics = Array.Empty<Nic>();
        long nicRefresh, netTick;
        volatile bool nicDirty = true;
        bool nicHooked;

        void RefreshNics()
        {
            var old = nics.ToDictionary(n => n.Id);
            var list = new List<Nic>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                IPInterfaceProperties ip; try { ip = ni.GetIPProperties(); } catch { continue; }
                var n = new Nic
                {
                    Ni = ni, Id = ni.Id, Name = ni.Name, Desc = ni.Description, Type = ni.NetworkInterfaceType.ToString(), Speed = ni.Speed,
                    Mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
                    Ips = ip.UnicastAddresses.Select(a => a.Address.ToString()).ToArray(),
                    Gw = ip.GatewayAddresses.Select(a => a.Address.ToString()).ToArray(),
                    Dns = ip.DnsAddresses.Select(a => a.ToString()).ToArray(),
                };
                if (old.TryGetValue(n.Id, out var o)) { n.PrevRx = o.PrevRx; n.PrevTx = o.PrevTx; }
                list.Add(n);
            }
            nics = list.ToArray();
            Gateways = nics.SelectMany(n => n.Gw).Where(g => g.Contains('.') && g != "0.0.0.0").Distinct().ToArray();
        }

        /// IPv4 default gateways of the up adapters (used by the latency monitor).
        public string[] Gateways { get; private set; } = Array.Empty<string>();

        public object Net()
        {
            if (!nicHooked)
            {
                nicHooked = true;
                try { NetworkChange.NetworkAddressChanged += (_, __) => nicDirty = true; NetworkChange.NetworkAvailabilityChanged += (_, __) => nicDirty = true; } catch { }
            }
            long tick = Environment.TickCount64;
            if (nicDirty || tick - nicRefresh > 5000) { nicDirty = false; nicRefresh = tick; try { RefreshNics(); } catch (Exception ex) { Log.Write("net", ex.Message); } }
            long now = Stopwatch.GetTimestamp();
            double dt = netTick == 0 ? 1 : (now - netTick) / (double)Stopwatch.Frequency; netTick = now;
            var list = new List<object>(nics.Length);
            foreach (var n in nics)
            {
                long rx, tx;
                try { var st = n.Ni.GetIPStatistics(); rx = st.BytesReceived; tx = st.BytesSent; } catch { nicDirty = true; continue; }
                double down = 0, up = 0;
                if (n.PrevRx >= 0) { down = Math.Max(0, (rx - n.PrevRx) / dt); up = Math.Max(0, (tx - n.PrevTx) / dt); }
                n.PrevRx = rx; n.PrevTx = tx;
                list.Add(new { id = n.Id, name = n.Name, desc = n.Desc, type = n.Type, speed = n.Speed, mac = n.Mac, ips = n.Ips, gw = n.Gw, dns = n.Dns, down = Math.Round(down), up = Math.Round(up), rx, tx });
            }
            return list;
        }

        // ---------- drives (background refresh) ----------
        object drives = Array.Empty<object>();
        long drivesAt; int drivesBusy;
        public object Drives()
        {
            long now = Environment.TickCount64;
            if (now - drivesAt > 5000 && Interlocked.Exchange(ref drivesBusy, 1) == 0)
            {
                drivesAt = now;
                Task.Run(() =>
                {
                    try { drives = ReadDrives(); }
                    catch (Exception ex) { Log.Write("drives", ex.Message); }
                    finally { drivesBusy = 0; }
                });
            }
            return drives;
        }
        static object ReadDrives() => DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } })
            .Select(d => { try { return (object)new { name = d.Name, label = d.VolumeLabel, fmt = d.DriveFormat, type = d.DriveType.ToString(), total = d.TotalSize, free = d.AvailableFreeSpace }; } catch { return null; } })
            .Where(x => x != null).ToArray();

        // ---------- processes (SystemProcessInformation = 5) ----------
        // row: [pid, ppid, name, cpu%, privWS, private(commit), threads, handles, ioRead B/s, ioWrite B/s, gpu%, vramBytes, session, basePrio, createTime(ms epoch), ws]
        sealed class Prev { public long Cpu, Rd, Wr; public string Name; public int Gen; }
        readonly Dictionary<long, Prev> prevProc = new();   // key = pid<<32 ^ create
        int gen;
        public int ProcCount { get; private set; }

        /// Samples every process and writes the rows as a JSON array of arrays.
        public void WriteProcs(Utf8JsonWriter w)
        {
            w.WriteStartArray();
            int ret;
            if (procBufLen == 0) { procBufLen = 1 << 20; procBuf = Marshal.AllocHGlobal(procBufLen); }
            while (true)
            {
                int st = Native.NtQuerySystemInformation(5, procBuf, procBufLen, out ret);
                if (st == unchecked((int)0xC0000004)) { Marshal.FreeHGlobal(procBuf); procBufLen = Math.Max(ret + 65536, procBufLen * 2); procBuf = Marshal.AllocHGlobal(procBufLen); continue; }
                if (st != 0) { w.WriteEndArray(); return; }
                break;
            }
            long now = Stopwatch.GetTimestamp();
            double dt = lastTick == 0 ? 1 : (now - lastTick) / (double)Stopwatch.Frequency; lastTick = now;
            double cpuDen = dt * 1e7 * Cores;
            var gpu = GpuProc.Snapshot();
            gen++; int count = 0;
            int off = 0;
            while (true)
            {
                IntPtr p = procBuf + off;
                int next = Marshal.ReadInt32(p, 0);
                int threads = Marshal.ReadInt32(p, 4);
                long privWs = Marshal.ReadInt64(p, 8);
                long create = Marshal.ReadInt64(p, 32), user = Marshal.ReadInt64(p, 40), kern = Marshal.ReadInt64(p, 48);
                int prio = Marshal.ReadInt32(p, 72);
                int pid = (int)Marshal.ReadInt64(p, 80), ppid = (int)Marshal.ReadInt64(p, 88);
                int handles = Marshal.ReadInt32(p, 96), session = Marshal.ReadInt32(p, 100);
                long ws = Marshal.ReadInt64(p, 144), priv = Marshal.ReadInt64(p, 184);
                long rd = Marshal.ReadInt64(p, 232), wr = Marshal.ReadInt64(p, 240);
                long key = ((long)pid << 32) ^ create;
                long cpuT = user + kern;
                double cpu = 0, rdr = 0, wrr = 0;
                if (prevProc.TryGetValue(key, out var pv))
                {
                    cpu = Math.Clamp(100.0 * (cpuT - pv.Cpu) / cpuDen, 0, 100);
                    rdr = Math.Max(0, (rd - pv.Rd) / dt); wrr = Math.Max(0, (wr - pv.Wr) / dt);
                }
                else
                {
                    int nameLen = Marshal.ReadInt16(p, 56); IntPtr nameBuf = Marshal.ReadIntPtr(p, 64);
                    pv = new Prev { Name = pid == 0 ? "System Idle Process" : (nameBuf != IntPtr.Zero && nameLen > 0 ? Marshal.PtrToStringUni(nameBuf, nameLen / 2) : "?") };
                    prevProc[key] = pv;
                }
                pv.Cpu = cpuT; pv.Rd = rd; pv.Wr = wr; pv.Gen = gen;
                gpu.TryGetValue(pid, out var g);
                long createMs = create > 0 ? (create - 116444736000000000L) / 10000 : 0;

                w.WriteStartArray();
                w.WriteNumberValue(pid); w.WriteNumberValue(ppid); w.WriteStringValue(pv.Name); w.WriteNumberValue(Math.Round(cpu, 2));
                w.WriteNumberValue(privWs); w.WriteNumberValue(priv); w.WriteNumberValue(threads); w.WriteNumberValue(handles);
                w.WriteNumberValue(Math.Round(rdr)); w.WriteNumberValue(Math.Round(wrr)); w.WriteNumberValue(Math.Round(g.util, 1)); w.WriteNumberValue(g.vram);
                w.WriteNumberValue(session); w.WriteNumberValue(prio); w.WriteNumberValue(createMs); w.WriteNumberValue(ws);
                w.WriteEndArray();
                count++;
                if (next == 0) break;
                off += next;
            }
            w.WriteEndArray();
            ProcCount = count;
            if (prevProc.Count > count + 64)
            {
                var dead = new List<long>();
                foreach (var kv in prevProc) if (kv.Value.Gen != gen) dead.Add(kv.Key);
                foreach (var k in dead) prevProc.Remove(k);
            }
        }
    }

    /// Per-process GPU utilisation + dedicated VRAM via the "GPU Engine" / "GPU Process Memory" counter sets
    /// (the same source Task Manager uses). Refreshed on a background task every 2 s — reading the GPU Engine
    /// category is the single most expensive thing we do, so it never runs on the sampler thread.
    public sealed class GpuProc
    {
        volatile Dictionary<int, (double util, long vram)> cur = new();
        Dictionary<string, CounterSample> prev = new();
        long lastRun; bool broken; int busy;
        public double LastMs { get; private set; }
        public Dictionary<int, (double util, long vram)> Snapshot() => cur;

        public void Refresh()
        {
            if (broken) return;
            long now = Environment.TickCount64; if (now - lastRun < 2000) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            lastRun = now;
            Task.Run(() => { var sw = Stopwatch.StartNew(); try { Read(); } finally { LastMs = sw.Elapsed.TotalMilliseconds; busy = 0; } });
        }

        void Read()
        {
            try
            {
                var res = new Dictionary<int, (double util, long vram)>();
                var eng = new PerformanceCounterCategory("GPU Engine").ReadCategory();
                var util = eng["Utilization Percentage"];
                var next = new Dictionary<string, CounterSample>();
                var perPidEng = new Dictionary<(int, string), double>();
                if (util != null)
                    foreach (InstanceData d in util.Values)
                    {
                        var inst = d.InstanceName; int pid = ParsePid(inst); if (pid < 0) continue;
                        var s = d.Sample; next[inst] = s;
                        if (!prev.TryGetValue(inst, out var ps)) continue;
                        double v = CounterSample.Calculate(ps, s);
                        int ei = inst.IndexOf("engtype_"); string et = ei >= 0 ? inst[(ei + 8)..] : "?";
                        var k = (pid, et); perPidEng[k] = (perPidEng.TryGetValue(k, out var e0) ? e0 : 0) + v;
                    }
                prev = next;
                foreach (var kv in perPidEng)
                {
                    res.TryGetValue(kv.Key.Item1, out var r);
                    res[kv.Key.Item1] = (Math.Min(100, Math.Max(r.util, kv.Value)), r.vram);
                }
                var mem = new PerformanceCounterCategory("GPU Process Memory").ReadCategory();
                var ded = mem["Dedicated Usage"];
                if (ded != null)
                    foreach (InstanceData d in ded.Values)
                    {
                        int pid = ParsePid(d.InstanceName); if (pid < 0 || d.RawValue <= 0 || d.RawValue > (64L << 30)) continue;   // drop bogus adapter rows
                        res.TryGetValue(pid, out var r); res[pid] = (r.util, r.vram + d.RawValue);
                    }
                cur = res;
            }
            catch (Exception ex) { broken = true; Log.Write("gpuproc", ex.Message); }
        }

        static int ParsePid(string s)
        {
            if (!s.StartsWith("pid_")) return -1;
            int e = s.IndexOf('_', 4); return e > 4 && int.TryParse(s.AsSpan(4, e - 4), out int p) ? p : -1;
        }
    }
}
