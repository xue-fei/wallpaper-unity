using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

public class WallpaperController : MonoBehaviour
{
    // ====================================================================================
    // 1. P/Invoke 声明 (Windows API)
    // ====================================================================================

    // 查找窗口
    [DllImport("user32.dll")]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    // 查找子窗口
    [DllImport("user32.dll")]
    static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowTitle);

    // 发送带有超时的消息
    [DllImport("user32.dll")]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint timeout, out IntPtr lpdwResult);

    // 设置父窗口
    [DllImport("user32.dll")]
    static extern bool SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    // 设置窗口位置和大小
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // 显示/隐藏窗口
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // 获取/设置窗口样式
    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // 设置分层窗口属性
    [DllImport("user32.dll")]
    static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    // 枚举子窗口
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    // 获取窗口类名
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // 获取当前活动窗口句柄
    [DllImport("user32.dll")]
    static extern IntPtr GetActiveWindow();

    // EnumChildWindows 的回调委托
    delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    // ====================================================================================
    // 2. 常量定义
    // ====================================================================================

    // 窗口样式常量
    const int GWL_EXSTYLE = -20;
    const int WS_EX_LAYERED = 0x00080000;      // 分层窗口
    const int WS_EX_NOACTIVATE = 0x08000000;   // 不激活窗口，不接受焦点
    const uint LWA_ALPHA = 0x2;                // 设置透明度

    // Z-Order 常量
    static readonly IntPtr HWND_TOP = new IntPtr(0);
    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

    // SetWindowPos 标志
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;

    // ShowWindow 常量
    const int SW_SHOW = 5; // 显示窗口

    // 内部状态
    private IntPtr unityWindow = IntPtr.Zero;
    private static IntPtr foundWorkerW = IntPtr.Zero;
    private static IntPtr foundDefView = IntPtr.Zero; // 桌面图标容器

    // ====================================================================================
    // 3. Unity Start 方法 (核心逻辑)
    // ====================================================================================

    void Start()
    {
#if UNITY_STANDALONE_WIN
        // 1. 获取 Unity 窗口句柄
        unityWindow = GetActiveWindow();
        if (unityWindow == IntPtr.Zero)
        {
            Debug.LogError("无法获取 Unity 窗口句柄");
            return;
        }

        // 临时隐藏窗口，避免闪烁
        ShowWindow(unityWindow, 0);

        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            Debug.LogError("未找到 Progman");
            return;
        }

        // 2. 发送消息激活 WorkerW（生成或显示用于放置背景的 WorkerW 窗口）
        // 0x052C 是 WM_SPAWN_WORKERW 消息
        SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        // 3. 遍历 Progman 的子窗口，寻找 WorkerW (真正的壁纸容器)
        foundWorkerW = IntPtr.Zero;
        EnumChildWindows(progman, (hwnd, param) =>
        {
            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);

            // 找到 WorkerW 窗口
            if (className.ToString() == "WorkerW")
            {
                // 记录找到的 WorkerW，继续查找以确保找到最新的 WorkerW
                foundWorkerW = hwnd;
            }
            return true;
        }, IntPtr.Zero);

        IntPtr parentWindow = foundWorkerW != IntPtr.Zero ? foundWorkerW : progman;

        if (foundWorkerW == IntPtr.Zero)
        {
            Debug.LogWarning("未找到 WorkerW，回退使用 Progman 作为父窗口。");
        }
        else
        {
            Debug.Log("✅ 找到 WorkerW 作为父窗口。");
        }


        // 4. 设置窗口样式：分层 + 不激活
        // WS_EX_NOACTIVATE 确保 Unity 窗口不会抢夺焦点，这是避免 Z-order 混乱的关键。
        int exStyle = GetWindowLong(unityWindow, GWL_EXSTYLE);
        SetWindowLong(unityWindow, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_NOACTIVATE);
        // 必须设为不透明 (255)，否则可能导致图标背景变成黑色或透明
        SetLayeredWindowAttributes(unityWindow, 0, 255, LWA_ALPHA);

        // 5. 将 Unity 窗口嵌入到 WorkerW/Progman 中
        SetParent(unityWindow, parentWindow);

        // 6. 设置 Unity 窗口大小和 Z-Order
        // 将 Unity 窗口置于 WorkerW 子窗口的 **最底层 (HWND_BOTTOM)**
        // 这样可以确保它在桌面图标（SHELLDLL_DefView）之下。
        SetWindowPos(unityWindow, HWND_BOTTOM, 0, 0, Screen.width, Screen.height, SWP_NOACTIVATE);

        // 7. 查找 SHELLDLL_DefView（桌面图标容器）
        // 这一步现在是可选的，因为我们依赖 SetParent/HWND_BOTTOM 的组合。
        // 保留此步用于日志和备用 Z-order 提升。

        // 尝试在 Progman 的所有子窗口中查找 DefView
        foundDefView = IntPtr.Zero;
        EnumChildWindows(progman, (hwnd, param) =>
        {
            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() == "SHELLDLL_DefView")
            {
                foundDefView = hwnd;
                return false; // 找到即停
            }
            return true;
        }, IntPtr.Zero);

        // 8. 强制桌面图标回到顶层 (备用步骤，可能需要也可能不需要)
        if (foundDefView != IntPtr.Zero)
        {
            // 将 DefView 窗口提升到最顶层（不改变位置和大小，不激活）
            // 在第 6 步使用 HWND_BOTTOM 成功后，这一步可能不再需要。
            // 但如果图标仍被覆盖，取消注释这一行。
            // SetWindowPos(foundDefView, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            ShowWindow(foundDefView, SW_SHOW);
            Debug.Log("✅ 桌面图标容器已处理。");
        }
        else
        {
            Debug.LogError("❌ 未找到 SHELLDLL_DefView，图标可能被遮挡。");
        }

        // 9. 显示 Unity 壁纸
        ShowWindow(unityWindow, SW_SHOW);

        // 10. 转移焦点（防止 Unity 窗口激活破坏 Z-order）
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
        {
            // 再次确保任务栏可见，触发焦点转移
            ShowWindow(tray, SW_SHOW);
        }
#endif
    }

    // 移除手动提升图标的 Update 逻辑（因为 Start 中已处理）
}