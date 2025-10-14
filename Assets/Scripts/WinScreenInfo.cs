using System;
using System.Runtime.InteropServices; 

public static class WinScreenInfo
{
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc callback, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public Rect rcMonitor;      // 总区域
        public Rect rcWork;         // 工作区（不含任务栏）
        public uint dwFlags;
    }

    public static void GetVirtualScreenSize(out int width, out int height)
    {
        // 使用 Windows API 获取虚拟屏幕
        var rect = GetSystemMetrics(78 /* SM_XVIRTUALSCREEN */);
        var left = GetSystemMetrics(76 /* SM_XVIRTUALSCREEN */);
        var top = GetSystemMetrics(77 /* SM_YVIRTUALSCREEN */);
        width = GetSystemMetrics(78 /* SM_CXVIRTUALSCREEN */);
        height = GetSystemMetrics(79 /* SM_CYVIRTUALSCREEN */);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}