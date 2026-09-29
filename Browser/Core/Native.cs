using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Browser.Core;

internal static class Native
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_MBUTTON = 0x04;

    public static bool IsCtrlDown => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
    public static bool IsShiftDown => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
    public static bool IsAltDown => (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
    public static bool IsMiddleDown => (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

    // ---- Process memory (private working set — like the "Memory" column in Task Manager) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx2 counters, uint size);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public static long PrivateWorkingSet(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            var counters = new ProcessMemoryCountersEx2 { cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
            if (GetProcessMemoryInfo(handle, ref counters, counters.cb))
                return counters.PrivateWorkingSetSize > 0 ? (long)counters.PrivateWorkingSetSize : (long)counters.WorkingSetSize;
            return 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static long OwnPrivateWorkingSet() => PrivateWorkingSet(Environment.ProcessId);

    // ---- Окно ----

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    public static void ExtendFrame(IntPtr hwnd, bool sheet)
    {
        var m = sheet ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : default;
        DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newLong);

    private const int GWL_STYLE = -16, WS_SYSMENU = 0x00080000;

    /// <summary>
    /// Removes WS_SYSMENU so Windows draws no native min/max/close caption buttons. WindowChrome +
    /// UseAeroCaptionButtons=False only stops hit-test forwarding — on Windows 11 the OS still paints
    /// the native buttons in the extended (Mica) frame, which appear as a second set beside our own.
    /// Window state is left otherwise untouched, so WebView2 keyboard-accelerator forwarding is unaffected.
    /// </summary>
    public static void HideNativeCaptionButtons(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GWL_STYLE);
        if ((style & WS_SYSMENU) != 0) SetWindowLong(hwnd, GWL_STYLE, style & ~WS_SYSMENU);
    }

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public static void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
