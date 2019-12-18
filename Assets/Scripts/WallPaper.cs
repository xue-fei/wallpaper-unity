using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;
using System.Windows.Forms;
using System.Drawing;

public class WallPaper : MonoBehaviour
{
    //NotifyIcon 设置托盘相关参数
    NotifyIcon notifyIcon = new NotifyIcon();
    //托盘图标的宽高
    int _width = 50, _height = 50;

    public Text t;

    public int ResWidth;//窗口宽度
    public int ResHeight;//窗口高度

    IntPtr wallPaper;
    IntPtr progman;
    IntPtr result;

    void Main()
    { 
        ResWidth = UnityEngine.Screen.width;
        ResHeight = UnityEngine.Screen.height;
        UnityEngine.Screen.SetResolution(ResWidth, ResHeight, true, 30);
        //注释掉这一行，编辑器程序就会变成桌面背景
        if (UnityEngine.Application.platform == RuntimePlatform.WindowsPlayer)
        {
            wallPaper = GetForegroundWindow();
            SetWindowText(wallPaper.ToInt32(), UnityEngine.Application.productName);
            progman = FindWindow("Progman", null);

            result = IntPtr.Zero;

            // 向 Program Manager 窗口发送 0x52c 的一个消息，超时设置为0x3e8（1秒）。
            SendMessageTimeout(progman, 0x52c, IntPtr.Zero, IntPtr.Zero, 0, 0x3e8, result);

            EnumWindows((hwnd, lParam) =>
            {
                // 找到包含 SHELLDLL_DefView 这个窗口句柄的 WorkerW
                if (FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    // 找到当前 WorkerW 窗口的，后一个 WorkerW 窗口。 
                    IntPtr tempHwnd = FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);

                    // 隐藏这个窗口
                    ShowWindow(tempHwnd, 0);
                }
                return true;
            }, IntPtr.Zero);

            SetParent(wallPaper, progman);
        }
    }

    private void Start()
    {
        InitTray();
    }

    public void InitTray()
    {
        //托盘气泡显示内容
        notifyIcon.BalloonTipText = "Unity壁纸程序已启动";
        notifyIcon.Text = "Unity壁纸程序";
        //托盘按钮是否可见 
        notifyIcon.Visible = true;
        notifyIcon.Icon = SetTrayIcon(@UnityEngine.Application.streamingAssetsPath + "/icon.png", _width, _height);
        //托盘气泡显示时间
        notifyIcon.ShowBalloonTip(2000);

        MenuItem help = new MenuItem("帮助");
        help.Click += new EventHandler(help_Click);
        MenuItem exit = new MenuItem("关闭");
        exit.Click += new EventHandler(exit_Click);
        MenuItem[] childen = new MenuItem[] { help, exit };
        notifyIcon.ContextMenu = new System.Windows.Forms.ContextMenu(childen);
    }

    /// <summary>  
    /// 帮助选项  
    /// </summary>  
    /// <param name="sender"></param>  
    /// <param name="e"></param>  
    private void help_Click(object sender, EventArgs e)
    {
        UnityEngine.Application.OpenURL("https://www.xuefei.net.cn");
    }

    private void exit_Click(object sender, EventArgs e)
    {
        UnityEngine.Application.Quit();
    }

    /// <summary>
    /// 设置程序托盘图标
    /// </summary>
    /// <param name="iconPath">图标路径</param>
    /// <param name="width">宽</param>
    /// <param name="height">高</param>
    /// <returns>图标</returns>
    private Icon SetTrayIcon(string iconPath, int width, int height)
    {
        Bitmap bt = new Bitmap(iconPath);
        Bitmap fitSizeBt = new Bitmap(bt, width, height);
        return Icon.FromHandle(fitSizeBt.GetHicon());
    }

    // Update is called once per frame
    void Update()
    {
        t.text = Time.time.ToString();
    }

    void OnGUI()
    {
        if (Event.current.isMouse && Event.current.type == EventType.MouseDown && Event.current.clickCount == 2)
        {
            UnityEngine.Debug.Log("ni shuang ji");
            Process process = new Process();
            process.StartInfo.FileName = "C:/Program Files/Unity/Editor/Unity.exe";
            process.StartInfo.CreateNoWindow = false;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
            process.Start();
        }
    }

    private void OnApplicationFocus(bool focus)
    {
        if (UnityEngine.Application.platform == RuntimePlatform.WindowsPlayer)
        {
            if (focus)
            {

            }
            else
            {

            }
        }
    }

    private void OnApplicationQuit()
    {
        SetParent(wallPaper, IntPtr.Zero);
    }

    #region
    [DllImport("user32.dll")]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string className, string winName);

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr hwnd, IntPtr parentHwnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlage,
        uint timeout,
        IntPtr result);

    /// <summary>
    /// 获得本窗体的句柄  
    /// </summary>
    /// <returns></returns>
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 设置窗口名
    /// </summary>
    /// <param name="hwnd"></param>
    /// <param name="lpString"></param>
    /// <returns></returns>
    [DllImport("user32.dll", EntryPoint = "SetWindowText", CharSet = CharSet.Ansi)]
    public static extern int SetWindowText(int hwnd, string lpString);
    #endregion
}