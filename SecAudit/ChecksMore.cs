// Security audit engine, from XenoSec (read-only checks). Built into XenoSuite so the Audit tab needs no separate install.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace XenoSuite.SecAudit;

/// <summary>Credential, code-integrity and persistence checks. Read-only like the rest.</summary>
static partial class Checks
{
    // ---------------------------------------------------------------- credentials
    static IEnumerable<Finding> WDigest()
    {
        // UseLogonCredential=1 makes Windows keep plaintext passwords in LSASS memory, where Mimikatz-style tools read them.
        int? v = Reg.Int(HKLM, @"SYSTEM\CurrentControlSet\Control\SecurityProviders\WDigest", "UseLogonCredential");
        return One(v == 1
            ? F("cred.wdigest", "Accounts", "Password caching (WDigest)", "fail", "high",
                "WDigest is set to keep plaintext passwords in memory. Nothing modern needs this; attackers switch it on to harvest passwords.",
                new[] { "UseLogonCredential = 1" },
                "Set HKLM\\SYSTEM\\CurrentControlSet\\Control\\SecurityProviders\\WDigest\\UseLogonCredential to 0 (or delete it), then restart. Investigate how it got set.")
            : F("cred.wdigest", "Accounts", "Password caching (WDigest)", "pass", "high", "Windows doesn't keep plaintext passwords in memory (WDigest off).",
                new[] { $"UseLogonCredential = {(v?.ToString() ?? "not set (off by default)")}" }));
    }

    static IEnumerable<Finding> DefenderExclusions()
    {
        var st = Wmi.Query(Defender, "SELECT AntivirusEnabled FROM MSFT_MpComputerStatus").FirstOrDefault();
        var pref = st != null && Wmi.Get<bool>(st, "AntivirusEnabled")
            ? Wmi.Query(Defender, "SELECT ExclusionPath, ExclusionProcess, ExclusionExtension FROM MSFT_MpPreference").FirstOrDefault()
            : null;
        if (pref == null)
            return One(F("av.exclusions", "Antivirus", "Antivirus exclusions", "info", "info",
                "Microsoft Defender isn't the active antivirus. Check exclusions in your antivirus's own settings."));
        string[] Arr(string p) => (pref[p] as string[]) ?? Array.Empty<string>();
        var paths = Arr("ExclusionPath"); var procs = Arr("ExclusionProcess"); var exts = Arr("ExclusionExtension");
        // Since Windows 11 non-admins see "N/A: Must be an administrator to view exclusions" instead of the list.
        if (paths.Concat(procs).Concat(exts).Any(x => x.StartsWith("N/A", StringComparison.OrdinalIgnoreCase)))
            return One(F("av.exclusions", "Antivirus", "Antivirus exclusions", "skipped", "medium", "Run XenoSec as administrator to read Defender's exclusions.", admin: true));
        var ev = paths.Select(p => "Folder/file: " + p).Concat(procs.Select(p => "Process: " + p)).Concat(exts.Select(e => "Extension: " + e)).ToList();
        if (ev.Count == 0)
            return One(F("av.exclusions", "Antivirus", "Antivirus exclusions", "pass", "medium", "Defender has no exclusions, so it scans everything."));
        bool broad = paths.Any(p => p.Length <= 3 || p.TrimEnd('\\').Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
                                    || p.Contains(@"\Temp", StringComparison.OrdinalIgnoreCase) || p.Contains(@"\Downloads", StringComparison.OrdinalIgnoreCase))
                     || exts.Any(e => e.Trim('.').ToLowerInvariant() is "exe" or "dll" or "ps1" or "bat" or "js" or "vbs");
        return One(F("av.exclusions", "Antivirus", "Antivirus exclusions", "warn", broad ? "high" : "low",
            broad
                ? "Defender skips a broad location or a whole file type (whole drive, profile, Temp, Downloads or executables). Malware commonly adds exclusions like these."
                : $"Defender skips {ev.Count} item(s). Fine if you added them (games, dev folders); remove any you don't recognize.",
            ev, "Windows Security → Virus & threat protection → Manage settings → Exclusions.", "windowsdefender://threatsettings"));
    }

