using AOT;
using System;
using System.Text;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 纯 C# 壁纸控制器（无原生 DLL、无注入 Explorer）。
///
/// 职责：
///   1. 把 Unity 窗口挂接到桌面层（Progman / WorkerW），成为真正的壁纸；
///   2. 在本进程内安装 WH_MOUSE_LL 低层鼠标钩子（OS 直接回调本进程主线程），
///      捕获桌面上的鼠标移动/按键并换算到 Unity 窗口坐标；
///   3. 把钩子事件喂给自定义的 WallpaperInputModule → UGUI，使按钮/拖拽正常响应；
///   4. 托盘菜单 / 退出时解除挂接并卸载钩子。
///
/// 仅“打包后(!UNITY_EDITOR)”才做壁纸与钩子；编辑器中走真实鼠标，便于开发调试。
/// </summary>
public class WallpaperController : MonoBehaviour
{
    // ============================================================================
    // 常量
    // ============================================================================
    const int GWL_EXSTYLE = -20;
    const int GWL_STYLE = -16;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_NOREDIRECTIONBITMAP = 0x02000000;  // raised desktop（Win11 / 较新 Win10）
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_CHILD = unchecked((int)0x40000000);
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const int SW_HIDE = 0;
    const int SW_SHOW = 5;
    const int SW_RESTORE = 9;
    const int WM_SPAWN_WORKERW = 0x052C;   // 0x052C 触发 Explorer 创建 WorkerW 结构
    const int SPAWN_WPARAM = 0x0D;         // 必须 0xD，否则 shell 静默无操作
    const int SPAWN_LPARAM = 0x1;          // 必须 0x1

    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    static readonly IntPtr HWND_TOP = IntPtr.Zero;

    // ============================================================================
    // 字段
    // ============================================================================
    private IntPtr unityWindow = IntPtr.Zero;
    private IntPtr originalParent = IntPtr.Zero;
    private int originalExStyle;
    private int originalStyle;

    private NotifyIconItem m_NotifyIconItem;
    private WallpaperInputModule m_InputModule;
    private WallpaperClickQueue m_Queue = new WallpaperClickQueue();

    // 演示 UI（点击计数 + 状态）
    private Text m_StatusText;
    private Button m_DemoButton;
    private int m_ClickCount;
    private float m_DebugTimer;

    // 钩子（静态，便于回调访问）
    private static WallpaperController s_Instance;
    private static IntPtr s_HookId = IntPtr.Zero;
    private static User32.LowLevelMouseProc s_MouseProc;
    private static bool s_HookEnabled;
    private static IntPtr s_UnityWindowStatic = IntPtr.Zero;
    private static WallpaperClickQueue s_QueueStatic;
    private static User32.RECT s_WindowScreenRect;
    private static bool s_AnyButtonHeld;
    private static int s_FedMoves;   // 调试：已喂给队列的移动计数
    private static int s_FedDowns;   // 调试：已喂给队列的按下计数

    // 钩子运行在独立线程（自建消息泵），避免阻塞 Unity 主线程造成鼠标卡顿
    private System.Threading.Thread m_HookThread;
    private volatile bool m_HookThreadRunning;
    private uint m_HookThreadId;

    // ============================================================================
    // 生命周期
    // ============================================================================
    void Start()
    {
        s_Instance = this;
        s_QueueStatic = m_Queue;

        m_NotifyIconItem = new NotifyIconItem();
        m_NotifyIconItem.Init();
        NotifyIconItem.Controller = this;

        int width, height;
        WinScreenInfo.GetVirtualScreenSize(out width, out height);
        Screen.SetResolution(width, height, false);
        Application.targetFrameRate = 60;

        SetupDemoUI();
        SetupDebugText();

#if !UNITY_EDITOR
        SetupInputModule();
        unityWindow = AcquireUnityWindow();
        if (unityWindow == IntPtr.Zero)
        {
            Debug.LogError("[Wallpaper] 无法获取 Unity 窗口句柄");
        }
        else
        {
            s_UnityWindowStatic = unityWindow;
            Invoke("AttachToDesktop", 0.1f);
        }
#endif
    }

