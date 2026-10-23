using System;
using UnityEngine;

/// <summary>
/// 轻量日志封装：仅在 Unity 主线程把诊断信息输出到 Unity 日志（Player.log）。
///
/// 不再落盘写 WallpaperDebug.log —— 避免产生额外的日志文件。
/// 钩子线程调用时静默丢弃（Unity 日志只能在主线程写入）。
///
/// 用法：WallpaperDebugLog.Write("任意内容")。
/// </summary>
public static class WallpaperDebugLog
{
    private static int s_MainThreadId = -1;

    /// <summary>主线程初始化时调用，记录主线程 ID（用于区分钩子线程）。</summary>
    public static void SetMainThread()
    {
        s_MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
    }

    private static bool IsMainThread
    {
        get { return s_MainThreadId == System.Threading.Thread.CurrentThread.ManagedThreadId; }
    }

    /// <summary>写一行日志到 Unity 日志（仅主线程；非主线程静默丢弃）。</summary>
    public static void Write(string msg)
    {
        try
        {
            if (IsMainThread) UnityEngine.Debug.Log("[Wallpaper] " + msg);
        }
        catch { /* 日志本身不应影响主流程 */ }
    }

    /// <summary>进程退出时调用（保留接口兼容，无操作）。</summary>
    public static void Shutdown() { }
}