    // ---------------------------------------------------------------- code integrity
    static IEnumerable<Finding> DriverBlocklist()
    {
        int? v = Reg.Int(HKLM, @"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable");
        return One(v == 0
            ? F("drv.blocklist", "Hardening", "Vulnerable driver blocklist", "warn", "medium",
                "Microsoft's blocklist of known-vulnerable drivers is off. Attackers load those drivers to switch off security software.", new[] { "VulnerableDriverBlocklistEnable = 0" },
                "Windows Security → Device security → Core isolation details → Microsoft Vulnerable Driver Blocklist.", "windowsdefender://coreisolation")
            : F("drv.blocklist", "Hardening", "Vulnerable driver blocklist", "pass", "medium", "Microsoft's blocklist of known-vulnerable drivers is on.",
                new[] { $"VulnerableDriverBlocklistEnable = {(v?.ToString() ?? "not set (on by default)")}" }));
    }

    static IEnumerable<Finding> SmartAppControl()
    {
        int? v = Reg.Int(HKLM, @"SYSTEM\CurrentControlSet\Control\CI\Policy", "VerifiedAndReputablePolicyState");
        return One(v switch
        {
            1 => F("sac", "Hardening", "Smart App Control", "pass", "low", "Smart App Control is on: only apps Microsoft trusts or that are signed can run."),
            2 => F("sac", "Hardening", "Smart App Control", "info", "low", "Smart App Control is in evaluation mode and will decide on its own whether to switch on.", null, null, "windowsdefender://appbrowser"),
            0 => F("sac", "Hardening", "Smart App Control", "info", "low",
                   "Smart App Control is off. Normal for PCs that run dev tools or unsigned apps; it can only be turned back on by resetting Windows.", null, null, "windowsdefender://appbrowser"),
            _ => F("sac", "Hardening", "Smart App Control", "info", "info", "Smart App Control isn't available on this Windows version."),
        });
    }

    // ---------------------------------------------------------------- remote help and network
    static IEnumerable<Finding> RemoteAssistance()
    {
        int v = Reg.Int(HKLM, @"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp") ?? 0;
        return One(v == 1
            ? F("ra", "Remote access", "Remote Assistance", "warn", "low",
                "Remote Assistance invitations are allowed. Tech-support scammers lean on this; turn it off if you don't use it.", null,
                "System Properties → Remote → uncheck \"Allow Remote Assistance connections to this computer\".")
            : F("ra", "Remote access", "Remote Assistance", "pass", "low", "Remote Assistance invitations are off."));
    }

    static IEnumerable<Finding> NetworkProfile()
    {
        var profiles = Wmi.Query(@"root\StandardCimv2", "SELECT Name, InterfaceAlias, NetworkCategory, IPv4Connectivity FROM MSFT_NetConnectionProfile");
        if (profiles.Count == 0) return One(F("net.profile", "Network", "Network profile", "info", "info", "No connected network was reported."));
        var ev = profiles.Select(p =>
            $"{Wmi.Get<string>(p, "InterfaceAlias")}: {Wmi.Get<uint>(p, "NetworkCategory") switch { 0 => "Public", 1 => "Private", 2 => "Domain", _ => "?" }}"
            + (Wmi.Get<uint>(p, "IPv4Connectivity") == 4 ? " (internet)" : "")).ToList();
        int priv = profiles.Count(p => Wmi.Get<uint>(p, "NetworkCategory") == 1);
        return One(F("net.profile", "Network", "Network profile", "info", "low",
            priv > 0
                ? $"{priv} network(s) are set to Private, which lets other devices on them discover this PC and its shares. Right at home; on café or hotel Wi-Fi it should be Public."
                : "All networks are set to Public: this PC hides itself from other devices on them.",
            ev, "Settings → Network & internet → the connection → Network profile type.", "ms-settings:network-status"));
    }

