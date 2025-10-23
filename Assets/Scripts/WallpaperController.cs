using AOT;
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Application = UnityEngine.Application;

public class WallpaperController : MonoBehaviour
{
    // ====================================================================================
    // 2. 常量定义
    // ====================================================================================

    // 窗口样式常量
    const int GWL_EXSTYLE = -20;
    const int WS_EX_LAYERED = 0x00080000;      // 分层窗口
    const int WS_EX_NOACTIVATE = 0x08000000;   // 不激活窗口，不接受焦点
    const uint LWA_ALPHA = 0x2;                // 设置透明度

    const int GWL_STYLE = -16;
    const int WS_POPUP = 8388608;

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

    // 新增：用于保持对象引用的 GCHandle
    private GCHandle gcHandle;
    // 新增：静态委托，符合 EnumChildProc 签名
    private static User32.EnumChildProc staticEnumChildProc = StaticEnumChildWindowsCallback;

    private static User32.EnumChildProc staticEnumChildProc2 = StaticEnumWorkerWCallback;

    // ====================================================================================
    // 3. Unity Start 方法 (核心逻辑)
    // ====================================================================================

    int width = 2720;
    int height = 1080;

    public Text text;

    //托盘图标的宽高
    int _width = 50, _height = 50;

    void Start()
    {
        WinScreenInfo.GetVirtualScreenSize(out width, out height);

        UnityEngine.Screen.SetResolution(width, height, false);
        Application.targetFrameRate = 25;
        // 1. 获取 Unity 窗口句柄
        unityWindow = User32.GetActiveWindow();
        Invoke("SetWallPaper", 0.1f);
    }

    void SetWallPaper()
    {
#if UNITY_STANDALONE_WIN

        if (unityWindow == IntPtr.Zero)
        {
            Debug.LogError("无法获取 Unity 窗口句柄");
            return;
        }

        // 临时隐藏窗口，避免闪烁
        User32.ShowWindow(unityWindow, 0);

        IntPtr progman = User32.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            Debug.LogError("未找到 Progman");
            return;
        }

        // 2. 发送消息激活 WorkerW（生成或显示用于放置背景的 WorkerW 窗口）
        // 0x052C 是 WM_SPAWN_WORKERW 消息
        User32.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        // 3. 遍历 Progman 的子窗口，寻找 WorkerW (真正的壁纸容器)
        foundWorkerW = IntPtr.Zero;
        User32.EnumChildWindows(progman, staticEnumChildProc2, IntPtr.Zero);

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
        int exStyle = User32.GetWindowLong(unityWindow, GWL_EXSTYLE); //| WS_EX_LAYERED | WS_EX_NOACTIVATE
        User32.SetWindowLong(unityWindow, GWL_EXSTYLE, exStyle);
        int style = User32.GetWindowLong(unityWindow, GWL_STYLE);
        User32.SetWindowLong(unityWindow, GWL_STYLE, WS_POPUP);
        // 必须设为不透明 (255)，否则可能导致图标背景变成黑色或透明
        User32.SetLayeredWindowAttributes(unityWindow, 0, 255, LWA_ALPHA);

        // 5. 将 Unity 窗口嵌入到 WorkerW/Progman 中
        User32.SetParent(unityWindow, parentWindow);

        // 6. 设置 Unity 窗口大小和 Z-Order
        // 将 Unity 窗口置于 WorkerW 子窗口的 **最底层 (HWND_BOTTOM)**
        // 这样可以确保它在桌面图标（SHELLDLL_DefView）之下。
        User32.SetWindowPos(unityWindow, HWND_BOTTOM, 0, 0, width, height, SWP_NOACTIVATE);

        // 7. 查找 SHELLDLL_DefView（桌面图标容器）
        // 这一步现在是可选的，因为我们依赖 SetParent/HWND_BOTTOM 的组合。
        // 保留此步用于日志和备用 Z-order 提升。

        // 尝试在 Progman 的所有子窗口中查找 DefView
        foundDefView = IntPtr.Zero;
        User32.EnumChildWindows(progman, staticEnumChildProc, IntPtr.Zero);

        // 8. 强制桌面图标回到顶层 (备用步骤，可能需要也可能不需要)
        if (foundDefView != IntPtr.Zero)
        {
            // 将 DefView 窗口提升到最顶层（不改变位置和大小，不激活）
            // 在第 6 步使用 HWND_BOTTOM 成功后，这一步可能不再需要。
            // 但如果图标仍被覆盖，取消注释这一行。
            // SetWindowPos(foundDefView, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            User32.ShowWindow(foundDefView, SW_SHOW);
            Debug.Log("✅ 桌面图标容器已处理。");
        }
        else
        {
            Debug.LogError("❌ 未找到 SHELLDLL_DefView，图标可能被遮挡。");
        }

        // 9. 显示 Unity 壁纸
        User32.ShowWindow(unityWindow, SW_SHOW);

        // 10. 转移焦点（防止 Unity 窗口激活破坏 Z-order）
        IntPtr tray = User32.FindWindow("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero)
        {
            // 再次确保任务栏可见，触发焦点转移
            User32.ShowWindow(tray, SW_SHOW);
        }
#endif
    }

    bool focus = false;
    private void FixedUpdate()
    {
        text.text = "焦点：" + focus;
        if (!focus)
        {
            User32.SetFocus(unityWindow);
        }
        //text.text = "鼠标位置:" + Input.mousePosition;
        if (Input.GetMouseButton(0))
        {
            text.text = "鼠标点击";
        }
    }

    private void OnApplicationFocus(bool focus)
    {
        this.focus = focus;
    }

    [MonoPInvokeCallback(typeof(User32.EnumChildProc))]
    private static bool StaticEnumWorkerWCallback(IntPtr hwnd, IntPtr lParam)
    {
        var className = new StringBuilder(256);
        User32.GetClassName(hwnd, className, className.Capacity);

        // 找到 WorkerW 窗口
        if (className.ToString() == "WorkerW")
        {
            // 记录找到的 WorkerW，继续查找以确保找到最新的 WorkerW
            foundWorkerW = hwnd;
        }
        return true;
    }

    [MonoPInvokeCallback(typeof(User32.EnumChildProc))]
    private static bool StaticEnumChildWindowsCallback(IntPtr hwnd, IntPtr lParam)
    {
        var className = new StringBuilder(256);
        User32.GetClassName(hwnd, className, className.Capacity);
        if (className.ToString() == "SHELLDLL_DefView")
        {
            foundDefView = hwnd;
            return false; // 找到即停
        }
        return true;
    }
}