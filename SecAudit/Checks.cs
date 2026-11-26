// Security audit engine, from XenoSec (read-only checks). Built into XenoSuite so the Audit tab needs no separate install.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace XenoSuite.SecAudit;

/// <summary>
/// Every audit check. Each one only reads the system and returns one or more findings.
/// FixUri values are ms-settings: / windowsdefender: links the UI may offer to open; XenoSec itself changes nothing.
/// </summary>
static partial class Checks
{
    static readonly RegistryKey HKLM = Registry.LocalMachine, HKCU = Registry.CurrentUser;
    const string Defender = @"root\Microsoft\Windows\Defender";

    public static List<(string id, string title, Func<IEnumerable<Finding>> run)> All() => new()
    {
        ("av.active", "Antivirus", Antivirus),
        ("av.defender", "Defender settings", DefenderSettings),
        ("fw.profiles", "Firewall", Firewall),
        ("os.support", "Windows support status", OsSupport),
        ("upd.recent", "Windows Update", Updates),
        ("upd.reboot", "Pending restart", PendingReboot),
        ("uac", "User Account Control", Uac),
        ("smb1", "SMBv1", Smb1),
        ("rdp", "Remote Desktop", RemoteDesktop),
        ("bitlocker", "Drive encryption", BitLocker),
        ("secureboot", "Secure Boot", SecureBoot),
        ("tpm", "TPM", Tpm),
        ("vbs", "Core isolation", CoreIsolation),
        ("lsa", "LSA protection", LsaProtection),
        ("smartscreen", "SmartScreen", SmartScreen),
        ("acct.builtin", "Built-in accounts", BuiltinAccounts),
        ("acct.admins", "Administrators", Administrators),
        ("net.listeners", "Exposed network ports", Listeners),
        ("svc.remote", "Remote-access services", RemoteServices),
        ("ps.v2", "PowerShell 2.0", PowerShell2),
        ("ps.policy", "PowerShell script policy", ExecutionPolicy),
        ("autorun", "AutoRun / AutoPlay", AutoRun),
        ("startup", "Startup programs", Startup),
        ("hosts", "Hosts file", HostsFile),
        ("proxy", "Proxy settings", Proxy),
        ("cred.wdigest", "Password caching (WDigest)", WDigest),
        ("av.exclusions", "Antivirus exclusions", DefenderExclusions),
        ("drv.blocklist", "Vulnerable driver blocklist", DriverBlocklist),
        ("sac", "Smart App Control", SmartAppControl),
        ("ra", "Remote Assistance", RemoteAssistance),
        ("net.profile", "Network profile", NetworkProfile),
        ("svc.unquoted", "Unquoted service paths", UnquotedServicePaths),
        ("tasks", "Scheduled tasks", ScheduledTasks),
    };

    static Finding F(string id, string cat, string title, string status, string sev, string summary,
                     IEnumerable<string>? evidence = null, string? fix = null, string? uri = null, bool admin = false)
        => new(id, cat, title, status, sev, summary, evidence?.ToList() ?? new(), fix, uri, admin);

    static IEnumerable<Finding> One(Finding f) { yield return f; }

    // ---------------------------------------------------------------- antivirus
    static IEnumerable<Finding> Antivirus()
    {
        // Security Center knows about every registered AV, not just Defender.
        var products = Wmi.Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct")
            .Select(o => (name: Wmi.Get<string>(o, "displayName") ?? "?", state: Wmi.Get<uint>(o, "productState")))
            .Select(p => (p.name, on: ((p.state >> 12) & 0xF) == 1, current: ((p.state >> 4) & 0xF) == 0))
            .ToList();
        var ev = products.Select(p => $"{p.name}: {(p.on ? "on" : "off")}, definitions {(p.current ? "up to date" : "OUT OF DATE")}").ToList();
        var active = products.Where(p => p.on).ToList();
        if (products.Count == 0)
            return One(F("av.active", "Antivirus", "Antivirus", "error", "info", "Windows Security Center didn't report any antivirus product.", null));
        if (active.Count == 0)
            return One(F("av.active", "Antivirus", "Antivirus", "fail", "high", "No antivirus has real-time protection turned on.", ev,
                "Turn on real-time protection in Windows Security → Virus & threat protection.", "windowsdefender://threat"));
        if (active.Any(p => !p.current))
            return One(F("av.active", "Antivirus", "Antivirus", "warn", "medium", "Antivirus is on, but its definitions are out of date.", ev,
                "Update definitions in Windows Security → Virus & threat protection → Protection updates.", "windowsdefender://threat"));
        return One(F("av.active", "Antivirus", "Antivirus", "pass", "high", $"Real-time protection is on ({string.Join(", ", active.Select(a => a.name))}).", ev));
    }

