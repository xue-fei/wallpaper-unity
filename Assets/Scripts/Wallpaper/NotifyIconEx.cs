using System;
using System.Runtime.InteropServices;

/// <summary>
/// 任务栏托盘图标封装。底层 P/Invoke（Shell_NotifyIcon / ExtractAssociatedIcon / LoadIcon）
/// 与 NOTIFYICONDATA 结构体已统一收归 User32.cs，本类只保留托盘逻辑与常量。
/// https://www.codeproject.com/articles/4972/shell-notifyiconex-with-balloon-tooltip
/// </summary>
public class NotifyIconEx
{
    // 注意:此组件由于重点在气泡提示，它要求Shell32.dll 5.0(ie 5.0) 以上

    private readonly IntPtr formTmpHwnd = IntPtr.Zero;
    // 这是一个由VersionPass 返回的属性，它允许开发者检测当前机子的Shell32.dll(可能在win95 或未知平台上版本) 合适此组，不符则用.net 自己的notifyicon
    // 这是一个私有标志，它允许开发者在程序退出时忘记调用DelNotifyBox 来清除图标时会自动在析构里清掉它。
    private bool forgetDelNotifyBox = false;

    // 这是调用此组件的主窗口句柄（当前实例有效，可多个icon 不冲突）
    internal IntPtr formHwnd = IntPtr.Zero;
    // 这是菜单的句柄（当前实例有效，可多个icon 不冲突）
    internal IntPtr contextMenuHwnd = IntPtr.Zero;

    public NotifyIconEx(IntPtr windHandle) // 构造
    {
        formTmpHwnd = windHandle;
    }

    ~NotifyIconEx()
    {
        if (forgetDelNotifyBox)
            this.DelNotifyBox(); //如果开发者忘记则清理icon
    }

    #region API_Consts
    public const int WM_NOTIFY_TRAY = 0x0400 + 2001;
    public const int uID = 5000;

    // 常数定义，有VC 的可以参见 shellapi.h
    private const int NIIF_NONE = 0x00;
    private const int NIIF_INFO = 0x01;
    private const int NIIF_WARNING = 0x02;
    private const int NIIF_ERROR = 0x03;

    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIF_STATE = 0x08;
    private const int NIF_INFO = 0x10;

    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;
    private const int NIM_SETFOCUS = 0x03;
    private const int NIM_SETVERSION = 0x04;

    private const int NIS_HIDDEN = 0x01;
    private const int NIS_SHAREDICON = 0x02;

    private const int NOTIFYICON_OLDVERSION = 0x00;
    private const int NOTIFYICON_VERSION = 0x03;

    public enum SystemIcons
    {
        IDI_APPLICATION = 32512,
        IDI_HAND = 32513,
        IDI_QUESTION = 32514,
        IDI_EXCLAMATION = 32515,
        IDI_ASTERISK = 32516,
        IDI_WINLOGO = 32517,
        IDI_WARNING = IDI_EXCLAMATION,
        IDI_ERROR = IDI_HAND,
        IDI_INFORMATION = IDI_ASTERISK,
    }
    #endregion

    /// <summary>
    /// 建一个结构
    /// </summary>
    private User32.NOTIFYICONDATA GetNOTIFYICONDATA(IntPtr iconHwnd, string sTip, string boxTitle, string boxText)
    {
        User32.NOTIFYICONDATA nData = new User32.NOTIFYICONDATA();
        // 结构的大小
        nData.cbSize = Marshal.SizeOf(nData);
        // 处理消息循环的窗体句柄，可以移成主窗体
        nData.hwnd = formTmpHwnd;
        // 消息的 WParam，回调时用
        nData.uID = uID;
        // 标志，表示由消息、图标、提示、信息组成
        nData.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_INFO;
        // 消息ID，回调用
        nData.uCallbackMessage = WM_NOTIFY_TRAY;
        if (iconHwnd != IntPtr.Zero)
        {
            nData.hIcon = iconHwnd;
        }
        else
        {
            // 使用默认的程序图标
            nData.hIcon = User32.LoadIcon(IntPtr.Zero, (IntPtr)SystemIcons.IDI_APPLICATION);
        }

        // 提示的超时值（几秒后自动消失）和版本
        //nData.uTimeoutAndVersion = 10 * 1000 | NOTIFYICON_VERSION;
        // 类型标志，有INFO、WARNING、ERROR，更改此值将影响气泡提示框的图标类型
        nData.dwInfoFlags = NIIF_INFO;

        // 图标的提示信息
        nData.szTip = sTip;
        // 气泡提示框的标题
        nData.szInfoTitle = boxTitle;
        // 气泡提示框的提示内容
        nData.szInfo = boxText;

        return nData;
    }

    /// <summary>
    /// 加一个新图标
    /// </summary>
    public int AddNotifyBox(IntPtr iconHwnd, string sTip, string boxTitle, string boxText)
    {
        User32.NOTIFYICONDATA nData = GetNOTIFYICONDATA(iconHwnd, sTip, boxTitle, boxText);
        if (User32.Shell_NotifyIcon(NIM_ADD, ref nData))
        {
            this.forgetDelNotifyBox = true;
            return 1;
        }
        else
        {
            return 0;
        }
    }

    public int DelNotifyBox()
    {
        User32.NOTIFYICONDATA nData = GetNOTIFYICONDATA(IntPtr.Zero, null, null, null);
        if (User32.Shell_NotifyIcon(NIM_DELETE, ref nData))
        {
            this.forgetDelNotifyBox = false;
            return 1;
        }
        else
        {
            return 0;
        }
    }

    public int ModiNotifyBox(IntPtr iconHwnd, string sTip, string boxTitle, string boxText)
    {
        User32.NOTIFYICONDATA nData = GetNOTIFYICONDATA(iconHwnd, sTip, boxTitle, boxText);
        return User32.Shell_NotifyIcon(NIM_MODIFY, ref nData) ? 1 : 0;
    }

    #region Optional Module //这里是可选方法
    /// <summary>
    /// 连接一个已存在的 contextMenu
    /// </summary>
    public void ConnectMyMenu(IntPtr _formHwnd, IntPtr _contextMenuHwnd)
    {
        formHwnd = _formHwnd;
        contextMenuHwnd = _contextMenuHwnd;
    }

    public void Dispose()
    {
    }
    #endregion
}
