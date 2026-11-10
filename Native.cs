using System;
using System.Runtime.InteropServices;
using System.Text;

namespace XenoSuite
{
    static class Native
    {
        [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int retLen);
        [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
                KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }
        [DllImport("psapi.dll")] public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION p, uint cb);

        public const uint PROCESS_TERMINATE = 0x0001, PROCESS_SET_INFORMATION = 0x0200, PROCESS_SUSPEND_RESUME = 0x0800,
            PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_VM_READ = 0x0010;
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr info, int len, out int ret);
        [DllImport("kernel32.dll")] public static extern bool IsWow64Process(IntPtr h, out bool wow);
        [DllImport("kernel32.dll")] public static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr proc, out UIntPtr sys);
        [DllImport("kernel32.dll")] public static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);

        public static string ImagePath(int pid)
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        public static string ProcessUser(int pid)
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                if (!OpenProcessToken(h, 0x0008, out var tok)) return null;
                try
                {
                    GetTokenInformation(tok, 1, IntPtr.Zero, 0, out int len);
                    if (len <= 0) return null;
                    var buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        if (!GetTokenInformation(tok, 1, buf, len, out len)) return null;
                        var sidPtr = Marshal.ReadIntPtr(buf);
                        var sid = new System.Security.Principal.SecurityIdentifier(sidPtr);
                        try { var acct = sid.Translate(typeof(System.Security.Principal.NTAccount)).ToString(); int i = acct.IndexOf('\\'); return i >= 0 ? acct[(i + 1)..] : acct; }
                        catch { return sid.Value; }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(tok); }
            }
            finally { CloseHandle(h); }
        }

        public static bool Is32Bit(int pid)
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try { return IsWow64Process(h, out bool w) && w; } finally { CloseHandle(h); }
        }

        // ---- TCP / UDP tables ----
        [DllImport("iphlpapi.dll")] public static extern uint GetExtendedTcpTable(IntPtr tbl, ref int size, bool order, int af, int cls, uint reserved);
        [DllImport("iphlpapi.dll")] public static extern uint GetExtendedUdpTable(IntPtr tbl, ref int size, bool order, int af, int cls, uint reserved);

        // ---- dark title bar ----
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO psfi, uint cb, uint flags);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    }
}