    static IEnumerable<Finding> DefenderSettings()
    {
        var st = Wmi.Query(Defender, "SELECT * FROM MSFT_MpComputerStatus").FirstOrDefault();
        if (st == null || Wmi.Get<bool>(st, "AntivirusEnabled") != true)
        {
            yield return F("av.defender", "Antivirus", "Microsoft Defender", "info", "info",
                "Microsoft Defender isn't the active antivirus, so its own settings weren't checked.");
            yield break;
        }

        uint sigAge = Wmi.Get<uint>(st, "AntivirusSignatureAge");
        var sigEv = new[] { $"Definitions are {sigAge} day(s) old (version {Wmi.Get<string>(st, "AntivirusSignatureVersion")})" };
        yield return sigAge switch
        {
            > 7 => F("av.signatures", "Antivirus", "Defender definitions", "fail", "medium", "Defender's virus definitions are over a week old.", sigEv,
                     "Windows Security → Virus & threat protection → Protection updates → Check for updates.", "windowsdefender://threat"),
            > 3 => F("av.signatures", "Antivirus", "Defender definitions", "warn", "low", "Defender's virus definitions are a few days old.", sigEv,
                     "Windows Security → Virus & threat protection → Protection updates → Check for updates.", "windowsdefender://threat"),
            _ => F("av.signatures", "Antivirus", "Defender definitions", "pass", "medium", "Defender's virus definitions are current.", sigEv),
        };

        yield return Wmi.Get<bool>(st, "IsTamperProtected")
            ? F("av.tamper", "Antivirus", "Tamper protection", "pass", "medium", "Tamper protection is on, so malware can't quietly switch Defender off.")
            : F("av.tamper", "Antivirus", "Tamper protection", "warn", "medium", "Tamper protection is off. Malware with admin rights could turn Defender off.", null,
                "Windows Security → Virus & threat protection → Manage settings → Tamper Protection.", "windowsdefender://threatsettings");

        var pref = Wmi.Query(Defender, "SELECT PUAProtection, EnableControlledFolderAccess, MAPSReporting FROM MSFT_MpPreference").FirstOrDefault();
        if (pref != null)
        {
            yield return Wmi.Get<byte>(pref, "PUAProtection") == 1
                ? F("av.pua", "Antivirus", "Potentially unwanted apps", "pass", "low", "Blocking of potentially unwanted apps (adware, bundleware) is on.")
                : F("av.pua", "Antivirus", "Potentially unwanted apps", "warn", "low", "Blocking of potentially unwanted apps (adware, bundleware) is off.", null,
                    "Windows Security → App & browser control → Reputation-based protection → Potentially unwanted app blocking.", "windowsdefender://appbrowser");
            yield return Wmi.Get<byte>(pref, "EnableControlledFolderAccess") == 1
                ? F("av.cfa", "Antivirus", "Ransomware protection", "pass", "low", "Controlled folder access (ransomware protection) is on.")
                : F("av.cfa", "Antivirus", "Ransomware protection", "info", "low",
                    "Controlled folder access (ransomware protection) is off. It's optional; it can block legitimate apps from saving to Documents until you allow them.", null,
                    "Windows Security → Virus & threat protection → Ransomware protection.", "windowsdefender://threat");
            yield return Wmi.Get<uint>(pref, "MAPSReporting") > 0
                ? F("av.cloud", "Antivirus", "Cloud protection", "pass", "low", "Cloud-delivered protection is on (faster detection of new threats).")
                : F("av.cloud", "Antivirus", "Cloud protection", "warn", "low", "Cloud-delivered protection is off, so brand-new threats are detected more slowly.", null,
                    "Windows Security → Virus & threat protection → Manage settings → Cloud-delivered protection.", "windowsdefender://threatsettings");
        }
    }

