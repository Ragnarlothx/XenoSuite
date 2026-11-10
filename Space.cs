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
    /// <summary>Disk-space analyzer: builds a folder size tree, parallelized across cores.</summary>
    static class Space
    {
        class Node { public string path; public long size; public int files; public ConcurrentDictionary<string, Node> kids = new(); }
        static readonly ConcurrentDictionary<string, Node> cache = new();

        public static object Scan(JsonObject a, Job j)
        {
            string root = A.S(a, "root");
            var opts = ScanOpts.From(a);
            long scanned = 0;
            var node = BuildParallel(root, opts, j, ref scanned);
            cache[root] = node;
            return Shape(node, 1);
        }

        // Parallelize the top-level subtrees so all cores stay busy on a big drive.
        static Node BuildParallel(string dir, ScanOpts o, Job j, ref long scanned)
        {
            var n = new Node { path = dir };
            DirectoryInfo dinfo; try { dinfo = new DirectoryInfo(dir); } catch { return n; }
            try { foreach (var f in dinfo.GetFiles()) { if (!o.IncludeFile(f)) continue; n.size += f.Length; n.files++; } } catch { }
            DirectoryInfo[] subs = null;
            try { subs = dinfo.GetDirectories(); } catch { }
            if (subs == null) return n;
            long localScanned = scanned;
            var po = new ParallelOptions { MaxDegreeOfParallelism = o.Dop, CancellationToken = j.Token };
            Parallel.ForEach(subs.Where(d => !o.SkipDir(d)), po, d =>
            {
                var child = BuildSeq(d.FullName, o, j);
                n.kids[d.FullName] = child;
                lock (n) { n.size += child.size; n.files += child.files; }
                Interlocked.Add(ref localScanned, child.files);
                j.Progress(-1, $"{n.size / 1073741824.0:0.0} GB · {d.Name}");
            });
            scanned = localScanned;
            return n;
        }

        static Node BuildSeq(string dir, ScanOpts o, Job j)
        {
            j.Check();
            var n = new Node { path = dir };
            DirectoryInfo dinfo; try { dinfo = new DirectoryInfo(dir); } catch { return n; }
            try { foreach (var f in dinfo.GetFiles()) { if (!o.IncludeFile(f)) continue; n.size += f.Length; n.files++; } } catch { }
            DirectoryInfo[] subs = null; try { subs = dinfo.GetDirectories(); } catch { }
            if (subs != null)
                foreach (var di in subs)
                {
                    if (o.SkipDir(di)) continue;
                    var c = BuildSeq(di.FullName, o, j);
                    n.kids[di.FullName] = c; n.size += c.size; n.files += c.files;
                }
            return n;
        }

        static object Shape(Node n, int depth) => new
        {
            path = n.path,
            name = Path.GetFileName(n.path.TrimEnd('\\')) is { Length: > 0 } nm ? nm : n.path,
            size = n.size,
            files = n.files,
            children = depth <= 0 ? null : n.kids.Values.OrderByDescending(k => k.size).Take(500).Select(k => Shape(k, depth - 1)).ToList(),
        };

        public static object Children(string path)
        {
            var found = FindNode(path);
            if (found == null) return new { children = new List<object>() };
            return new { children = found.kids.Values.OrderByDescending(k => k.size).Take(1000).Select(k => Shape(k, 0)).ToList() };
        }

        public static object Types(string path)
        {
            var map = new ConcurrentDictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            void Walk(string d)
            {
                try { foreach (var f in new DirectoryInfo(d).GetFiles()) { var e = string.IsNullOrEmpty(f.Extension) ? "(none)" : f.Extension.ToLower(); map.AddOrUpdate(e, _ => new[] { f.Length, 1L }, (_, v) => { v[0] += f.Length; v[1]++; return v; }); } } catch { }
                try { foreach (var s in Directory.GetDirectories(d)) { try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; } Walk(s); } } catch { }
            }
            Walk(path);
            var types = map.OrderByDescending(x => x.Value[0]).Take(40).Select(x => new { ext = x.Key, size = x.Value[0], count = x.Value[1] }).ToList();
            return new { types };
        }

        static Node FindNode(string path) => cache.Values.Select(r => Descend(r, path)).FirstOrDefault(x => x != null);
        static Node Descend(Node n, string path)
        {
            if (string.Equals(n.path, path, StringComparison.OrdinalIgnoreCase)) return n;
            if (!path.StartsWith(n.path, StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var k in n.kids.Values) { var r = Descend(k, path); if (r != null) return r; }
            return null;
        }
    }
}
