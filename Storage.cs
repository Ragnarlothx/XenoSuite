using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

namespace XenoSuite
{
    /// <summary>Disk, partition and volume enumeration + management via the Storage WMI provider,
    /// with diskpart used for the operations WMI does not cover cleanly.</summary>
    static class Storage
    {
        // ---------------------------------------------------------------- LIST
        public static object ListDisks()
        {
            var vols = Wmi.Query(Wmi.StorageNs, "SELECT * FROM MSFT_Volume").ToList();
            var parts = Wmi.Query(Wmi.StorageNs, "SELECT * FROM MSFT_Partition").ToList();
            var pdisks = Wmi.Query(Wmi.StorageNs, "SELECT * FROM MSFT_PhysicalDisk").ToList();
            var disks = new List<object>();

            foreach (var d in Wmi.Query(Wmi.StorageNs, "SELECT * FROM MSFT_Disk").OrderBy(x => Wmi.Ui(x, "Number")))
            {
                uint num = Wmi.Ui(d, "Number");
                ulong size = Wmi.U(d, "Size");
                ulong alloc = Wmi.U(d, "AllocatedSize");
                var pd = pdisks.FirstOrDefault(p => Wmi.Str(p, "DeviceId") == num.ToString());
                var myParts = parts.Where(p => Wmi.Ui(p, "DiskNumber") == num).OrderBy(p => Wmi.U(p, "Offset")).ToList();

                var plist = new List<object>();
                foreach (var p in myParts)
                {
                    ulong off = Wmi.U(p, "Offset"), psize = Wmi.U(p, "Size");
                    string letter = null; var lv = Wmi.Val(p, "DriveLetter");
                    if (lv is char c && c != '\0' && c != ' ') letter = c + ":";
                    var vol = letter != null ? vols.FirstOrDefault(v => (Wmi.Str(v, "DriveLetter") ?? "").TrimEnd(':').ToUpper() == letter.TrimEnd(':').ToUpper()) : null;
                    plist.Add(new
                    {
                        number = Wmi.Ui(p, "PartitionNumber"),
                        offset = off,
                        size = psize,
                        letter,
                        type = Wmi.Str(p, "GptType") ?? Wmi.Str(p, "MbrType"),
                        typeName = PartTypeName(d, p),
                        isActive = Wmi.Bool(p, "IsActive"),
                        isBoot = Wmi.Bool(p, "IsBoot"),
                        isSystem = Wmi.Bool(p, "IsSystem"),
                        isHidden = Wmi.Bool(p, "IsHidden"),
                        isReadOnly = Wmi.Bool(p, "IsReadOnly"),
                        fs = vol != null ? Wmi.Str(vol, "FileSystem") : null,
                        label = vol != null ? Wmi.Str(vol, "FileSystemLabel") : null,
                        free = vol != null ? Wmi.U(vol, "SizeRemaining") : 0UL,
                        volHealth = vol != null ? HealthName(Wmi.Ui(vol, "HealthStatus")) : null,
                    });
                }

                // free regions
                var gaps = FreeRegions(num, size, myParts);

                disks.Add(new
                {
                    number = num,
                    size,
                    allocated = alloc,
                    model = (pd != null ? Wmi.Str(pd, "FriendlyName") : null) ?? Wmi.Str(d, "FriendlyName") ?? Wmi.Str(d, "Model"),
                    serial = (pd != null ? Wmi.Str(pd, "SerialNumber") : Wmi.Str(d, "SerialNumber"))?.Trim(),
                    bus = pd != null ? BusName(Wmi.Ui(pd, "BusType")) : BusName(Wmi.Ui(d, "BusType")),
                    media = pd != null ? MediaName(Wmi.Ui(pd, "MediaType")) : null,
                    style = PartStyle(Wmi.Ui(d, "PartitionStyle")),
                    isSystem = Wmi.Bool(d, "IsSystem"),
                    isBoot = Wmi.Bool(d, "IsBoot"),
                    offline = Wmi.Bool(d, "IsOffline"),
                    readOnly = Wmi.Bool(d, "IsReadOnly"),
                    removable = pd != null && (Wmi.Ui(pd, "MediaType") == 0 || BusName(Wmi.Ui(pd, "BusType")) == "USB"),
                    usb = (pd != null ? BusName(Wmi.Ui(pd, "BusType")) : BusName(Wmi.Ui(d, "BusType"))) == "USB",
                    health = pd != null ? HealthName(Wmi.Ui(pd, "HealthStatus")) : "Unknown",
                    spindle = pd != null ? Wmi.Ui(pd, "SpindleSpeed") : 0,
                    partitions = plist,
                    free = gaps,
                });
            }
            return new { admin = MainWindow.IsAdmin(), disks };
        }