    // ---------------------------------------------------------------- firewall
    static IEnumerable<Finding> Firewall()
    {
        var profiles = new[] { ("Domain", "DomainProfile", "medium"), ("Private", "StandardProfile", "medium"), ("Public", "PublicProfile", "high") };
        foreach (var (label, key, sev) in profiles)
        {
            // a group policy value overrides the local one
            int? policy = Reg.Int(HKLM, $@"SOFTWARE\Policies\Microsoft\WindowsFirewall\{key}", "EnableFirewall");
            int? local = Reg.Int(HKLM, $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{key}", "EnableFirewall");
            bool on = (policy ?? local ?? 1) == 1;
            var ev = new List<string> { $"Local setting: {Describe(local)}" };
            if (policy != null) ev.Add($"Group policy: {Describe(policy)} (overrides the local setting)");
            yield return on
                ? F($"fw.{label.ToLower()}", "Firewall", $"Firewall ({label} networks)", "pass", sev, $"The firewall is on for {label.ToLower()} networks.", ev)
                : F($"fw.{label.ToLower()}", "Firewall", $"Firewall ({label} networks)", "fail", sev,
                    $"The firewall is OFF for {label.ToLower()} networks.", ev,
                    "Windows Security → Firewall & network protection → turn the firewall on for this network type.", "windowsdefender://network");
        }
        static string Describe(int? v) => v switch { 1 => "on", 0 => "off", _ => "not set (on by default)" };
    }

    // ---------------------------------------------------------------- updates and support
    static IEnumerable<Finding> OsSupport()
    {
        const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        int build = int.TryParse(Reg.Str(HKLM, cv, "CurrentBuild"), out var b) ? b : 0;
        string edition = Reg.Str(HKLM, cv, "EditionID") ?? "";
        string version = Reg.Str(HKLM, cv, "DisplayVersion") ?? "";
        bool enterprise = edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) || edition.Contains("Education", StringComparison.OrdinalIgnoreCase);
        var ev = new List<string> { $"{(build >= 22000 ? "Windows 11" : "Windows 10")} {version} (build {build}), edition {edition}" };

        // Microsoft's published end-of-servicing dates (Home/Pro vs Enterprise/Education).
        DateTime? end = build switch
        {
            >= 26200 => enterprise ? new DateTime(2028, 10, 10) : new DateTime(2027, 10, 12),   // 25H2
            >= 26100 => enterprise ? new DateTime(2027, 10, 12) : new DateTime(2026, 10, 13),   // 24H2
            >= 22631 => enterprise ? new DateTime(2026, 11, 10) : new DateTime(2025, 11, 11),   // 23H2
            >= 22000 => new DateTime(2025, 10, 14),                                              // 21H2/22H2
            > 0 => new DateTime(2025, 10, 14),                                                   // Windows 10
            _ => null,
        };
        if (end == null)
            return One(F("os.support", "Updates", "Windows version", "info", "info", "Couldn't read the Windows build.", ev));
        ev.Add($"End of security updates for this version: {end:yyyy-MM-dd} (Microsoft lifecycle)");
        var today = DateTime.Today;
        if (end < today)
            return One(F("os.support", "Updates", "Windows version", "fail", "high",
                build < 22000
                    ? "Windows 10 no longer gets security updates (unless you enrolled in Extended Security Updates)."
                    : $"This Windows 11 version ({version}) has reached end of servicing and no longer gets security updates.",
                ev, "Settings → Windows Update: install the latest Windows 11 feature update.", "ms-settings:windowsupdate"));
        if (end < today.AddDays(60))
            return One(F("os.support", "Updates", "Windows version", "warn", "medium",
                $"This Windows version stops getting security updates on {end:MMMM d, yyyy}.", ev,
                "Settings → Windows Update: install the latest Windows 11 feature update.", "ms-settings:windowsupdate"));
        return One(F("os.support", "Updates", "Windows version", "pass", "high", $"This Windows version gets security updates until {end:MMMM yyyy}.", ev));
    }

