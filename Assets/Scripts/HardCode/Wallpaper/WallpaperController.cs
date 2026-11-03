using AOT;
using System;
using System.Text;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;
using System.Threading;

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
    // 需要清除的非客户区样式（否则子窗口仍保留标题栏/边框，客户区比窗口小几像素 → 边缘漏桌面）
    const int WS_CAPTION = 0x00C00000;        // 标题栏（含菜单）
    const int WS_THICKFRAME = 0x00040000;     // 可调整大小的粗边框
    const int WS_BORDER = 0x00800000;         // 细边框
    const int WS_SYSMENU = 0x00080000;        // 标题栏系统菜单
    const int WS_MINIMIZEBOX = 0x00020000;    // 最小化按钮
    const int WS_MAXIMIZEBOX = 0x00010000;    // 最大化按钮
    const int WS_EX_CLIENTEDGE = 0x00000200;  // 凹陷客户区边缘
    const int WS_EX_WINDOWEDGE = 0x00000100;  // 凸起窗口边缘
    const int WS_EX_STATICEDGE = 0x00020000;  // 静态边缘
    const int WS_EX_DLGMODALFRAME = 0x00000001; // 对话框模态边框
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_FRAMECHANGED = 0x0020;     // 强制重算非客户区（使样式变更立即生效）
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

    // 演示/调试 UI 已抽离到 WallpaperDemoUI（UI 层），本类只保留壁纸引擎职责

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

    // 退出诊断：心跳计时器（每 60 秒输出一次运行状态）
    private float m_HeartbeatTimer;
    // 退出诊断：上一次心跳时的窗口可见性/父窗口，用于状态突变时立即输出
    private bool m_LastHeartbeatVisible;
    private IntPtr m_LastHeartbeatParent = IntPtr.Zero;
    // 退出诊断：暂停状态（Unity 无 Application.isPaused 静态属性，只能由回调记录）
    private bool m_IsPaused;

    // 存活守护：用户主动退出 / 系统关机 时不自动重启
    public bool UserRequestedQuit { get; private set; }
    private static bool s_SystemShutdown;
    public bool IsSystemShuttingDown => s_SystemShutdown;

    // 存活守护：后台线程等待 Explorer 进程句柄，Explorer 重启/崩溃时自动拉起新实例。
    // 【重要】Explorer 重启会销毁 WorkerW 子树，而 DestroyWindow 会【递归销毁子窗口】，
    // 所以本进程的 Unity 窗口（WorkerW 的子窗口）也会被一并销毁 —— 实测 isWindow=False。
    // 因此无法在原进程内"重新挂接"（窗口已不存在），只能重启进程。
    private System.Threading.Thread m_WatchdogThread;
    private volatile bool m_WatchdogRunning;
    private volatile bool m_SelfTerminate;
    // 用户主动退出（托盘菜单）：不调用 Application.Quit()（在 TrackPopupMenuEx 模态消息循环里会卡死），
    // 改为设置本标志，由 Update() 在主循环里执行清理并硬终止。
    private volatile bool m_QuitRequested;

    // 原子标记：保证“只重启一次”，防止看门狗线程与 WM_DESTROY 路径重复拉起新实例
    private static int s_RelaunchedFlag;
    public static bool TryMarkRelaunched()
    {
        return System.Threading.Interlocked.CompareExchange(ref s_RelaunchedFlag, 1, 0) == 0;
    }

    /// <summary>
    /// 请求本进程在创建独立启动器后自行退出。
    /// 【为什么必须退出】新实例在本进程仍存活时无法完成 Unity 引擎初始化（会被完全阻塞），
    /// 所以重启时序必须是「旧进程先退出 → 启动器再拉起新实例」。
    /// Update() 检测到本标志后会直接 TerminateProcess 硬终止（窗口已销毁，Application.Quit 会卡死）。
    /// </summary>
    public void RequestSelfTerminate()
    {
        if (!m_SelfTerminate)
        {
            Debug.Log("[ExitTrace] 收到自退出请求：独立启动器已就位，本进程将退出");
            m_SelfTerminate = true;
        }
    }

    // ============================================================================
    // 生命周期
    // ============================================================================
    void Start()
    {
        s_Instance = this;
        s_QueueStatic = m_Queue;

        // 关闭 Debug.Log 的脚本堆栈输出（默认 ScriptOnly，每行日志都会附带 3 行堆栈，
        // 22 分钟心跳就能写 34KB）。仅影响 LogType.Log；Warning/Error 仍保留堆栈便于排查。
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);

        try
        {
            m_NotifyIconItem = new NotifyIconItem();
            m_NotifyIconItem.Init();
            NotifyIconItem.Controller = this;
        }
        catch (Exception e)
        {
            Debug.Log("警告: 托盘初始化失败 " + e.Message);
        }

        // 各步骤独立容错：任何一步异常都【不能】中断后续挂载。
        // 曾经因为 SetupDebugText 抛异常 → Start 中断 → AttachToDesktop 从未执行，
        // 但 Screen.SetResolution 已生效，窗口变成全屏普通顶层窗口盖住了桌面图标。
        try
        {
            int width, height;
            User32.GetVirtualScreenSize(out width, out height);
            Screen.SetResolution(width, height, false);
            // vSyncCount > 0 时 Unity 会完全忽略 targetFrameRate，
            // 必须显式清零，否则 144Hz 屏上会跑 72fps（vSync=2），GPU 白烧 2.4 倍。
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 30;
        }
        catch (Exception e)
        {
            Debug.Log("警告: 分辨率设置失败 " + e.Message);
        }

        Debug.Log("WallpaperController 启动 Unity=" + Application.unityVersion
            + " 分辨率=" + Screen.width + "x" + Screen.height
            + " dataPath=" + Application.dataPath);

        if (Application.platform != RuntimePlatform.WindowsEditor)
        {
            // 输入模块 + 挂接：失败也不能让窗口停留在「全屏普通顶层窗口」状态
            try
            {
                SetupInputModule();
            }
            catch (Exception e)
            {
                Debug.Log("错误: 输入模块装配异常 " + e.Message);
            }

            try
            {
                unityWindow = AcquireUnityWindow();
            }
            catch (Exception e)
            {
                Debug.Log("错误: 获取窗口句柄异常 " + e.Message);
            }

            if (unityWindow == IntPtr.Zero)
            {
                Debug.Log("错误: 无法获取 Unity 窗口句柄，保持窗口模式");
            }
            else
            {
                s_UnityWindowStatic = unityWindow;
                Invoke("AttachToDesktop", 0.1f);
            }
        }

        Debug.Log("[ExitTrace] Start 完成，进入正常运行（心跳每 60 秒一次，窗口状态变化时立即输出）");
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
        if (es == null)
        {
            Debug.Log("错误: 场景中缺少 EventSystem"); return;
        }
        StandaloneInputModule existing = es.GetComponent<StandaloneInputModule>();
        if (existing != null && !(existing is WallpaperInputModule))
        {
            Destroy(existing);
        }
        m_InputModule = es.GetComponent<WallpaperInputModule>();
        if (m_InputModule == null)
        {
            m_InputModule = es.gameObject.AddComponent<WallpaperInputModule>();
        }
        m_InputModule.BindQueue(m_Queue);
        es.UpdateModules();   // 必须：把新模块注册进 EventSystem 的模块列表，否则 Process() 不会被调用
        Debug.Log("输入模块已装配: " + (m_InputModule != null ? m_InputModule.GetType().Name : "null"));
    }

    // ============================================================================
    // 对外只读计数（供 WallpaperDemoUI 显示，不暴露内部可变状态）
    // ============================================================================
    public static bool IsHookInstalled()
    {
        return s_HookId != IntPtr.Zero;
    }

    public static int FedMoves
    {
        get { return s_FedMoves; }
    }

    public static int FedDowns
    {
        get { return s_FedDowns; }
    }

    public int InputQueueCount
    {
        get { return (m_Queue != null) ? m_Queue.Count : 0; }
    }

    // ============================================================================
    // 桌面挂接
    // ============================================================================
    void AttachToDesktop()
    {
        // 整体容错：挂接过程中任何异常都绝不能让窗口停留在
        // 「全屏普通顶层窗口」状态（那会盖住桌面图标，正是本轮事故现象）。
        try
        {
            AttachToDesktopCore();
        }
        catch (Exception e)
        {
            Debug.Log("错误: 挂接桌面异常 " + e.GetType().Name + " " + e.Message);
            Debug.LogError("[Wallpaper] 挂接桌面异常 " + e.Message);
            try
            {
                RestoreToWindow();
            }
            catch
            {
            }
        }
    }

    void AttachToDesktopCore()
    {
        if (unityWindow == IntPtr.Zero)
        {
            return;
        }
        originalParent = User32.GetParent(unityWindow);
        originalExStyle = User32.GetWindowLong(unityWindow, GWL_EXSTYLE);
        originalStyle = User32.GetWindowLong(unityWindow, GWL_STYLE);

        User32.ShowWindow(unityWindow, SW_HIDE);

        IntPtr progman = User32.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            Debug.Log("错误: 未找到 Progman"); User32.ShowWindow(unityWindow, SW_SHOW); return;
        }

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
            // 自愈：Explorer 有时会延迟创建壁纸层（图标层刚建立时），重试 2 轮再放弃
            for (int retry = 0; retry < 2 && workerW == IntPtr.Zero; retry++)
            {
                Debug.Log("未找到壁纸层 WorkerW，第 " + (retry + 1) + " 次重试 Spawn 0x052C");
                Thread.Sleep(400);
                SpawnWallpaperWorkerW(progman);
                workerW = FindWallpaperWorkerW(progman);
            }
        }
        if (workerW == IntPtr.Zero)
        {
            Debug.Log("未找到壁纸层 WorkerW，无法挂接 → 回退普通窗口");
            Debug.LogError("[Wallpaper] 未找到壁纸层 WorkerW，无法挂接");
            RestoreToWindow();
            return;
        }

        // 3) 关键：先改样式（清 WS_POPUP、置 WS_CHILD）再 SetParent。
        //    跳过这步会停在「半挂载」状态：GetParent() 返回 NULL，z 序与坐标全乱。
        int style = User32.GetWindowLong(unityWindow, GWL_STYLE);
        style &= ~WS_POPUP;
        style |= WS_CHILD;
        // 关键：清掉所有非客户区样式（标题栏/边框/系统菜单/最大最小化按钮）。
        // 否则 WS_CHILD 子窗口仍保留 1px 边框，客户区比窗口小 2px，
        // 壁纸右下边缘露出桌面（实测 1918x1078 vs 真实 1920x1080，差 2px）。
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_SYSMENU
                   | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        User32.SetWindowLong(unityWindow, GWL_STYLE, style);

        int ex = User32.GetWindowLong(unityWindow, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE;
        // 清掉扩展边框样式（客户区/窗口/静态/对话框模态边缘）
        ex &= ~(WS_EX_CLIENTEDGE | WS_EX_WINDOWEDGE | WS_EX_STATICEDGE | WS_EX_DLGMODALFRAME);
        User32.SetWindowLong(unityWindow, GWL_EXSTYLE, ex);

        // 4) 挂到壁纸层 WorkerW（该层在图标之下、默认壁纸图之上）。
        bool setParentOk = User32.SetParent(unityWindow, workerW);
        Debug.Log("[Reattach] SetParent(" + unityWindow + ", " + workerW + ") 返回 "
            + setParentOk + " err=" + Marshal.GetLastWin32Error()
            + " IsWindow(workerW)=" + User32.IsWindow(workerW)
            + " GetParent=" + User32.GetParent(unityWindow)
            + " style=0x" + User32.GetWindowLong(unityWindow, GWL_STYLE).ToString("X8"));

        // 铺满目标取自壁纸层 WorkerW 的客户区尺寸（精确等于桌面覆盖区域，
        // 绕开分辨率/DPI 误差，单屏多屏都准确）。
        User32.RECT parentClient;
        User32.GetClientRect(workerW, out parentClient);
        int fillW = parentClient.right - parentClient.left;
        int fillH = parentClient.bottom - parentClient.top;
        if (fillW <= 0 || fillH <= 0)
        {
            fillW = Screen.width; fillH = Screen.height;
        }

        // 5) 沉底 + 铺满全屏
        //    SWP_FRAMECHANGED 让上面清掉的边框/标题栏立即重算，客户区精确铺满。
        User32.SetWindowPos(unityWindow, HWND_BOTTOM, 0, 0, fillW, fillH,
            SWP_NOACTIVATE | SWP_FRAMECHANGED);
        // 保险：读取真实客户区，若仍有残余边框导致偏小，按差值放大窗口，杜绝边缘漏桌面
        EnsureClientSize(unityWindow, fillW, fillH);
        User32.ShowWindow(unityWindow, SW_SHOW);

        // 缓存窗口屏幕矩形（用于钩子精确命中判断）
        User32.GetWindowRect(unityWindow, out s_WindowScreenRect);

        InstallMouseHook();
        Debug.Log("已挂接为桌面壁纸（父窗口=壁纸层 WorkerW，Explorer PID=" + explorerPid + "）"
            + " 窗口=" + unityWindow + " 屏幕矩形=" + s_WindowScreenRect.left + "," + s_WindowScreenRect.top
            + "," + s_WindowScreenRect.right + "," + s_WindowScreenRect.bottom
            + " Screen=" + Screen.width + "x" + Screen.height + " dpi=" + Screen.dpi);

        // 挂接成功后启动 Explorer 存活守护：Explorer 重启会销毁 WorkerW 子树，
        // 本进程随之退出，守护线程检测到 Explorer 退出即拉起新实例重新挂接。
        StartExplorerWatchdog();
    }

    // 壁纸层 WorkerW 搜索结果（静态，供枚举回调使用）
    private static IntPtr s_FoundWorkerW;
    private static uint s_ExplorerPid;

    /// <summary>
    /// 确保子窗口客户区精确等于目标尺寸。清除边框后若仍有隐藏非客户区导致客户区偏小，
    /// 按（目标 - 实际）差值放大窗口外层尺寸，确保壁纸铺满全屏、右下边缘不露桌面。
    /// 客户区偏大（理论上不会）则不处理。
    /// </summary>
    void EnsureClientSize(IntPtr hwnd, int targetW, int targetH)
    {
        User32.RECT client;
        User32.GetClientRect(hwnd, out client);
        int cw = client.right - client.left;
        int ch = client.bottom - client.top;
        int dw = targetW - cw;
        int dh = targetH - ch;
        if (dw <= 0 && dh <= 0) return;   // 已铺满，无需补偿
        User32.RECT win;
        User32.GetWindowRect(hwnd, out win);
        int ww = (win.right - win.left) + dw;
        int wh = (win.bottom - win.top) + dh;
        User32.SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, ww, wh, SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

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
        if (s_Instance != null && s_Instance.IsWallpaperWorkerW(hWnd))
        {
            s_FoundWorkerW = hWnd; return false;
        }
        return true;
    }

    [MonoPInvokeCallback(typeof(User32.EnumChildProc))]
    private static bool EnumProgmanChildProc(IntPtr hWnd, IntPtr lParam)
    {
        if (s_Instance != null && s_Instance.IsWallpaperWorkerW(hWnd))
        {
            s_FoundWorkerW = hWnd; return false;
        }
        return true;
    }

    IntPtr FindWallpaperWorkerW(IntPtr progman)
    {
        s_FoundWorkerW = IntPtr.Zero;
        // 1) 顶层 WorkerW（Win10 / 常规）
        User32.EnumWindows(new User32.EnumWindowsProc(EnumWorkerWProc), IntPtr.Zero);
        if (s_FoundWorkerW != IntPtr.Zero)
        {
            return s_FoundWorkerW;
        }
        // 2) Progman 的直接子窗口（Win11 raised desktop）
        User32.EnumChildWindows(progman, new User32.EnumChildProc(EnumProgmanChildProc), IntPtr.Zero);
        if (s_FoundWorkerW != IntPtr.Zero)
        {
            return s_FoundWorkerW;
        }
        return IntPtr.Zero;
    }

    // ============================================================================
    // 低层鼠标钩子（进程内）
    // ============================================================================
    void InstallMouseHook()
    {
        if (s_HookId != IntPtr.Zero)
        {
            return;
        }
        s_MouseProc = MouseHookCallback;
        m_HookThreadRunning = true;
        m_HookThread = new Thread(HookThreadProc);
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
            Debug.Log("错误: 安装鼠标钩子失败，错误码 " + Marshal.GetLastWin32Error());
            m_HookThreadRunning = false;
            return;
        }
        s_HookEnabled = true;
        Debug.Log("鼠标钩子已安装 (WH_MOUSE_LL, 独立线程) hookId=" + s_HookId);

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
            //
            // 注意：WindowFromPoint 对隐藏/禁用窗口返回 NULL；且跨进程 SetParent 后
            // 可能返回 NULL。此时该点位于桌面区域，应视为「桌面」继续处理（否则事件完全不喂）。
            IntPtr hit = User32.WindowFromPoint(screen);
            bool desktopish = (hit == IntPtr.Zero)
                || hit == s_UnityWindowStatic
                || IsWindowClass(hit, "Progman") || IsWindowClass(hit, "WorkerW")
                || IsWindowClass(hit, "SHELLDLL_DefView") || IsWindowClass(hit, "SysListView32");

            if (!desktopish)
            {
                // 落在非桌面窗口上（如浏览器）：不喂事件，强制释放可能卡住的按键
                if (s_AnyButtonHeld)
                {
                    s_QueueStatic.EnqueueForceRelease(); s_AnyButtonHeld = false;
                }
                return User32.CallNextHookEx(s_HookId, nCode, wParam, lParam);
            }

            // 换算到本窗口客户区像素坐标。
            // GraphicRaycaster 用 ScreenToViewportPoint(eventPosition) 做射线，
            // 因此必须传【真实屏幕像素坐标】，不能传归一化值（DPI/参考分辨率不同会整体偏移）。
            // 注意：GDI 客户区坐标是 Y 向下（左上原点），而 Unity 屏幕坐标是 Y 向上（左下原点），
            // 两者方向相反，故下方对 Y 做翻转，否则传入 Unity 的位置会上下镜像。
            User32.RECT client;
            User32.GetClientRect(s_UnityWindowStatic, out client);
            User32.POINT cp = screen;
            User32.ScreenToClient(s_UnityWindowStatic, ref cp);

            int px = cp.X;
            int py = cp.Y;

            if (px < 0) px = 0; if (py < 0) py = 0;
            if (px > client.right) px = client.right;
            if (py > client.bottom) py = client.bottom;

            // GDI→Unity 坐标方向转换：Y 轴翻转（client.bottom 即客户区高度）
            py = client.bottom - py;

            switch (msg)
            {
                case User32.WM_MOUSEMOVE:
                    s_QueueStatic.EnqueueMove(px, py); s_FedMoves++; break;
                case User32.WM_LBUTTONDOWN:
                    s_QueueStatic.EnqueueMove(px, py);
                    s_QueueStatic.EnqueueButton(0, true); s_AnyButtonHeld = true; s_FedDowns++; break;
                case User32.WM_LBUTTONUP:
                    s_QueueStatic.EnqueueButton(0, false); s_AnyButtonHeld = false; break;
                case User32.WM_RBUTTONDOWN:
                    s_QueueStatic.EnqueueMove(px, py);
                    s_QueueStatic.EnqueueButton(1, true); s_AnyButtonHeld = true; break;
                case User32.WM_RBUTTONUP:
                    s_QueueStatic.EnqueueButton(1, false); s_AnyButtonHeld = false; break;
                case User32.WM_MBUTTONDOWN:
                    s_QueueStatic.EnqueueMove(px, py);
                    s_QueueStatic.EnqueueButton(2, true); s_AnyButtonHeld = true; break;
                case User32.WM_MBUTTONUP:
                    s_QueueStatic.EnqueueButton(2, false); s_AnyButtonHeld = false; break;
                case User32.WM_MOUSEWHEEL:
                    s_QueueStatic.EnqueueWheel((short)((info.mouseData >> 16) & 0xFFFF) / 120f); break;
                case User32.WM_MOUSEHWHEEL:
                    s_QueueStatic.EnqueueWheelH((short)((info.mouseData >> 16) & 0xFFFF) / 120f); break;
            }
        }
        return User32.CallNextHookEx(s_HookId, nCode, wParam, lParam);
    }

    private static bool IsWindowClass(IntPtr hwnd, string cls)
    {
        return GetClassNameOf(hwnd) == cls;
    }

    private static string GetClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "NULL";
        var sb = new StringBuilder(256);
        User32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
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
            // 挂接尚未完成时，original* 还是 0（未捕获），此时不能用它覆盖窗口样式
            bool hasOriginal = originalStyle != 0 || originalExStyle != 0;

            User32.ShowWindow(unityWindow, SW_HIDE);          // 隐藏后改样式，避免残影
            User32.SetParent(unityWindow, IntPtr.Zero);
            if (hasOriginal)
            {
                User32.SetWindowLong(unityWindow, GWL_EXSTYLE, originalExStyle);
                User32.SetWindowLong(unityWindow, GWL_STYLE, originalStyle);   // 清 WS_CHILD，还原 WS_POPUP
            }
            else
            {
                // 未捕获原样式：至少确保清掉 WS_CHILD，避免残留「子窗口」状态
                int st = User32.GetWindowLong(unityWindow, GWL_STYLE);
                st &= ~WS_CHILD;
                st |= WS_POPUP;
                User32.SetWindowLong(unityWindow, GWL_STYLE, st);
            }
            User32.SetWindowPos(unityWindow, HWND_TOP, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            User32.ShowWindow(unityWindow, SW_SHOW);
            User32.SetForegroundWindow(unityWindow);
        }
