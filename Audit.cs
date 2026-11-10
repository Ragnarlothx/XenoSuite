using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace XenoSuite
{
    /// <summary>Audit tab = the existing VulnAudit engine, launched/driven from the shared shell (kept as a separate engine).</summary>
    static class Audit
    {
        static string PathFile => Path.Combine(MainWindow.AppData, "vulnaudit-path.txt");

        static string Locate()
        {
            try { if (File.Exists(PathFile)) { var p = File.ReadAllText(PathFile).Trim(); if (File.Exists(p)) return p; } } catch { }
            var guesses = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "VulnAudit.exe"),
                Path.Combine(AppContext.BaseDirectory, "audit", "VulnAudit.exe"),
            };
            return guesses.FirstOrDefault(File.Exists);
        }

        static (string json, string html, string when) LatestReport(string exe)
        {
            string dir = exe != null ? Path.GetDirectoryName(exe) : null;
            string json = null, html = null; string when = null;
            foreach (var d in new[] { dir, AppContext.BaseDirectory }.Where(x => !string.IsNullOrEmpty(x)))
            {
                var j = Path.Combine(d, "VulnAudit-Report.json");
                if (File.Exists(j) && (json == null || File.GetLastWriteTime(j) > File.GetLastWriteTime(json))) { json = j; when = File.GetLastWriteTime(j).ToString("yyyy-MM-dd HH:mm"); }
                var h = Path.Combine(d, "VulnAudit-Report.html");
                if (File.Exists(h)) html = h;
            }
            return (json, html, when);
        }

        public static object Dispatch(string m, JsonObject a)
        {
            switch (m)
            {
                case "auditStatus":
                    {
                        var exe = Locate();
                        var (json, html, when) = LatestReport(exe);
                        return new { found = exe != null, exe, reportWhen = when, hasReport = json != null, hasHtml = html != null };
                    }
                case "auditSetPath":
                    {
                        var p = A.S(a, "path");
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) { File.WriteAllText(PathFile, p); return new { ok = true, exe = p }; }
                        return new { ok = false };
                    }
                case "auditRunGui":
                    {
                        var exe = Locate() ?? throw new InvalidOperationException("VulnAudit.exe not found — set its path.");
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                        return new { ok = true };
                    }
                case "auditOpenHtml":
                    {
                        var exe = Locate(); var (_, html, _) = LatestReport(exe);
                        if (html == null) throw new InvalidOperationException("No HTML report yet — run a scan first.");
                        Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
                        return new { ok = true };
                    }
                case "auditReport":
                    {
                        var exe = Locate(); var (json, _, when) = LatestReport(exe);
                        if (json == null) return new { findings = Array.Empty<object>(), when = (string)null };
                        try { var node = JsonNode.Parse(File.ReadAllText(json)); return new { when, report = node }; }
                        catch (Exception ex) { return new { error = ex.Message }; }
                    }
                case "auditScan":
                    {
                        var exe = Locate() ?? throw new InvalidOperationException("VulnAudit.exe not found — set its path.");
                        var dir = Path.GetDirectoryName(exe);
                        var psi = new ProcessStartInfo(exe, "cli") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
                        using var p = Process.Start(psi);
                        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                        p.WaitForExit(1000 * 60 * 10);
                        var (json, _, when) = LatestReport(exe);
                        if (json == null) throw new InvalidOperationException("Scan finished but no report was produced.");
                        try { var node = JsonNode.Parse(File.ReadAllText(json)); return new { when, report = node }; }
                        catch (Exception ex) { return new { error = ex.Message }; }
                    }
            }
            throw new InvalidOperationException("Unknown audit method: " + m);
        }
    }
}
