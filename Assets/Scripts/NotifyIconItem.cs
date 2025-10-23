using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

public class NotifyIconItem
{
    private static IntPtr m_HWnd;
    private Shell_NotifyIconEx m_NotifyIcon;

    public IntPtr HWnd => m_HWnd;

    private const int MaximizeID = 1001;                 // 任务栏菜单—最大化
    private const int MinimizeID = 1002;                 // 任务栏菜单—最小化
    private const int QuitID = 1003;                 // 任务栏菜单—退出

    public void Init()
    {
        InitWndProc();
        CreateNotifyIcon();
        //#if !UNITY_EDITOR && UNITY_EDITOR_WIN
        //            User32.ShowWindowAsync(m_HWnd, User32.SW_HIDE);
        //#endif
    }

    public void Dispose()
    {
        TermWndProc();
    }

    // 创建任务栏窗体
    private void CreateNotifyIcon()
    {
        if (m_HWnd == IntPtr.Zero)
            return;
        DirectoryInfo assetData = new DirectoryInfo(Application.dataPath);
        if (assetData.Parent == null)
            return;
        var exeFilePath = $"{assetData.Parent.FullName}\\{Application.productName}.exe";
        StringBuilder exeFileSb = new StringBuilder(exeFilePath);
        IntPtr iconPtr = Shell_NotifyIconEx.ExtractAssociatedIcon(m_HWnd, exeFileSb, out ushort uIcon);
        m_NotifyIcon = new Shell_NotifyIconEx(m_HWnd);
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
        else if (msg == Shell_NotifyIconEx.WM_NOTIFY_TRAY)
        {
            // 任务栏菜单图标事件
            if ((int)wParam == Shell_NotifyIconEx.uID)
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
                        // 左键双击
                        User32.ShowWindowAsync(hWnd, User32.SW_SHOW);
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
                case MinimizeID:
                    // 最小化
                    User32.ShowWindowAsync(hWnd, User32.SW_MINIMIZE);
                    break;
                case MaximizeID:
                    // 最大化
                    User32.ShowWindowAsync(hWnd, User32.SW_MAXIMIZE);
                    break;
                case QuitID:
                    // 关闭
                    Application.Quit(0);
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
        User32.AppendMenu(menuPtr, User32.MenuFlags.MF_STRING, MinimizeID, "隐藏壁纸");
        User32.AppendMenu(menuPtr, User32.MenuFlags.MF_STRING, MaximizeID, "显示壁纸");
        User32.AppendMenu(menuPtr, User32.MenuFlags.MF_SEPARATOR, 0, "");
        User32.AppendMenu(menuPtr, User32.MenuFlags.MF_STRING, QuitID, "退出");

        // 注意:调用SetForegroundWindow是为了鼠标点击别处时隐藏弹出的菜单，不能省略
        // https://stackoverflow.com/questions/4145561/system-tray-context-menu-doesnt-disappear
        User32.SetForegroundWindow(m_HWnd);
        // 菜单点击时会发送WinUser32.WM_COMMAND消息,wParam为菜单的ID值
        User32.TrackPopupMenuEx(
            menuPtr,
            2,
            cursorPoint.X,
            cursorPoint.Y,
            m_HWnd,
            IntPtr.Zero
        );
        User32.DestroyMenu(menuPtr);
    }

    class MonoPInvokeCallbackAttribute : Attribute
    {
        public MonoPInvokeCallbackAttribute() { }
    }

    #endregion
}