#endif
        Debug.Log("已恢复为普通窗口");
    }

    public void QuitApp()
    {
        UserRequestedQuit = true;
        Debug.Log("[ExitTrace] QuitApp 被调用（托盘菜单退出）t="
            + Time.realtimeSinceStartup.ToString("F1") + "s");
        RestoreToWindow();
        // 【关键】不要在 TrackPopupMenuEx 的模态消息循环里调用 Application.Quit()：
        // 实测退出流程能走完（OnApplicationQuit/OnDestroy 都执行），但进程卡在引擎 shutdown
        // 阶段不退出（日志止于 Memory Statistics）。改为设置标志，由 Update() 在下一帧
        // 主循环正常执行时清理并硬终止，与 m_SelfTerminate 路径保持一致。
        m_QuitRequested = true;
    }

    public void SetSystemShutdown()
    {
        s_SystemShutdown = true;
    }

    /// <summary>
    /// 桌面重建(Explorer 重启 / 显示变化 / 锁屏解锁)时，本进程窗口(WorkerW 子窗口)会被 OS 销毁。
    /// 此时自动拉起一个新实例继续挂接壁纸，而不是让壁纸消失。
    /// 仅在「非用户主动退出、非系统关机」时生效；带防抖避免 Explorer 持续崩溃导致无限重启。
    /// </summary>
    public bool RelaunchSelf()
    {
        if (UserRequestedQuit || s_SystemShutdown) return false;
        if (!ShouldRelaunchByBackoff()) return false;

        try
        {
            System.Threading.Thread.Sleep(400); // 给 Explorer 一点时间重建桌面(WorkerW)
            string exeDir = System.IO.Path.GetDirectoryName(Application.dataPath); // .../WallPaper
            string exePath = System.IO.Path.Combine(exeDir, Application.productName + ".exe");
            if (!System.IO.File.Exists(exePath))
            {
                Debug.LogWarning("[ExitTrace] 重启失败：找不到 " + exePath);
                return false;
            }
            Debug.Log("[ExitTrace] 检测到桌面重建(Explorer重启/显示变化)，准备重启壁纸进程... path=" + exePath);

            // 【关键时序】不能在本进程仍存活时启动新实例。
            // 实测第二个 WallPaper 实例会被完全阻塞在 Unity 引擎初始化阶段
            // （内存恒 ~21MB、CPU 时间不增长、无任何日志输出、窗口已创建但永不挂接），
            // 旧进程若此时误判"新实例已稳定"而退出，壁纸将永久消失。
            // 因此顺序必须是：本进程先退出 → 独立启动器检测到本进程消失 → 再拉起唯一的新实例。
            int myPid = (int)User32.GetCurrentProcessId();
            if (TrySpawnDetachedLauncher(exePath, myPid, out int launcherPid))
            {
                Debug.Log("[ExitTrace] 已创建独立启动器 PID=" + launcherPid
                    + "（本进程退出后将自动拉起新实例）");
                return true;
            }

            Debug.LogWarning("[ExitTrace] 独立启动器创建失败，壁纸未恢复！");
            return false;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ExitTrace] 重启异常 " + e.GetType().FullName + ": " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// 生成一个「等待本进程退出后再启动壁纸」的独立启动器并静默运行。
    /// 为什么需要外部启动器：本进程必须先退出（否则新实例会卡在 Unity 引擎初始化），
    /// 而退出后由谁去拉起新实例？答案是一个脱离 Job、不随本进程消亡的 cmd.exe。
    /// 返回 true 表示启动器进程已成功创建。
    /// </summary>
    private bool TrySpawnDetachedLauncher(string exePath, int ownerPid, out int launcherPid)
    {
        launcherPid = 0;
        try
        {
            // 1) 写临时脚本：轮询 tasklist，等 ownerPid 消失后再 start 新实例。
            //    计数器用 !n! 延迟展开——括号块内 %n% 会取到进入块之前的旧值，永远为 0。
            //    用 findstr 而非 find：部分环境（如沙箱/精简系统）find.exe 参数解析异常，恒返回错误码。
            //    第二道 findstr 按镜像名过滤：防止 ownerPid 在极端情况下被其他进程复用导致空等。
            //    超时(20s)仍存活则 taskkill 强杀旧进程——新实例与旧进程并存会卡在 Unity 引擎初始化，
            //    所以必须确保旧进程真的消失后才能启动新实例（注意不能加 /T，否则会杀掉启动器自身）。
            string cmdFile = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "wallpaper_relaunch_" + ownerPid + ".cmd");
            string logFile = System.IO.Path.ChangeExtension(cmdFile, ".log");
            string imgName = System.IO.Path.GetFileName(exePath);
            string script =
                "@echo off\r\n" +
                "setlocal enabledelayedexpansion\r\n" +
                "set n=0\r\n" +
                ":wait_owner_exit\r\n" +
                "tasklist /fi \"PID eq " + ownerPid + "\" /nh 2>nul | findstr \"" + ownerPid + "\" | findstr /i \"" + imgName + "\" >nul\r\n" +
                "if not errorlevel 1 (\r\n" +
                "  set /a n+=1\r\n" +
                "  if !n! gtr 20 (\r\n" +
                "    echo [!time!] force-kill owner " + ownerPid + " >> \"" + logFile + "\"\r\n" +
                "    taskkill /F /PID " + ownerPid + " >nul 2>&1\r\n" +
                "    timeout /t 2 /nobreak >nul\r\n" +
                "    goto do_launch\r\n" +
                "  )\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                "  goto wait_owner_exit\r\n" +
                ")\r\n" +
                ":do_launch\r\n" +
                "start \"\" \"" + exePath + "\"\r\n" +
                "exit /b 0\r\n";
            System.IO.File.WriteAllText(cmdFile, script);

            // 2) CreateProcess 启动 cmd.exe /c "<脚本>"。
            //    必须带 CREATE_BREAKAWAY_FROM_JOB，否则本进程退出（或 Job 关闭）时它被连带杀掉。
            string cmdPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            if (!System.IO.File.Exists(cmdPath))
            {
                Debug.LogWarning("[ExitTrace] 找不到 cmd.exe: " + cmdPath);
                return false;
            }

            string cmdLine = "\"" + cmdPath + "\" /c \"\"" + cmdFile + "\"\"";

            uint[] flags = {
                User32.CREATE_BREAKAWAY_FROM_JOB | User32.CREATE_NO_WINDOW,
                User32.CREATE_NO_WINDOW
            };
            string[] flagNames = { "BREAKAWAY|NO_WINDOW", "NO_WINDOW" };

            for (int i = 0; i < flags.Length; i++)
            {
                User32.STARTUPINFO si = new User32.STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(User32.STARTUPINFO));
                if (si.cb <= 0) si.cb = 104;   // x64 下 STARTUPINFO 固定大小
                si.dwFlags = User32.STARTF_USESHOWWINDOW;
                si.wShowWindow = 0;             // SW_HIDE

                User32.PROCESS_INFORMATION pi;
                bool ok = User32.CreateProcess(
                    null, cmdLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    flags[i], IntPtr.Zero, null,
                    ref si, out pi);

                if (ok)
                {
                    launcherPid = pi.dwProcessId;
                    if (pi.hThread != IntPtr.Zero) User32.CloseHandle(pi.hThread);
                    if (pi.hProcess != IntPtr.Zero) User32.CloseHandle(pi.hProcess);
                    Debug.Log("[ExitTrace] 启动器创建成功(" + flagNames[i] + ") PID=" + launcherPid
                        + " script=" + cmdFile);
                    return true;
                }

                Debug.LogWarning("[ExitTrace] 启动器创建失败(" + flagNames[i] + ") err="
                    + Marshal.GetLastWin32Error());
            }

            // 3) 备用：用 ShellExecute 打开同一个"等待脚本"（仍保证旧进程先退出）。
            //    注意不能直接启动 exePath——那会让新实例与本进程并存，卡在 Unity 引擎初始化。
            Debug.LogWarning("[ExitTrace] CreateProcess 均失败，尝试 ShellExecute 打开启动脚本...");
            IntPtr r = User32.ShellExecute(IntPtr.Zero, "open", cmdFile, null, null, User32.SW_HIDE);
            Debug.Log("[ExitTrace] ShellExecute(script) 返回 " + r);
            if (r.ToInt64() > 32)
            {
                Debug.LogWarning("[ExitTrace] ShellExecute 已打开启动脚本（无法验证是否真正运行）");
                return true;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ExitTrace] 启动器异常 " + e.GetType().Name + ": " + e.Message);
        }
        return false;
    }

    // 防抖：最近 60 秒内重启超过 5 次则放弃本次重启，避免 Explorer 持续崩溃导致无限重启
    private bool ShouldRelaunchByBackoff()
    {
        try
        {
            string guard = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wallpaper_relaunch_guard.tmp");
            var now = DateTime.Now;
            var times = new System.Collections.Generic.List<DateTime>();
            if (System.IO.File.Exists(guard))
            {
                foreach (var line in System.IO.File.ReadAllLines(guard))
                {
                    if (DateTime.TryParse(line, out var t)) times.Add(t);
                }
            }
            times = times.FindAll(t => (now - t).TotalSeconds < 60);
            times.Add(now);
            System.IO.File.WriteAllLines(guard, times.ConvertAll(t => t.ToString("o")));
            return times.Count <= 5;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 启动 Explorer 存活守护线程。挂接成功后调用一次。
    /// 后台等待 Explorer 进程句柄：一旦 Explorer 重启/崩溃，线程等几秒让新 Explorer 起来后
    /// 拉起一个新实例重新挂接壁纸。
    /// 【为什么必须重启进程】Explorer 重启会销毁 WorkerW，而 DestroyWindow 会递归销毁子窗口，
    /// 本进程的 Unity 窗口（WorkerW 子窗口）随之被销毁（实测 isWindow=False），
    /// 原进程内已无窗口可挂，只能重启。
    /// 用户主动退出 / 系统关机时不触发。
    /// </summary>
    private void StartExplorerWatchdog()
    {
        if (m_WatchdogRunning) return;
        if (s_ExplorerPid == 0) return;

        uint pid = s_ExplorerPid;
        IntPtr hProc = User32.OpenProcess(User32.SYNCHRONIZE, false, pid);
        if (hProc == IntPtr.Zero)
        {
            Debug.LogWarning("[Watchdog] 无法打开 Explorer 进程句柄(PID=" + pid + ")，跳过存活守护");
            return;
        }

        m_WatchdogRunning = true;
        m_WatchdogThread = new System.Threading.Thread(() =>
        {
            try
            {
                try { User32.WaitForSingleObject(hProc, User32.INFINITE); }
                catch { }
                User32.CloseHandle(hProc);
                m_WatchdogRunning = false;

                // Explorer 已退出：WorkerW 子树被销毁，本进程窗口也被递归销毁。
                Debug.Log("[ExitTrace] [Watchdog] 检测到 Explorer 退出(PID=" + pid + ")，准备重启壁纸");

                if (UserRequestedQuit || s_SystemShutdown)
                {
                    Debug.Log("[ExitTrace] [Watchdog] 用户主动退出/系统关机，不重启");
                    return;
                }
                // 等几秒：让新 Explorer 起来（Progman / 任务栏重建）。
                // 系统关机时不会重建，NewDesktopReady 返回 false，这里就不重启。
                // 注意：WorkerW 由 0x052C 消息创建，Explorer 重启后不会自动出现，不能用它判断。
                if (!NewDesktopReady(pid, 10000))
                {
                    Debug.Log("[ExitTrace] [Watchdog] 未发现新桌面（可能系统关机），不重启");
                    return;
                }
                // 再等 2 秒，确保新 Explorer 完成桌面初始化（图标层建立后再 Spawn WorkerW）
                System.Threading.Thread.Sleep(2000);
                if (UserRequestedQuit || s_SystemShutdown)
                {
                    Debug.Log("[ExitTrace] [Watchdog] 等待期间用户退出/系统关机，取消重启");
                    return;
                }
                // 窗口已被销毁，只能重启进程。
                // 顺序：先由 RelaunchSelf 创建独立启动器，再让本进程退出（新实例必须等旧进程消失后才启动）
                if (TryMarkRelaunched() && RelaunchSelf())
                {
                    m_SelfTerminate = true;
                }
                else
                {
                    // 重启失败：旧进程窗口已被销毁，无法恢复壁纸，但保留进程与托盘图标便于排查。
                    Debug.LogWarning("[ExitTrace] [Watchdog] 自动重启失败，壁纸未恢复！");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ExitTrace] [Watchdog] 恢复流程异常 " + e.GetType().Name + ": " + e.Message);
            }
        });
        m_WatchdogThread.IsBackground = true;
        m_WatchdogThread.Start();
        Debug.Log("[ExitTrace] [Watchdog] 已启动 Explorer 存活守护 (PID=" + pid + ")");
    }

    /// <summary>
    /// 轮询等待「新版桌面就绪」。
    /// Explorer 重启后：Progman 会重建（PID 变化）或任务栏 Shell_TrayWnd 重新出现。
    /// 系统关机时：Explorer 退出且不再重建 → 返回 false。
    /// 注意：WorkerW 由 0x052C 消息创建，Explorer 重启后不会自动出现，不能用它判断。
    /// </summary>
    private static bool NewDesktopReady(uint oldPid, int timeoutMs)
    {
        int elapsed = 0;
        while (elapsed < timeoutMs)
        {
            // 1) Progman 存在且 PID 变化 → 新 Explorer 已启动
            IntPtr progman = User32.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                User32.GetWindowThreadProcessId(progman, out uint pid);
                if (pid != 0 && pid != oldPid)
                {
                    return true;
                }
            }
            // 2) 任务栏出现（Explorer 启动后创建）
            if (User32.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
            {
                return true;
            }
            System.Threading.Thread.Sleep(200);
            elapsed += 200;
        }
        return false;
    }

    void OnApplicationPause(bool pauseStatus)
    {
        m_IsPaused = pauseStatus;
        Debug.Log("[ExitTrace] OnApplicationPause=" + pauseStatus
            + " t=" + Time.realtimeSinceStartup.ToString("F1") + "s");
    }

    void OnApplicationQuit()
    {
        Debug.Log("[ExitTrace] OnApplicationQuit 被调用 t="
            + Time.realtimeSinceStartup.ToString("F1") + "s frame=" + Time.frameCount);
        // 用户主动退出时窗口恢复已在 RestoreToWindow() 完成，这里跳过，避免重复 SetParent 触发卡死
        if (m_QuitRequested)
        {
            Debug.Log("[ExitTrace] OnApplicationQuit: 用户主动退出路径，跳过重复清理");
            return;
        }
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
        Debug.Log("[ExitTrace] OnDestroy 被调用");
        UninstallMouseHook();
        if (m_NotifyIconItem != null)
        {
            m_NotifyIconItem.Dispose();
        }
    }

    void Update()
    {
        if (m_SelfTerminate)
        {
            Debug.Log("[ExitTrace] [Watchdog] 旧进程主动退出，由新实例接管壁纸");
            // 【关键】窗口已被 Explorer 销毁，实测 IL2CPP 下 Application.Quit() 会卡死：
            // 进程在调用后仍长期存活（日志停在下一行、无 OnApplicationQuit/OnDestroy），
            // 导致独立启动器一直空等、壁纸无法恢复。窗口已不存在，无需优雅退出，
            // 直接硬终止本进程，让启动器立刻检测到并拉起新实例。
            User32.TerminateProcess(User32.GetCurrentProcess(), 0);
            // TerminateProcess 成功时不会返回；若失败（极少数情况），退化为 Environment.Exit
            System.Environment.Exit(0);
            return;
        }

        // 用户主动退出（托盘菜单）：RestoreToWindow() 已做 UninstallMouseHook + 窗口恢复，
        // 这里只需补托盘清理后硬终止（Application.Quit 在模态消息循环里会卡死）。
        if (m_QuitRequested)
        {
            Debug.Log("[ExitTrace] 用户主动退出，执行清理并硬终止（避免 Application.Quit 卡死）");
            UninstallMouseHook();
            if (m_NotifyIconItem != null)
            {
                m_NotifyIconItem.Dispose();
                m_NotifyIconItem = null;
            }
            User32.TerminateProcess(User32.GetCurrentProcess(), 0);
            System.Environment.Exit(0);
            return;
        }

        // 退出诊断：每 60 秒输出一次心跳；窗口可见性/父窗口发生变化时立即输出
        //（「壁纸突然消失」的直接线索就是 windowVisible 或 parent 变化）
        m_HeartbeatTimer += Time.unscaledDeltaTime;

        bool visNow = unityWindow != IntPtr.Zero && User32.IsWindowVisible(unityWindow);
        IntPtr parentNow = User32.GetParent(unityWindow);
        bool stateChanged = visNow != m_LastHeartbeatVisible || parentNow != m_LastHeartbeatParent;
        m_LastHeartbeatVisible = visNow;
        m_LastHeartbeatParent = parentNow;

        if (m_HeartbeatTimer >= 60f || stateChanged)
        {
            m_HeartbeatTimer = 0f;
            Debug.Log("[Heartbeat] t=" + Time.realtimeSinceStartup.ToString("F1")
                + "s frame=" + Time.frameCount
                + " windowVisible=" + visNow
                + " parent=" + parentNow
                + " isFocused=" + Application.isFocused
                + " isPaused=" + m_IsPaused
                + " rect=" + s_WindowScreenRect.left + "," + s_WindowScreenRect.top
                + "," + s_WindowScreenRect.right + "," + s_WindowScreenRect.bottom);
        }

        // 周期刷新窗口屏幕矩形（多屏/分辨率变化也能正确命中）
        if (unityWindow != IntPtr.Zero)
        {
            User32.GetWindowRect(unityWindow, out s_WindowScreenRect);
        }
    }
}
