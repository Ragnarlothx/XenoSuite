using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace XenoSuite
{
    public partial class MainWindow : Window
    {
        public static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XenoSuite");
        public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XenoSuite");
        public static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public static MainWindow Instance;

        public readonly HwSensors Hw = new();
        public readonly Pinger Ping = new();
        readonly Sampler sampler = new();
        readonly SysCounters ctr = new();
        volatile int intervalMs = 1000;
        volatile bool monReady, running = true, paused;
        volatile string currentPage = "mon";
        volatile bool hidden;
        int postPending;           // 1 while a tick is queued on the UI thread → the sampler drops a frame instead of piling up
        Thread loop;
        CsvLog csv;
        Rect normalBounds; bool mini;
        DispatcherTimerLite devTimer;

        // one WebView per tab, created on first visit and kept alive
        CoreWebView2Environment env;
        readonly Dictionary<string, WebView2> views = new();
        readonly Dictionary<string, Task> viewInit = new();
        readonly Dictionary<CoreWebView2, string> pageOf = new();
        CoreWebView2 MonCore => views["mon"].CoreWebView2;

        // routing: every Disk-domain method name (from Api.Dispatch)
        static readonly HashSet<string> DiskMethods = new() {
            "benchmark","bitlocker","capacityTest","chkdsk","cleanupTemp","clearDisk","convertDisk","createPartition",
            "deletePartition","diskDetail","diskOnline","diskReadOnly","disks","eject","fileHistory","findDupes","findFiles",
            "formatVolume","freeLetters","hashFile","health","imageBackup","imageWrite","initDisk","jobCancel","jobs","moveFiles",
            "openDiskMgmt","openFile","optimizeVolume","recycleFiles","recycleList","recycleRestore","removeLetter","repairVolume",
            "resizePartition","reveal","setLabel","setLetter","setPartAttr","setUsbWriteProtect","shadowBrowse","shadowCreate",
            "shadowList","shadowRestore","spaceChildren","spaceScan","spaceTypes","supportedSize","surfaceScan","usbWriteProtect" };
        static readonly HashSet<string> AuditMethods = new() { "auditStatus", "auditRunGui", "auditScan", "auditOpenHtml", "auditSetPath", "auditReport" };
        static readonly HashSet<string> UiMethods = new() {
            "ready","suiteNav","setInterval","pause","setTopmost","setMini","icons","logStart","logStop","setDarkTitle","setTheme",
            "devTools","restartAdmin","minimize","quit","loadSettings","saveSettings","log","pickFolder","pickFile","saveFile","hwMeta" };

        public MainWindow()
        {
            Instance = this;
            Directory.CreateDirectory(AppData); Directory.CreateDirectory(DataDir);
            InitializeComponent();
            views["mon"] = webMon; views["disk"] = webDisk; views["audit"] = webAudit;
            RestorePlacement();
            Loaded += async (_, __) => await InitWeb();
            SourceInitialized += (_, __) => { DarkTitle(true); var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); src?.AddHook(WndProc); };
            Closing += (_, __) => { SavePlacement(); running = false; csv?.Dispose(); Jobs.CancelAll(); };
            Closed += (_, __) => { try { loop?.Join(1500); } catch { } Hw.Dispose(); };
            StateChanged += (_, __) => hidden = WindowState == WindowState.Minimized;
            devTimer = new DispatcherTimerLite(1200, () => Push("devchange", new { t = DateTime.Now.Ticks }));
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) { if (msg == 0x0219) devTimer.Bounce(); return IntPtr.Zero; }

        async Task InitWeb()
        {
            try { env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppData, "WebView2")); await InitView("mon"); }
            catch (Exception ex) { MessageBox.Show("WebView2 runtime failed to start:\n" + ex.Message, "XenoSuite"); Close(); }
        }

        Task InitView(string page)
        {
            if (viewInit.TryGetValue(page, out var t)) return t;
            return viewInit[page] = InitViewCore(page);
        }

        async Task InitViewCore(string page)
        {
            var wv = views[page];
            await wv.EnsureCoreWebView2Async(env);
            var cw = wv.CoreWebView2;
            pageOf[cw] = page;
            cw.SetVirtualHostNameToFolderMapping("xenosuite.local", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Allow);
            cw.Settings.AreDefaultContextMenusEnabled = false;
            cw.Settings.AreBrowserAcceleratorKeysEnabled = Debugger.IsAttached;
            cw.Settings.IsStatusBarEnabled = false;
            cw.Settings.IsZoomControlEnabled = false;
            cw.WebMessageReceived += OnMessage;
            cw.NewWindowRequested += (s, e) => { e.Handled = true; Shell(e.Uri); };
            cw.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("https://xenosuite.local/")) { e.Cancel = true; Shell(e.Uri); } };
            cw.Navigate($"https://xenosuite.local/{page}/index.html");
        }

        async void ShowPage(string to)
        {
            if (to != "mon" && to != "disk" && to != "audit") to = "mon";
            currentPage = to;
            foreach (var kv in views) kv.Value.Visibility = kv.Key == to ? Visibility.Visible : Visibility.Collapsed;
            try { await InitView(to); views[to].Focus(); }
            catch (Exception ex) { Log.Write("nav", ex.ToString()); }
        }

        public static void Shell(string u) { try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } }

        // ---------------- sampling loop (monitor) ----------------
        void StartLoop()
        {
            if (loop != null) return;
            loop = new Thread(Loop) { IsBackground = true, Name = "XenoSuite.Sampler", Priority = ThreadPriority.AboveNormal };
            loop.Start();
        }

        static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

        void Loop()
        {
            Push("status", new { stage = "sensors", msg = "Opening hardware sensors (PawnIO / NVAPI / ADL)…" }, "mon");
            Hw.Open();
            Hw.Start(() => !paused && currentPage == "mon" && !hidden && monReady, () => intervalMs);   // sensors poll on their own thread
            Push("status", new { stage = "ready", lhm = Hw.Ok, error = Hw.Error, admin = IsAdmin(), cores = sampler.Cores, machine = Environment.MachineName }, "mon");
            var buf = new ArrayBufferWriter<byte>(256 * 1024);
            var w = new Utf8JsonWriter(buf, new JsonWriterOptions { SkipValidation = true });
            // warm-up: prime every delta-based reading so the first real tick has rates
            sampler.Cpu(); sampler.Net(); sampler.Disk(); sampler.Drives();
            w.WriteStartArray(); sampler.WriteProcs(w); ctr.Write(w); w.WriteEndArray(); w.Flush();
            Ping.Gateways = () => sampler.Gateways; Ping.Start();
            Thread.Sleep(300);

            var prof = new double[7]; int n = 0, dropped = 0;
            var sw = new Stopwatch();
            while (running)
            {
                sw.Restart();
                try
                {
                    bool active = !paused && currentPage == "mon" && !hidden && monReady;
                    Ping.Active = active; ctr.Active = active;
                    if (active && Volatile.Read(ref postPending) == 0)
                    {
                        buf.ResetWrittenCount(); w.Reset(buf);
                        sampler.GpuProc.Refresh();                         // non-blocking: kicks a background read when stale
                        long s = Stopwatch.GetTimestamp();
                        var cpu = sampler.Cpu(); var mem = sampler.Mem();
                        double tCpu = Ms(s); s = Stopwatch.GetTimestamp();

                        w.WriteStartObject(); w.WriteString("evt", "tick"); w.WriteStartObject("data");
                        w.WriteNumber("t", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                        w.WritePropertyName("cpu"); JsonSerializer.Serialize(w, cpu, J);
                        w.WritePropertyName("mem"); JsonSerializer.Serialize(w, mem, J);
                        w.WritePropertyName("procs"); sampler.WriteProcs(w);
                        double tProcs = Ms(s); s = Stopwatch.GetTimestamp();
                        w.WritePropertyName("net"); JsonSerializer.Serialize(w, sampler.Net(), J);
                        w.WritePropertyName("drives"); JsonSerializer.Serialize(w, sampler.Drives(), J);
                        w.WritePropertyName("disk"); JsonSerializer.Serialize(w, sampler.Disk(), J);
                        double tNet = Ms(s); s = Stopwatch.GetTimestamp();
                        w.WritePropertyName("hw"); Hw.Write(w);                       // latest snapshot from the sensor thread
                        double tHw = Ms(s); s = Stopwatch.GetTimestamp();
                        w.WritePropertyName("sys"); ctr.Write(w);
                        w.WritePropertyName("ping"); Ping.Write(w);
                        double tCtr = Ms(s);
                        csv?.Write(sampler.LastCpuTotal, sampler.LastMemUsed, Hw);
                        w.WriteNumber("up", Environment.TickCount64);
                        if (csv != null) w.WriteString("logging", csv.File); else w.WriteNull("logging");
                        double cost = sw.Elapsed.TotalMilliseconds;
                        double[] cur = { tCpu, tProcs, tNet, tHw, tCtr, sampler.GpuProc.LastMs, cost };
                        for (int i = 0; i < prof.Length; i++) prof[i] = n == 0 ? cur[i] : prof[i] * 0.85 + cur[i] * 0.15;
                        w.WriteNumber("cost", Math.Round(cost, 1));
                        w.WriteStartObject("prof");
                        w.WriteNumber("cpu", Math.Round(prof[0], 2)); w.WriteNumber("procs", Math.Round(prof[1], 2)); w.WriteNumber("net", Math.Round(prof[2], 2));
                        w.WriteNumber("hw", Math.Round(prof[3], 2)); w.WriteNumber("ctr", Math.Round(prof[4], 2)); w.WriteNumber("gpuBg", Math.Round(prof[5], 1)); w.WriteNumber("sensorBg", Math.Round(Hw.LastCycleMs, 1));
                        w.WriteNumber("bytes", buf.WrittenCount); w.WriteNumber("dropped", dropped);
                        w.WriteEndObject();
                        w.WriteEndObject(); w.WriteEndObject(); w.Flush();
                        var json = Encoding.UTF8.GetString(buf.WrittenSpan);
                        Interlocked.Exchange(ref postPending, 1);
                        Dispatcher.InvokeAsync(() => { try { MonCore?.PostWebMessageAsJson(json); } catch { } finally { Volatile.Write(ref postPending, 0); } });
                        n++;
                        if (n == 30 || n % 1800 == 0)
                            Log.Write("perf", $"tick avg {prof[6]:0.0}ms (cpu+mem {prof[0]:0.00} procs {prof[1]:0.00} [{sampler.ProcCount}] net/drives {prof[2]:0.00} hw {prof[3]:0.00} counters {prof[4]:0.00}) gpu-bg {prof[5]:0}ms payload {buf.WrittenCount / 1024.0:0.0}KB dropped {dropped} || {Hw.ProfileText()}");
                    }
                    else if (active) dropped++;
                }
                catch (Exception ex) { Log.Write("loop", ex.ToString()); }
                int wait = intervalMs - (int)sw.ElapsedMilliseconds;
                Thread.Sleep(Math.Max(20, wait));
            }
        }

        /// Sends an event to one tab (or every tab that's been opened when page is null).
        public void Push(string evt, object data, string page = null)
        {
            var json = JsonSerializer.Serialize(new { evt, data }, J);
            Dispatcher.InvokeAsync(() =>
            {
                foreach (var kv in views)
                {
                    if (page != null && kv.Key != page) continue;
                    try { kv.Value.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
                }
            });
        }

        public static bool IsAdmin() => new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        // ---------------- messaging ----------------
        async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var cw = sender as CoreWebView2;
            if (!e.Source.StartsWith("https://xenosuite.local/", StringComparison.OrdinalIgnoreCase)) { Log.Write("call", "rejected message from " + e.Source); return; }
            string page = cw != null && pageOf.TryGetValue(cw, out var pg) ? pg : "mon";
            int id = 0; object result = null; string error = null;
            try
            {
                if (JsonNode.Parse(e.WebMessageAsJson) is not JsonObject o) return;
                id = o["id"] is JsonValue iv && iv.TryGetValue<int>(out var n) ? n : 0;
                string method = o["method"]?.GetValue<string>() ?? "";
                var a = o["args"] as JsonObject ?? new JsonObject();
                if (UiMethods.Contains(method)) result = Ui(method, a, page, cw);
                else if (DiskMethods.Contains(method)) result = await Task.Run(() => Api.Dispatch(method, a));
                else if (AuditMethods.Contains(method)) result = await Task.Run(() => Audit.Dispatch(method, a));
                else result = await Task.Run(() => Tools.Dispatch(method, a, this));
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException ? ex.InnerException ?? ex : ex;
                error = inner.Message; Log.Write("call", inner.ToString());
            }
            if (id == 0) return;
            try { cw?.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, result, error }, J)); } catch { }
        }

        object Ui(string m, JsonObject a, string page, CoreWebView2 cw)
        {
            switch (m)
            {
                case "ready":
                    if (page == "mon") { monReady = true; Hw.ForceMeta(); StartLoop(); }
                    return new { admin = IsAdmin(), cores = Environment.ProcessorCount, data = DataDir, machine = Environment.MachineName, version = typeof(App).Assembly.GetName().Version?.ToString(3), sysDrive = Path.GetPathRoot(Environment.SystemDirectory), page };
                case "hwMeta": Hw.ForceMeta(); return true;
                case "suiteNav": ShowPage(a["to"]?.GetValue<string>() ?? "mon"); return true;
                case "setInterval": intervalMs = Math.Clamp((int)a["ms"], 250, 10000); return intervalMs;
                case "pause": paused = (bool)a["on"]; return paused;
                case "setTopmost": Topmost = (bool)a["on"]; return Topmost;
                case "setDarkTitle": DarkTitle((bool)a["on"]); return true;
                case "setTheme": DarkTitle(a["dark"]?.GetValue<bool>() ?? true); return true;
                case "minimize": WindowState = WindowState.Minimized; return true;
                case "quit": Close(); return true;
                case "devTools": cw?.OpenDevToolsWindow(); return true;
                case "restartAdmin":
                    try { Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas" }); Application.Current.Shutdown(); return true; } catch { return false; }
                case "setMini":
                    {
                        bool on = (bool)a["on"]; if (on == mini) return mini;
                        if (on) { normalBounds = new Rect(Left, Top, Width, Height); if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; Width = 360; Height = 560; Topmost = true; var wa = SystemParameters.WorkArea; Left = wa.Right - Width - 12; Top = wa.Top + 12; }
                        else { Topmost = a["topmost"] is JsonValue tv && (bool)tv; Left = normalBounds.Left; Top = normalBounds.Top; Width = normalBounds.Width; Height = normalBounds.Height; }
                        mini = on; return mini;
                    }
                case "logStart": csv?.Dispose(); csv = new CsvLog(Path.Combine(DataDir, "logs", $"xenosuite-{DateTime.Now:yyyyMMdd-HHmmss}.csv")); return csv.File;
                case "logStop": { var f = csv?.File; csv?.Dispose(); csv = null; return f; }
                case "icons":
                    {
                        var res = new Dictionary<string, string>();
                        foreach (var p in a["paths"].AsArray().Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).Distinct().Take(80)) res[p] = IconPng(p);
                        return res;
                    }
                case "loadSettings": { var f = SettingsFile(page); return File.Exists(f) ? File.ReadAllText(f) : "{}"; }
                case "saveSettings": File.WriteAllText(SettingsFile(page), a["json"]?.GetValue<string>() ?? "{}"); return true;
                case "log": Log.Write("js." + page + "." + (a["level"]?.GetValue<string>() ?? "info"), a["text"]?.GetValue<string>()); return true;
                case "pickFolder": { var d = new Microsoft.Win32.OpenFolderDialog { Title = a["title"]?.GetValue<string>() ?? "Choose a folder" }; return d.ShowDialog(this) == true ? d.FolderName : null; }
                case "pickFile": { var d = new Microsoft.Win32.OpenFileDialog { Title = a["title"]?.GetValue<string>() ?? "Choose a file", Filter = a["filter"]?.GetValue<string>() ?? "All files|*.*" }; return d.ShowDialog(this) == true ? d.FileName : null; }
                case "saveFile": { var d = new Microsoft.Win32.SaveFileDialog { Title = a["title"]?.GetValue<string>() ?? "Save as", Filter = a["filter"]?.GetValue<string>() ?? "All files|*.*", FileName = a["name"]?.GetValue<string>() ?? "" }; return d.ShowDialog(this) == true ? d.FileName : null; }
            }
            return null;
        }

        static string SettingsFile(string page) => Path.Combine(AppData, (page == "disk" ? "disk" : page == "audit" ? "audit" : "mon") + "-settings.json");

        // icon cache: the same exe path asked for again (process restarts, tab revisits) skips the shell + PNG encode
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> iconCache = new(StringComparer.OrdinalIgnoreCase);
        static string IconPng(string path)
        {
            if (iconCache.TryGetValue(path, out var hit)) return hit;
            string r = null;
            try
            {
                var fi = new Native.SHFILEINFO();
                var res = Native.SHGetFileInfo(path, 0, ref fi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(fi), 0x100 | 0x1);
                if (res != IntPtr.Zero && fi.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(src));
                        using var ms = new MemoryStream(); enc.Save(ms);
                        r = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                    finally { Native.DestroyIcon(fi.hIcon); }
                }
            }
            catch { }
            if (iconCache.Count < 2000) iconCache[path] = r;
            return r;
        }

        void DarkTitle(bool on) { try { var h = new WindowInteropHelper(this).Handle; int v = on ? 1 : 0; Native.DwmSetWindowAttribute(h, 20, ref v, 4); } catch { } }

        string PlacementFile => Path.Combine(AppData, "window.json");
        void RestorePlacement()
        {
            try
            {
                if (!File.Exists(PlacementFile)) return;
                var o = JsonNode.Parse(File.ReadAllText(PlacementFile));
                double l = (double)o["l"], t = (double)o["t"], w = (double)o["w"], h = (double)o["h"];
                if (w < 400 || h < 300) return;
                if (l < SystemParameters.VirtualScreenLeft - 50 || t < SystemParameters.VirtualScreenTop - 50 ||
                    l > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 || t > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100) return;
                WindowStartupLocation = WindowStartupLocation.Manual; Left = l; Top = t; Width = w; Height = h;
                if ((bool?)o["max"] == true) WindowState = WindowState.Maximized;
            }
            catch { }
        }
        void SavePlacement()
        {
            try { var b = mini ? normalBounds : (WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds); File.WriteAllText(PlacementFile, JsonSerializer.Serialize(new { l = b.Left, t = b.Top, w = b.Width, h = b.Height, max = WindowState == WindowState.Maximized })); }
            catch { }
        }
    }

    /// Small debounce timer on the UI dispatcher.
    sealed class DispatcherTimerLite
    {
        readonly System.Windows.Threading.DispatcherTimer t;
        public DispatcherTimerLite(int ms, Action onTick) { t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) }; t.Tick += (_, __) => { t.Stop(); onTick(); }; }
        public void Bounce() { t.Stop(); t.Start(); }
    }

    /// Writes every monitor tick to CSV (straight from the sensor table — no JSON round-trip).
    sealed class CsvLog : IDisposable
    {
        public string File { get; }
        StreamWriter w; List<string> cols; string[] lastIds; Dictionary<string, int> idx;
        readonly StringBuilder sb = new(4096);
        public CsvLog(string f) { File = f; Directory.CreateDirectory(Path.GetDirectoryName(f)); w = new StreamWriter(f, false, new UTF8Encoding(false)) { AutoFlush = true }; }
        public void Write(double cpuTotal, ulong memUsed, HwSensors hw)
        {
            if (w == null) return;
            try
            {
                var (ids, labels, vals) = hw.Ok ? hw.Table() : (Array.Empty<string>(), Array.Empty<string>(), Array.Empty<double?>());
                if (cols == null) { cols = ids.ToList(); w.WriteLine("time,cpu_total_pct,mem_used_bytes," + string.Join(",", labels.Select(l => "\"" + l.Replace("\"", "'") + "\""))); }
                if (!ReferenceEquals(ids, lastIds)) { lastIds = ids; idx = new Dictionary<string, int>(); for (int i = 0; i < ids.Length; i++) idx[ids[i]] = i; }
                sb.Clear();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(',').Append(cpuTotal.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(memUsed);
                foreach (var k in cols) { sb.Append(','); if (idx.TryGetValue(k, out var i) && vals[i] is double v) sb.Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                w.WriteLine(sb.ToString());
            }
            catch { }
        }
        public void Dispose() { try { w?.Dispose(); } catch { } w = null; }
    }
}
