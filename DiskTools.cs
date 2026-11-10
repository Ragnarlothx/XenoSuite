using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace XenoSuite
{
    /// <summary>chkdsk, raw disk image backup/restore, hashing, temp cleanup.</summary>
    static class DiskTools
    {
        public static object Chkdsk(string letter, string mode, Job j)
        {
            letter = Storage.DriveLetter(letter);
            string flags = mode switch { "fix" => "/F", "scan" => "/scan", "rebuild" => "/F /R", _ => "" };
            j.Progress(5, "Running chkdsk " + letter + " " + flags);
            var r = Proc.Run("chkdsk.exe", $"{letter.TrimEnd(':')}: {flags}", null, j);
            if (r.Code >= 2) throw new IOException("chkdsk did not complete successfully (exit " + r.Code + "). " + r.All);
            j.Progress(100, "Done");
            return new { log = r.All, code = r.Code, note = (mode == "fix" || mode == "rebuild") ? "In-use volumes may require a separately scheduled reboot-time check; this app does not answer interactive prompts." : null };
        }

        // ---- raw image backup/restore of a physical disk ----
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE = 0x3, OPEN_EXISTING = 3;
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandleNET CreateFile(string n, uint a, uint s, IntPtr sec, uint d, uint f, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandleNET h, byte[] b, int n, out int r, IntPtr o);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandleNET h, byte[] b, int n, out int w, IntPtr o);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFilePointerEx(SafeFileHandleNET h, long d, out long np, uint m);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandleNET h, uint c, IntPtr ib, uint ibs, IntPtr ob, uint obs, out uint ret, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FlushFileBuffers(SafeFileHandleNET h);

        static void SeekDisk(SafeFileHandleNET h, long position)
        {
            if (!SetFilePointerEx(h, position, out long actual, 0) || actual != position)
                throw new IOException("Disk seek failed at " + position + " (" + Marshal.GetLastWin32Error() + ")");
        }

        static void ReadDisk(SafeFileHandleNET h, byte[] buffer, int count, long position)
        {
            if (!ReadFile(h, buffer, count, out int read, IntPtr.Zero))
                throw new IOException("Disk read failed at " + position + " (" + Marshal.GetLastWin32Error() + ")");
            if (read != count) throw new EndOfStreamException($"Short disk read at {position}: expected {count}, received {read}.");
        }

        static void ReadImage(Stream source, byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = source.Read(buffer, got, count - got);
                if (n == 0) throw new EndOfStreamException("Image ended before the expected length.");
                got += n;
            }
        }

        public static object ImageBackup(int disk, string file, bool verify, int bufferMB, Job j)
        {
            // Reject the common self-imaging case before creating a large output.
            // Conservatively treat mount folders under a source drive letter as on it.
            string targetRoot = Path.GetPathRoot(Path.GetFullPath(file));
            foreach (var partition in Wmi.Query(Wmi.StorageNs, $"SELECT * FROM MSFT_Partition WHERE DiskNumber={disk}"))
            {
                var letter = Wmi.Val(partition, "DriveLetter");
                if (letter is char drive && drive != '\0' && drive != ' ' &&
                    string.Equals(targetRoot, drive + ":\\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Save the image on a different disk/drive letter or network share, not on the disk being imaged.");
            }
            ulong size = Wmi.U(Storage.Disk(disk), "Size");
            if (size == 0 || size > long.MaxValue) throw new InvalidOperationException("Invalid source disk size.");
            using var h = CreateFile($"\\\\.\\PhysicalDrive{disk}", GENERIC_READ, FILE_SHARE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) throw new InvalidOperationException("Open PhysicalDrive" + disk + " failed (" + Marshal.GetLastWin32Error() + ")");
            int chunk = Math.Clamp(bufferMB, 1, 64) * 1024 * 1024; var buf = new byte[chunk];
            // Never silently overwrite a previous backup. Read verification uses this
            // same handle; reopening while FileShare.None is held fails on Windows.
            using var outp = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, chunk);
            ulong pos = 0;
            while (pos < size)
            {
                j.Check();
                int want = (int)Math.Min((ulong)chunk, size - pos);
                SeekDisk(h, (long)pos);
                ReadDisk(h, buf, want, (long)pos);
                outp.Write(buf, 0, want); pos += (ulong)want;
                j.Progress(pos * (verify ? 50.0 : 100.0) / size, $"{pos / 1024 / 1024} / {size / 1024 / 1024} MB");
            }
            outp.Flush(true);
            if (verify)
            {
                outp.Position = 0;
                j.Progress(50, "Verifying…");
                var vr = new byte[chunk]; ulong vpos = 0;
                SeekDisk(h, 0);
                while (vpos < pos)
                {
                    j.Check();
                    int want = (int)Math.Min((ulong)chunk, pos - vpos);
                    ReadDisk(h, buf, want, (long)vpos);
                    ReadImage(outp, vr, want);
                    if (!buf.AsSpan(0, want).SequenceEqual(vr.AsSpan(0, want)))
                        throw new InvalidOperationException($"Verify mismatch in block at {vpos}.");
                    vpos += (ulong)want;
                    j.Progress(50 + vpos * 50.0 / pos, $"Verify {vpos / 1024 / 1024} / {pos / 1024 / 1024} MB");
                }
            }
            j.Progress(100, "Done");
            return new { bytes = pos, file, verified = verify, note = "A live raw-disk image is not a VSS-consistent backup. Verification compares bytes read during this operation." };
        }

        public static object ImageWrite(string file, int disk, bool verify, int bufferMB, Job j)
        {
            var d = Storage.Disk(disk);
            if (Wmi.Bool(d, "IsSystem") || Wmi.Bool(d, "IsBoot"))
                throw new InvalidOperationException("Refusing to write over the system/boot disk.");
            // FSCTL_LOCK_VOLUME is a volume control, not a physical-disk lock.
            // Require an explicitly offlined target instead of ignoring a failed lock.
            if (!Wmi.Bool(d, "IsOffline"))
                throw new InvalidOperationException("Take the target disk offline before writing an image. No data has been written.");
            if (Wmi.Bool(d, "IsReadOnly")) throw new InvalidOperationException("Target disk is read-only.");
            int chunk = Math.Clamp(bufferMB, 1, 64) * 1024 * 1024; var buf = new byte[chunk];
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, chunk);
            long imageLength = src.Length;
            ulong capacity = Wmi.U(d, "Size");
            uint sector = Wmi.Ui(d, "LogicalSectorSize");
            if (imageLength <= 0 || (ulong)imageLength > capacity)
                throw new InvalidOperationException("Image must be nonempty and no larger than the target disk.");
            if (sector == 0 || imageLength % sector != 0)
                throw new InvalidOperationException("Image length must be a multiple of the target logical sector size.");
            using var h = CreateFile($"\\\\.\\PhysicalDrive{disk}", GENERIC_READ | GENERIC_WRITE, FILE_SHARE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) throw new InvalidOperationException("Open PhysicalDrive" + disk + " failed (" + Marshal.GetLastWin32Error() + ")");
            long pos = 0;
            SeekDisk(h, 0);
            while (pos < imageLength)
            {
                j.Check();
                int r = (int)Math.Min(chunk, imageLength - pos);
                ReadImage(src, buf, r);
                if (!WriteFile(h, buf, r, out int w, IntPtr.Zero) || w != r) throw new InvalidOperationException("Write failed at " + pos + " (" + Marshal.GetLastWin32Error() + ")");
                pos += r;
                j.Progress(pos * (verify ? 50.0 : 100.0) / imageLength, $"{pos / 1024 / 1024} / {imageLength / 1024 / 1024} MB");
            }
            if (!FlushFileBuffers(h)) throw new IOException("Flushing the written disk failed (" + Marshal.GetLastWin32Error() + ")");
            if (verify)
            {
                src.Position = 0;
                SeekDisk(h, 0);
                var readback = new byte[chunk];
                long checkedBytes = 0;
                while (checkedBytes < pos)
                {
                    j.Check();
                    int want = (int)Math.Min(chunk, pos - checkedBytes);
                    ReadImage(src, buf, want);
                    ReadDisk(h, readback, want, checkedBytes);
                    if (!buf.AsSpan(0, want).SequenceEqual(readback.AsSpan(0, want)))
                        throw new IOException("Image verification mismatch in block at " + checkedBytes);
                    checkedBytes += want;
                    j.Progress(50 + checkedBytes * 50.0 / pos, $"Verify {checkedBytes / 1024 / 1024} / {pos / 1024 / 1024} MB");
                }
            }
            j.Progress(100, "Done");
            return new { bytes = pos, verified = verify, note = "Reconnect/replug the device and re-read partitions. Bytes beyond the image length are not erased." };
        }

        public static object HashFile(string file, Job j)
        {
            using var md5 = MD5.Create(); using var sha1 = SHA1.Create(); using var sha256 = SHA256.Create();
            var fi = new FileInfo(file); long total = fi.Length, done = 0;
            var buf = new byte[1024 * 1024];
            using (var fs = File.OpenRead(file))
            {
                int r;
                while ((r = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    j.Check();
                    md5.TransformBlock(buf, 0, r, null, 0);
                    sha1.TransformBlock(buf, 0, r, null, 0);
                    sha256.TransformBlock(buf, 0, r, null, 0);
                    done += r; j.Progress(total == 0 ? 100 : done * 100.0 / total, $"{done / 1024 / 1024}/{total / 1024 / 1024} MB");
                }
            }
            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return new
            {
                md5 = Convert.ToHexString(md5.Hash),
                sha1 = Convert.ToHexString(sha1.Hash),
                sha256 = Convert.ToHexString(sha256.Hash),
                size = total,
            };
        }

        public static object CleanTemp(bool dryRun, Job j)
        {
            string[] roots = {
                Path.GetTempPath(),
                Environment.ExpandEnvironmentVariables(@"%SystemRoot%\Temp"),
                Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Microsoft\Windows\INetCache"),
            };
            long bytes = 0; int files = 0, failed = 0;
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var f in SafeFiles(root))
                {
                    j.Check();
                    try
                    {
                        var fi = new FileInfo(f);
                        long length = fi.Length;
                        if (!dryRun) fi.Delete();
                        bytes += length; files++;
                        if ((files & 0xFF) == 0) j.Progress(-1, $"{files:n0} files, {bytes / 1024 / 1024} MB");
                    }
                    catch { failed++; }
                }
            }
            j.Progress(100, "Done");
            return new { files, bytes, failed, dryRun, note = dryRun ? "Dry run — nothing deleted. Re-run with Clean to remove." : $"Removed {files} files; {failed} files could not be removed." };
        }

        static System.Collections.Generic.IEnumerable<string> SafeFiles(string root)
        {
            var stack = new System.Collections.Generic.Stack<string>(); stack.Push(root);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                string[] subs = Array.Empty<string>(); try { subs = Directory.GetDirectories(d); } catch { }
                foreach (var s in subs)
                    try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) == 0) stack.Push(s); } catch { }
                string[] fs = Array.Empty<string>(); try { fs = Directory.GetFiles(d); } catch { }
                foreach (var f in fs) yield return f;
            }
        }
    }
}
