using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

public class NotifyIconItem
{
    private static IntPtr m_HWnd;
    private NotifyIconEx m_NotifyIcon;

    public IntPtr HWnd => m_HWnd;

    // 壁纸控制器引用（托盘菜单的“恢复窗口 / 退出”转发给它）
    public static WallpaperController Controller;

    private const int AboutID = 1001;                    // 任务栏菜单—关于（打开作者主页）
    private const int ShowID = 1002;                     // 任务栏菜单—显示壁纸
    private const int HideID = 1003;                     // 任务栏菜单—隐藏壁纸
    private const int QuitID = 1004;                     // 任务栏菜单—退出

    // ---------- 托盘右键菜单的独立宿主窗口 ----------
    // 【为什么】壁纸窗口是 Progman 下的 WS_CHILD，必须靠 z 序压在 SHELLDLL_DefView 之下。
    // 若用它当菜单 owner，SetForegroundWindow 会激活它并提到子链最顶端；
    // 事后压回又要改一次 z 序 —— 两次变化会让 Explorer 的图标层整片重绘，
    // 表现为「桌面图标一起闪一下」。故菜单全程不碰壁纸窗口。
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const int GWLP_WNDPROC = -4;

    private static IntPtr m_MenuHost = IntPtr.Zero;
    private static IntPtr m_MenuHostOldProc = IntPtr.Zero;
    // 静态持有委托，防止被 GC 回收（回调来自非托管代码）
    private static User32.WndProcDelegate m_MenuHostProc;

    public void Init()
    {
        InitWndProc();
        CreateMenuHost();
        CreateNotifyIcon(); 
    }

    public void Dispose()
    {
        DestroyMenuHost();
        TermWndProc();
    }

    // 创建任务栏窗体
    private void CreateNotifyIcon()
    {
        if (m_HWnd == IntPtr.Zero)
        {
            return;
        }
        DirectoryInfo assetData = new DirectoryInfo(Application.dataPath);
        if (assetData.Parent == null)
        {
            return;
        }
        var exeFilePath = $"{assetData.Parent.FullName}\\{Application.productName}.exe";
        StringBuilder exeFileSb = new StringBuilder(exeFilePath);
        IntPtr iconPtr = User32.ExtractAssociatedIcon(m_HWnd, exeFileSb, out ushort uIcon);
        m_NotifyIcon = new NotifyIconEx(m_HWnd);
        int state = m_NotifyIcon.AddNotifyBox(iconPtr, "Unity壁纸程序", Application.productName, $"Unity壁纸程序已启动");
        if (state <= 0)
        {
            Debug.Log("创建任务栏图标失败");
        }
        else
        {
            Debug.Log("创建任务栏图标成功");
        }
    }

    // ========================================================================
    // 托盘菜单宿主窗口
    // ========================================================================

    /// <summary>
    /// 创建 1x1 全透明、位于屏幕外的顶层工具窗口，专门接收 SetForegroundWindow /
    /// TrackPopupMenuEx。它可激活、不产生任何可见像素、不进任务栏。
    /// 用系统预定义 "Static" 类，省去 RegisterClass。
    /// </summary>
    private static void CreateMenuHost()
    {
        if (m_MenuHost != IntPtr.Zero)
        {
            return;
        }
        m_MenuHost = User32.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_LAYERED,
            "Static", null,
            WS_POPUP | WS_VISIBLE,
            -100, -100, 1, 1,
            IntPtr.Zero, IntPtr.Zero,
            User32.GetModuleHandle(null), IntPtr.Zero);

        if (m_MenuHost == IntPtr.Zero)
        {
            Debug.LogWarning("[TrayMenu] 菜单宿主窗口创建失败，回退为壁纸窗口");
            return;
        }

        // alpha=0 → 完全透明，避免屏幕上出现任何像素
        User32.SetLayeredWindowAttributes(m_MenuHost, 0, 0, LWA_ALPHA);