    IntPtr AcquireUnityWindow()
    {
        IntPtr hwnd = User32.FindWindow("UnityWndClass", null);
        if (hwnd == IntPtr.Zero) hwnd = User32.GetActiveWindow();
        if (hwnd == IntPtr.Zero) hwnd = User32.GetWindow(Application.productName);
        return hwnd;
    }

    void SetupInputModule()
    {
        EventSystem es = EventSystem.current ?? FindObjectOfType<EventSystem>();
        if (es == null) { Debug.LogError("[Wallpaper] 场景中缺少 EventSystem"); return; }
        StandaloneInputModule existing = es.GetComponent<StandaloneInputModule>();
        if (existing != null && !(existing is WallpaperInputModule)) Destroy(existing);
        m_InputModule = es.GetComponent<WallpaperInputModule>();
        if (m_InputModule == null) m_InputModule = es.gameObject.AddComponent<WallpaperInputModule>();
        m_InputModule.BindQueue(m_Queue);
    }

    // ============================================================================
    // 桌面挂接
    // ============================================================================
    void AttachToDesktop()
    {
        if (unityWindow == IntPtr.Zero) return;

        originalParent = User32.GetParent(unityWindow);
        originalExStyle = User32.GetWindowLong(unityWindow, GWL_EXSTYLE);
        originalStyle = User32.GetWindowLong(unityWindow, GWL_STYLE);

        User32.ShowWindow(unityWindow, SW_HIDE);

        IntPtr progman = User32.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) { Debug.LogError("[Wallpaper] 未找到 Progman"); return; }

        // Explorer 的 PID = Progman 的 PID（用于过滤「属于 Explorer」的 WorkerW）
        User32.GetWindowThreadProcessId(progman, out uint explorerPid);
        s_ExplorerPid = explorerPid;

        // 1) 发送 0x052C 创建壁纸层 WorkerW。
        //    必须 wParam=0xD、lParam=0x1，否则 shell 静默无操作（壁纸层保持隐藏）。
        SpawnWallpaperWorkerW(progman);

        // 2) 找到壁纸层 WorkerW（可见 + 属于 Explorer + 不含 SHELLDLL_DefView）。
        IntPtr workerW = FindWallpaperWorkerW(progman);
        if (workerW == IntPtr.Zero)
        {
            Debug.LogError("[Wallpaper] 未找到壁纸层 WorkerW，无法挂接");
            User32.ShowWindow(unityWindow, SW_SHOW);
            return;
        }

        // 3) 关键：先改样式（清 WS_POPUP、置 WS_CHILD）再 SetParent。
        //    跳过这步会停在「半挂载」状态：GetParent() 返回 NULL，z 序与坐标全乱。
        int style = User32.GetWindowLong(unityWindow, GWL_STYLE);
        style &= ~WS_POPUP;
        style |= WS_CHILD;
        User32.SetWindowLong(unityWindow, GWL_STYLE, style);

        int ex = User32.GetWindowLong(unityWindow, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE;
        User32.SetWindowLong(unityWindow, GWL_EXSTYLE, ex);

        // 4) 挂到壁纸层 WorkerW（该层在图标之下、默认壁纸图之上）。
        User32.SetParent(unityWindow, workerW);
        // 5) 沉底 + 铺满全屏
        User32.SetWindowPos(unityWindow, HWND_BOTTOM, 0, 0, Screen.width, Screen.height, SWP_NOACTIVATE);
        User32.ShowWindow(unityWindow, SW_SHOW);

        // 缓存窗口屏幕矩形（用于钩子精确命中判断）
        User32.GetWindowRect(unityWindow, out s_WindowScreenRect);

        InstallMouseHook();
        Debug.Log("[Wallpaper] 已挂接为桌面壁纸（父窗口=壁纸层 WorkerW，Explorer PID=" + explorerPid + "）");
    }

    // 壁纸层 WorkerW 搜索结果（静态，供枚举回调使用）
    private static IntPtr s_FoundWorkerW;
    private static uint s_ExplorerPid;