        static List<object> FreeRegions(uint disk, ulong diskSize, List<ManagementObject> parts)
        {
            var gaps = new List<object>();
            try
            {
                // Compute free regions from partition offsets/sizes.
                ulong cursor = 1024UL * 1024UL; // 1MB alignment start
                foreach (var p in parts.OrderBy(p => Wmi.U(p, "Offset")))
                {
                    ulong off = Wmi.U(p, "Offset"), size = Wmi.U(p, "Size");
                    if (off > cursor + 16UL * 1024 * 1024) gaps.Add(new { offset = cursor, size = off - cursor });
                    cursor = Math.Max(cursor, off + size);
                }
                ulong tail = diskSize > 1024UL * 1024 ? diskSize - 1024UL * 1024 : diskSize; // reserve ~1MB secondary GPT
                if (tail > cursor + 16UL * 1024 * 1024) gaps.Add(new { offset = cursor, size = tail - cursor });
            }
            catch { }
            return gaps;
        }

        // ---------------------------------------------------------------- DETAIL
        public static object DiskDetail(int disk)
        {
            var d = Disk(disk);
            var pd = Wmi.One(Wmi.StorageNs, $"SELECT * FROM MSFT_PhysicalDisk WHERE DeviceId='{disk}'");
            object rel = null;
            if (pd != null)
            {
                var rl = Wmi.One(Wmi.StorageNs, $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId='{Wmi.Str(pd, "ObjectId").Replace("'", "\\'")}'}} WHERE ResultClass=MSFT_StorageReliabilityCounter");
                if (rl != null) rel = new
                {
                    temp = Wmi.Ui(rl, "Temperature"),
                    tempMax = Wmi.Ui(rl, "TemperatureMax"),
                    wear = Wmi.Ui(rl, "Wear"),
                    powerOnHours = Wmi.U(rl, "PowerOnHours"),
                    readErrors = Wmi.U(rl, "ReadErrorsTotal"),
                    writeErrors = Wmi.U(rl, "WriteErrorsTotal"),
                    startStop = Wmi.U(rl, "StartStopCycleCount"),
                };
            }
            return new
            {
                number = disk,
                path = Wmi.Str(d, "Path"),
                location = Wmi.Str(d, "Location"),
                guid = Wmi.Str(d, "Guid"),
                firmware = pd != null ? Wmi.Str(pd, "FirmwareVersion") : Wmi.Str(d, "FirmwareVersion"),
                logicalSector = Wmi.Ui(d, "LogicalSectorSize"),
                physicalSector = Wmi.Ui(d, "PhysicalSectorSize"),
                reliability = rel,
            };
        }

        // ---------------------------------------------------------------- CREATE / DELETE / RESIZE
        public static object SupportedSize(int disk, int part)
        {
            var p = Partition(disk, part);
            var outp = p.InvokeMethod("GetSupportedSize", null, null);
            return new { min = Wmi.U(outp, "SizeMin"), max = Wmi.U(outp, "SizeMax") };
        }

        public static object CreatePartition(int disk, long sizeMB, string fs, string label, string letter, bool quick)
        {
            if (sizeMB < 0 || sizeMB > long.MaxValue / (1024 * 1024))
                throw new ArgumentOutOfRangeException(nameof(sizeMB));
            var d = Disk(disk);
            // If disk uninitialized, require explicit init first (don't silently wipe).
            if (PartStyle(Wmi.Ui(d, "PartitionStyle")) == "RAW")
                throw new InvalidOperationException("Disk is not initialized. Initialize it (GPT or MBR) first.");

