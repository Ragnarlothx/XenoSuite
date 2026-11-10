using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XenoSuite
{
    /// <summary>Shared scan options + a multi-threaded directory walker that uses all cores.</summary>
    class ScanOpts
    {
        public int Threads;                 // 0 = all logical cores
        public bool FollowReparse;          // follow junctions/symlinks (off by default to avoid loops)
        public bool IncludeHidden = true;
        public bool IncludeSystem = false;
        public HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase);

        public int Dop => Threads > 0 ? Threads : Environment.ProcessorCount;

        public static ScanOpts From(JsonObject a)
        {
            var o = new ScanOpts
            {
                Threads = A.I(a, "threads", 0),
                FollowReparse = A.B(a, "followReparse", false),
                IncludeHidden = A.B(a, "includeHidden", true),
                IncludeSystem = A.B(a, "includeSystem", false),
            };
            foreach (var s in A.SA(a, "skipDirs")) if (!string.IsNullOrWhiteSpace(s)) o.Skip.Add(s.Trim());
            return o;
        }

        // Overload that reuses the DirectoryInfo's already-fetched attributes (no extra stat syscall per dir).
        public bool SkipDir(DirectoryInfo di)
        {
            if (Skip.Contains(di.Name)) return true;
            if (hasPathSkips) { var full = di.FullName; foreach (var s in pathSkips) if (full.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return true; }
            try
            {
                var attr = di.Attributes;
                if (!FollowReparse && (attr & FileAttributes.ReparsePoint) != 0) return true;
                if (!IncludeHidden && (attr & FileAttributes.Hidden) != 0) return true;
                if (!IncludeSystem && (attr & FileAttributes.System) != 0) return true;
            }
            catch { return true; }
            return false;
        }

        string[] _pathSkips; bool _computed;
        string[] pathSkips { get { Compute(); return _pathSkips; } }
        bool hasPathSkips { get { Compute(); return _pathSkips.Length > 0; } }
        void Compute() { if (_computed) return; _computed = true; _pathSkips = Skip.Where(s => s.Contains('\\')).ToArray(); }

        public bool IncludeFile(FileInfo fi)
        {
            var a = fi.Attributes;
            if (!IncludeHidden && (a & FileAttributes.Hidden) != 0) return false;
            if (!IncludeSystem && (a & FileAttributes.System) != 0) return false;
            return true;
        }
    }

    static class Scan
    {
        /// <summary>Level-synchronous parallel BFS. onFile is called from many threads, so it must be thread-safe.</summary>
        public static long Walk(string root, ScanOpts o, Action<FileInfo> onFile, Job j, Action<long> progress = null)
        {
            long count = 0;
            var level = new List<string> { root };
            var po = new ParallelOptions { MaxDegreeOfParallelism = o.Dop, CancellationToken = j.Token };
            while (level.Count > 0)
            {
                j.Check();
                var next = new ConcurrentBag<string>();
                Parallel.ForEach(level, po, dir =>
                {
                    DirectoryInfo dinfo;
                    try { dinfo = new DirectoryInfo(dir); } catch { return; }
                    FileInfo[] files = null;
                    try { files = dinfo.GetFiles(); } catch { }
                    if (files != null)
                        foreach (var fi in files)
                        {
                            if (!o.IncludeFile(fi)) continue;
                            try { onFile(fi); } catch { }
                            long c = Interlocked.Increment(ref count);
                            if ((c & 0x7FF) == 0) { progress?.Invoke(c); j.Check(); }
                        }
                    DirectoryInfo[] subs = null;
                    try { subs = dinfo.GetDirectories(); } catch { }   // one enumeration, attributes already attached
                    if (subs != null)
                        foreach (var di in subs)
                        {
                            if (di.Name.Equals(".xenodisk_captest", StringComparison.OrdinalIgnoreCase)) continue;
                            if (di.Name.Equals(".xenodisk_bench.tmp", StringComparison.OrdinalIgnoreCase)) continue;
                            if (o.SkipDir(di)) continue;
                            next.Add(di.FullName);
                        }
                });
                level = next.ToList();
            }
            return count;
        }
    }
}
