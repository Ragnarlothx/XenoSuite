using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XenoSuite
{
    /// <summary>
    /// Audit tab. The security audit engine (SecAudit\, from XenoSec) is built in: no separate download or install.
    /// It only READS the system (registry, WMI, services, files, sockets) and never changes a setting; each finding
    /// says where to change it, and "Open settings" links go only to Windows' own ms-settings: / windowsdefender: pages.
    /// The last report is kept in %APPDATA%\XenoSuite\audit\ (report.json + report.html).
    /// The old external VulnAudit.exe still works as an optional extra (one-click fixes) if it's found.
    /// </summary>
    static class Audit
    {
        static string Dir => Path.Combine(MainWindow.AppData, "audit");
        static string ReportJson => Path.Combine(Dir, "report.json");
        static string ReportHtml => Path.Combine(Dir, "report.html");
        static string LegacyPathFile => Path.Combine(MainWindow.AppData, "vulnaudit-path.txt");
        static readonly object scanLock = new();

        /// <summary>Optional legacy VulnAudit.exe, if the user has it.</summary>
        static string LegacyExe()
        {
            try { if (File.Exists(LegacyPathFile)) { var p = File.ReadAllText(LegacyPathFile).Trim(); if (File.Exists(p)) return p; } } catch { }
            return new[] { Path.Combine(AppContext.BaseDirectory, "VulnAudit.exe"), Path.Combine(AppContext.BaseDirectory, "audit", "VulnAudit.exe") }.FirstOrDefault(File.Exists);
        }

        static string When() => File.Exists(ReportJson) ? File.GetLastWriteTime(ReportJson).ToString("yyyy-MM-dd HH:mm") : null;

        public static object Dispatch(string m, JsonObject a)
        {
            switch (m)
            {
                case "auditStatus":
                    return new
                    {
                        found = true, builtIn = true, isAdmin = SecAudit.Audit.IsAdmin,
                        reportWhen = When(), hasReport = File.Exists(ReportJson), hasHtml = File.Exists(ReportHtml),
                        legacyExe = LegacyExe(),
                    };

                case "auditScan":
                    {
                        if (!System.Threading.Monitor.TryEnter(scanLock)) throw new InvalidOperationException("A scan is already running.");
                        try
                        {
                            var raw = SecAudit.Audit.Run((i, n, title) =>
                                MainWindow.Instance?.Push("auditProgress", new { i, n, title }, "audit"));
                            var report = Shape(JsonSerializer.SerializeToNode(raw, MainWindow.J)!.AsObject());
                            Directory.CreateDirectory(Dir);
                            File.WriteAllText(ReportJson, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                            File.WriteAllText(ReportHtml, Html(report), new UTF8Encoding(false));
                            return new { when = When(), report };
                        }
                        finally { System.Threading.Monitor.Exit(scanLock); }
                    }

                case "auditReport":
                    {
                        if (!File.Exists(ReportJson)) return new { when = (string)null, report = (JsonNode)null };
                        try { return new { when = When(), report = JsonNode.Parse(File.ReadAllText(ReportJson)) }; }
                        catch (Exception ex) { return new { error = ex.Message }; }
                    }

                case "auditOpenHtml":
                    if (!File.Exists(ReportHtml)) throw new InvalidOperationException("No report yet. Run a scan first.");
                    MainWindow.Shell(ReportHtml);
                    return new { ok = true };

                case "auditOpenFix":
                    {
                        // only Windows' own settings pages, never arbitrary commands or files
                        var uri = A.S(a, "uri") ?? "";
                        if (!uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Not a Windows settings link.");
                        MainWindow.Shell(uri);
                        return new { ok = true };
                    }

                // ---- optional legacy VulnAudit.exe (one-click fixes) ----
                case "auditRunGui":
                    {
                        var exe = LegacyExe() ?? throw new InvalidOperationException("VulnAudit.exe isn't set. It's optional: the built-in audit doesn't need it.");
                        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                        return new { ok = true };
                    }
                case "auditSetPath":
                    {
                        var p = A.S(a, "path");
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) { File.WriteAllText(LegacyPathFile, p); return new { ok = true, exe = p }; }
                        return new { ok = false };
                    }
            }
            throw new InvalidOperationException("Unknown audit method: " + m);
        }

        /// <summary>Engine result → the report the Audit tab draws: problems as findings, passed checks listed separately.</summary>
        static JsonObject Shape(JsonObject r)
        {
            var os = r["os"];
            string osText = os == null ? "" : $"{os["product"]} {os["version"]} (build {os["build"]}.{os["ubr"]})".Replace("  ", " ");
            var findings = new JsonArray();
            var passed = new JsonArray();
            var skipped = new JsonArray();
            foreach (var f in r["findings"]?.AsArray() ?? new JsonArray())
            {
                string status = (string)f["status"], sev = (string)f["severity"] ?? "info";
                var evidence = f["evidence"]?.AsArray().Select(e => (string)e).Where(e => !string.IsNullOrWhiteSpace(e)).ToList() ?? new List<string>();
                var item = new JsonObject
                {
                    ["id"] = (string)f["id"],
                    ["category"] = (string)f["category"],
                    ["title"] = (string)f["title"],
                    ["status"] = status,
                    ["severity"] = status is "warn" or "fail" ? sev : "info",
                    ["detail"] = (string)f["summary"],
                    ["evidence"] = new JsonArray(evidence.Select(e => (JsonNode)e).ToArray()),
                    ["fix"] = (string)f["fix"],
                    ["link"] = (string)f["fixUri"],
                    ["needsAdmin"] = (bool?)f["needsAdmin"] ?? false,
                };
                if (status == "pass") passed.Add(item);
                else if (status == "skipped") skipped.Add(item);
                else findings.Add(item);
            }
            return new JsonObject
            {
                ["engine"] = "XenoSuite built-in audit (XenoSec engine)",
                ["generated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                ["grade"] = (string)r["grade"],
                ["score"] = (int?)r["score"],
                ["os"] = osText,
                ["isAdmin"] = (bool?)r["isAdmin"] ?? false,
                ["ms"] = (long?)r["ms"] ?? 0,
                ["counts"] = r["counts"]?.DeepClone(),
                ["findings"] = findings,
                ["passed"] = passed,
                ["skipped"] = skipped,
            };
        }

        static string Html(JsonObject rep)
        {
            static string E(JsonNode n) => WebUtility.HtmlEncode(n?.ToString() ?? "");
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>XenoSuite security audit</title><style>")
              .Append("body{font:14px/1.5 'Segoe UI',sans-serif;background:#1e1f22;color:#dfe1e5;max-width:960px;margin:30px auto;padding:0 20px}")
              .Append("h1{font-weight:600}.g{display:inline-block;font:700 34px Consolas;padding:6px 16px;border-radius:10px;color:#fff;background:#4f9cf9;margin-right:12px}")
              .Append(".f{border:1px solid #393b40;border-left:4px solid #6f737a;border-radius:8px;padding:10px 14px;margin:10px 0;background:#2b2d30}")
              .Append(".high{border-left-color:#f14c4c}.medium{border-left-color:#cca700}.low{border-left-color:#4f9cf9}")
              .Append(".m{color:#9da0a8;font-size:12.5px}.fix{background:#1e1f22;border-radius:5px;padding:6px 10px;margin-top:6px}ul{margin:4px 0}h2{margin-top:28px;font-size:16px;color:#4f9cf9}")
              .Append("</style></head><body>");
            sb.Append($"<h1><span class=\"g\">{E(rep["grade"])}</span>Security score {E(rep["score"])} / 100</h1>");
            sb.Append($"<p class=\"m\">{E(rep["os"])} · {E(rep["generated"])} · {(((bool?)rep["isAdmin"] ?? false) ? "run as administrator" : "run without administrator: some checks were limited")}</p>");
            sb.Append("<h2>Findings</h2>");
            var fs = rep["findings"]!.AsArray();
            if (fs.Count == 0) sb.Append("<p>No problems found.</p>");
            foreach (var f in fs)
            {
                sb.Append($"<div class=\"f {E(f["severity"])}\"><b>{E(f["title"])}</b> <span class=\"m\">· {E(f["category"])} · {E(f["severity"])}</span><div>{E(f["detail"])}</div>");
                var ev = f["evidence"]?.AsArray();
                if (ev != null && ev.Count > 0) sb.Append("<ul class=\"m\">").Append(string.Concat(ev.Take(12).Select(e => $"<li>{E(e)}</li>"))).Append("</ul>");
                if (!string.IsNullOrEmpty((string)f["fix"])) sb.Append($"<div class=\"fix\"><b>Fix:</b> {E(f["fix"])}</div>");
                sb.Append("</div>");
            }
            sb.Append($"<h2>Passed ({rep["passed"]!.AsArray().Count})</h2><ul>");
            foreach (var p in rep["passed"]!.AsArray()) sb.Append($"<li>{E(p["title"])} <span class=\"m\">· {E(p["detail"])}</span></li>");
            sb.Append("</ul><p class=\"m\">XenoSuite only reads your settings during an audit; it never changes them.</p></body></html>");
            return sb.ToString();
        }
    }
}
