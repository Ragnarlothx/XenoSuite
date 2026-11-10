using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace XenoSuite
{
    /// <summary>Runs console tools (diskpart, chkdsk, defrag, vssadmin, powershell…) and streams output to a Job.</summary>
    static class Proc
    {
        public class Result { public int Code; public string Out = ""; public string Err = ""; public string All => (Out + "\n" + Err).Trim(); }

        /// <summary>Run a process, optionally piping stdin, streaming every line to job.Line.</summary>
        public static Result Run(string exe, string args, string stdin = null, Job job = null, int timeoutMs = 0, string cwd = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = cwd ?? Environment.SystemDirectory,
            };
            var so = new StringBuilder(); var se = new StringBuilder();
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) { so.AppendLine(e.Data); job?.Line(e.Data); } };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) { se.AppendLine(e.Data); job?.Line(e.Data); } };
            p.Start();
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (stdin != null) p.StandardInput.Write(stdin);
            p.StandardInput.Close(); // interactive tools get EOF instead of an invisible prompt

            if (job != null)
            {
                // cooperative cancellation
                while (!p.WaitForExit(200))
                {
                    if (job.Token.IsCancellationRequested) { try { p.Kill(true); } catch { } throw new OperationCanceledException(); }
                    if (timeoutMs > 0 && (DateTime.Now - p.StartTime).TotalMilliseconds > timeoutMs) { try { p.Kill(true); } catch { } throw new TimeoutException(); }
                }
            }
            else if (!p.WaitForExit(timeoutMs > 0 ? timeoutMs : 1000 * 60 * 60))
            {
                try { p.Kill(true); } catch { }
                throw new TimeoutException($"{exe} timed out");
            }
            p.WaitForExit();
            return new Result { Code = p.ExitCode, Out = so.ToString(), Err = se.ToString() };
        }

        /// <summary>Feed a diskpart script via stdin and return its full transcript.</summary>
        public static Result DiskPart(string script, Job job = null)
        {
            var normalized = script.Replace("\r\n", "\n").Replace("\n", "\r\n");
            if (!normalized.TrimEnd().EndsWith("exit", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.TrimEnd() + "\r\nexit\r\n";
            return Run("diskpart.exe", "", normalized, job);
        }

        public static Result Ps(string script, Job job = null, int timeoutMs = 0)
            => Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
                "$ErrorActionPreference = 'Stop'\r\ntry {\r\n" + script +
                "\r\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\r\n", job, timeoutMs);
    }
}
