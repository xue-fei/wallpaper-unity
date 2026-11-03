using System;
using System.Runtime.InteropServices;
using System.Text;

public class User32
{
    // ====================================================================================
    // 1. P/Invoke 声明 (Windows API)
    // ====================================================================================

    // 查找窗口
    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    // 通过 Shell 启动程序（Process.Start 失败时的备用通道）
    // 返回值 > 32 表示成功；<= 32 是 SE_ERR_* 错误码
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ShellExecute(IntPtr hwnd, string lpOperation,
        string lpFile, string lpParameters, string lpDirectory, int nShowCmd);

    public const int SW_SHOWNORMAL = 1;

    // 查找子窗口
    [DllImport("user32.dll")]
    public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowTitle);

    // 发送带有超时的消息
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint timeout, out IntPtr lpdwResult);

    // SendMessageTimeout 的 fuFlags（避免 flags=0 时 WorkerW 不生成）
    public const uint SMTO_NORMAL = 0x0000;
    public const uint SMTO_BLOCK = 0x0001;        // 阻塞直到超时或收到返回
    public const uint SMTO_ABORTIFHUNG = 0x0002;  // 目标挂起则提前返回
    public const uint SMTO_NOTIMEOUTIFNOTHUNG = 0x0008;
    public const uint SMTO_FLAGS = SMTO_NORMAL | SMTO_BLOCK | SMTO_ABORTIFHUNG;

    // 设置父窗口
    [DllImport("user32.dll")]
    public static extern bool SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    // 设置窗口位置和大小
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // 显示/隐藏窗口
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // 获取/设置窗口样式
    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // 设置分层窗口属性
    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    // 枚举子窗口
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    // 枚举顶层窗口（找 WorkerW 用）
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // 取窗口所属进程 ID（用于判断是否属于 Explorer）
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // 打开进程句柄（存活守护用：等待 Explorer 进程退出）
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    // 等待内核对象（进程句柄在进程退出时变为 signaled）
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    // 关闭内核对象句柄
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    // 句柄访问权限 / 等待常量
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_TIMEOUT = 0x00000102;   // WaitForSingleObject：超时（对象仍处于未触发状态）

    // ---------------------------------------------------------------------------------
    // CreateProcess：IL2CPP 下唯一可靠的「创建进程」方式。
    //   为什么不用 System.Diagnostics.Process：
    //     打包(IL2CPP)后 Process.Start 无论 UseShellExecute=true/false 都抛
    //     Win32Exception("Native error= Success")，异常里 CommandLine='' 为空，
    //     说明托管侧构造命令行失败，根本没走到 Win32 CreateProcess。
    //   为什么不用 ShellExecute：
    //     它要经过 Shell（Explorer）做关联/动词解析，Explorer 重启瞬间 Shell 处于
    //     过渡状态，实测返回 42 但进程并未真正创建（没有日志轮转）。
    // ---------------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcess(
        string lpApplicationName,     // 可执行文件完整路径；为 null 时从 lpCommandLine 解析
        string lpCommandLine,         // 命令行；lpApplicationName 非 null 时可传 null
        IntPtr lpProcessAttributes,   // 进程安全属性，一般 IntPtr.Zero
        IntPtr lpThreadAttributes,    // 线程安全属性，一般 IntPtr.Zero
        bool bInheritHandles,         // 是否继承调用方的可继承句柄
        uint dwCreationFlags,         // CREATE_* 标志
        IntPtr lpEnvironment,         // 环境块；IntPtr.Zero = 继承调用方环境
        string lpCurrentDirectory,    // 工作目录；null = 使用调用方当前目录
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    // 让新进程脱离调用方所属的 Job Object。
    // 若本进程处于带 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 的 Job 中，普通 CreateProcess
    // 创建的子进程会随本进程退出被连带杀掉；加此标志可让子进程脱离 Job 独立存活。
    // 注意：若该 Job 不允许 breakaway，CreateProcess 会返回 ERROR_ACCESS_DENIED，
    // 调用方需退化为普通 CreateProcess（见 WallpaperController.TryCreateProcess）。
    public const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    // 不为新进程创建控制台窗口（用于静默启动 cmd.exe，避免黑框一闪）
    public const uint CREATE_NO_WINDOW = 0x08000000;

    // STARTUPINFO.dwFlags：以下字段 wShowWindow 才生效
    public const int STARTF_USESHOWWINDOW = 0x00000001;

    // 获取当前进程 ID（用于生成"等待本进程退出"的启动器脚本）
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentProcessId();

    // 当前进程伪句柄（-1），可直接用于 TerminateProcess
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    // 强制终止进程（不可被阻塞/拦截，退出码立即生效）
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    // 窗口是否可见（父窗口隐藏时子窗口 WS_VISIBLE 仍为 TRUE，必须用这个）
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    // 窗口句柄是否仍然有效（跨进程句柄可能已失效）
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    // 获取窗口类名
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // 获取当前活动窗口句柄
    [DllImport("user32.dll")]
    public static extern IntPtr GetActiveWindow();

    // EnumChildWindows 的回调委托
    public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    // 设置指定窗口的键盘焦点
    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    public static extern int SetWindowLong32(HandleRef hWnd, int nIndex, int dwNewLong);
    
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    public static extern IntPtr SetWindowLongPtr64(HandleRef hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool AppendMenu(IntPtr hMenu, MenuFlags uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    public static extern bool TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y,
            IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    // 将消息信息传递给指定的窗口过程
    [DllImport("user32.dll")]
    public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // http://www.pinvoke.net/default.aspx/user32/CreatePopupMenu.html
    [DllImport("user32")]
    public static extern IntPtr CreatePopupMenu();




    public const int WM_SYSCOMMAND = 0x0112;

    public const int SC_CLOSE = 0xF060;
    public const int SC_MAXIMIZE = 0xF030;
    public const int SC_MINIMIZE = 0xF020;

    public const int WM_LBUTTONDOWN = 0x0201;                   // 左键
    public const int WM_LBUTTONDBLCLK = 0x0203;                 // 左键双击
    public const int WM_RBUTTONDOWN = 0x0204;                   // 右键
    public const int WM_RBUTTONDBLCLK = 0x0206;                 // 右键双击
    public const int WM_MBUTTONDOWN = 0x0207;                   // 中键

    // https://docs.microsoft.com/zh-cn/windows/win32/api/winuser/nf-winuser-showwindow?redirectedfrom=MSDN
    public const int WM_CREATE = 0x0001;
    public const int WM_DESTROY = 0x0002;
    public const int WM_COMMAND = 0x0111;

    // Ref:
    // https://docs.microsoft.com/zh-cn/windows/win32/winmsg/about-windows#desktop-window
    // https://docs.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow
    public const int SW_HIDE = 0;                               // 隐藏窗口，大小不变，激活状态不变
    public const int SW_MAXIMIZE = 3;                           // 最大化窗口，显示状态不变，激活状态不变
    public const int SW_SHOW = 5;                               // 在窗口原来的位置以原来的尺寸激活和显示窗口
    public const int SW_MINIMIZE = 6;                           // 最小化指定的窗口并且激活在Z序中的下一个顶层窗口
    public const int SW_RESTORE = 9;                            // 激活并显示窗口。如果窗口最小化或最大化，则系统将窗口恢复到原来的尺寸和位置。在恢复最小化窗口时，应用程序应该指定这个标志




    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            this.X = x;
            this.Y = y;
        }
    }

    [Flags]
    public enum MenuFlags : uint
    {
        MF_STRING = 0,
        MF_BYPOSITION = 0x400,
        MF_SEPARATOR = 0x800,
        MF_REMOVE = 0x1000,
    }

    // ====================================================================================
    // 低层鼠标钩子 (WH_MOUSE_LL) 与坐标换算 —— 纯 C# 壁纸输入（无需注入 Explorer）
    // ====================================================================================
    public const int WH_MOUSE_LL = 14;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_MOUSEHWHEEL = 0x020E;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // 注意：GetModuleHandle 实际导出在 kernel32.dll（不是 user32.dll）。
    // 传 null 字符串得到当前进程模块句柄，作为 WH_MOUSE_LL 钩子的 hMod。
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PtInRect(ref RECT lprc, POINT pt);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>获取指定屏幕坐标处最顶层的窗口（含子窗口）。用于精确判定鼠标是否落在桌面/壁纸层。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    // ---- 独立钩子线程所需的消息泵 API ----

    public const int WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public static IntPtr GetWindow(string titleOrClassname)
    {
        IntPtr hWnd = FindWindow(null, titleOrClassname); ;
        if (hWnd == IntPtr.Zero)
        {
            hWnd = FindWindow(titleOrClassname, null);
        }

        return hWnd;
    }

    public static IntPtr SetWindowLongPtr(HandleRef hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
        else
        {
            return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }
    }

    // ====================================================================================
    // 屏幕 / 显示器 API
    // ====================================================================================
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>虚拟屏幕尺寸（多屏拼接），用于设置壁纸渲染分辨率。</summary>
    public static void GetVirtualScreenSize(out int width, out int height)
    {
        width = GetSystemMetrics(78 /* SM_CXVIRTUALSCREEN */);
        height = GetSystemMetrics(79 /* SM_CYVIRTUALSCREEN */);
    }

    // 显示器枚举（当前未直接使用，保留以备多屏扩展）
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc callback, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ====================================================================================
    // Shell 托盘 API（任务栏图标 / 图标提取）
    // ====================================================================================
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hwnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutAndVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIcon", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, StringBuilder lpIconPath, out ushort lpiIcon);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}