    /// <summary>
    /// 发送 0x052C 创建壁纸层 WorkerW，轮询重试直到它真正出现。
    /// 必须 wParam=0xD、lParam=0x1（写 (0,0) 时 shell 静默无操作）。
    /// </summary>
    void SpawnWallpaperWorkerW(IntPtr progman)
    {
        for (int i = 0; i < 10; i++)
        {
            User32.SendMessageTimeout(progman, WM_SPAWN_WORKERW,
                new IntPtr(SPAWN_WPARAM), new IntPtr(SPAWN_LPARAM),
                User32.SMTO_FLAGS, 1000, out _);
            System.Threading.Thread.Sleep(120);
            if (FindWallpaperWorkerW(progman) != IntPtr.Zero) break;   // 已就绪
        }
    }

    /// <summary>
    /// 壁纸层 WorkerW 的判定：类名 WorkerW + 可见 + 属于 Explorer + 不含 SHELLDLL_DefView。
    /// 位置随 shell 版本变化（顶层 WorkerW / Progman 直接子窗口），两处都要找。
    /// </summary>
    bool IsWallpaperWorkerW(IntPtr hwnd)
    {
        if (!IsWindowClass(hwnd, "WorkerW")) return false;
        if (!User32.IsWindowVisible(hwnd)) return false;                 // 父窗口隐藏时子窗口 WS_VISIBLE 仍为 TRUE
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != s_ExplorerPid) return false;                          // 类名可重名，必须验进程归属
        if (User32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return false; // 含 DefView 的是图标宿主
        return true;
    }

    [MonoPInvokeCallback(typeof(User32.EnumWindowsProc))]
    private static bool EnumWorkerWProc(IntPtr hWnd, IntPtr lParam)
    {
        if (s_Instance != null && s_Instance.IsWallpaperWorkerW(hWnd)) { s_FoundWorkerW = hWnd; return false; }
        return true;
    }

    [MonoPInvokeCallback(typeof(User32.EnumChildProc))]
    private static bool EnumProgmanChildProc(IntPtr hWnd, IntPtr lParam)
    {
        if (s_Instance != null && s_Instance.IsWallpaperWorkerW(hWnd)) { s_FoundWorkerW = hWnd; return false; }
        return true;
    }

    IntPtr FindWallpaperWorkerW(IntPtr progman)
    {
        s_FoundWorkerW = IntPtr.Zero;
        // 1) 顶层 WorkerW（Win10 / 常规）
        User32.EnumWindows(new User32.EnumWindowsProc(EnumWorkerWProc), IntPtr.Zero);
        if (s_FoundWorkerW != IntPtr.Zero) { Debug.Log("[Wallpaper] 已定位壁纸层 WorkerW（顶层）"); return s_FoundWorkerW; }
        // 2) Progman 的直接子窗口（Win11 raised desktop）
        User32.EnumChildWindows(progman, new User32.EnumChildProc(EnumProgmanChildProc), IntPtr.Zero);
        if (s_FoundWorkerW != IntPtr.Zero) { Debug.Log("[Wallpaper] 已定位壁纸层 WorkerW（Progman 子窗口）"); return s_FoundWorkerW; }
        return IntPtr.Zero;
    }

    // ============================================================================
    // 低层鼠标钩子（进程内）
    // ============================================================================
    void InstallMouseHook()
    {
        if (s_HookId != IntPtr.Zero) return;
        s_MouseProc = MouseHookCallback;
        m_HookThreadRunning = true;
        m_HookThread = new System.Threading.Thread(HookThreadProc);
        m_HookThread.IsBackground = true;
        m_HookThread.Name = "WallpaperMouseHook";
        m_HookThread.Start();
    }

    /// <summary>
    /// 钩子线程：在本线程安装 WH_MOUSE_LL 并自建消息泵。
    /// 这样钩子回调运行在独立线程，不再阻塞 Unity 主线程，从根本上消除鼠标卡顿。
    /// </summary>
    void HookThreadProc()
    {
        m_HookThreadId = User32.GetCurrentThreadId();
        IntPtr hMod = User32.GetModuleHandle(null);  // 钩子过程在本 exe 内
        s_HookId = User32.SetWindowsHookEx(User32.WH_MOUSE_LL, s_MouseProc, hMod, 0);
        if (s_HookId == IntPtr.Zero)
        {
            Debug.LogError("[Wallpaper] 安装鼠标钩子失败，错误码 " + Marshal.GetLastWin32Error());
            m_HookThreadRunning = false;
            return;
        }
        s_HookEnabled = true;
        Debug.Log("[Wallpaper] 鼠标钩子已安装 (WH_MOUSE_LL, 独立线程)");

        User32.MSG msg;
        while (m_HookThreadRunning)
        {
            if (!User32.GetMessage(out msg, IntPtr.Zero, 0, 0)) break; // 收到 WM_QUIT → 退出
            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }
        User32.UnhookWindowsHookEx(s_HookId);
        s_HookId = IntPtr.Zero;
        s_HookEnabled = false;
    }