            uint newPart = 0; char newLetter = '\0';
            Wmi.Invoke(d, "CreatePartition", inp =>
            {
                if (sizeMB > 0) inp["Size"] = (ulong)sizeMB * 1024 * 1024;
                else inp["UseMaximumSize"] = true;
                if (!string.IsNullOrEmpty(letter) && letter != "auto") inp["DriveLetter"] = letter[0];
                else inp["AssignDriveLetter"] = true;
                inp["Alignment"] = 1048576U;
            });
            // Re-read to find the created partition (highest offset newest). Simpler: enumerate, pick one without FS.
            var created = Wmi.Query(Wmi.StorageNs, $"SELECT * FROM MSFT_Partition WHERE DiskNumber={disk}")
                .OrderByDescending(x => Wmi.U(x, "Offset")).FirstOrDefault();
            int pn = created != null ? (int)Wmi.Ui(created, "PartitionNumber") : 0;

            if (!string.IsNullOrEmpty(fs) && fs != "none" && pn > 0)
                FormatPartition(disk, pn, fs, label, quick, 0);
            return new { ok = true, partition = pn };
        }

        public static object DeletePartition(int disk, int part)
        {
            var p = Partition(disk, part);
            if (Wmi.Bool(p, "IsSystem") || Wmi.Bool(p, "IsBoot"))
                throw new InvalidOperationException("Refusing to delete a system/boot partition.");
            Wmi.Invoke(p, "DeleteObject");
            return new { ok = true };
        }

        public static object ResizePartition(int disk, int part, long sizeMB)
        {
            if (sizeMB <= 0 || sizeMB > long.MaxValue / (1024 * 1024))
                throw new ArgumentOutOfRangeException(nameof(sizeMB), "Partition size must be positive and fit in bytes.");
            var p = Partition(disk, part);
            Wmi.Invoke(p, "Resize", inp => inp["Size"] = (ulong)sizeMB * 1024 * 1024);
            return new { ok = true };
        }

        // ---------------------------------------------------------------- FORMAT
        public static object FormatPartition(int disk, int part, string fs, string label, bool quick, int cluster)
        {
            var p = Partition(disk, part);
            char dl = ' '; var lv = Wmi.Val(p, "DriveLetter"); if (lv is char c && c != '\0') dl = c;
            var vol = FindVolumeForPartition(p);
            if (Wmi.Bool(p, "IsSystem") || Wmi.Bool(p, "IsBoot"))
                throw new InvalidOperationException("Refusing to format the system/boot partition.");
            if (vol != null)
            {
                Wmi.Invoke(vol, "Format", inp =>
                {
                    inp["FileSystem"] = NormFs(fs);
                    inp["FileSystemLabel"] = label ?? "";
                    inp["Full"] = !quick;
                    if (cluster > 0) inp["AllocationUnitSize"] = (uint)cluster;
                    inp["Force"] = true;
                });
                return new { ok = true };
            }
            // No volume object yet (freshly created) — format by drive letter via Format-Volume
            if (dl != ' ')
            {
                var r = Proc.Ps($"Format-Volume -DriveLetter {dl} -FileSystem {NormFs(fs)} -NewFileSystemLabel '{(label ?? "").Replace("'", "''")}' {(quick ? "" : "-Full")} {(cluster > 0 ? "-AllocationUnitSize " + cluster : "")} -Force -Confirm:$false");
                if (r.Code != 0) throw new InvalidOperationException("Format failed: " + r.All);
                return new { ok = true };
            }
            throw new InvalidOperationException("No volume or drive letter found to format.");
        }

        // ---------------------------------------------------------------- LABEL / LETTER / ATTRS
        public static object SetLabel(int disk, int part, string label)
        {
            var vol = FindVolumeForPartition(Partition(disk, part)) ?? throw new InvalidOperationException("No volume.");
            vol["FileSystemLabel"] = label ?? "";
            vol.Put();
            return new { ok = true };
        }

        public static object SetLetter(int disk, int part, string letter)
        {
            letter = DriveLetter(letter);
            var p = Partition(disk, part);
            Wmi.Invoke(p, "AddAccessPath", inp => inp["AccessPath"] = letter.TrimEnd(':') + ":\\");
            return new { ok = true };
        }