    static IEnumerable<Finding> Updates()
    {
        int? wu = Reg.Int(HKLM, @"SYSTEM\CurrentControlSet\Services\wuauserv", "Start");
        if (wu == 4)
            yield return F("upd.service", "Updates", "Windows Update service", "fail", "high", "The Windows Update service is disabled, so no security updates install.",
                new[] { "wuauserv start type: Disabled" }, "Re-enable the Windows Update service (services.msc → Windows Update → Startup type: Manual).");

        var hotfixes = Wmi.Query(@"root\cimv2", "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering", 60)
            .Select(o => (id: Wmi.Get<string>(o, "HotFixID") ?? "", on: ParseDate(Wmi.Get<string>(o, "InstalledOn"))))
            .Where(h => h.on != null).OrderByDescending(h => h.on).ToList();
        if (hotfixes.Count == 0)
        {
            yield return F("upd.recent", "Updates", "Recent updates", "info", "info", "Windows didn't report any installed update dates.");
            yield break;
        }
        var last = hotfixes[0].on!.Value;
        int days = (int)(DateTime.Today - last.Date).TotalDays;
        var ev = hotfixes.Take(5).Select(h => $"{h.id} installed {h.on:yyyy-MM-dd}").ToList();
        yield return days switch
        {
            > 90 => F("upd.recent", "Updates", "Recent updates", "fail", "high", $"No update has been installed for {days} days.", ev,
                      "Settings → Windows Update → Check for updates.", "ms-settings:windowsupdate"),
            > 45 => F("upd.recent", "Updates", "Recent updates", "warn", "medium", $"The last update was installed {days} days ago.", ev,
                      "Settings → Windows Update → Check for updates.", "ms-settings:windowsupdate"),
            _ => F("upd.recent", "Updates", "Recent updates", "pass", "high", $"Updates are current (last installed {days} day(s) ago).", ev),
        };

        static DateTime? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) return d;
            if (DateTime.TryParse(s, out d)) return d;
            // some builds store a hex FILETIME
            if (long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var ft)) try { return DateTime.FromFileTime(ft); } catch { }
            return null;
        }
    }

    static IEnumerable<Finding> PendingReboot()
    {
        var reasons = new List<string>();
        if (Reg.Exists(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) reasons.Add("Windows Update is waiting for a restart");
        if (Reg.Exists(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")) reasons.Add("Component servicing is waiting for a restart");
        return One(reasons.Count == 0
            ? F("upd.reboot", "Updates", "Pending restart", "pass", "low", "No restart is pending.")
            : F("upd.reboot", "Updates", "Pending restart", "warn", "low", "Installed updates aren't fully applied until you restart.", reasons, "Restart the PC."));
    }

    // ---------------------------------------------------------------- hardening
    static IEnumerable<Finding> Uac()
    {
        const string p = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        int lua = Reg.Int(HKLM, p, "EnableLUA") ?? 1;
        int prompt = Reg.Int(HKLM, p, "ConsentPromptBehaviorAdmin") ?? 5;
        int secure = Reg.Int(HKLM, p, "PromptOnSecureDesktop") ?? 1;
        var ev = new[] { $"EnableLUA={lua}", $"ConsentPromptBehaviorAdmin={prompt}", $"PromptOnSecureDesktop={secure}" };
        const string fix = "Control Panel → User Accounts → Change User Account Control settings → at least the default (third) level.";
        if (lua == 0) return One(F("uac", "Hardening", "User Account Control", "fail", "high", "UAC is turned off: every program runs with full admin rights.", ev, fix));
        if (prompt == 0) return One(F("uac", "Hardening", "User Account Control", "fail", "medium", "UAC elevates admin programs without asking.", ev, fix));
        if (secure == 0) return One(F("uac", "Hardening", "User Account Control", "warn", "low", "UAC prompts don't use the secure desktop, so other programs could interfere with them.", ev, fix));
        return One(F("uac", "Hardening", "User Account Control", "pass", "high", "UAC is on and asks before programs get admin rights.", ev));
    }

    static IEnumerable<Finding> Smb1()
    {
        var feat = Wmi.Query(@"root\cimv2", "SELECT InstallState FROM Win32_OptionalFeature WHERE Name='SMB1Protocol'").FirstOrDefault();
        uint state = feat != null ? Wmi.Get<uint>(feat, "InstallState") : 2;
        int? reg = Reg.Int(HKLM, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1");
        bool on = state == 1 || reg == 1;
        return One(on
            ? F("smb1", "Hardening", "SMBv1", "fail", "high", "The obsolete SMBv1 file-sharing protocol is installed. It's what WannaCry and NotPetya spread through.",
                new[] { $"SMB1Protocol feature: {(state == 1 ? "enabled" : "disabled")}" },
                "Turn Windows features on or off → uncheck \"SMB 1.0/CIFS File Sharing Support\".", "ms-settings:optionalfeatures")
            : F("smb1", "Hardening", "SMBv1", "pass", "high", "The obsolete SMBv1 protocol is off."));
    }

    static IEnumerable<Finding> RemoteDesktop()
    {
        // Home editions ("Core*") can't host Remote Desktop at all; their registry flags are meaningless leftovers.
        var edition = Reg.Str(HKLM, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID") ?? "";
        if (edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
            return One(F("rdp", "Remote access", "Remote Desktop", "pass", "medium", "This Home edition of Windows can't accept Remote Desktop connections.",
                new[] { $"Edition: {edition}" }));
        int deny = Reg.Int(HKLM, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections") ?? 1;
        int nla = Reg.Int(HKLM, @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", "UserAuthentication") ?? 1;
        if (deny == 1) return One(F("rdp", "Remote access", "Remote Desktop", "pass", "medium", "Remote Desktop is off."));
        bool listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == 3389);
        var ev = new[] { "Remote Desktop: on", $"Network Level Authentication: {(nla == 1 ? "required" : "NOT required")}", $"Port 3389 listening: {(listening ? "yes" : "no")}" };
        return One(nla == 1
            ? F("rdp", "Remote access", "Remote Desktop", "warn", "medium", "Remote Desktop is on. Fine if you use it; otherwise it's an open door worth closing.", ev,
                "Settings → System → Remote Desktop.", "ms-settings:remotedesktop")
            : F("rdp", "Remote access", "Remote Desktop", "fail", "high", "Remote Desktop is on without Network Level Authentication, so anyone can reach the login screen.", ev,
                "Settings → System → Remote Desktop → require Network Level Authentication (or turn it off).", "ms-settings:remotedesktop"));
    }

    static IEnumerable<Finding> BitLocker()
    {
        if (!Audit.IsAdmin)
            return One(F("bitlocker", "Data protection", "Drive encryption", "skipped", "medium", "Run XenoSec as administrator to check drive encryption.", admin: true));
        var vols = Wmi.Query(@"root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume");
        if (vols.Count == 0)
            return One(F("bitlocker", "Data protection", "Drive encryption", "info", "medium", "BitLocker isn't available on this edition of Windows.", null,
                "Windows Home may offer \"Device encryption\" instead: Settings → Privacy & security → Device encryption.", "ms-settings:deviceencryption"));
        var sys = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").ToUpperInvariant();
        var ev = vols.Select(v => $"{Wmi.Get<string>(v, "DriveLetter") ?? "(no letter)"}: {Wmi.Get<uint>(v, "ProtectionStatus") switch { 1 => "encrypted, protection on", 0 => "not protected", _ => "unknown" }}").ToList();
        var sysVol = vols.FirstOrDefault(v => string.Equals(Wmi.Get<string>(v, "DriveLetter"), sys, StringComparison.OrdinalIgnoreCase));
        bool sysOn = sysVol != null && Wmi.Get<uint>(sysVol, "ProtectionStatus") == 1;
        return One(sysOn
            ? F("bitlocker", "Data protection", "Drive encryption", "pass", "medium", $"The system drive ({sys}) is encrypted.", ev)
            : F("bitlocker", "Data protection", "Drive encryption", "warn", "medium", $"The system drive ({sys}) isn't encrypted. If the PC is stolen, its files can be read.", ev,
                "Control Panel → BitLocker Drive Encryption (save the recovery key somewhere safe first)."));
    }

    static IEnumerable<Finding> SecureBoot()
    {
        int? v = Reg.Int(HKLM, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        return One(v switch
        {
            1 => F("secureboot", "Hardening", "Secure Boot", "pass", "medium", "Secure Boot is on, which blocks boot-level malware (bootkits)."),
            0 => F("secureboot", "Hardening", "Secure Boot", "warn", "medium", "Secure Boot is off. It's turned on in the UEFI/BIOS setup.", null,
                   "Enable Secure Boot in your motherboard's UEFI setup (the PC must boot in UEFI mode)."),
            _ => F("secureboot", "Hardening", "Secure Boot", "info", "medium", "Secure Boot isn't supported, likely because the PC boots in legacy BIOS mode."),
        });
    }

    static IEnumerable<Finding> Tpm()
    {
        if (!Audit.IsAdmin)
            return One(F("tpm", "Hardening", "TPM", "skipped", "low", "Run XenoSec as administrator to check the TPM.", admin: true));
        var t = Wmi.Query(@"root\CIMV2\Security\MicrosoftTpm", "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm").FirstOrDefault();
        if (t == null) return One(F("tpm", "Hardening", "TPM", "warn", "low", "No TPM was found (needed for BitLocker without a password and for Windows 11)."));
        bool on = Wmi.Get<bool>(t, "IsEnabled_InitialValue") && Wmi.Get<bool>(t, "IsActivated_InitialValue");
        var ev = new[] { $"Spec version: {Wmi.Get<string>(t, "SpecVersion")}" };
        return One(on
            ? F("tpm", "Hardening", "TPM", "pass", "low", "The TPM security chip is on.", ev)
            : F("tpm", "Hardening", "TPM", "warn", "low", "A TPM exists but isn't enabled.", ev, "Enable the TPM (fTPM / PTT) in your UEFI setup."));
    }

    static IEnumerable<Finding> CoreIsolation()
    {
        var dg = Wmi.Query(@"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard").FirstOrDefault();
        var running = (dg?["SecurityServicesRunning"] as uint[]) ?? Array.Empty<uint>();
        bool hvci = running.Contains(2u) || Reg.Int(HKLM, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") == 1;
        return One(hvci
            ? F("vbs", "Hardening", "Memory integrity", "pass", "low", "Memory integrity (core isolation) is on, blocking malicious drivers.")
            : F("vbs", "Hardening", "Memory integrity", "warn", "low", "Memory integrity (core isolation) is off. Some older drivers or anti-cheat software need it off.", null,
                "Windows Security → Device security → Core isolation details → Memory integrity.", "windowsdefender://coreisolation"));
    }

    static IEnumerable<Finding> LsaProtection()
    {
        int? ppl = Reg.Int(HKLM, @"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL");
        return One(ppl is 1 or 2
            ? F("lsa", "Hardening", "LSA protection", "pass", "low", "Local Security Authority protection is on, making password theft from memory harder.")
            : F("lsa", "Hardening", "LSA protection", "warn", "low", "Local Security Authority protection is off.", null,
                "Windows Security → Device security → Core isolation details → Local Security Authority protection.", "windowsdefender://coreisolation"));
    }

    static IEnumerable<Finding> SmartScreen()
    {
        int? policy = Reg.Int(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen");
        string? local = Reg.Str(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled");
        bool off = policy == 0 || string.Equals(local, "Off", StringComparison.OrdinalIgnoreCase);
        return One(off
            ? F("smartscreen", "Hardening", "SmartScreen", "warn", "medium", "SmartScreen app checks are off, so unknown downloaded programs run without a warning.", null,
                "Windows Security → App & browser control → Reputation-based protection → Check apps and files.", "windowsdefender://appbrowser")
            : F("smartscreen", "Hardening", "SmartScreen", "pass", "medium", "SmartScreen checks downloaded apps and files."));
    }

    // ---------------------------------------------------------------- accounts
    static IEnumerable<Finding> BuiltinAccounts()
    {
        var users = Wmi.Query(@"root\cimv2", "SELECT Name, SID, Disabled, PasswordRequired FROM Win32_UserAccount WHERE LocalAccount=True");
        foreach (var u in users)
        {
            var sid = Wmi.Get<string>(u, "SID") ?? "";
            bool disabled = Wmi.Get<bool>(u, "Disabled");
            if (sid.EndsWith("-500"))
                yield return disabled
                    ? F("acct.admin500", "Accounts", "Built-in Administrator", "pass", "medium", "The built-in Administrator account is disabled.")
                    : F("acct.admin500", "Accounts", "Built-in Administrator", "warn", "medium", "The built-in Administrator account is enabled. It's a well-known target for password guessing.", null,
                        "Disable it in Computer Management → Local Users and Groups (use your own admin account instead).");
            else if (sid.EndsWith("-501"))
                yield return disabled
                    ? F("acct.guest", "Accounts", "Guest account", "pass", "medium", "The Guest account is disabled.")
                    : F("acct.guest", "Accounts", "Guest account", "fail", "medium", "The Guest account is enabled.", null, "Disable it in Computer Management → Local Users and Groups.");
        }
        var blank = users.Where(u => !Wmi.Get<bool>(u, "Disabled") && !Wmi.Get<bool>(u, "PasswordRequired")
                                     && !(Wmi.Get<string>(u, "SID") ?? "").EndsWith("-501")
                                     && !(Wmi.Get<string>(u, "SID") ?? "").EndsWith("-503")).ToList();   // 503 = DefaultAccount
        if (blank.Count > 0)
            yield return F("acct.nopwd", "Accounts", "Accounts allowed a blank password", "info", "low",
                $"{blank.Count} enabled account(s) aren't required to have a password. This flag is also set on most Microsoft-account logins; make sure each one really has a password or PIN.",
                blank.Select(u => Wmi.Get<string>(u, "Name") ?? "?"));
    }

    static IEnumerable<Finding> Administrators()
    {
        // find the Administrators group by SID: its name is translated on non-English Windows
        var group = Wmi.Query(@"root\cimv2", "SELECT Name, Domain FROM Win32_Group WHERE LocalAccount=True AND SID='S-1-5-32-544'").FirstOrDefault();
        if (group == null) return One(F("acct.admins", "Accounts", "Administrators", "info", "info", "Couldn't read the Administrators group."));
        var name = Wmi.Get<string>(group, "Name"); var domain = Wmi.Get<string>(group, "Domain");
        var members = Wmi.Query(@"root\cimv2",
                $"SELECT PartComponent FROM Win32_GroupUser WHERE GroupComponent=\"Win32_Group.Domain='{domain}',Name='{name}'\"")
            .Select(m => Wmi.Get<string>(m, "PartComponent") ?? "")
            .Select(p => System.Text.RegularExpressions.Regex.Match(p, "Name=\"([^\"]+)\"").Groups[1].Value)
            .Where(n => n != "").ToList();
        return One(F("acct.admins", "Accounts", "Administrators", members.Count > 2 ? "warn" : "info", "low",
            $"{members.Count} account(s) have administrator rights." + (members.Count > 2 ? " Every extra admin is another way in; remove any you don't use." : ""),
            members));
    }

    // ---------------------------------------------------------------- network exposure
    static readonly Dictionary<int, (string what, string sev)> RiskyPorts = new()
    {
        [21] = ("FTP", "medium"), [22] = ("SSH", "low"), [23] = ("Telnet", "high"), [445] = ("SMB file sharing", "low"),
        [3389] = ("Remote Desktop", "medium"), [5900] = ("VNC remote control", "high"), [5985] = ("WinRM (HTTP)", "medium"),
        [5986] = ("WinRM (HTTPS)", "low"), [1433] = ("SQL Server", "medium"), [3306] = ("MySQL", "medium"),
        [5432] = ("PostgreSQL", "medium"), [6379] = ("Redis", "high"), [27017] = ("MongoDB", "high"), [9200] = ("Elasticsearch", "high"),
        [11434] = ("Ollama API", "medium"), [8000] = ("dev web server", "low"), [8080] = ("web server/proxy", "low"),
    };

    static IEnumerable<Finding> Listeners()
    {
        var exposed = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(ep => !IPAddress.IsLoopback(ep.Address))
            .GroupBy(ep => ep.Port).Select(g => g.Key).ToHashSet();
        var risky = exposed.Where(RiskyPorts.ContainsKey).OrderBy(p => p).ToList();
        var ev = risky.Select(p => $"TCP {p}: {RiskyPorts[p].what}").ToList();
        ev.Add($"{exposed.Count} TCP port(s) in total listen beyond this PC (the firewall decides whether they're actually reachable).");
        if (risky.Count == 0)
            return One(F("net.listeners", "Network", "Exposed network ports", "pass", "medium", "No commonly attacked service is listening for outside connections.", ev));
        string worst = risky.Any(p => RiskyPorts[p].sev == "high") ? "high" : risky.Any(p => RiskyPorts[p].sev == "medium") ? "medium" : "low";
        return One(F("net.listeners", "Network", "Exposed network ports", "warn", worst,
            $"{risky.Count} commonly attacked service(s) accept connections from other machines.", ev,
            "Stop services you don't need, or limit them to this PC (127.0.0.1). XenoNet's Listeners view shows which app owns each port."));
    }

    static IEnumerable<Finding> RemoteServices()
    {
        var watch = new[] { ("RemoteRegistry", "Remote Registry"), ("WinRM", "Windows Remote Management"), ("TlntSvr", "Telnet server"), ("SNMP", "SNMP"), ("sshd", "OpenSSH server") };
        var auto = new List<string>();
        foreach (var (svc, label) in watch)
        {
            int? start = Reg.Int(HKLM, $@"SYSTEM\CurrentControlSet\Services\{svc}", "Start");
            if (start == 2) auto.Add($"{label} ({svc}) starts automatically");
        }
        return One(auto.Count == 0
            ? F("svc.remote", "Remote access", "Remote-access services", "pass", "medium", "No remote-administration service is set to start automatically.")
            : F("svc.remote", "Remote access", "Remote-access services", "warn", "medium", "Remote-administration services start with Windows. Fine if you use them; otherwise set them to Manual.", auto,
                "services.msc → the service → Startup type: Manual or Disabled."));
    }

    // ---------------------------------------------------------------- scripting and AutoRun
    static IEnumerable<Finding> PowerShell2()
    {
        var feat = Wmi.Query(@"root\cimv2", "SELECT InstallState FROM Win32_OptionalFeature WHERE Name='MicrosoftWindowsPowerShellV2Root'").FirstOrDefault();
        bool on = feat != null && Wmi.Get<uint>(feat, "InstallState") == 1;
        return One(on
            ? F("ps.v2", "Hardening", "PowerShell 2.0", "warn", "low", "The legacy PowerShell 2.0 engine is installed. Attackers use it to dodge script logging and antivirus scanning.", null,
                "Turn Windows features on or off → uncheck \"Windows PowerShell 2.0\".", "ms-settings:optionalfeatures")
            : F("ps.v2", "Hardening", "PowerShell 2.0", "pass", "low", "The legacy PowerShell 2.0 engine isn't installed."));
    }

    static IEnumerable<Finding> ExecutionPolicy()
    {
        string? lm = Reg.Str(HKLM, @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy");
        string? cu = Reg.Str(HKCU, @"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy");
        string effective = cu ?? lm ?? "Restricted";
        var ev = new[] { $"Current user: {cu ?? "not set"}", $"Machine: {lm ?? "not set"}" };
        bool loose = effective.Equals("Unrestricted", StringComparison.OrdinalIgnoreCase) || effective.Equals("Bypass", StringComparison.OrdinalIgnoreCase);
        return One(loose
            ? F("ps.policy", "Hardening", "PowerShell script policy", "warn", "low", $"PowerShell runs any script ({effective}), including unsigned ones downloaded from the internet.", ev,
                "Use RemoteSigned instead: Settings → System → For developers → PowerShell.")
            : F("ps.policy", "Hardening", "PowerShell script policy", "pass", "low", $"PowerShell script policy: {effective}.", ev));
    }

    static IEnumerable<Finding> AutoRun()
    {
        int? lm = Reg.Int(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun");
        int? cu = Reg.Int(HKCU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun");
        int? autoplayOff = Reg.Int(HKCU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers", "DisableAutoplay");
        var ev = new[] { $"AutoRun policy: {(lm ?? cu)?.ToString("X") ?? "Windows default"}", $"AutoPlay: {(autoplayOff == 1 ? "off" : "on")}" };
        return One(autoplayOff == 1 || (lm ?? cu) == 0xFF
            ? F("autorun", "Hardening", "AutoPlay", "pass", "low", "AutoPlay is off, so plugging in a USB drive won't prompt to run anything.", ev)
            : F("autorun", "Hardening", "AutoPlay", "info", "low", "AutoPlay is on. Windows no longer auto-runs programs from USB drives, but turning AutoPlay off removes the prompt entirely.", ev,
                "Settings → Bluetooth & devices → AutoPlay → off.", "ms-settings:autoplay"));
    }

    // ---------------------------------------------------------------- persistence
    static IEnumerable<Finding> Startup()
    {
        var entries = new List<(string where, string name, string command)>();
        void Run(RegistryKey hive, string path, string label, RegistryView view = RegistryView.Registry64)
        {
            foreach (var (n, v) in Reg.Values(hive, path, view)) entries.Add((label, n, v));
        }
        Run(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "All users (Run)");
        Run(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "All users (Run, 32-bit)", RegistryView.Registry32);
        Run(HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "All users (RunOnce)");
        Run(HKCU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "This user (Run)");
        Run(HKCU, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "This user (RunOnce)");
        foreach (var (folder, label) in new[] { (Environment.SpecialFolder.Startup, "This user (Startup folder)"), (Environment.SpecialFolder.CommonStartup, "All users (Startup folder)") })
            try { foreach (var f in Directory.GetFiles(Environment.GetFolderPath(folder)).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))) entries.Add((label, Path.GetFileName(f), f)); } catch { }

        var ev = new List<string>();
        int suspicious = 0;
        foreach (var e in entries.DistinctBy(e => (e.name, e.command)))
        {
            var exe = ExePath(e.command);
            string note;
            if (exe == null) note = "target not resolved";
            else if (!File.Exists(exe)) { note = "TARGET MISSING"; suspicious++; }
            else
            {
                var signer = Signer(exe);
                bool tempDir = exe.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase);
                if (signer == null || tempDir) suspicious++;
                note = (signer == null ? "UNSIGNED" : "signed: " + signer) + (tempDir ? ", runs from a TEMP folder" : "");
            }
            ev.Add($"[{e.where}] {e.name}: {note}");
        }
        if (entries.Count == 0)
            return One(F("startup", "Persistence", "Startup programs", "pass", "low", "Nothing is set to start with Windows from the Run keys or Startup folders."));
        return One(suspicious == 0
            ? F("startup", "Persistence", "Startup programs", "info", "low", $"{entries.Count} program(s) start with Windows, all signed by their publishers.", ev, null, "ms-settings:startupapps")
            : F("startup", "Persistence", "Startup programs", "warn", "low",
                $"{entries.Count} program(s) start with Windows; {suspicious} are unsigned, missing or run from a temp folder. Unsigned isn't proof of malware (small tools often aren't signed), but check each one is something you installed.",
                ev, "Settings → Apps → Startup to turn off anything you don't recognize.", "ms-settings:startupapps"));
    }

    /// <summary>Executable path from a Run-key command line: quoted path, or the longest prefix that exists.</summary>
    static string? ExePath(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command ?? "").Trim();
        if (c.Length == 0) return null;
        if (c.StartsWith('"')) { int end = c.IndexOf('"', 1); return end > 1 ? c[1..end] : null; }
        if (c.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || File.Exists(c)) return c;
        var parts = c.Split(' ');
        for (int i = parts.Length; i > 0; i--)
        {
            var candidate = string.Join(' ', parts.Take(i));
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(candidate + ".exe")) return candidate + ".exe";
        }
        return parts[0];
    }

    /// <summary>Signer name if the file has a valid signature, embedded or through a Windows catalog (most built-in
    /// Windows programs are catalog-signed), or null if it isn't validly signed.</summary>
    static string? Signer(string path)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return "shortcut";
        try
        {
            var sig = Trust.Check(path);
            if (sig.Signed && sig.Valid) return (sig.Signer ?? "valid signature") + (sig.Catalog ? " (Windows catalog)" : "");
            if (sig.Problem == null || sig.Signed) return null;
        }
        catch { }
        try
        {
#pragma warning disable SYSLIB0057
            var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            var m = System.Text.RegularExpressions.Regex.Match(cert.Subject, "CN=\"?([^\",]+)");
            return m.Success ? m.Groups[1].Value : cert.Subject;
        }
        catch { return null; }
    }

    static IEnumerable<Finding> HostsFile()
    {
        var path = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch (Exception ex) { return One(F("hosts", "Network", "Hosts file", "error", "info", "Couldn't read the hosts file: " + ex.Message)); }
        var active = lines.Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0)
                          .Where(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"^(127\.0\.0\.1|::1)\s+localhost$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                          .ToList();
        return One(active.Count == 0
            ? F("hosts", "Network", "Hosts file", "pass", "low", "The hosts file has no custom entries.")
            : F("hosts", "Network", "Hosts file", "info", "low",
                $"The hosts file has {active.Count} custom entr{(active.Count == 1 ? "y" : "ies")}. Ad blockers and dev tools add these, and so does some malware: check that you recognize them.",
                active.Take(30)));
    }

    static IEnumerable<Finding> Proxy()
    {
        const string p = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings";
        int on = Reg.Int(HKCU, p, "ProxyEnable") ?? 0;
        string? server = Reg.Str(HKCU, p, "ProxyServer");
        string? pac = Reg.Str(HKCU, p, "AutoConfigURL");
        if (on != 1 && string.IsNullOrEmpty(pac))
            return One(F("proxy", "Network", "Proxy settings", "pass", "low", "No system proxy is set; web traffic goes out directly."));
        var ev = new List<string>();
        if (on == 1) ev.Add($"Proxy server: {server}");
        if (!string.IsNullOrEmpty(pac)) ev.Add($"Auto-config script: {pac}");
        return One(F("proxy", "Network", "Proxy settings", "warn", "low",
            "A system proxy is set, so web traffic passes through it. Expected with some VPNs and work networks; malware also uses this to intercept browsing.",
            ev, "Settings → Network & internet → Proxy.", "ms-settings:network-proxy"));
    }
}
