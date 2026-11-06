using System;
using System.IO;
using System.Text;
using UnityEngine;

// ============================================================================
// 每日重启日志
//
// 路径：Application.streamingAssetsPath
//       （打包后 = <exe目录>/<ProductName>_Data/StreamingAssets/，方便直接查看）
// 文件：restart-yyyy-MM-dd.log（按天一个，本地时间）
// 内容：每行一条「yyyy-MM-dd HH:mm:ss | 原因 | 说明」
//
// 记录两类事件：
//   1) 重启原因   —— 旧进程决定拉起新实例时（RelaunchSelf 成功创建启动器之后）
//   2) 新实例启动 —— 每次进程启动时
//
// 特点：跨重启持续追加到当天文件，进程重启不会丢日志。
// ============================================================================
public static class RestartLogger
{
    // 多线程安全：看门狗后台线程 / 窗口线程 / 主线程都会写
    private static readonly object s_Lock = new object();

    /// <summary>
    /// 写一条日志：yyyy-MM-dd HH:mm:ss | 原因 | 说明
    /// </summary>
    public static void Write(string reason, string detail)
    {
        // 编辑器下不写：避免污染 Assets/StreamingAssets
        if (Application.isEditor) return;

        try
        {
            string dir = Application.streamingAssetsPath;
            string file = Path.Combine(dir, "restart-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + " | " + reason
                + " | " + detail
                + Environment.NewLine;

            lock (s_Lock)
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(file, line, new UTF8Encoding(false));
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RestartLogger] 写入失败: " + e.Message);
        }
    }

    /// <summary>
    /// 记录新实例启动（每次进程启动调用一次）。
    /// </summary>
    public static void WriteStartup()
    {
        Write("新实例启动", "PID=" + User32.GetCurrentProcessId());
    }
}
