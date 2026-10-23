using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 极简落盘日志：把诊断信息写到 exe 同目录的 WallpaperDebug.log。
///
/// 为什么需要它：
///   1. 打包是 Release（BuildOptions.None），Player.log 里的 Unity 日志可能不完整；
///   2. 屏幕调试文本在浅色壁纸背景下可能看不清（白字白底）；
///   3. 有了落盘文件，可以【事后】回看点击/命中的完整计数，不依赖截图。
///
/// 为什么用后台写线程：
///   本日志会被【鼠标钩子线程】和【Unity 主线程】同时调用。若在钩子回调里直接
///   UnityEngine.Debug.Log + 文件 IO，可能在 Unity 日志锁内阻塞主线程（鼠标卡顿），
///   或与主线程的写入竞争导致死锁。因此 Write 只做「入队」，由单个后台线程顺序落盘。
///
/// 用法：WallpaperDebugLog.Write("任意内容")，或 WallpaperDebugLog.Diag("文本")。
/// </summary>
public static class WallpaperDebugLog
{
    private static readonly object s_Lock = new object();
    private static readonly Queue<string> s_Queue = new Queue<string>();
    private static string s_Path;
    private static bool s_Opened;
    private static StreamWriter s_Writer;
    private static System.Threading.Thread s_Thread;
    private static volatile bool s_Running = true;

    /// <summary>日志文件路径（exe 同目录）。失败时返回空串。</summary>
    public static string LogPath
    {
        get
        {
            try
            {
                string dataPath = Application.dataPath;          // 打包后 = <exeDir>/<App>_Data
                string exeDir = Path.GetDirectoryName(dataPath);
                if (string.IsNullOrEmpty(exeDir)) exeDir = Directory.GetCurrentDirectory();
                return Path.Combine(exeDir, "WallpaperDebug.log");
            }
            catch { return string.Empty; }
        }
    }

    private static void EnsureOpen()
    {
        if (s_Opened) return;
        s_Opened = true;
        try
        {
            s_Path = LogPath;
            if (string.IsNullOrEmpty(s_Path)) return;
            s_Writer = new StreamWriter(s_Path, false, Encoding.UTF8) { AutoFlush = true };
            StartWriterThread();
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[WallpaperDebugLog] 无法创建日志文件: " + e.Message);
            s_Writer = null;
        }
    }

    private static bool s_WriterStarted;

    private static void StartWriterThread()
    {
        if (s_WriterStarted) return;
        s_WriterStarted = true;
        s_Running = true;
        s_Thread = new System.Threading.Thread(WriterThread);
        s_Thread.IsBackground = true;
        s_Thread.Name = "WallpaperDebugLog";
        s_Thread.Start();
    }

    /// <summary>后台写线程：逐条出队落盘。失败静默，绝不影响主流程。</summary>
    private static void WriterThread()
    {
        while (s_Running)
        {
            string line = null;
            lock (s_Lock)
            {
                if (s_Queue.Count > 0) { line = s_Queue.Dequeue(); }
            }
            if (line == null)
            {
                System.Threading.Thread.Sleep(20);
                continue;
            }
            try { s_Writer?.WriteLine(line); }
            catch { /* 磁盘满/被占用等，静默丢弃 */ }
        }
        // 退出前把残余写完
        try
        {
            string line;
            while (true)
            {
                lock (s_Lock)
                {
                    if (s_Queue.Count == 0) break;
                    line = s_Queue.Dequeue();
                }
                s_Writer?.WriteLine(line);
            }
            s_Writer?.Flush();
            s_Writer?.Dispose();
        }
        catch { }
    }

    private static void Enqueue(string line)
    {
        lock (s_Lock)
        {
            s_Queue.Enqueue(line);
            // 防止异常高频把内存吃光（正常采样频率远低于此上限）
            while (s_Queue.Count > 2000) s_Queue.Dequeue();
            EnsureOpen();
        }
    }

    /// <summary>写一行日志（自动加时间戳）。主线程同时输出到 Unity 日志。</summary>
    public static void Write(string msg)
    {
        try
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
            Enqueue(line);
            // 注意：Unity 的 Debug.Log 只能在主线程调用；钩子线程调用会阻塞主线程（鼠标卡顿）。
            // 因此非主线程只入队，不进 Unity 日志。
            if (IsMainThread) UnityEngine.Debug.Log("[Wallpaper] " + msg);
        }
        catch { /* 日志本身不应影响主流程 */ }
    }

    private static int s_MainThreadId = -1;
    private static bool IsMainThread { get { return s_MainThreadId == System.Threading.Thread.CurrentThread.ManagedThreadId; } }

    /// <summary>在主线程初始化时调用，记录主线程 ID（用于区分钩子线程）。</summary>
    public static void SetMainThread()
    {
        s_MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
    }

    /// <summary>诊断专用：写入诊断计数快照（不进 Unity 日志，避免噪声）。</summary>
    public static void Diag(string msg)
    {
        try { Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + "  [DIAG] " + msg); }
        catch { }
    }

    /// <summary>进程退出时调用：停写线程并冲刷残余日志。</summary>
    public static void Shutdown()
    {
        s_Running = false;
        try { s_Thread?.Join(500); } catch { }
    }
}
