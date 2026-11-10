using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace XenoSuite
{
    /// Monitor ▸ Health: one on-demand report of the things that explain "why is this box acting up" —
    /// stability events (unexpected shutdowns, bugchecks, WHEA hardware errors, disk / file-system errors,
    /// GPU driver resets, service and app crashes), devices with driver problems, pending reboot, OS / boot facts,
    /// security posture at a glance, who's logged on, and SMB shares + who's connected to them.
    /// Every section runs in parallel and fails on its own.
    static class SysHealth
    {
        public static object Report(int days)
        {
            days = Math.Clamp(days <= 0 ? 7 : days, 1, 90);
            var sw = Stopwatch.StartNew();
            var parts = new (string key, Func<object> fn)[]
            {
                ("events", () => Events(days)), ("devices", Devices), ("reboot", PendingReboot), ("os", OsInfo),
                ("security", Security), ("sessions", Sessions), ("smb", Smb),
            };
            var tasks = parts.Select(p => Task.Run(() => { try { return p.fn(); } catch (Exception ex) { Log.Write("health." + p.key, ex.ToString()); return (object)new { error = ex.Message }; } })).ToArray();
            Task.WaitAll(tasks, 25000);
            var res = new Dictionary<string, object> { ["days"] = days, ["admin"] = MainWindow.IsAdmin() };
            for (int i = 0; i < parts.Length; i++) res[parts[i].key] = tasks[i].IsCompleted ? tasks[i].Result : new { error = "timed out" };
            res["ms"] = sw.ElapsedMilliseconds;
            return res;
        }

        // ------------------------------------------------------------------ events
        sealed record Cat(string Key, string Title, string Log, string Filter, string Sev, string Hint);

        static object Events(int days)
        {
            long ms = days * 86400000L;
            string T = $"TimeCreated[timediff(@SystemTime) <= {ms}]";
            var cats = new[]
            {
                new Cat("shutdown", "Unexpected shutdowns", "System", $"Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and {T}", "bad",
                    "Kernel-Power 41: the box rebooted without shutting down cleanly — power loss, PSU, hard hang, or an unstable overclock."),
                new Cat("bsod", "Blue screens", "System", $"(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='BugCheck']) and EventID=1001 and {T}", "bad",
                    "Bugcheck 1001 — the message has the stop code and dump file path."),
                new Cat("whea", "Hardware errors (WHEA)", "System", $"Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2 or Level=3) and {T}", "bad",
                    "CPU / memory / PCIe errors reported by the hardware. Corrected errors (warnings) on an overclocked rig usually mean it's not fully stable."),
                new Cat("disk", "Disk & file-system errors", "System", $"(Provider[@Name='disk'] or Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs'] or Provider[@Name='volmgr'] or Provider[@Name='stornvme'] or Provider[@Name='storahci'] or Provider[@Name='Microsoft-Windows-StorPort']) and (Level=1 or Level=2 or Level=3) and {T}", "warn",
                    "Bad blocks (disk 7), paging errors (51), I/O retries (153), NTFS corruption (55/98). Check the drive in Disk ▸ Health."),
                new Cat("gpu", "GPU driver resets", "System", $"((Provider[@Name='Display'] and EventID=4101) or ((Provider[@Name='nvlddmkm'] or Provider[@Name='amdkmdag'] or Provider[@Name='amdwddmg']) and (Level=1 or Level=2))) and {T}", "warn",
                    "Display 4101 = driver stopped responding and recovered (TDR). Frequent ones point at GPU clocks, power or the driver."),
                new Cat("svc", "Service crashes", "System", $"Provider[@Name='Service Control Manager'] and (EventID=7031 or EventID=7034) and {T}", "warn",
                    "A service terminated unexpectedly (7031 = SCM restarted it, 7034 = it stayed down)."),
                new Cat("app", "App crashes & hangs", "Application", $"((Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002)) and {T}", "info",
                    "Grouped by program below."),
                new Cat("wu", "Windows Update", "System", $"Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20) and {T}", "info",
                    "19 = installed, 20 = failed."),
            };
            var results = new object[cats.Length];
            Parallel.For(0, cats.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                var c = cats[i];
                try { results[i] = ReadCat(c); }
                catch (Exception ex) { results[i] = new { key = c.Key, title = c.Title, sev = c.Sev, hint = c.Hint, count = 0, error = ex.Message }; }
            });
            return results;
        }

        static object ReadCat(Cat c)
        {
            var q = new EventLogQuery(c.Log, PathType.LogName, $"*[System[{c.Filter}]]") { ReverseDirection = true };
            using var rd = new EventLogReader(q);
            int count = 0; var latest = new List<object>();
            var apps = new Dictionary<string, (int n, DateTime last, string path, int hangs)>(StringComparer.OrdinalIgnoreCase);
            int failed = 0; DateTime? lastOk = null;
            for (EventRecord e; (e = rd.ReadEvent()) != null;)
            {
                using (e)
                {
                    count++;
                    var t = e.TimeCreated ?? DateTime.MinValue;
                    if (c.Key == "app")
                    {
                        string name = Prop(e, 0) ?? "?"; string path = e.Id == 1000 ? Prop(e, 10) : Prop(e, 6);
                        apps.TryGetValue(name, out var a);
                        apps[name] = (a.n + 1, a.last > t ? a.last : t, a.path ?? path, a.hangs + (e.Id == 1002 ? 1 : 0));
                    }
                    if (c.Key == "wu") { if (e.Id == 20) failed++; else lastOk ??= t; }
                    if (latest.Count < 8)
                        latest.Add(new { time = t, id = e.Id, provider = e.ProviderName, level = e.LevelDisplayNameSafe(), msg = Msg(e) });
                }
                if (count >= 500) break;
            }
            return new
            {
                key = c.Key, title = c.Title, sev = c.Sev, hint = c.Hint, count, capped = count >= 500, latest,
                apps = c.Key == "app" ? apps.OrderByDescending(a => a.Value.n).Take(25).Select(a => new { name = a.Key, count = a.Value.n, hangs = a.Value.hangs, last = a.Value.last, path = a.Value.path }).ToList() : null,
                wuFailed = c.Key == "wu" ? failed : (int?)null, wuLastOk = lastOk,
            };
        }

        static string LevelDisplayNameSafe(this EventRecord e) { try { return e.LevelDisplayName; } catch { return e.Level?.ToString(); } }
        static string Prop(EventRecord e, int i) { try { return e.Properties.Count > i ? e.Properties[i].Value?.ToString() : null; } catch { return null; } }
        static string Msg(EventRecord e)
        {
            string m = null;
            try { m = e.FormatDescription(); } catch { }
            if (string.IsNullOrWhiteSpace(m)) try { m = string.Join(" · ", e.Properties.Select(p => p.Value?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(8)); } catch { }
            m = Regex.Replace(m ?? "", @"\s+", " ").Trim();
            return m.Length > 420 ? m[..420] + "…" : m;
        }

        // ------------------------------------------------------------------ devices with problems
        static readonly Dictionary<uint, string> CmErr = new()
        {
            [1] = "Not configured correctly", [3] = "Driver may be corrupted / low memory", [10] = "Device cannot start", [12] = "Not enough free resources",
            [14] = "Restart required", [18] = "Reinstall drivers", [19] = "Registry configuration corrupt", [21] = "Being removed",
            [22] = "Disabled", [24] = "Not present / not working", [28] = "Drivers not installed", [29] = "Disabled by firmware",
            [31] = "Driver failed to load", [32] = "Driver service disabled", [37] = "Driver initialization failed", [39] = "Driver corrupted or missing",
            [41] = "Driver loaded but device not found", [43] = "Stopped — reported problems (code 43)", [48] = "Driver blocked", [52] = "Unsigned driver",
        };
        static object Devices()
        {
            var list = new List<object>();
            foreach (var o in Wmi.Query(Wmi.Cimv2, "SELECT Name, PNPClass, ConfigManagerErrorCode, DeviceID, Manufacturer FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
            {
                uint code = Wmi.Ui(o, "ConfigManagerErrorCode"); if (code == 45) continue;   // 45 = not currently connected
                list.Add(new { name = Wmi.Str(o, "Name") ?? Wmi.Str(o, "DeviceID"), cls = Wmi.Str(o, "PNPClass"), code, meaning = CmErr.TryGetValue(code, out var m) ? m : "Problem code " + code, id = Wmi.Str(o, "DeviceID"), mfr = Wmi.Str(o, "Manufacturer") });
            }
            return list;
        }

        // ------------------------------------------------------------------ pending reboot
        static object PendingReboot()
        {
            var reasons = new List<string>();
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            bool Exists(string k) { using var x = lm.OpenSubKey(k); return x != null; }
            if (Exists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) reasons.Add("Windows servicing (CBS) needs a restart");
            if (Exists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) reasons.Add("Windows Update needs a restart");
            using (var sm = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
                if (sm?.GetValue("PendingFileRenameOperations") is string[] pf && pf.Any(s => !string.IsNullOrWhiteSpace(s)))
                    reasons.Add($"File rename/delete operations queued for boot ({pf.Count(s => !string.IsNullOrWhiteSpace(s)) / 2} files)");
            using (var a = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName"))
            using (var b = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName"))
            {
                var an = a?.GetValue("ComputerName") as string; var bn = b?.GetValue("ComputerName") as string;
                if (an != null && bn != null && !an.Equals(bn, StringComparison.OrdinalIgnoreCase)) reasons.Add($"Computer rename pending ({an} → {bn})");
            }
            return new { pending = reasons.Count > 0, reasons };
        }

        // ------------------------------------------------------------------ OS / boot / power
        static object OsInfo()
        {
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cv = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = cv?.GetValue("ProductName") as string, disp = cv?.GetValue("DisplayVersion") as string, build = cv?.GetValue("CurrentBuild") as string;
            int ubr = cv?.GetValue("UBR") is int u ? u : 0;
            if (int.TryParse(build, out var bn) && bn >= 22000 && product != null) product = product.Replace("Windows 10", "Windows 11");
            DateTime? installed = cv?.GetValue("InstallDate") is int idt ? DateTimeOffset.FromUnixTimeSeconds((uint)idt).LocalDateTime : null;
            var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
            bool? fastStartup = null;
            using (var p = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power")) if (p?.GetValue("HiberbootEnabled") is int hb) fastStartup = hb == 1;
            string plan = null;
            try { var r = Proc.Run("powercfg.exe", "/getactivescheme", timeoutMs: 5000); var m = Regex.Match(r.Out, @"\(([^)]+)\)"); plan = m.Success ? m.Groups[1].Value : r.Out.Trim(); } catch { }
            long? bootMs = null; long? mainPathMs = null;
            try
            {
                var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[EventID=100]]") { ReverseDirection = true };
                using var rd = new EventLogReader(q); using var e = rd.ReadEvent();
                if (e != null)
                {
                    var x = e.ToXml();
                    var m1 = Regex.Match(x, @"Name='BootTime'>(\d+)<"); if (m1.Success) bootMs = long.Parse(m1.Groups[1].Value);
                    var m2 = Regex.Match(x, @"Name='MainPathBootTime'>(\d+)<"); if (m2.Success) mainPathMs = long.Parse(m2.Groups[1].Value);
                }
            }
            catch { }
            return new { product, version = disp, build = build + (ubr > 0 ? "." + ubr : ""), installed, boot, uptimeMs = Environment.TickCount64, fastStartup, plan, bootMs, mainPathMs, machine = Environment.MachineName, domain = Environment.UserDomainName };
        }

        // ------------------------------------------------------------------ security posture
        static object Security()
        {
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            int? Dw(string key, string val) { using var k = lm.OpenSubKey(key); return k?.GetValue(val) is int i ? i : null; }
            const string fw = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
            var firewall = new { domain = Dw(fw + "DomainProfile", "EnableFirewall") == 1, priv = Dw(fw + "StandardProfile", "EnableFirewall") == 1, pub = Dw(fw + "PublicProfile", "EnableFirewall") == 1 };
            int? sb = Dw(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            int? lua = Dw(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA");
            int? deny = Dw(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections");
            int? nla = Dw(@"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication");

            object defender = null;
            try
            {
                var o = Wmi.One(@"root\Microsoft\Windows\Defender", "SELECT AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge, AntivirusSignatureLastUpdated, QuickScanAge, FullScanAge, IsTamperProtected FROM MSFT_MpComputerStatus");
                if (o != null) defender = new { service = Wmi.Bool(o, "AMServiceEnabled"), av = Wmi.Bool(o, "AntivirusEnabled"), rtp = Wmi.Bool(o, "RealTimeProtectionEnabled"), sigAge = Wmi.Ui(o, "AntivirusSignatureAge"), quickScanAge = Wmi.Ui(o, "QuickScanAge"), fullScanAge = Wmi.Ui(o, "FullScanAge"), tamper = Wmi.Bool(o, "IsTamperProtected") };
            }
            catch { }
            var avs = new List<object>();
            try
            {
                foreach (var o in Wmi.Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct"))
                {
                    uint st = Wmi.Ui(o, "productState");
                    avs.Add(new { name = Wmi.Str(o, "displayName"), on = ((st >> 12) & 0xF) == 1, upToDate = ((st >> 4) & 0xF) == 0 });
                }
            }
            catch { }
            object smb = null;
            try
            {
                var o = Wmi.One(@"root\Microsoft\Windows\SMB", "SELECT EnableSMB1Protocol, EnableSMB2Protocol, RequireSecuritySignature, EncryptData FROM MSFT_SmbServerConfiguration");
                if (o != null) smb = new { smb1 = Wmi.Bool(o, "EnableSMB1Protocol"), smb2 = Wmi.Bool(o, "EnableSMB2Protocol"), signing = Wmi.Bool(o, "RequireSecuritySignature"), encrypt = Wmi.Bool(o, "EncryptData") };
            }
            catch { }
            object tpm = null;
            try
            {
                var o = Wmi.One(@"root\cimv2\Security\MicrosoftTpm", "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion, ManufacturerVersion FROM Win32_Tpm");
                tpm = o == null ? new { present = false, enabled = false, spec = (string)null } : new { present = true, enabled = Wmi.Bool(o, "IsEnabled_InitialValue"), spec = Wmi.Str(o, "SpecVersion")?.Split(',')[0].Trim() };
            }
            catch { }
            return new { firewall, secureBoot = sb == null ? (bool?)null : sb == 1, uac = lua != 0, rdp = deny == 0, rdpNla = nla != 0, defender, avs, smb, tpm };
        }

        // ------------------------------------------------------------------ logged-on sessions
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WTSEnumerateSessionsW(IntPtr srv, int res, int ver, out IntPtr info, out int count);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool WTSQuerySessionInformationW(IntPtr srv, int id, int cls, out IntPtr buf, out int bytes);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
        static readonly string[] WtsState = { "Active", "Connected", "ConnectQuery", "Shadow", "Disconnected", "Idle", "Listen", "Reset", "Down", "Init" };

        static object Sessions()
        {
            var list = new List<object>();
            if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buf, out int n)) throw new Exception("WTSEnumerateSessions failed: " + Marshal.GetLastWin32Error());
            try
            {
                int sz = IntPtr.Size == 8 ? 24 : 12;
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = buf + i * sz;
                    int id = Marshal.ReadInt32(p); string station = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, IntPtr.Size)); int state = Marshal.ReadInt32(p, IntPtr.Size * 2);
                    string user = Q(id, 5), dom = Q(id, 7), client = Q(id, 10);
                    if (string.IsNullOrEmpty(user)) continue;
                    DateTime? logon = null; double? idleMin = null;
                    if (WTSQuerySessionInformationW(IntPtr.Zero, id, 24, out var si, out _) && si != IntPtr.Zero)
                    {
                        try
                        {
                            long lastInput = Marshal.ReadInt64(si, 192), logonT = Marshal.ReadInt64(si, 200), cur = Marshal.ReadInt64(si, 208);
                            if (logonT > 0) logon = DateTime.FromFileTime(logonT);
                            if (lastInput > 0 && cur > lastInput) idleMin = Math.Round((cur - lastInput) / 6e8, 1);
                        }
                        finally { WTSFreeMemory(si); }
                    }
                    list.Add(new { id, station, state = state >= 0 && state < WtsState.Length ? WtsState[state] : state.ToString(), user = string.IsNullOrEmpty(dom) ? user : dom + "\\" + user, client, logon, idleMin, remote = station?.StartsWith("RDP", StringComparison.OrdinalIgnoreCase) == true });
                }
            }
            finally { WTSFreeMemory(buf); }
            return list;
        }
        static string Q(int id, int cls)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, id, cls, out var b, out _) || b == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(b); } finally { WTSFreeMemory(b); }
        }

        // ------------------------------------------------------------------ SMB shares / clients / mapped drives
        static object Smb()
        {
            var shares = new List<object>(); var sessions = new List<object>(); var conns = new List<object>(); var mapped = new List<object>();
            try
            {
                foreach (var o in Wmi.Query(Wmi.Cimv2, "SELECT Name, Path, Description, Type FROM Win32_Share"))
                {
                    uint t = Wmi.Ui(o, "Type");
                    shares.Add(new { name = Wmi.Str(o, "Name"), path = Wmi.Str(o, "Path"), desc = Wmi.Str(o, "Description"), special = (t & 0x80000000) != 0, kind = (t & 0xFF) switch { 0 => "Disk", 1 => "Printer", 2 => "Device", 3 => "IPC", _ => "?" } });
                }
            }
            catch { }
            try
            {
                foreach (var o in Wmi.Query(Wmi.Cimv2, "SELECT ComputerName, UserName, ActiveTime, IdleTime, ResourcesOpened, ClientType FROM Win32_ServerSession"))
                    sessions.Add(new { computer = Wmi.Str(o, "ComputerName"), user = Wmi.Str(o, "UserName"), active = Wmi.U(o, "ActiveTime"), idle = Wmi.U(o, "IdleTime"), open = Wmi.Ui(o, "ResourcesOpened"), client = Wmi.Str(o, "ClientType") });
            }
            catch { }
            try
            {
                foreach (var o in Wmi.Query(Wmi.Cimv2, "SELECT ShareName, UserName, ComputerName, NumberOfFiles, ActiveTime FROM Win32_ServerConnection"))
                    conns.Add(new { share = Wmi.Str(o, "ShareName"), user = Wmi.Str(o, "UserName"), computer = Wmi.Str(o, "ComputerName"), files = Wmi.Ui(o, "NumberOfFiles"), active = Wmi.U(o, "ActiveTime") });
            }
            catch { }
            try
            {
                foreach (var o in Wmi.Query(Wmi.Cimv2, "SELECT LocalName, RemoteName, ConnectionState, UserName, Status FROM Win32_NetworkConnection"))
                    mapped.Add(new { local = Wmi.Str(o, "LocalName"), remote = Wmi.Str(o, "RemoteName"), state = Wmi.Str(o, "ConnectionState"), user = Wmi.Str(o, "UserName"), status = Wmi.Str(o, "Status") });
            }
            catch { }
            return new { shares, sessions, conns, mapped };
        }
    }
}