    // ---------------------------------------------------------------- persistence / privilege escalation
    static IEnumerable<Finding> UnquotedServicePaths()
    {
        // An unquoted service path with spaces (C:\Program Files\My App\svc.exe) lets anyone who can write C:\Program.exe
        // or C:\Program Files\My.exe get code run as SYSTEM. Only paths whose folders a normal user could write matter in practice.
        var hits = new List<string>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var services = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var name in services?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = services!.OpenSubKey(name);
                if (k?.GetValue("ImagePath") is not string image || k.GetValue("Type") is not int type || (type & 0x30) == 0) continue;   // Win32 services only
                var path = Environment.ExpandEnvironmentVariables(image.Trim());
                if (path.StartsWith('"') || !path.Contains(' ')) continue;
                var exeEnd = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeEnd < 0) continue;
                var exe = path[..(exeEnd + 4)];
                if (!exe.Contains(' ')) continue;   // spaces only in the arguments are harmless
                hits.Add($"{name}: {image}");
            }
        }
        catch (Exception ex) { return One(F("svc.unquoted", "Persistence", "Unquoted service paths", "error", "info", "Couldn't read services: " + ex.Message)); }
        return One(hits.Count == 0
            ? F("svc.unquoted", "Persistence", "Unquoted service paths", "pass", "medium", "Every service path with spaces is quoted.")
            : F("svc.unquoted", "Persistence", "Unquoted service paths", "warn", "medium",
                $"{hits.Count} service(s) have an unquoted path containing spaces: a known privilege-escalation trick if any folder on that path is writable. Usually a vendor bug; update or reinstall that software.",
                hits, "Report it to the vendor or update the app. (Fixing it means editing the service's ImagePath in the registry to add quotes.)"));
    }

    static IEnumerable<Finding> ScheduledTasks()
    {
        var rows = new List<(string task, string exe, string note, bool suspicious)>();
        int total = 0;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return One(F("tasks", "Persistence", "Scheduled tasks", "error", "info", "Task Scheduler isn't available."));
            dynamic svc = Activator.CreateInstance(type)!;
            svc.Connect();
            void Walk(dynamic folder)
            {
                string fpath = folder.Path;
                if (fpath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;   // Windows' own tasks
                foreach (dynamic t in folder.GetTasks(1 /* include hidden */))
                {
                    total++;
                    if (!t.Enabled) continue;
                    foreach (dynamic a in t.Definition.Actions)
                    {
                        if ((int)a.Type != 0) continue;   // 0 = run a program
                        string exe = Environment.ExpandEnvironmentVariables(((string)a.Path ?? "").Trim('"'));
                        string args = a.Arguments ?? "";
                        string note; bool bad = false;
                        if (!Path.IsPathRooted(exe)) { note = "runs " + exe; }
                        else if (!File.Exists(exe)) { note = "TARGET MISSING"; bad = true; }
                        else
                        {
                            var signer = Signer(exe);
                            bool temp = exe.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\AppData\Roaming\", StringComparison.OrdinalIgnoreCase) && signer == null;
                            bad = signer == null || temp;
                            note = (signer == null ? "UNSIGNED" : "signed: " + signer) + (temp ? ", in a user-writable folder" : "");
                        }
                        // script hosts with encoded or downloaded payloads are a classic persistence pattern
                        if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(exe), @"^(powershell|pwsh|wscript|cscript|mshta|rundll32|regsvr32)(\.exe)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                            && System.Text.RegularExpressions.Regex.IsMatch(args, @"-enc|-e\s|frombase64|downloadstring|iex|http[s]?://|javascript:", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        { bad = true; note += ", SCRIPT WITH ENCODED/DOWNLOADED CODE"; }
                        rows.Add(((string)t.Path, exe, note, bad));
                    }
                }
                foreach (dynamic sub in folder.GetFolders(0)) Walk(sub);
            }
            Walk(svc.GetFolder(@"\"));
        }
        catch (Exception ex) { return One(F("tasks", "Persistence", "Scheduled tasks", "error", "info", "Couldn't read scheduled tasks: " + ex.Message)); }

        int badCount = rows.Count(r => r.suspicious);
        var ev = rows.OrderByDescending(r => r.suspicious).Select(r => $"{r.task}: {Path.GetFileName(r.exe)} ({r.note})").Take(40).ToList();
        if (rows.Count == 0)
            return One(F("tasks", "Persistence", "Scheduled tasks", "pass", "low", "No enabled non-Microsoft scheduled task runs a program."));
        return One(badCount == 0
            ? F("tasks", "Persistence", "Scheduled tasks", "info", "low", $"{rows.Count} non-Microsoft scheduled task action(s) run programs, all signed.", ev)
            : F("tasks", "Persistence", "Scheduled tasks", "warn",
                rows.Any(r => r.note.Contains("SCRIPT WITH")) ? "high" : "low",
                $"{badCount} of {rows.Count} non-Microsoft scheduled task(s) run something unsigned, missing, or a script with hidden code. Check that you recognize each one.",
                ev, "Task Scheduler (taskschd.msc): disable anything you don't recognize, then look it up before deleting."));
    }
}