        public static object RemoveLetter(int disk, int part)
        {
            var p = Partition(disk, part);
            char dl = ' '; var lv = Wmi.Val(p, "DriveLetter"); if (lv is char c && c != '\0') dl = c;
            if (dl == ' ') return new { ok = true };
            Wmi.Invoke(p, "RemoveAccessPath", inp => inp["AccessPath"] = dl + ":\\");
            return new { ok = true };
        }

        public static object SetPartitionAttributes(int disk, int part, bool? active, bool? hidden, bool? readOnly)
        {
            // WMI MSFT_Partition has no single "SetAttributes"; use diskpart for active flag (MBR) and set hidden/readonly.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"select disk {disk}");
            sb.AppendLine($"select partition {part}");
            if (active.HasValue) sb.AppendLine(active.Value ? "active" : "inactive");
            if (hidden.HasValue) sb.AppendLine(hidden.Value ? "attributes volume set hidden" : "attributes volume clear hidden");
            if (readOnly.HasValue) sb.AppendLine(readOnly.Value ? "attributes volume set readonly" : "attributes volume clear readonly");
            var r = Proc.DiskPart(sb.ToString());
            return new { ok = true, log = r.All };
        }

        // ---------------------------------------------------------------- DISK-LEVEL
        public static object InitializeDisk(int disk, string style)
        {
            var d = Disk(disk);
            if (Wmi.Bool(d, "IsReadOnly")) SetDiskReadOnly(disk, false);
            if (Wmi.Bool(d, "IsOffline")) SetOnline(disk, true);
            Wmi.Invoke(d, "Initialize", inp => inp["PartitionStyle"] = style?.ToUpper() == "MBR" ? (ushort)1 : (ushort)2);
            return new { ok = true };
        }

        public static object ClearDisk(int disk)
        {
            var d = Disk(disk);
            if (Wmi.Bool(d, "IsSystem") || Wmi.Bool(d, "IsBoot"))
                throw new InvalidOperationException("Refusing to clear the system/boot disk.");
            Wmi.Invoke(d, "Clear", inp => { inp["RemoveData"] = true; inp["RemoveOEM"] = true; });
            return new { ok = true };
        }

        public static object ConvertDisk(int disk, string style)
        {
            // Convert between MBR/GPT (requires no partitions per Windows rules; diskpart enforces).
            var r = Proc.DiskPart($"select disk {disk}\r\nconvert {(style?.ToUpper() == "MBR" ? "mbr" : "gpt")}");
            if (r.Code != 0 || PartStyle(Wmi.Ui(Disk(disk), "PartitionStyle")) != (style?.ToUpper() == "MBR" ? "MBR" : "GPT"))
                throw new InvalidOperationException(r.All.Trim());
            return new { ok = true, log = r.All };
        }

        public static object SetOnline(int disk, bool on)
        {
            var d = Disk(disk);
            // Taking a disk offline must not first bring an offline disk online.
            Wmi.Invoke(d, on ? "Online" : "Offline", null);
            return new { ok = true };
        }

        public static object SetDiskReadOnly(int disk, bool on)
        {
            var result = Proc.DiskPart($"select disk {disk}\r\nattributes disk {(on ? "set" : "clear")} readonly");
            if (result.Code != 0 || Wmi.Bool(Disk(disk), "IsReadOnly") != on)
                throw new InvalidOperationException("Disk read-only state did not change as requested. " + result.All);
            return new { ok = true };
        }

        public static object FreeLetters()
        {
            var used = new HashSet<char>(System.IO.DriveInfo.GetDrives().Select(x => char.ToUpper(x.Name[0])));
            var free = new List<string>();
            for (char ch = 'D'; ch <= 'Z'; ch++) if (!used.Contains(ch)) free.Add(ch + ":");
            return free;
        }

        // ---------------------------------------------------------------- REPAIR / OPTIMIZE / BITLOCKER
        public static object RepairVolume(string letter, string mode, Job j)
        {
            letter = DriveLetter(letter);
            j.Progress(5, "Scanning " + letter + "…");
            string scan = mode == "fix" ? "-OfflineScanAndFix" : (mode == "spotfix" ? "-SpotFix" : "-Scan");
            var r = Proc.Ps($"Repair-Volume -DriveLetter {letter.TrimEnd(':')} {scan}", j);
            if (r.Code != 0) throw new InvalidOperationException("Volume repair failed: " + r.All);
            j.Progress(100, "Done");
            return new { log = r.All, ok = r.Code == 0 };
        }

