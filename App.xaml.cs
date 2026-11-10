using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace XenoSuite
{
    public partial class App : Application
    {
        Mutex mutex;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            mutex = new Mutex(true, "XenoSuite.SingleInstance.v1", out bool fresh);
            if (!fresh)
            {
                var h = FindWindow(null, "XenoSuite");
                if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); }
                Shutdown(); return;
            }
            DispatcherUnhandledException += (s, ex) => { Log.Write("ui", ex.Exception.ToString()); ex.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => Log.Write("fatal", ex.ExceptionObject?.ToString());
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            new MainWindow().Show();
        }
    }

    static class Log
    {
        static readonly object L = new();
        public static void Write(string lvl, string text)
        {
            try { lock (L) System.IO.File.AppendAllText(System.IO.Path.Combine(MainWindow.AppData, "xenosuite.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {lvl}: {text}\r\n"); }
            catch { }
        }
    }
}
