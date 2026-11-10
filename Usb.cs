using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XenoSuite
{
    /// <summary>Removable-media operations: safe eject, write-protect policy, capacity (fake-size) test.</summary>
    static class Usb
    {
        // ---- safe eject via IOCTL ----
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandleNET CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandleNET h, uint code, IntPtr inBuf, uint inSz, IntPtr outBuf, uint outSz, out uint ret, IntPtr ov);

        const uint FSCTL_LOCK_VOLUME = 0x00090018;
        const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
        const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
        const uint IOCTL_STORAGE_MEDIA_REMOVAL = 0x002D4804;

        public static object Eject(int disk)
        {
            var info = Storage.Disk(disk);
            if (Wmi.Bool(info, "IsSystem") || Wmi.Bool(info, "IsBoot"))
                throw new InvalidOperationException("Cannot eject a system/boot disk.");
            // Find a volume on this disk to open; eject operates on the device.
            string path = $"\\\\.\\PhysicalDrive{disk}";
            using var h = CreateFile(path, 0xC0000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) throw new InvalidOperationException("Cannot open " + path);
            if (!DeviceIoControl(h, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return new { ok = false, note = "Could not confirm a safe volume lock. Nothing was ejected. Use Windows Safely Remove Hardware instead." };
            if (!DeviceIoControl(h, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return new { ok = false, note = "Could not confirm dismount. Nothing was ejected. Use Windows Safely Remove Hardware instead." };
            bool ok = DeviceIoControl(h, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!ok) return new { ok = false, note = "The eject request failed. Safe removal is not confirmed; use Windows Safely Remove Hardware." };
            return new { ok = true };
        }

        // ---- global USB write-protect policy (StorageDevicePolicies) ----
        const string PolKey = @"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies";
        public static object GetWriteProtect()
        {
            using var k = Registry.LocalMachine.OpenSubKey(PolKey);
            int v = k?.GetValue("WriteProtect") is int i ? i : 0;
            return new { on = v == 1 };
        }
        public static object SetWriteProtect(bool on)
        {
            using var k = Registry.LocalMachine.CreateSubKey(PolKey);
            k.SetValue("WriteProtect", on ? 1 : 0, RegistryValueKind.DWord);
            return new { ok = true, note = "Applies to removable storage; may need reconnect/replug to take effect." };
        }

        // ---- capacity / fake-flash test (writes verifiable patterns, reads them back) ----
        public static object CapacityTest(string letter, long maxMB, Job j)
        {
            letter = Storage.DriveLetter(letter);
            string root = letter.TrimEnd(':') + ":\\";
            var di = new System.IO.DriveInfo(root);
            long freeMB = di.AvailableFreeSpace / 1024 / 1024;
            long testMB = maxMB > 0 ? Math.Min(maxMB, freeMB - 64) : freeMB - 64;
            if (testMB < 16) throw new InvalidOperationException("Not enough free space to test.");
            string dir = System.IO.Path.Combine(root, ".xenodisk_captest-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            const int blockMB = 32;
            var block = new byte[blockMB * 1024 * 1024];
            long written = 0; int firstBad = -1;
            try
            {
                int blocks = checked((int)((testMB + blockMB - 1) / blockMB));
                for (int i = 0; i < blocks; i++)
                {
                    j.Check();
                    FillPattern(block, i);
                    string f = System.IO.Path.Combine(dir, $"blk_{i:D5}.bin");
                    int count = (int)Math.Min(blockMB, testMB - (long)i * blockMB) * 1024 * 1024;
                    using (var fs = new System.IO.FileStream(f, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write,
                        System.IO.FileShare.None, 1024 * 1024, System.IO.FileOptions.WriteThrough))
                    { fs.Write(block, 0, count); fs.Flush(true); }
                    written += count / 1024 / 1024;
                    j.Progress(i * 50.0 / blocks, $"Writing {written} / {testMB} MB");
                }
                // verify
                var rb = new byte[block.Length];
                for (int i = 0; i < blocks; i++)
                {
                    j.Check();
                    string f = System.IO.Path.Combine(dir, $"blk_{i:D5}.bin");
                    int count = (int)Math.Min(blockMB, testMB - (long)i * blockMB) * 1024 * 1024;
                    int off = 0;
                    using (var fs = System.IO.File.OpenRead(f))
                    {
                        while (off < count) { int r = fs.Read(rb, off, count - off); if (r == 0) break; off += r; }
                    }
                    FillPattern(block, i);
                    if (off != count || !block.AsSpan(0, count).SequenceEqual(rb.AsSpan(0, count))) { firstBad = i; break; }
                    j.Progress(50 + i * 50.0 / blocks, $"Verifying {i * blockMB} / {testMB} MB");
                }
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
            j.Progress(100, "Done");
            bool fake = firstBad >= 0;
            return new
            {
                ok = !fake,
                verifiedMB = fake ? (long)firstBad * blockMB : written,
                claimedFreeMB = freeMB,
                verdict = fake ? $"Data mismatch or short read in test block {firstBad}; this does not determine the device's physical capacity." : $"No mismatch in {written} MB tested; untested space and total physical capacity are not certified.",
            };
        }

        static void FillPattern(byte[] b, int seed)
        {
            var rnd = new Random(0x5EED0000 ^ seed);
            rnd.NextBytes(b);
            // sprinkle deterministic markers so wrap-around/aliasing shows up
            for (int i = 0; i < b.Length; i += 4096)
            {
                b[i] = (byte)(seed & 0xFF); b[i + 1] = (byte)((seed >> 8) & 0xFF);
                b[i + 2] = (byte)(i & 0xFF); b[i + 3] = (byte)rnd.Next(256);
            }
        }
        static bool Eq(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i += 4096) { if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) return false; }
            return true;
        }
    }
}
