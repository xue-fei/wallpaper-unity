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

    // 窗口是否可见（父窗口隐藏时子窗口 WS_VISIBLE 仍为 TRUE，必须用这个）
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

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

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}