        // 子类化：菜单点击的 WM_COMMAND 会发给本窗口，转发回主窗口 WndProc
        m_MenuHostProc = new User32.WndProcDelegate(MenuHostWndProc);
        m_MenuHostOldProc = User32.SetWindowLongPtr(
            new HandleRef(null, m_MenuHost), GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(m_MenuHostProc));
    }

    private static void DestroyMenuHost()
    {
        if (m_MenuHost == IntPtr.Zero)
        {
            return;
        }
        if (m_MenuHostOldProc != IntPtr.Zero)
        {
            User32.SetWindowLongPtr(new HandleRef(null, m_MenuHost), GWLP_WNDPROC, m_MenuHostOldProc);
        }
        User32.DestroyWindow(m_MenuHost);
        m_MenuHost = IntPtr.Zero;
        m_MenuHostOldProc = IntPtr.Zero;
        m_MenuHostProc = null;
    }

    /// <summary>
    /// 宿主窗口过程：只把 WM_COMMAND 转交给壁纸窗口原有的 WndProc，
    /// 复用「关于 / 显示 / 隐藏 / 退出」处理逻辑，其余交给默认过程。
    /// </summary>
    [MonoPInvokeCallback]
    private static IntPtr MenuHostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == User32.WM_COMMAND)
        {
            return WndProc(m_HWnd, msg, wParam, lParam);
        }
        if (m_MenuHostOldProc != IntPtr.Zero)
        {
            return User32.CallWindowProc(m_MenuHostOldProc, hWnd, msg, wParam, lParam);
        }
        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    #region 监听窗体事件

    private HandleRef m_HMainWindow;
    private static IntPtr m_OldWndProcPtr;
    private IntPtr m_NewWndProcPtr;
    private User32.WndProcDelegate m_NewWndProc;

    private void InitWndProc()
    {
        m_HWnd = User32.GetWindow(Application.productName);
        m_HMainWindow = new HandleRef(null, m_HWnd);
        m_NewWndProc = new User32.WndProcDelegate(WndProc);
        m_NewWndProcPtr = Marshal.GetFunctionPointerForDelegate(m_NewWndProc);
        m_OldWndProcPtr = User32.SetWindowLongPtr(m_HMainWindow, -4, m_NewWndProcPtr);
    }

    private void TermWndProc()
    {
        User32.SetWindowLongPtr(m_HMainWindow, -4, m_OldWndProcPtr);
        m_HMainWindow = new HandleRef(null, IntPtr.Zero);
        m_OldWndProcPtr = IntPtr.Zero;
        m_NewWndProcPtr = IntPtr.Zero;
        m_NewWndProc = null;
    }

    [MonoPInvokeCallback]
    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 退出诊断：只记录退出/会话/显示变化等关键窗口消息（过滤高频消息，避免刷屏）
        // 0x0002 WM_DESTROY / 0x0010 WM_CLOSE / 0x0011 WM_QUERYENDSESSION / 0x0016 WM_ENDSESSION / 0x001A WM_DISPLAYCHANGE
        // 不含 WM_SETTINGCHANGE / WM_SYSCOMMAND：触发过于频繁，与退出恢复无关
        if (msg == 0x0002 || msg == 0x0010 || msg == 0x0011 || msg == 0x0016 || msg == 0x001A)
        {
            Debug.Log("[ExitTrace] WndProc msg=0x" + msg.ToString("X4")
                + " wParam=0x" + wParam.ToInt64().ToString("X")
                + " lParam=0x" + lParam.ToInt64().ToString("X")
                + " t=" + Time.realtimeSinceStartup.ToString("F1") + "s");
        }

        // 系统关机 / 会话结束：标记，避免后续 WM_DESTROY 触发自动重启
        if (msg == 0x0011 || msg == 0x0016) // WM_QUERYENDSESSION / WM_ENDSESSION
        {
            if (Controller != null) Controller.SetSystemShutdown();
        }
        // 窗口被销毁：壁纸窗口是 Explorer 桌面(WorkerW)的子窗口，
        // Explorer 重启 / 显示变化 / 锁屏解锁 会销毁它。
        // 恢复交给看门狗线程统一处理（它会等几秒后拉起新实例），
        // 这里用 TryMarkRelaunched 与看门狗互斥，确保只重启一次（看门狗失效时的冗余备份）。
        if (msg == 0x0002) // WM_DESTROY
        {
            if (Controller != null && !Controller.UserRequestedQuit && !Controller.IsSystemShuttingDown)
            {
                // 新实例必须等本进程退出后才能启动（并存会导致新实例卡在 Unity 引擎初始化），
                // 所以启动器创建成功后要主动让本进程退出。
                if (WallpaperController.TryMarkRelaunched()
                    && Controller.RelaunchSelf("窗口销毁", "WM_DESTROY（Explorer重启/显示变化/锁屏解锁）"))
                {
                    Controller.RequestSelfTerminate();
                }
            }
            return User32.CallWindowProc(m_OldWndProcPtr, hWnd, msg, wParam, lParam);
        }

        if (msg == User32.WM_SYSCOMMAND)
        {
            // 屏蔽窗口顶部关闭最小化事件
            switch ((int)wParam)
            {
                case User32.SC_CLOSE:
                    // 关闭
                    User32.ShowWindowAsync(hWnd, User32.SW_HIDE);
                    return IntPtr.Zero;
                case User32.SC_MAXIMIZE:
                    // 最大化
                    break;
                case User32.SC_MINIMIZE:
                    // 最小化
                    User32.ShowWindowAsync(hWnd, User32.SW_HIDE);
                    return IntPtr.Zero;
            }
        }
        else if (msg == NotifyIconEx.WM_NOTIFY_TRAY)
        {
            // 任务栏菜单图标事件
            if ((int)wParam == NotifyIconEx.uID)
            {
                switch ((int)lParam)
                {
                    case User32.WM_LBUTTONDOWN:
                        // 左键点击
                        break;
                    case User32.WM_RBUTTONDOWN:
                        // 右键点击
                        CreateNotifyIconMenu();
                        break;
                    case User32.WM_MBUTTONDOWN:
                        // 中键点击
                        break;
                    case User32.WM_LBUTTONDBLCLK:
                        // 左键双击：恢复为普通窗口
                        if (Controller != null) Controller.RestoreToWindow();
                        else User32.ShowWindowAsync(hWnd, User32.SW_SHOW);
                        break;
                    case User32.WM_RBUTTONDBLCLK:
                        // 右键双击
                        break;
                }
            }
        }
        else if (msg == User32.WM_COMMAND)
        {
            // 任务栏菜单点击事件
            switch ((int)wParam)
            {
                case AboutID:
                    // 关于：在默认浏览器打开作者主页（IL2CPP 下 Process.Start 不可用，走 ShellExecute）
                    Debug.Log("托盘菜单点击「关于」，打开 https://www.xuefei.net.cn");
                    User32.ShellExecute(IntPtr.Zero, "open", "https://www.xuefei.net.cn",
                        null, null, User32.SW_SHOWNORMAL);
                    break;
                case ShowID:
                    // 显示壁纸（壁纸窗口是 WorkerW 子窗口，用 SW_SHOW 而非 SW_MAXIMIZE）
                    User32.ShowWindowAsync(hWnd, User32.SW_SHOW);
                    break;
                case HideID:
                    // 隐藏壁纸
                    User32.ShowWindowAsync(hWnd, User32.SW_HIDE);
                    break;
                case QuitID:
                    // 退出（先解除壁纸挂接，再退出）
                    Debug.Log("[ExitTrace] 托盘菜单点击「退出」");
                    if (Controller != null) Controller.QuitApp();
                    else User32.TerminateProcess(User32.GetCurrentProcess(), 0);
                    break;
                default:
                    Debug.Log($"未处理的菜单点击事件 ID值:{(int)wParam}");
                    break;
            }
        }
        else if (msg == User32.WM_LBUTTONDOWN)
        {
            // 鼠标点击事件
            //var x = LOWORD(lParam);
            //var y = HIWORD(lParam);
        }

        //Debug.Log("WndProc msg:0x" + msg.ToString("x4") + " wParam:0x" + wParam.ToString("x4") + " lParam:0x" + lParam.ToString("x4"));
        return User32.CallWindowProc(m_OldWndProcPtr, hWnd, msg, wParam, lParam);
    }

    // 创建任务栏菜单
    private static void CreateNotifyIconMenu()
    {
        User32.GetCursorPos(out var cursorPoint);
        IntPtr menuPtr = User32.CreatePopupMenu();
        User32.AppendMenu(menuPtr, User32.MenuFlags.MF_STRING, AboutID, "关于");
        User32.AppendMenu(menuPtr, User32.MenuFlags.MF_SEPARATOR, 0, "");
        User32.AppendMenu(menuPtr, User32.MenuFlags.MF_STRING, ShowID, "显示");
        User32.AppendMenu(menuPtr, User32.MenuFlags.MF_STRING, HideID, "隐藏");
        User32.AppendMenu(menuPtr, User32.MenuFlags.MF_STRING, QuitID, "退出");

        // 菜单宿主用独立的 1x1 透明隐藏窗口，绝不能用壁纸窗口 m_HWnd：
        // 对壁纸窗口 SetForegroundWindow 会改变它在 Progman 子窗口链里的 z 序，
        // 两次 z 序变化（提上去 + 压回来）会让桌面图标层整片重绘 → 图标一起闪一下。
        IntPtr host = m_MenuHost != IntPtr.Zero ? m_MenuHost : m_HWnd;

        // 注意:调用SetForegroundWindow是为了鼠标点击别处时隐藏弹出的菜单，不能省略
        // https://stackoverflow.com/questions/4145561/system-tray-context-menu-doesnt-disappear
        User32.SetForegroundWindow(host);

        // 菜单点击时发送 WM_COMMAND，由 MenuHostWndProc 转发回主窗口 WndProc 处理
        User32.TrackPopupMenuEx(
            menuPtr,
            2,
            cursorPoint.X,
            cursorPoint.Y,
            host,
            IntPtr.Zero
        );
        User32.DestroyMenu(menuPtr);

        // 兜底：仅当壁纸窗口 z 序真的漂移时才校正。正常情况下内部直接 return，
        // 不触发 SetWindowPos，因此不会引起图标重绘（无闪烁）。
        if (Controller != null)
        {
            Controller.EnsureZOrderBelowIcons(false);
        }
    }

    class MonoPInvokeCallbackAttribute : Attribute
    {
        public MonoPInvokeCallbackAttribute()
        {
        }
    }

    #endregion
}