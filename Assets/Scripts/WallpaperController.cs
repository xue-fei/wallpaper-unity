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
            Application.targetFrameRate = 30;
        }
        catch (Exception e)
        {
        	Debug.Log("警告: 分辨率设置失败 " + e.Message);
        }

        try
        {
        	SetupDemoUI();
        }
        catch (Exception e)
        {
        	Debug.Log("警告: 演示 UI 装配失败 " + e.Message);
        }

        //try { DisableDecorativeRaycast(); }
        //catch (Exception e) { Debug.Log("警告: 装饰层 raycast 关闭失败 " + e.Message); }

        try
        {
        	SetupDebugText();
        }
        catch (Exception e)
        {
        	Debug.Log("警告: 调试文本创建失败 " + e.Message);
        }

        Debug.Log("WallpaperController 启动 Unity=" + Application.unityVersion
            + " 分辨率=" + Screen.width + "x" + Screen.height
            + " dataPath=" + Application.dataPath);

#if !UNITY_EDITOR
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
        if (es == null)
        {
        	Debug.Log("错误: 场景中缺少 EventSystem"); return;
        }
        StandaloneInputModule existing = es.GetComponent<StandaloneInputModule>();
        if (existing != null && !(existing is WallpaperInputModule)) Destroy(existing);
        m_InputModule = es.GetComponent<WallpaperInputModule>();
        if (m_InputModule == null) m_InputModule = es.gameObject.AddComponent<WallpaperInputModule>();
        m_InputModule.BindQueue(m_Queue);
        es.UpdateModules();   // 必须：把新模块注册进 EventSystem 的模块列表，否则 Process() 不会被调用
        Debug.Log("输入模块已装配: " + (m_InputModule != null ? m_InputModule.GetType().Name : "null"));
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
        if (unityWindow == IntPtr.Zero) return;

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
                System.Threading.Thread.Sleep(400);
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
        User32.SetParent(unityWindow, workerW);

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
        	Debug.Log("已定位壁纸层 WorkerW（顶层）"); return s_FoundWorkerW;
        }
        // 2) Progman 的直接子窗口（Win11 raised desktop）
        User32.EnumChildWindows(progman, new User32.EnumChildProc(EnumProgmanChildProc), IntPtr.Zero);
        if (s_FoundWorkerW != IntPtr.Zero)
        {
        	Debug.Log("已定位壁纸层 WorkerW（Progman 子窗口）"); return s_FoundWorkerW;
        }
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
        if (btn == null)
        {
        	Debug.Log("警告: 未找到名为 Button 的对象"); return;
        }

        m_DemoButton = btn.GetComponent<Button>();
        if (m_DemoButton == null)
        {
        	Debug.Log("警告: Button 上缺少 Button 组件"); return;
        }
        m_DemoButton.onClick.AddListener(OnDemoButtonClicked);
    }

    /// <summary>
    /// 让纯装饰性 UI 不参与 raycast。
    /// 场景里的 Text(637×129, RootOrder 2) 完全遮挡 Button(160×30, RootOrder 1)，
    /// 会把点击吃掉（日志实测 upHandler=无 → 点击永不触发）。
    /// 装饰层设为 raycastTarget=false 后，射线可穿透到 Button。
    ///
    /// 注意：必须排除所有【交互控件】（Button / InputField / Selectable），
    /// 否则会连带破坏输入框等本来可用的控件。
    /// </summary>
    void DisableDecorativeRaycast()
    {
        Canvas canvas = FindObjectOfType<Canvas>();
        if (canvas == null) return;
        foreach (var g in canvas.GetComponentsInChildren<Graphic>(true))
        {
            if (g == null) continue;
            var go = g.gameObject;
            // 交互控件：Button、InputField 及其子项（Selectable 基类）一律保留
            if (go.GetComponent<Selectable>() != null) continue;
            g.raycastTarget = false;
        }
        Debug.Log("装饰层 raycast 已关闭（交互控件保留）");
    }

    void OnDemoButtonClicked()
    {
        m_ClickCount++;
    }

    void SetupDebugText()
    {
        Canvas canvas = FindObjectOfType<Canvas>();
        if (canvas == null)
        {
        	Debug.Log("警告: 未找到 Canvas，调试文本不显示"); return;
        }

        GameObject go = new GameObject("WallpaperDebugText");
        go.transform.SetParent(canvas.transform, false);

        RectTransform rt = go.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(12f, -12f);
        rt.sizeDelta = new Vector2(460f, 150f);

        // 底板必须是【独立子节点】：Image 与 Text 都是 Graphic 子类，
        // Unity 限制「一个 GameObject 只能有一个 Graphic 组件」，
        // 同一节点上加 Image 再 AddComponent<Text> 会抛异常并中断 Start()（挂载全部失效）。
        GameObject bgGo = new GameObject("DebugBackground");
        bgGo.transform.SetParent(go.transform, false);
        RectTransform bgRt = bgGo.AddComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero;
        bgRt.anchorMax = Vector2.one;
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;
        Image bg = bgGo.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.62f);
        bg.raycastTarget = false;   // 底板不参与 raycast，避免干扰 UI 命中

        Text t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = 22;
        t.color = Color.white;
        t.alignment = TextAnchor.UpperLeft;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;     // 调试文本不参与 raycast

        // 描边（Outline 继承 BaseMeshEffect，不是 Graphic，可与 Text 共存）
        Outline o = go.AddComponent<Outline>();
        o.effectColor = new Color(0f, 0f, 0f, 1f);
        o.effectDistance = new Vector2(1.5f, -1.5f);

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
                string raycast = (m_InputModule != null)
                    ? "\n按下:" + m_InputModule.PressCount + " 抬起:" + m_InputModule.ReleaseCount
                      + "\n命中:" + m_InputModule.RaycastHitCount + " 点击:" + m_InputModule.ClickCount
                      + "\n命中物:" + m_InputModule.LastHitName
                    : "";
                m_StatusText.text = hook
                    + "\n按钮点击次数: " + m_ClickCount
                    + "\n已喂事件 移动:" + s_FedMoves + " 按下:" + s_FedDowns
                    + "\n队列剩余: " + m_Queue.Count
                    + raycast;
            }
        }
    }
}
