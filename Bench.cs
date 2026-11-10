using System;
using System.Diagnostics;
using System.IO;

namespace XenoSuite
{
    /// <summary>Simple sequential + random read/write throughput benchmark on a volume.</summary>
    static class Bench
    {
        public static object Run(string letter, int sizeMB, Job j)
        {
            letter = Storage.DriveLetter(letter);
            if (sizeMB < 1 || sizeMB > 65536) throw new ArgumentOutOfRangeException(nameof(sizeMB));
            string root = letter.TrimEnd(':') + ":\\";
            var di = new DriveInfo(root);
            if (di.AvailableFreeSpace < (long)(sizeMB + 128) * 1024 * 1024)
                throw new InvalidOperationException("Not enough free space for the benchmark.");
            string file = Path.Combine(root, ".xenodisk_bench-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool created = false;
            const int block = 1024 * 1024;
            var buf = new byte[block];
            new Random(1).NextBytes(buf);
            double seqW, seqR, randW, randR;
            try
            {
                // sequential write
                j.Progress(5, "Sequential write…");
                var sw = Stopwatch.StartNew();
                using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, block, FileOptions.WriteThrough))
                {
                    created = true;
                    for (int i = 0; i < sizeMB; i++) { j.Check(); fs.Write(buf, 0, block); if ((i & 63) == 0) j.Progress(5 + i * 20.0 / sizeMB, $"Seq write {i}/{sizeMB} MB"); }
                    fs.Flush(true);
                }
                sw.Stop(); seqW = sizeMB / sw.Elapsed.TotalSeconds;

                // Buffered read: SequentialScan is a cache hint, not cache bypass.
                j.Progress(28, "Sequential read…");
                sw.Restart();
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None, block, FileOptions.SequentialScan))
                { int r; long tot = 0; while ((r = fs.Read(buf, 0, block)) > 0) { tot += r; j.Check(); } }
                sw.Stop(); seqR = sizeMB / sw.Elapsed.TotalSeconds;

                // random 4K
                int ops = Math.Min(20000, sizeMB * 64);
                var small = new byte[4096];
                var rnd = new Random(7);
                long len = (long)sizeMB * 1024 * 1024;
                j.Progress(52, "Random 4K write…");
                sw.Restart();
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    for (int i = 0; i < ops; i++) { j.Check(); fs.Position = (rnd.NextInt64(len / 4096)) * 4096; fs.Write(small, 0, 4096); if ((i & 1023) == 0) j.Progress(52 + i * 20.0 / ops, $"Rand write {i}/{ops}"); }
                    fs.Flush(true);
                }
                sw.Stop(); randW = ops / sw.Elapsed.TotalSeconds;

                j.Progress(74, "Random 4K read…");
                sw.Restart();
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.RandomAccess))
                {
                    for (int i = 0; i < ops; i++) { j.Check(); fs.Position = (rnd.NextInt64(len / 4096)) * 4096; fs.Read(small, 0, 4096); if ((i & 1023) == 0) j.Progress(74 + i * 22.0 / ops, $"Rand read {i}/{ops}"); }
                }
                sw.Stop(); randR = ops / sw.Elapsed.TotalSeconds;
            }
            finally { if (created) { try { File.Delete(file); } catch { } } }
            j.Progress(100, "Done");
            return new
            {
                seqWriteMBps = Math.Round(seqW, 1),
                seqReadMBps = Math.Round(seqR, 1),
                randWriteIOPS = Math.Round(randW),
                randReadIOPS = Math.Round(randR),
                randWriteMBps = Math.Round(randW * 4096 / 1024 / 1024, 1),
                randReadMBps = Math.Round(randR * 4096 / 1024 / 1024, 1),
            };
        }
    }
}
