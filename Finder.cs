using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XenoSuite
{
    /// <summary>Find old/large/by-pattern files, find duplicates, and move/copy/recycle them.
    /// Scans run multi-threaded across all cores via Scan.Walk.</summary>
    static class Finder
    {
        public static object Find(JsonObject a, Job j)
        {
            string root = A.S(a, "root");
            string pattern = A.S(a, "pattern") ?? "*";
            long minKB = A.L(a, "minKB", 0), maxKB = A.L(a, "maxKB", 0);
            int olderDays = A.I(a, "olderDays", 0), newerDays = A.I(a, "newerDays", 0);
            string timeField = A.S(a, "timeField") ?? "modified";
            string extsCsv = A.S(a, "exts");
            var exts = string.IsNullOrWhiteSpace(extsCsv) ? null :
                new HashSet<string>(extsCsv.Split(',', ';', ' ').Where(x => x.Length > 0).Select(x => "." + x.TrimStart('.').ToLower()));
            int limit = A.I(a, "limit", 5000);
            string sort = A.S(a, "sort") ?? "size";
            var opts = ScanOpts.From(a);
            var now = DateTime.Now;
            var patterns = (pattern ?? "*").Split(';', ',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (patterns.Length == 0) patterns = new[] { "*" };
            var regexes = patterns[0] == "*" ? null : patterns.Select(Wild).ToArray();

            var results = new ConcurrentBag<FileRow>();
            long scanned = Scan.Walk(root, opts, fi =>
            {
                if (exts != null && !exts.Contains(fi.Extension.ToLower())) return;
                if (regexes != null && !regexes.Any(r => r.IsMatch(fi.Name))) return;
                long kb = fi.Length / 1024;
                if (minKB > 0 && kb < minKB) return;
                if (maxKB > 0 && kb > maxKB) return;
                DateTime t;
                try { t = timeField == "accessed" ? fi.LastAccessTime : timeField == "created" ? fi.CreationTime : fi.LastWriteTime; } catch { return; }
                if (olderDays > 0 && (now - t).TotalDays < olderDays) return;
                if (newerDays > 0 && (now - t).TotalDays > newerDays) return;
                results.Add(new FileRow { path = fi.FullName, name = fi.Name, size = fi.Length, modified = fi.LastWriteTime, accessed = SafeTime(() => fi.LastAccessTime), created = fi.CreationTime, ext = fi.Extension.ToLower() });
            }, j, c => j.Progress(-1, $"Scanned {c:n0} files, {results.Count:n0} matches"));

            var list = Trim(results.ToList(), sort, limit);
            return new { count = list.Count, scanned, files = list };
        }

        static List<FileRow> Trim(List<FileRow> r, string sort, int n)
        {
            IEnumerable<FileRow> q = sort switch
            {
                "oldest" => r.OrderBy(x => x.modified),
                "newest" => r.OrderByDescending(x => x.modified),
                "accessed" => r.OrderBy(x => x.accessed),
                "name" => r.OrderBy(x => x.name),
                _ => r.OrderByDescending(x => x.size),
            };
            return q.Take(n).ToList();
        }

        public static object Duplicates(JsonObject a, Job j)
        {
            string root = A.S(a, "root");
            long minKB = A.L(a, "minKB", 1);
            string algo = (A.S(a, "hashAlgo") ?? "sha256").ToLower();
            var opts = ScanOpts.From(a);

            // 1) group by size (parallel walk)
            var bySize = new ConcurrentDictionary<long, ConcurrentBag<string>>();
            long scanned = Scan.Walk(root, opts, fi =>
            {
                if (fi.Length < minKB * 1024) return;
                bySize.GetOrAdd(fi.Length, _ => new ConcurrentBag<string>()).Add(fi.FullName);
            }, j, c => j.Progress(-1, $"Indexing {c:n0} files…"));

            // 2) hash colliding sizes in parallel across all cores
            var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
            int total = candidates.Sum(c => c.Value.Count), done = 0;
            var groups = new ConcurrentBag<object>();
            long wasted = 0;
            var po = new ParallelOptions { MaxDegreeOfParallelism = opts.Dop, CancellationToken = j.Token };
            Parallel.ForEach(candidates, po, kv =>
            {
                var byHash = new Dictionary<string, List<string>>();
                foreach (var f in kv.Value)
                {
                    j.Check();
                    string hsh; try { hsh = Hash(f, algo); } catch { continue; }
                    (byHash.TryGetValue(hsh, out var l) ? l : byHash[hsh] = new()).Add(f);
                    int d = Interlocked.Increment(ref done);
                    if ((d & 0x3F) == 0) j.Progress(total == 0 ? 0 : d * 100.0 / total, $"Hashing {d}/{total}");
                }
                foreach (var g in byHash.Where(x => x.Value.Count > 1))
                {
                    Interlocked.Add(ref wasted, kv.Key * (g.Value.Count - 1));
                    groups.Add(new { size = kv.Key, hash = g.Key, files = g.Value });
                }
            });
            return new { groups = groups.OrderByDescending(x => (long)((dynamic)x).size).Take(2000).ToList(), wasted, scanned };
        }

        public static object ToRecycleBin(string[] paths)
        {
            int ok = 0; var errs = new List<string>();
            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    ok++;
                }
                catch (Exception ex) { errs.Add(Path.GetFileName(p) + ": " + ex.Message); }
            }
            return new { ok, errors = errs };
        }

        public static object MoveOrCopy(string[] paths, string dest, bool copy, Job j)
        {
            Directory.CreateDirectory(dest);
            int done = 0; var errs = new List<string>();
            foreach (var p in paths)
            {
                j.Check();
                j.Progress(done * 100.0 / Math.Max(1, paths.Length), (copy ? "Copying " : "Moving ") + Path.GetFileName(p));
                try
                {
                    string target = Unique(Path.Combine(dest, Path.GetFileName(p)));
                    if (copy) File.Copy(p, target); else File.Move(p, target);
                    done++;
                }
                catch (Exception ex) { errs.Add(Path.GetFileName(p) + ": " + ex.Message); }
            }
            return new { done, errors = errs };
        }

        public static void Reveal(string path) { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { } }

        // ---- helpers ----
        static string Hash(string path, string algo)
        {
            if (algo == "quick")
            {
                // size + first/last 64KB via xxHash (fast, good enough to flag likely dupes)
                var fi = new FileInfo(path);
                var x = new XxHash64();
                var buf = new byte[65536];
                using var fs = File.OpenRead(path);
                int r = fs.Read(buf, 0, buf.Length); x.Append(buf.AsSpan(0, r));
                if (fi.Length > 131072) { fs.Seek(-65536, SeekOrigin.End); r = fs.Read(buf, 0, buf.Length); x.Append(buf.AsSpan(0, r)); }
                return fi.Length + ":" + Convert.ToHexString(x.GetCurrentHash());
            }
            if (algo == "xxhash")
            {
                var x = new XxHash64(); var buf = new byte[1024 * 1024];
                using var fs = File.OpenRead(path); int r;
                while ((r = fs.Read(buf, 0, buf.Length)) > 0) x.Append(buf.AsSpan(0, r));
                return Convert.ToHexString(x.GetCurrentHash());
            }
            using var h = algo == "md5" ? (HashAlgorithm)MD5.Create() : SHA256.Create();
            using var f = File.OpenRead(path);
            return Convert.ToHexString(h.ComputeHash(f));
        }

        static System.Text.RegularExpressions.Regex Wild(string pat) =>
            new("^" + System.Text.RegularExpressions.Regex.Escape(pat).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        static string Unique(string target)
        {
            if (!File.Exists(target)) return target;
            string dir = Path.GetDirectoryName(target), bn = Path.GetFileNameWithoutExtension(target), ext = Path.GetExtension(target);
            for (int i = 1; ; i++) { var t = Path.Combine(dir, $"{bn} ({i}){ext}"); if (!File.Exists(t)) return t; }
        }
        static DateTime SafeTime(Func<DateTime> f) { try { return f(); } catch { return DateTime.MinValue; } }

        class FileRow
        {
            public string path { get; set; } public string name { get; set; } public long size { get; set; }
            public DateTime modified { get; set; } public DateTime accessed { get; set; } public DateTime created { get; set; } public string ext { get; set; }
        }
    }
}
