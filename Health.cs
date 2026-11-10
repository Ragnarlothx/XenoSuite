using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace XenoSuite
{
    /// <summary>SMART / reliability health, plus a read-only surface scan.</summary>
    static class Health
    {
        public static object Report()
        {
            var list = new List<object>();
            var pdisks = Wmi.Query(Wmi.StorageNs, "SELECT * FROM MSFT_PhysicalDisk").ToList();

            // Win32 predictive-failure (SMART) status
            var smartFail = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var diskDevices = Wmi.Query(Wmi.Cimv2, "SELECT Index, SerialNumber, PNPDeviceID FROM Win32_DiskDrive").ToList();
            try
            {
                foreach (var s in Wmi.Query(@"root\wmi", "SELECT * FROM MSStorageDriver_FailurePredictStatus"))
                {
                    var inst = Wmi.Str(s, "InstanceName") ?? "";
                    smartFail[inst] = Wmi.Bool(s, "PredictFailure");
                }
            }
            catch { }

            foreach (var pd in pdisks.OrderBy(x => Wmi.Str(x, "DeviceId")))
            {
                // PhysicalDisk.DeviceId and digits in a SMART instance name are
                // not guaranteed to be a Win32 physical-drive index. Match a unique serial.
                string serial = Wmi.Str(pd, "SerialNumber")?.Trim();
                var matches = string.IsNullOrEmpty(serial) ? new List<ManagementObject>() : diskDevices.Where(x =>
                    string.Equals(Wmi.Str(x, "SerialNumber")?.Trim(), serial, StringComparison.OrdinalIgnoreCase)).ToList();
                var device = matches.Count == 1 && pdisks.Count(x => string.Equals(Wmi.Str(x, "SerialNumber")?.Trim(), serial, StringComparison.OrdinalIgnoreCase)) == 1 ? matches[0] : null;
                int? id = device != null && int.TryParse(Wmi.Str(device, "Index"), out int n) ? n : null;
                string pnp = device == null ? null : Wmi.Str(device, "PNPDeviceID");
                var predictions = string.IsNullOrEmpty(pnp) ? new List<bool>() : smartFail.Where(x =>
                    x.Key.Equals(pnp, StringComparison.OrdinalIgnoreCase) || x.Key.StartsWith(pnp + "_", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).ToList();
                bool? prediction = predictions.Count == 0 ? null : predictions.Any(x => x);
                object rel = null;
                try
                {
                    var oid = Wmi.Str(pd, "ObjectId")?.Replace("\\", "\\\\").Replace("'", "\\'");
                    var rl = Wmi.One(Wmi.StorageNs, $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId='{oid}'}} WHERE ResultClass=MSFT_StorageReliabilityCounter");
                    if (rl != null) rel = new
                    {
                        temp = Wmi.Ui(rl, "Temperature"),
                        tempMax = Wmi.Ui(rl, "TemperatureMax"),
                        wear = Wmi.Ui(rl, "Wear"),
                        powerOnHours = Wmi.U(rl, "PowerOnHours"),
                        readErrors = Wmi.U(rl, "ReadErrorsUncorrected"),
                        writeErrors = Wmi.U(rl, "WriteErrorsUncorrected"),
                    };
                }
                catch { }
                list.Add(new
                {
                    disk = id,
                    model = Wmi.Str(pd, "FriendlyName"),
                    health = Wmi.Ui(pd, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                    usage = Wmi.Ui(pd, "Usage"),
                    smartPredictFail = prediction,
                    reliability = rel,
                });
            }
            return new { list };
        }

        // ---- read-only surface scan: read every sector, report unreadable regions ----
        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE = 0x3;
        const uint OPEN_EXISTING = 3;
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandleNET CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandleNET h, byte[] buf, int n, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFilePointerEx(SafeFileHandleNET h, long dist, out long np, uint method);

        public static object SurfaceScan(int disk, Job j)
        {
            ulong size = Wmi.U(Storage.Disk(disk), "Size");
            if (size == 0 || size > long.MaxValue) throw new InvalidOperationException("Invalid disk size.");
            using var h = CreateFile($"\\\\.\\PhysicalDrive{disk}", GENERIC_READ, FILE_SHARE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) throw new InvalidOperationException("Cannot open PhysicalDrive" + disk + " (Win32 " + Marshal.GetLastWin32Error() + ")");
            const int chunk = 8 * 1024 * 1024;
            var buf = new byte[chunk];
            ulong pos = 0; int badChunks = 0; var badRegions = new List<object>();
            while (pos < size)
            {
                j.Check();
                int want = (int)Math.Min((ulong)chunk, size - pos);
                if (!SetFilePointerEx(h, (long)pos, out long actual, 0) || actual != (long)pos)
                    throw new InvalidOperationException("Cannot seek disk at " + pos);
                bool readOk = ReadFile(h, buf, want, out int read, IntPtr.Zero);
                if (!readOk || read != want)
                {
                    int err = readOk ? 38 : Marshal.GetLastWin32Error(); // ERROR_HANDLE_EOF on a short read
                    badChunks++; badRegions.Add(new { offset = pos, error = err });
                    if (badRegions.Count > 500) break;
                    pos += (ulong)want; continue;
                }
                pos += (ulong)read;
                if ((pos & 0x3FFFFFFF) < chunk) j.Progress(size == 0 ? 0 : pos * 100.0 / size, $"{pos / 1024 / 1024 / 1024} / {size / 1024 / 1024 / 1024} GB — {badChunks} bad");
                j.Progress(size == 0 ? 0 : pos * 100.0 / size, $"{pos / 1024 / 1024 / 1024} / {size / 1024 / 1024 / 1024} GB — {badChunks} bad regions");
            }
            bool complete = pos >= size;
            j.Progress(complete ? 100 : pos * 100.0 / size, complete ? "Done" : "Stopped after too many read errors; scan incomplete");
            return new { badRegions, bad = badChunks, scanned = pos, complete };
        }
    }

    // minimal SafeHandle so we don't pull extra usings
    class SafeFileHandleNET : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFileHandleNET() : base(true) { }
        protected override bool ReleaseHandle() { return CloseHandle(handle); }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    }
}
