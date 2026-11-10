using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace XenoSuite
{
    /// <summary>Recycle Bin restore, Volume Shadow Copy (Previous Versions) browse/restore, snapshot create.</summary>
    static class Recovery
    {
        // ---------------- Recycle Bin (via Shell) ----------------
        public static object RecycleList()
        {
            var items = new List<object>();
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic bin = shell.NameSpace(10); // ssfBITBUCKET
                foreach (dynamic it in bin.Items())
                {
                    string orig = bin.GetDetailsOf(it, 1);   // original location
                    string del = bin.GetDetailsOf(it, 2);    // date deleted
                    long size = 0; try { size = (long)it.Size; } catch { }
                    items.Add(new
                    {
                        name = (string)it.Name,
                        originalPath = orig,
                        deleted = del,
                        size,
                        isFolder = (bool)it.IsFolder,
                        id = (string)it.Path, // the ::{recycle} path, used to re-locate for restore
                    });
                }
            }
            catch (Exception ex) { return new { error = ex.Message, items }; }
            return new { items };
        }

        public static object RecycleRestore(string[] ids, string dest)
        {
            int ok = 0; var errs = new List<string>();
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic bin = shell.NameSpace(10);
                var want = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
                foreach (dynamic it in bin.Items())
                {
                    string p = (string)it.Path;
                    if (!want.Contains(p)) continue;
                    try
                    {
                        if (string.IsNullOrEmpty(dest))
                        {
                            // invoke the "restore" / "Restore the selected items" verb -> original location
                            bool done = false;
                            foreach (dynamic v in it.Verbs())
                            {
                                string nm = ((string)v.Name).Replace("&", "");
                                if (nm.StartsWith("Restore", StringComparison.OrdinalIgnoreCase)) { v.DoIt(); done = true; break; }
                            }
                            if (!done) throw new InvalidOperationException("No Restore verb");
                        }
                        else
                        {
                            Directory.CreateDirectory(dest);
                            dynamic folder = shell.NameSpace(dest);
                            folder.MoveHere(it); // moves out of the bin into dest
                        }
                        ok++;
                    }
                    catch (Exception ex) { errs.Add((string)it.Name + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { return new { ok, error = ex.Message, errors = errs }; }
            return new { ok, errors = errs };
        }

        // ---------------- Volume Shadow Copies ----------------
        public static object ShadowList()
        {
            var list = new List<object>();
            foreach (var s in Wmi.Query(Wmi.Cimv2, "SELECT * FROM Win32_ShadowCopy"))
            {
                list.Add(new
                {
                    id = Wmi.Str(s, "ID"),
                    device = Wmi.Str(s, "DeviceObject"),      // \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN
                    volume = Wmi.Str(s, "VolumeName"),
                    created = WmiDate(Wmi.Str(s, "InstallDate")),
                    count = Wmi.Ui(s, "Count"),
                });
            }
            return new { list = list.OrderByDescending(x => ((dynamic)x).created).ToList() };
        }

        // Make the shadow copy browsable: create a temporary symlink dir to the device object.
        public static object ShadowBrowse(string device, string path)
        {
            string link = MountLink(device);
            string full = SnapshotPath(link, path);
            var dirs = new List<object>(); var files = new List<object>();
            try
            {
                foreach (var d in Directory.GetDirectories(full)) { var di = new DirectoryInfo(d); dirs.Add(new { name = di.Name, rel = Rel(link, d) }); }
                foreach (var f in Directory.GetFiles(full)) { var fi = new FileInfo(f); files.Add(new { name = fi.Name, size = fi.Length, modified = fi.LastWriteTime, rel = Rel(link, f) }); }
            }
            catch (Exception ex) { return new { error = ex.Message, dirs, files }; }
            return new { dirs = dirs.OrderBy(x => ((dynamic)x).name).ToList(), files = files.OrderBy(x => ((dynamic)x).name).ToList() };
        }

        public static object ShadowRestore(string device, string[] rels, string dest, Job j)
        {
            string link = MountLink(device);
            Directory.CreateDirectory(dest);
            int ok = 0; var errs = new List<string>();
            for (int i = 0; i < rels.Length; i++)
            {
                j.Check(); j.Progress(i * 100.0 / Math.Max(1, rels.Length), "Restoring " + rels[i]);
                try
                {
                    string src = SnapshotPath(link, rels[i]);
                    string target = Path.Combine(dest, Path.GetFileName(src.TrimEnd('\\')));
                    target = Uniq(target);
                    if (Directory.Exists(src)) CopyDir(src, target, j);
                    else File.Copy(src, target, false);
                    ok++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errs.Add(rels[i] + ": " + ex.Message); }
            }
            return new { ok, errors = errs };
        }

        public static object ShadowCreate(string letter)
        {
            var cls = new System.Management.ManagementClass(Wmi.Cimv2, "Win32_ShadowCopy", null);
            var inp = cls.GetMethodParameters("Create");
            inp["Volume"] = letter.TrimEnd(':') + ":\\";
            inp["Context"] = "ClientAccessible";
            var outp = cls.InvokeMethod("Create", inp, null);
            uint rv = Wmi.Ui(outp, "ReturnValue");
            if (rv != 0) throw new InvalidOperationException("Snapshot create failed (code " + rv + "). VSS may be unavailable on this volume.");
            return new { ok = true, id = Wmi.Str(outp, "ShadowID") };
        }

        public static object FileVersions(string path)
        {
            // list the same file across all shadow copies of its volume
            path = Path.GetFullPath(path);
            var mount = new StringBuilder(32768);
            if (!GetVolumePathName(path, mount, mount.Capacity))
                throw new IOException("Cannot resolve the file's volume (" + Marshal.GetLastWin32Error() + ").");
            string root = mount.ToString();
            var volume = new StringBuilder(128);
            if (!GetVolumeNameForVolumeMountPoint(root, volume, volume.Capacity))
                throw new IOException("Cannot resolve the local volume identity (" + Marshal.GetLastWin32Error() + ").");
            string rel = Path.GetRelativePath(root, path);
            var versions = new List<object>();
            foreach (var s in Wmi.Query(Wmi.Cimv2, "SELECT * FROM Win32_ShadowCopy"))
            {
                if (!string.Equals(Wmi.Str(s, "VolumeName")?.TrimEnd('\\'), volume.ToString().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                string dev = Wmi.Str(s, "DeviceObject");
                try
                {
                    string link = MountLink(dev);
                    string candidate = Path.Combine(link, rel);
                    if (File.Exists(candidate))
                    {
                        var fi = new FileInfo(candidate);
                        versions.Add(new { device = dev, created = WmiDate(Wmi.Str(s, "InstallDate")), size = fi.Length, modified = fi.LastWriteTime, rel });
                    }
                }
                catch { }
            }
            return new { versions = versions.OrderByDescending(x => ((dynamic)x).created).ToList() };
        }

        // ---- shadow device mount helpers ----
        static readonly Dictionary<string, string> mounts = new();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateSymbolicLink(string link, string target, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumePathName(string path, StringBuilder mount, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder volume, int length);

        static string MountLink(string device)
        {
            const string prefix = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy";
            if (device == null || !device.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(device.Substring(prefix.Length), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int snapshot) || snapshot <= 0)
                throw new ArgumentException("Invalid shadow-copy device.");
            lock (mounts)
            {
            if (mounts.TryGetValue(device, out var existing) && Directory.Exists(existing)) return existing;
            string dir = Path.Combine(Path.GetTempPath(), "xenodisk_vss", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.GetDirectoryName(dir));
            // device ends without trailing slash; symlink to device + \ makes it browsable
            string target = device.EndsWith("\\") ? device : device + "\\";
            if (!CreateSymbolicLink(dir, target, 0x1 | 0x2))
            {
                // fallback: access the device path directly with trailing backslash
                mounts[device] = target; return target;
            }
            mounts[device] = dir; return dir;
            }
        }

        static string SnapshotPath(string root, string relative)
        {
            relative ??= "";
            if (Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 ||
                relative.Split('\\', '/').Any(segment => segment == ".."))
                throw new ArgumentException("Snapshot paths must stay inside the selected snapshot.");
            return Path.Combine(root, relative);
        }

        static string Rel(string root, string full) => full.Substring(root.Length).TrimStart('\\', '/');
        static void CopyDir(string src, string dst, Job job)
        {
            job.Check();
            if ((File.GetAttributes(src) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Snapshot restore does not follow directory links: " + src);
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) { job.Check(); File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), false); }
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)), job);
        }
        static string Uniq(string t)
        {
            if (!File.Exists(t) && !Directory.Exists(t)) return t;
            string dir = Path.GetDirectoryName(t), bn = Path.GetFileNameWithoutExtension(t), ext = Path.GetExtension(t);
            for (int i = 1; ; i++) { var x = Path.Combine(dir, $"{bn} ({i}){ext}"); if (!File.Exists(x) && !Directory.Exists(x)) return x; }
        }
        static string WmiDate(string s)
        {
            try { return System.Management.ManagementDateTimeConverter.ToDateTime(s).ToString("yyyy-MM-dd HH:mm"); } catch { return s; }
        }
    }
}
