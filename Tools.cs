using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace XenoSuite
{
    /// On-demand operations called from the UI (everything that isn't the 1 Hz push).
    static class Tools
    {
        static readonly ConcurrentDictionary<int, object> metaCache = new();
        static readonly ConcurrentDictionary<string, (string desc, string company, string ver)> verCache = new();
        static readonly HashSet<int> suspended = new();

        static int I(JsonObject a, string k) => a[k] is JsonValue v ? (v.TryGetValue<int>(out var n) ? n : int.Parse(v.ToString())) : 0;
        static string S(JsonObject a, string k) => a[k]?.ToString();
        static bool B(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        public static object Dispatch(string m, JsonObject a, MainWindow w)
        {
            switch (m)
            {
                case "procMeta": return ProcMeta(a["pids"].AsArray().Select(x => (int)x).ToArray(), B(a, "fresh"));
                case "procDetails": return ProcDetails(I(a, "pid"));
                case "procKill": return ProcKill(I(a, "pid"), B(a, "tree"));
                case "procSuspend": return ProcSuspend(I(a, "pid"), true);
                case "procResume": return ProcSuspend(I(a, "pid"), false);
                case "procPriority": return ProcPriority(I(a, "pid"), S(a, "level"));
                case "procAffinity": return ProcAffinity(I(a, "pid"), S(a, "mask"));
                case "openLocation": Process.Start("explorer.exe", "/select,\"" + S(a, "path") + "\""); return true;
                case "openUrl": Process.Start(new ProcessStartInfo(S(a, "url")) { UseShellExecute = true }); return true;
                case "runTask": Process.Start(new ProcessStartInfo(S(a, "cmd")) { UseShellExecute = true }); return true;
                case "connections": return Connections();
                case "services": return Services();
                case "serviceAction": return ServiceAction(S(a, "name"), S(a, "action"));
                case "startup": return Startup();
                case "startupToggle": return StartupToggle(a, B(a, "enable"));
                case "sysInfo": return SysInfo();
                case "sysHealth": return SysHealth.Report(a["days"] is JsonValue dv && dv.TryGetValue<int>(out var dd) ? dd : 7);
                case "pingTargets": return w.Ping.Custom;
                case "setPingTargets": w.Ping.SetCustom(a["hosts"]?.AsArray().Select(x => x?.ToString()).ToArray() ?? Array.Empty<string>()); return w.Ping.Custom;
                case "hwReport": { var f = Path.Combine(MainWindow.DataDir, $"hw-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt"); File.WriteAllText(f, w.Hw.Report()); Process.Start("notepad.exe", f); return f; }
                case "resetMinMax": w.Hw.ResetMinMax(); return true;
                case "loadSettings": { var f = Path.Combine(MainWindow.AppData, "settings.json"); return File.Exists(f) ? File.ReadAllText(f) : "{}"; }
                case "saveSettings": File.WriteAllText(Path.Combine(MainWindow.AppData, "settings.json"), S(a, "json")); return true;
                case "writeFile": { var f = Path.Combine(MainWindow.DataDir, S(a, "name")); File.WriteAllText(f, S(a, "text")); return f; }
                case "openDataDir": Process.Start("explorer.exe", MainWindow.DataDir); return true;
                case "dnsLookup": try { return Dns.GetHostEntry(S(a, "ip")).HostName; } catch { return null; }
                case "log": Log.Write("js-" + S(a, "level"), S(a, "text")); return true;
                default: throw new Exception("Unknown method " + m);
            }
        }

        // ---------------- processes ----------------
        static object ProcMeta(int[] pids, bool fresh)
        {
            var res = new Dictionary<string, object>();
            foreach (var pid in pids)
            {
                if (!fresh && metaCache.TryGetValue(pid, out var c)) { res[pid.ToString()] = c; continue; }
                string path = pid <= 4 ? null : Native.ImagePath(pid);
                string user = pid <= 4 ? "SYSTEM" : Native.ProcessUser(pid);
                (string desc, string company, string ver) vi = (null, null, null);
                if (path != null)
                    vi = verCache.GetOrAdd(path, p =>
                    {
                        try { var fv = FileVersionInfo.GetVersionInfo(p); return (fv.FileDescription, fv.CompanyName, fv.FileVersion); }
                        catch { return (null, null, null); }
                    });
                var o = new { path, user, desc = vi.desc, company = vi.company, ver = vi.ver, x86 = pid > 4 && Native.Is32Bit(pid), susp = suspended.Contains(pid) };
                metaCache[pid] = o; res[pid.ToString()] = o;
            }
            return res;
        }

        static object ProcDetails(int pid)
        {
            string cmd = null, path = null;
            try
            {
                using var s = new ManagementObjectSearcher($"SELECT CommandLine, ExecutablePath FROM Win32_Process WHERE ProcessId={pid}");
                foreach (ManagementObject o in s.Get()) { cmd = o["CommandLine"] as string; path = o["ExecutablePath"] as string; }
            }
            catch { }
            path ??= Native.ImagePath(pid);
            var mods = new List<object>(); string prio = null, start = null, err = null; int threads = 0; string affinity = null;
            try
            {
                using var p = Process.GetProcessById(pid);
                threads = p.Threads.Count;
                try { prio = p.PriorityClass.ToString(); } catch { }
                try { start = p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
                try { affinity = ((long)p.ProcessorAffinity).ToString("X"); } catch { }
                try { foreach (ProcessModule md in p.Modules) { mods.Add(new { name = md.ModuleName, path = md.FileName, size = md.ModuleMemorySize, ver = md.FileVersionInfo?.FileVersion }); if (mods.Count > 600) break; } }
                catch (Exception ex) { err = ex.Message; }
            }
            catch (Exception ex) { err = ex.Message; }
            return new { pid, path, cmd, prio, start, threads, affinity, modules = mods, modErr = err, user = Native.ProcessUser(pid) };
        }

        static object ProcKill(int pid, bool tree)
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(tree); return true;
        }

        static object ProcSuspend(int pid, bool suspend)
        {
            var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
            if (h == IntPtr.Zero) throw new Exception("Access denied (" + Marshal.GetLastWin32Error() + ")");
            try
            {
                int st = suspend ? Native.NtSuspendProcess(h) : Native.NtResumeProcess(h);
                if (st != 0) throw new Exception($"NTSTATUS 0x{st:X8}");
                lock (suspended) { if (suspend) suspended.Add(pid); else suspended.Remove(pid); }
                metaCache.TryRemove(pid, out _);
                return true;
            }
            finally { Native.CloseHandle(h); }
        }

        static object ProcPriority(int pid, string level)
        {
            using var p = Process.GetProcessById(pid);
            p.PriorityClass = Enum.Parse<ProcessPriorityClass>(level); return p.PriorityClass.ToString();
        }

        static object ProcAffinity(int pid, string hexMask)
        {
            using var p = Process.GetProcessById(pid);
            if (string.IsNullOrEmpty(hexMask)) return ((long)p.ProcessorAffinity).ToString("X");
            p.ProcessorAffinity = (IntPtr)Convert.ToInt64(hexMask, 16); return ((long)p.ProcessorAffinity).ToString("X");
        }

        // ---------------- network connections ----------------
        static readonly string[] TcpStates = { "?", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB" };
        static int Port(uint p) => (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF));

        static object Connections()
        {
            var list = new List<object>();
            Table(true, 2, 5, 24, (b, o) => list.Add(new
            {
                proto = "TCP", state = TcpStates[Math.Min(12, Marshal.ReadInt32(b, o))],
                laddr = new IPAddress((uint)Marshal.ReadInt32(b, o + 4)).ToString(), lport = Port((uint)Marshal.ReadInt32(b, o + 8)),
                raddr = new IPAddress((uint)Marshal.ReadInt32(b, o + 12)).ToString(), rport = Port((uint)Marshal.ReadInt32(b, o + 16)),
                pid = Marshal.ReadInt32(b, o + 20)
            }));
            Table(true, 23, 5, 56, (b, o) => list.Add(new
            {
                proto = "TCP6", state = TcpStates[Math.Min(12, Marshal.ReadInt32(b, o + 48))],
                laddr = V6(b, o), lport = Port((uint)Marshal.ReadInt32(b, o + 20)),
                raddr = V6(b, o + 24), rport = Port((uint)Marshal.ReadInt32(b, o + 44)),
                pid = Marshal.ReadInt32(b, o + 52)
            }));
            Table(false, 2, 1, 12, (b, o) => list.Add(new
            {
                proto = "UDP", state = "", laddr = new IPAddress((uint)Marshal.ReadInt32(b, o)).ToString(), lport = Port((uint)Marshal.ReadInt32(b, o + 4)),
                raddr = "*", rport = 0, pid = Marshal.ReadInt32(b, o + 8)
            }));
            Table(false, 23, 1, 28, (b, o) => list.Add(new
            {
                proto = "UDP6", state = "", laddr = V6(b, o), lport = Port((uint)Marshal.ReadInt32(b, o + 20)),
                raddr = "*", rport = 0, pid = Marshal.ReadInt32(b, o + 24)
            }));
            return list;
        }

        static string V6(IntPtr b, int o) { var bytes = new byte[16]; Marshal.Copy(b + o, bytes, 0, 16); return new IPAddress(bytes).ToString(); }

        static void Table(bool tcp, int af, int cls, int rowSize, Action<IntPtr, int> row)
        {
            int size = 0;
            if (tcp) Native.GetExtendedTcpTable(IntPtr.Zero, ref size, true, af, cls, 0); else Native.GetExtendedUdpTable(IntPtr.Zero, ref size, true, af, cls, 0);
            size += 4096;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                uint r = tcp ? Native.GetExtendedTcpTable(buf, ref size, true, af, cls, 0) : Native.GetExtendedUdpTable(buf, ref size, true, af, cls, 0);
                if (r != 0) return;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++) row(buf, 4 + i * rowSize);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ---------------- services ----------------
        static object Services()
        {
            var list = new List<object>();
            using var s = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode, ProcessId, PathName, Description, StartName, ServiceType FROM Win32_Service");
            foreach (ManagementObject o in s.Get())
                list.Add(new
                {
                    name = o["Name"] as string, display = o["DisplayName"] as string, state = o["State"] as string, start = o["StartMode"] as string,
                    pid = Convert.ToInt32(o["ProcessId"] ?? 0), path = o["PathName"] as string, desc = o["Description"] as string, account = o["StartName"] as string,
                    type = o["ServiceType"] as string
                });
            return list;
        }

        static object ServiceAction(string name, string action)
        {
            using var sc = new ServiceController(name);
            var to = TimeSpan.FromSeconds(20);
            switch (action)
            {
                case "start": sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, to); break;
                case "stop": sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, to); break;
                case "restart":
                    if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, to); }
                    sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, to); break;
                case "pause": sc.Pause(); break;
                case "continue": sc.Continue(); break;
            }
            sc.Refresh(); return sc.Status.ToString();
        }

        // ---------------- startup ----------------
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        static object Startup()
        {
            var list = new List<object>();
            void FromKey(RegistryKey hive, string hiveName, string key, string approved, string scope)
            {
                using var k = hive.OpenSubKey(key); if (k == null) return;
                foreach (var n in k.GetValueNames())
                {
                    var cmd = k.GetValue(n)?.ToString();
                    list.Add(new { name = n, cmd, scope, source = $"{hiveName}\\{key}", approvedHive = hiveName, approved, valueName = n, enabled = Approved(hive, approved, n), exe = ExeFrom(cmd) });
                }
            }
            FromKey(Registry.CurrentUser, "HKCU", RunKey, ApprovedBase + "Run", "User");
            FromKey(Registry.LocalMachine, "HKLM", RunKey, ApprovedBase + "Run", "All users");
            FromKey(Registry.LocalMachine, "HKLM", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", ApprovedBase + "Run32", "All users (32-bit)");
            void FromFolder(string dir, RegistryKey hive, string hiveName, string scope)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir))
                {
                    var fn = Path.GetFileName(f); if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new { name = Path.GetFileNameWithoutExtension(f), cmd = f, scope, source = "Startup folder", approvedHive = hiveName, approved = ApprovedBase + "StartupFolder", valueName = fn, enabled = Approved(hive, ApprovedBase + "StartupFolder", fn), exe = f });
                }
            }
            FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "HKCU", "User");
            FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "HKLM", "All users");
            return list;
        }

        static string ExeFrom(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.StartsWith("\"")) { int e = cmd.IndexOf('"', 1); return e > 1 ? cmd[1..e] : cmd.Trim('"'); }
            int x = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return x > 0 ? cmd[..(x + 4)] : cmd.Split(' ')[0];
        }

        static bool Approved(RegistryKey hive, string key, string name)
        {
            using var k = hive.OpenSubKey(key);
            return k?.GetValue(name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
        }

        static object StartupToggle(JsonObject a, bool enable)
        {
            string hiveName = S(a, "approvedHive"), key = S(a, "approved"), name = S(a, "valueName");
            var hive = hiveName == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            // back up the key before touching it
            var dir = Path.Combine(MainWindow.DataDir, "regbackup"); Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{hiveName}-{Path.GetFileName(key)}.reg");
            using (var existing = hive.OpenSubKey(key))
                if (existing != null)
                {
                    var p = Process.Start(new ProcessStartInfo("reg.exe", $"export \"{hiveName}\\{key}\" \"{file}\" /y") { CreateNoWindow = true, UseShellExecute = false });
                    p.WaitForExit(10000);
                    if (p.ExitCode != 0) throw new Exception("Registry backup failed — nothing changed.");
                }
            using var k = hive.CreateSubKey(key, true);
            var val = new byte[12];
            val[0] = enable ? (byte)2 : (byte)3;
            if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(val, 4);
            k.SetValue(name, val, RegistryValueKind.Binary);
            return new { enabled = enable, backup = File.Exists(file) ? file : null };
        }

        // ---------------- system info ----------------
        static object SysInfo()
        {
            List<Dictionary<string, object>> Q(string wql)
            {
                var r = new List<Dictionary<string, object>>();
                try
                {
                    using var s = new ManagementObjectSearcher(wql);
                    foreach (ManagementObject o in s.Get())
                    {
                        var d = new Dictionary<string, object>();
                        foreach (var p in o.Properties) d[p.Name] = p.Value is Array arr ? string.Join(", ", arr.Cast<object>()) : p.Value;
                        r.Add(d);
                    }
                }
                catch (Exception ex) { r.Add(new Dictionary<string, object> { ["error"] = ex.Message }); }
                return r;
            }
            return new
            {
                os = Q("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, RegisteredUser, SystemDrive, WindowsDirectory FROM Win32_OperatingSystem"),
                cs = Q("SELECT Name, Manufacturer, Model, SystemType, TotalPhysicalMemory, Domain, UserName FROM Win32_ComputerSystem"),
                board = Q("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard"),
                bios = Q("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"),
                cpu = Q("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor"),
                ram = Q("SELECT BankLabel, DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory"),
                gpu = Q("SELECT Name, DriverVersion, DriverDate, AdapterRAM, VideoModeDescription, CurrentRefreshRate FROM Win32_VideoController"),
                disk = Q("SELECT Model, Size, InterfaceType, MediaType, SerialNumber, FirmwareRevision, Partitions FROM Win32_DiskDrive"),
                net = Q("SELECT Name, MACAddress, Speed, NetConnectionID FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE"),
                sound = Q("SELECT Name, Manufacturer FROM Win32_SoundDevice"),
                monitors = Q("SELECT Name, ScreenWidth, ScreenHeight FROM Win32_DesktopMonitor"),
                dotnet = RuntimeInformation.FrameworkDescription,
                machine = Environment.MachineName,
                uptime = Environment.TickCount64,
            };
        }
    }
}
