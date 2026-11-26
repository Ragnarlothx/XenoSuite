// Security audit engine, from XenoSec (read-only checks). Built into XenoSuite so the Audit tab needs no separate install.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace XenoSuite.SecAudit;

/// <summary>One audit result. Status: pass | warn | fail | info | skipped | error. Severity: high | medium | low | info.</summary>
sealed record Finding(
    string Id, string Category, string Title, string Status, string Severity, string Summary,
    List<string> Evidence, string? Fix = null, string? FixUri = null, bool NeedsAdmin = false, long Ms = 0);

/// <summary>
/// Runs every check in <see cref="Checks"/> and scores the machine. Everything here only READS the system
/// (registry, WMI, files, sockets). XenoSec never changes a setting; findings say where the user can change it.
/// </summary>
static class Audit
{
    public static readonly bool IsAdmin =
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public static object Run(Action<int, int, string>? progress = null, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var all = Checks.All();
        var results = new List<Finding>();
        for (int i = 0; i < all.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (id, title, check) = all[i];
            progress?.Invoke(i, all.Count, title);
            var sw = Stopwatch.StartNew();
            try { results.AddRange(check().Select(f => f with { Ms = sw.ElapsedMilliseconds })); }
            catch (Exception ex)
            {
                results.Add(new Finding(id, "Audit", title, "error", "info", "This check couldn't run: " + ex.Message, new(), Ms: sw.ElapsedMilliseconds));
            }
        }
        progress?.Invoke(all.Count, all.Count, "done");

        var (score, grade) = Score(results);
        return new
        {
            t = started.ToUnixTimeMilliseconds(),
            ms = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            isAdmin = IsAdmin,
            os = Os(),
            score,
            grade,
            counts = new
            {
                pass = results.Count(r => r.Status == "pass"),
                warn = results.Count(r => r.Status == "warn"),
                fail = results.Count(r => r.Status == "fail"),
                info = results.Count(r => r.Status == "info"),
                skipped = results.Count(r => r.Status == "skipped"),
                error = results.Count(r => r.Status == "error"),
            },
            findings = results
                .OrderBy(r => StatusRank(r.Status)).ThenBy(r => SeverityRank(r.Severity)).ThenBy(r => r.Category)
                .ToList(),
        };
    }

    /// <summary>100 minus a penalty per problem, weighted by severity. Info, skipped and error don't count.</summary>
    static (int score, string grade) Score(List<Finding> rs)
    {
        int penalty = 0;
        foreach (var r in rs)
            penalty += (r.Status, r.Severity) switch
            {
                ("fail", "high") => 15, ("fail", "medium") => 8, ("fail", _) => 3,
                ("warn", "high") => 7, ("warn", "medium") => 4, ("warn", _) => 1,
                _ => 0,
            };
        int score = Math.Clamp(100 - penalty, 0, 100);
        string grade = score >= 90 ? "A" : score >= 80 ? "B" : score >= 65 ? "C" : score >= 50 ? "D" : "F";
        return (score, grade);
    }

    static int StatusRank(string s) => s switch { "fail" => 0, "warn" => 1, "error" => 2, "skipped" => 3, "info" => 4, _ => 5 };
    static int SeverityRank(string s) => s switch { "high" => 0, "medium" => 1, "low" => 2, _ => 3 };

    public static object Os()
    {
        const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        int build = int.TryParse(Reg.Str(Registry.LocalMachine, cv, "CurrentBuild"), out var b) ? b : Environment.OSVersion.Version.Build;
        var product = Reg.Str(Registry.LocalMachine, cv, "ProductName") ?? "Windows";
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");   // ProductName still says 10 on 11
        return new
        {
            product,
            edition = Reg.Str(Registry.LocalMachine, cv, "EditionID"),
            version = Reg.Str(Registry.LocalMachine, cv, "DisplayVersion"),
            build,
            ubr = Reg.Int(Registry.LocalMachine, cv, "UBR"),
        };
    }
}

/// <summary>Read-only registry helpers (64-bit view, so a Program Files key isn't redirected).</summary>
static class Reg
{
    public static object? Value(RegistryKey hive, string path, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == Registry.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = root.OpenSubKey(path);
            return k?.GetValue(name);
        }
        catch { return null; }
    }

    public static int? Int(RegistryKey hive, string path, string name) => Value(hive, path, name) switch
    {
        int i => i, long l => (int)l, string s when int.TryParse(s, out var p) => p, _ => null,
    };

    public static string? Str(RegistryKey hive, string path, string name) => Value(hive, path, name)?.ToString();

    public static bool Exists(RegistryKey hive, string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == Registry.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = root.OpenSubKey(path);
            return k != null;
        }
        catch { return false; }
    }

    public static IEnumerable<(string name, string value)> Values(RegistryKey hive, string path, RegistryView view = RegistryView.Registry64)
    {
        var list = new List<(string, string)>();
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == Registry.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view);
            using var k = root.OpenSubKey(path);
            if (k != null) foreach (var n in k.GetValueNames()) list.Add((n, k.GetValue(n)?.ToString() ?? ""));
        }
        catch { }
        return list;
    }
}

/// <summary>Read-only WMI helper. Returns an empty list when the namespace or class isn't there (or needs admin).</summary>
static class Wmi
{
    public static List<ManagementBaseObject> Query(string scope, string query, int timeoutSec = 20)
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            var opts = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(timeoutSec), ReturnImmediately = true, Rewindable = false };
            using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), opts);
            foreach (var o in s.Get()) list.Add(o);
        }
        // missing namespace/class, no rights, or a provider that's present but inactive (Defender in passive mode under
        // another antivirus answers "Generic failure"): all mean "no data", not a broken check
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass
                                              or ManagementStatus.AccessDenied or ManagementStatus.Failed or ManagementStatus.NotSupported) { }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    public static T? Get<T>(ManagementBaseObject o, string prop)
    {
        try { var v = o[prop]; return v is T t ? t : v == null ? default : (T)Convert.ChangeType(v, typeof(T)); }
        catch { return default; }
    }
}
