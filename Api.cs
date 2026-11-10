using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XenoSuite
{
    /// <summary>Routes calls from the web UI to the backend modules.</summary>
    static class Api
    {
        public static object Dispatch(string m, JsonObject a)
        {
            switch (m)
            {
                // ---- disks / partitions / volumes ----
                case "disks": return Storage.ListDisks();
                case "diskDetail": return Storage.DiskDetail(A.I(a, "disk"));
                case "supportedSize": return Storage.SupportedSize(A.I(a, "disk"), A.I(a, "part"));
                case "createPartition": return Storage.CreatePartition(A.I(a, "disk"), A.L(a, "sizeMB"), A.S(a, "fs"), A.S(a, "label"), A.S(a, "letter"), A.B(a, "quick", true));
                case "deletePartition": return Storage.DeletePartition(A.I(a, "disk"), A.I(a, "part"));
                case "resizePartition": return Storage.ResizePartition(A.I(a, "disk"), A.I(a, "part"), A.L(a, "sizeMB"));
                case "formatVolume": return Storage.FormatPartition(A.I(a, "disk"), A.I(a, "part"), A.S(a, "fs"), A.S(a, "label"), A.B(a, "quick", true), A.I(a, "cluster", 0));
                case "setLabel": return Storage.SetLabel(A.I(a, "disk"), A.I(a, "part"), A.S(a, "label"));
                case "setLetter": return Storage.SetLetter(A.I(a, "disk"), A.I(a, "part"), A.S(a, "letter"));
                case "removeLetter": return Storage.RemoveLetter(A.I(a, "disk"), A.I(a, "part"));
                case "setPartAttr": return Storage.SetPartitionAttributes(A.I(a, "disk"), A.I(a, "part"), A.NB(a, "active"), A.NB(a, "hidden"), A.NB(a, "readOnly"));
                case "initDisk": return Storage.InitializeDisk(A.I(a, "disk"), A.S(a, "style"));
                case "clearDisk": return Storage.ClearDisk(A.I(a, "disk"));
                case "convertDisk": return Storage.ConvertDisk(A.I(a, "disk"), A.S(a, "style"));
                case "diskOnline": return Storage.SetOnline(A.I(a, "disk"), A.B(a, "on"));
                case "diskReadOnly": return Storage.SetDiskReadOnly(A.I(a, "disk"), A.B(a, "on"));
                case "freeLetters": return Storage.FreeLetters();
                case "repairVolume": return Jobs.Start("repair", "Repair " + A.S(a, "letter"), j => Storage.RepairVolume(A.S(a, "letter"), A.S(a, "mode"), j));
                case "optimizeVolume": return Jobs.Start("optimize", "Optimize " + A.S(a, "letter"), j => Storage.OptimizeVolume(A.S(a, "letter"), A.B(a, "analyzeOnly"), j));
                case "bitlocker": return Storage.BitLocker();

                // ---- health ----
                case "health": return Health.Report();
                case "surfaceScan": return Jobs.Start("surface", "Surface scan disk " + A.I(a, "disk"), j => Health.SurfaceScan(A.I(a, "disk"), j));

                // ---- USB / removable ----
                case "eject": return Usb.Eject(A.I(a, "disk"));
                case "usbWriteProtect": return Usb.GetWriteProtect();
                case "setUsbWriteProtect": return Usb.SetWriteProtect(A.B(a, "on"));
                case "capacityTest": return Jobs.Start("captest", "Capacity test " + A.S(a, "letter"), j => Usb.CapacityTest(A.S(a, "letter"), A.L(a, "maxMB"), j));

                // ---- recovery ----
                case "recycleList": return Recovery.RecycleList();
                case "recycleRestore": return Recovery.RecycleRestore(A.SA(a, "ids"), A.S(a, "dest"));
                case "shadowList": return Recovery.ShadowList();
                case "shadowBrowse": return Recovery.ShadowBrowse(A.S(a, "device"), A.S(a, "path"));
                case "shadowRestore": return Jobs.Start("shadowrestore", "Restore from snapshot", j => Recovery.ShadowRestore(A.S(a, "device"), A.SA(a, "paths"), A.S(a, "dest"), j));
                case "shadowCreate": return Recovery.ShadowCreate(A.S(a, "letter"));
                case "fileHistory": return Recovery.FileVersions(A.S(a, "path"));

                // ---- file finders ----
                case "findFiles": return Jobs.Start("find", "Find files in " + A.S(a, "root"), j => Finder.Find(a, j));
                case "findDupes": return Jobs.Start("dupes", "Duplicate scan in " + A.S(a, "root"), j => Finder.Duplicates(a, j));
                case "recycleFiles": return Finder.ToRecycleBin(A.SA(a, "paths"));
                case "moveFiles": return Jobs.Start("move", "Move files", j => Finder.MoveOrCopy(A.SA(a, "paths"), A.S(a, "dest"), A.B(a, "copy"), j));
                case "reveal": Finder.Reveal(A.S(a, "path")); return true;
                case "openFile": MainWindow.Shell(A.S(a, "path")); return true;

                // ---- space analyzer ----
                case "spaceScan": return Jobs.Start("space", "Analyze " + A.S(a, "root"), j => Space.Scan(a, j));
                case "spaceChildren": return Space.Children(A.S(a, "path"));
                case "spaceTypes": return Space.Types(A.S(a, "path"));

                // ---- tools ----
                case "benchmark": return Jobs.Start("bench", "Benchmark " + A.S(a, "letter"), j => Bench.Run(A.S(a, "letter"), A.I(a, "sizeMB", 1024), j));
                case "chkdsk": return Jobs.Start("chkdsk", "chkdsk " + A.S(a, "letter"), j => DiskTools.Chkdsk(A.S(a, "letter"), A.S(a, "mode"), j));
                case "imageBackup": return Jobs.Start("image", "Image disk " + A.I(a, "disk"), j => DiskTools.ImageBackup(A.I(a, "disk"), A.S(a, "file"), A.B(a, "verify", true), A.I(a, "bufferMB", 4), j));
                case "imageWrite": return Jobs.Start("imagewrite", "Write image to disk " + A.I(a, "disk"), j => DiskTools.ImageWrite(A.S(a, "file"), A.I(a, "disk"), A.B(a, "verify", true), A.I(a, "bufferMB", 4), j));
                case "hashFile": return Jobs.Start("hash", "Hash file", j => DiskTools.HashFile(A.S(a, "file"), j));
                case "cleanupTemp": return Jobs.Start("cleanup", "Clean temp files", j => DiskTools.CleanTemp(A.B(a, "dryRun", true), j));
                case "openDiskMgmt": Process.Start(new ProcessStartInfo("diskmgmt.msc") { UseShellExecute = true }); return true;

                // ---- jobs ----
                case "jobCancel": Jobs.Cancel(A.I(a, "id")); return true;
                case "jobs": return Jobs.List();
            }
            throw new InvalidOperationException("Unknown method: " + m);
        }
    }

    /// <summary>Argument helpers.</summary>
    static class A
    {
        public static string S(JsonObject a, string k) => a[k] is JsonValue v ? v.ToString() : null;
        public static int I(JsonObject a, string k, int d = 0)
        {
            if (a[k] is not JsonValue v) return d;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var dd)) return (int)dd;
            return int.TryParse(v.ToString(), out i) ? i : d;
        }
        public static long L(JsonObject a, string k, long d = 0)
        {
            if (a[k] is not JsonValue v) return d;
            if (v.TryGetValue<long>(out var i)) return i;
            if (v.TryGetValue<double>(out var dd)) return (long)dd;
            return long.TryParse(v.ToString(), out i) ? i : d;
        }
        public static bool B(JsonObject a, string k, bool d = false) => a[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
        public static bool? NB(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        public static string[] SA(JsonObject a, string k) => a[k] is JsonArray arr ? arr.Select(x => x?.ToString()).Where(x => x != null).ToArray() : Array.Empty<string>();
    }

    /// <summary>A long-running background operation with progress pushed to the UI.</summary>
    class Job
    {
        public int Id; public string Kind, Title; public DateTime Started = DateTime.Now;
        public readonly CancellationTokenSource Cts = new();
        public CancellationToken Token => Cts.Token;
        public double Pct; public string Msg = ""; public string State = "running";
        long lastPush;

        public void Progress(double pct, string msg, object extra = null, bool force = false)
        {
            Pct = pct; Msg = msg ?? Msg;
            long now = Environment.TickCount64;
            if (!force && now - lastPush < 150) return;
            lastPush = now;
            MainWindow.Instance?.Push("job", new { id = Id, kind = Kind, title = Title, pct = Math.Round(pct, 2), msg = Msg, state = State, extra });
        }
        public void Line(string text) => MainWindow.Instance?.Push("jobline", new { id = Id, kind = Kind, text });
        public void Check() => Token.ThrowIfCancellationRequested();
    }

    static class Jobs
    {
        static int seq;
        static readonly ConcurrentDictionary<int, Job> all = new();

        public static object Start(string kind, string title, Func<Job, object> work)
        {
            var j = new Job { Id = Interlocked.Increment(ref seq), Kind = kind, Title = title };
            all[j.Id] = j;
            j.Progress(0, "starting…", null, true);
            Task.Run(() =>
            {
                object result = null; string error = null;
                try { result = work(j); j.State = "done"; }
                catch (OperationCanceledException) { j.State = "cancelled"; error = "Cancelled"; }
                catch (Exception ex) { j.State = "error"; error = ex.Message; Log.Write("job." + kind, ex.ToString()); }
                MainWindow.Instance?.Push("jobdone", new { id = j.Id, kind, title, state = j.State, error, result });
            });
            return new { job = j.Id };
        }

        public static void Cancel(int id) { if (all.TryGetValue(id, out var j)) j.Cts.Cancel(); }
        public static void CancelAll() { foreach (var j in all.Values) try { j.Cts.Cancel(); } catch { } }
        public static object List() => all.Values.OrderByDescending(j => j.Id).Take(50).Select(j => new { id = j.Id, kind = j.Kind, title = j.Title, pct = j.Pct, msg = j.Msg, state = j.State, started = j.Started }).ToList();
    }
}