        public static object OptimizeVolume(string letter, bool analyzeOnly, Job j)
        {
            letter = DriveLetter(letter);
            j.Progress(5, (analyzeOnly ? "Analyzing " : "Optimizing ") + letter);
            var r = Proc.Run("defrag.exe", $"{letter.TrimEnd(':')}: {(analyzeOnly ? "/A" : "/O")} /U /V", null, j);
            if (r.Code != 0) throw new InvalidOperationException("Volume optimization failed: " + r.All);
            j.Progress(100, "Done");
            return new { log = r.All, ok = r.Code == 0 };
        }

        public static object BitLocker()
        {
            var list = new List<object>();
            try
            {
                foreach (var v in Wmi.Query(@"root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT * FROM Win32_EncryptableVolume"))
                {
                    uint status = 0; try { var o = v.InvokeMethod("GetProtectionStatus", null, null); status = Wmi.Ui(o, "ProtectionStatus"); } catch { }
                    list.Add(new { letter = Wmi.Str(v, "DriveLetter"), protection = status == 1 ? "On" : status == 2 ? "Unknown" : "Off" });
                }
            }
            catch (Exception ex) { return new { error = ex.Message, list }; }
            return new { list };
        }

        // ---------------------------------------------------------------- helpers
        internal static string DriveLetter(string value)
        {
            string letter = (value ?? "").Trim();
            if (letter.Length == 2 && letter[1] == ':') letter = letter.Substring(0, 1);
            if (letter.Length != 1 ||
                !(letter[0] >= 'A' && letter[0] <= 'Z' || letter[0] >= 'a' && letter[0] <= 'z'))
                throw new ArgumentException("Select a single drive letter.", nameof(value));
            return letter.ToUpperInvariant();
        }
        public static ManagementObject Disk(int n) => Wmi.One(Wmi.StorageNs, $"SELECT * FROM MSFT_Disk WHERE Number={n}") ?? throw new InvalidOperationException($"Disk {n} not found.");
        public static ManagementObject Partition(int disk, int part) => Wmi.One(Wmi.StorageNs, $"SELECT * FROM MSFT_Partition WHERE DiskNumber={disk} AND PartitionNumber={part}") ?? throw new InvalidOperationException($"Partition {disk}:{part} not found.");

        static ManagementObject FindVolumeForPartition(ManagementObject p)
        {
            try
            {
                var oid = Wmi.Str(p, "ObjectId")?.Replace("\\", "\\\\").Replace("'", "\\'");
                return Wmi.One(Wmi.StorageNs, $"ASSOCIATORS OF {{MSFT_Partition.ObjectId='{oid}'}} WHERE ResultClass=MSFT_Volume");
            }
            catch { return null; }
        }

        static string NormFs(string fs) => (fs ?? "NTFS").ToUpper() switch { "FAT" => "FAT", "FAT32" => "FAT32", "EXFAT" => "exFAT", "REFS" => "ReFS", _ => "NTFS" };

        static string PartTypeName(ManagementObject disk, ManagementObject p)
        {
            if (PartStyle(Wmi.Ui(disk, "PartitionStyle")) == "GPT")
            {
                var g = (Wmi.Str(p, "GptType") ?? "").ToLower();
                return g switch
                {
                    "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}" => "EFI System",
                    "{e3c9e316-0b5c-4db8-817d-f92df00215ae}" => "Microsoft Reserved",
                    "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}" => "Basic Data",
                    "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}" => "Recovery",
                    _ => "GPT",
                };
            }
            var m = Wmi.Str(p, "MbrType");
            return m switch { "7" => "NTFS/exFAT (07)", "12" => "FAT32 (0C)", "11" => "FAT32 (0B)", "6" => "FAT16 (06)", _ => m != null ? "MBR " + m : "MBR" };
        }

        static string PartStyle(uint v) => v switch { 1 => "MBR", 2 => "GPT", _ => "RAW" };
        static string BusName(uint v) => v switch { 1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 5 => "SSA", 6 => "Fibre", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 17 => "NVMe", _ => "Unknown" };
        static string MediaName(uint v) => v switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" };
        static string HealthName(uint v) => v switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
    }
}