    void UninstallMouseHook()
    {
        s_HookEnabled = false;
        m_HookThreadRunning = false;
        if (m_HookThreadId != 0)
            User32.PostThreadMessage(m_HookThreadId, User32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (s_HookId != IntPtr.Zero)
        {
            User32.UnhookWindowsHookEx(s_HookId);
            s_HookId = IntPtr.Zero;
        }
    }

    [MonoPInvokeCallback(typeof(User32.LowLevelMouseProc))]
    private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && s_HookEnabled && s_UnityWindowStatic != IntPtr.Zero && s_QueueStatic != null)
        {
            var info = (User32.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(User32.MSLLHOOKSTRUCT));
            int msg = wParam.ToInt32();
            User32.POINT screen = info.pt;

            // 精确判定：鼠标当前落点的最顶层窗口是否属于桌面/壁纸层。
            // 仅当落在桌面(Progman/WorkerW/SHELLDLL_DefView/SysListView32)或本窗口时才喂事件；
            // 落到其它程序(如浏览器)时不喂，避免误触壁纸按钮。
            IntPtr hit = User32.WindowFromPoint(screen);
            bool desktopish = hit == s_UnityWindowStatic
                || IsWindowClass(hit, "Progman") || IsWindowClass(hit, "WorkerW")
                || IsWindowClass(hit, "SHELLDLL_DefView") || IsWindowClass(hit, "SysListView32");
            if (!desktopish)
            {
                if (s_AnyButtonHeld) { s_QueueStatic.EnqueueForceRelease(); s_AnyButtonHeld = false; }
            }
            else
            {
                User32.RECT client;
                User32.GetClientRect(s_UnityWindowStatic, out client);
                User32.POINT cp = screen;
                User32.ScreenToClient(s_UnityWindowStatic, ref cp);
                if (cp.X >= 0 && cp.Y >= 0 && cp.X <= client.right && cp.Y <= client.bottom)
                {
                    double nx = (double)cp.X / (client.right == 0 ? 1 : client.right);
                    double ny = 1.0 - (double)cp.Y / (client.bottom == 0 ? 1 : client.bottom); // 翻转为 Y 向上
                    switch (msg)
                    {
                        case User32.WM_MOUSEMOVE:
                            s_QueueStatic.EnqueueMove((float)nx, (float)ny); s_FedMoves++; break;
                        case User32.WM_LBUTTONDOWN:
                            s_QueueStatic.EnqueueMove((float)nx, (float)ny);
                            s_QueueStatic.EnqueueButton(0, true); s_AnyButtonHeld = true; s_FedDowns++; break;
                        case User32.WM_LBUTTONUP:
                            s_QueueStatic.EnqueueButton(0, false); s_AnyButtonHeld = false; break;
                        case User32.WM_RBUTTONDOWN:
                            s_QueueStatic.EnqueueMove((float)nx, (float)ny);
                            s_QueueStatic.EnqueueButton(1, true); s_AnyButtonHeld = true; break;
                        case User32.WM_RBUTTONUP:
                            s_QueueStatic.EnqueueButton(1, false); s_AnyButtonHeld = false; break;
                        case User32.WM_MBUTTONDOWN:
                            s_QueueStatic.EnqueueMove((float)nx, (float)ny);
                            s_QueueStatic.EnqueueButton(2, true); s_AnyButtonHeld = true; break;
                        case User32.WM_MBUTTONUP:
                            s_QueueStatic.EnqueueButton(2, false); s_AnyButtonHeld = false; break;
                        case User32.WM_MOUSEWHEEL:
                            s_QueueStatic.EnqueueWheel((short)((info.mouseData >> 16) & 0xFFFF) / 120f); break;
                        case User32.WM_MOUSEHWHEEL:
                            s_QueueStatic.EnqueueWheelH((short)((info.mouseData >> 16) & 0xFFFF) / 120f); break;
                    }
                }
                else if (s_AnyButtonHeld) { s_QueueStatic.EnqueueForceRelease(); s_AnyButtonHeld = false; }
            }
        }
        return User32.CallNextHookEx(s_HookId, nCode, wParam, lParam);
    }

    private static bool IsWindowClass(IntPtr hwnd, string cls)
    {
        var sb = new StringBuilder(256);
        User32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString() == cls;
    }

    // ============================================================================
    // 恢复 / 退出
    // ============================================================================
    public void RestoreToWindow()
    {
        UninstallMouseHook();
#if UNITY_STANDALONE_WIN
        if (unityWindow != IntPtr.Zero)
        {
            User32.ShowWindow(unityWindow, SW_HIDE);          // 隐藏后改样式，避免残影
            User32.SetParent(unityWindow, IntPtr.Zero);
            User32.SetWindowLong(unityWindow, GWL_EXSTYLE, originalExStyle);
            User32.SetWindowLong(unityWindow, GWL_STYLE, originalStyle);   // 清 WS_CHILD，还原 WS_POPUP
            User32.SetWindowPos(unityWindow, HWND_TOP, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            User32.ShowWindow(unityWindow, SW_SHOW);
            User32.SetForegroundWindow(unityWindow);
        }
#endif
        Debug.Log("[Wallpaper] 已恢复为普通窗口");
    }

    public void QuitApp()
    {
        RestoreToWindow();
        Application.Quit(0);
    }

    void OnApplicationQuit()
    {
        UninstallMouseHook();
        if (unityWindow != IntPtr.Zero)
        {
            User32.ShowWindow(unityWindow, SW_HIDE);
            User32.SetParent(unityWindow, IntPtr.Zero);
            User32.SetWindowLong(unityWindow, GWL_EXSTYLE, originalExStyle);
            User32.SetWindowLong(unityWindow, GWL_STYLE, originalStyle);
        }
    }

    void OnDestroy()
    {
        UninstallMouseHook();
        if (m_NotifyIconItem != null) m_NotifyIconItem.Dispose();
    }

    // ============================================================================
    // 演示 UI（点击计数 + 状态文本）—— 用于直观验证“按钮点击正常”
    // ============================================================================
    void SetupDemoUI()
    {
        GameObject btn = GameObject.Find("Button");
        if (btn != null)
        {
            m_DemoButton = btn.GetComponent<Button>();
            if (m_DemoButton != null)
                m_DemoButton.onClick.AddListener(OnDemoButtonClicked);
        }
    }

    void OnDemoButtonClicked()
    {
        m_ClickCount++;
        Debug.Log("[Wallpaper] 按钮被点击！计数 = " + m_ClickCount);
    }

    void SetupDebugText()
    {
        Canvas canvas = FindObjectOfType<Canvas>();
        if (canvas == null) return;
        GameObject go = new GameObject("WallpaperDebugText");
        go.transform.SetParent(canvas.transform, false);
        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(12f, -12f);
        rt.sizeDelta = new Vector2(420f, 110f);
        Text t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = 22;
        t.color = Color.white;
        t.text = "壁纸就绪";
        m_StatusText = t;
    }

    void Update()
    {
        // 周期刷新窗口屏幕矩形（多屏/分辨率变化也能正确命中）
        m_DebugTimer += Time.deltaTime;
        if (m_DebugTimer >= 0.5f)
        {
            m_DebugTimer = 0f;
            if (unityWindow != IntPtr.Zero) User32.GetWindowRect(unityWindow, out s_WindowScreenRect);
            if (m_StatusText != null)
            {
                string hook = (s_HookId != IntPtr.Zero) ? "鼠标钩子: 已启用" : "鼠标钩子: 未启用";
                m_StatusText.text = hook
                    + "\n按钮点击次数: " + m_ClickCount
                    + "\n已喂事件 移动:" + s_FedMoves + " 按下:" + s_FedDowns
                    + "\n队列剩余: " + m_Queue.Count;
            }
        }
    }
}
