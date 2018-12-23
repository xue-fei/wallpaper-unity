using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;

public class WallPaper : MonoBehaviour
{ 
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
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlage, uint timeout, IntPtr result);

    public delegate bool WNDENUMPROC(IntPtr hwnd, uint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(WNDENUMPROC lpEnumFunc, uint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, ref uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern void SetLastError(uint dwErrCode);

    public Text t;

    public int ResWidth;//窗口宽度
    public int ResHeight;//窗口高度

    IntPtr wallPaper;
    IntPtr progman;
    IntPtr result;

    void Main()
    {
        ResWidth = Screen.width;
        ResHeight = Screen.height;
        //Screen.SetResolution(ResWidth, ResHeight, true, 30);

        if (Application.platform == RuntimePlatform.WindowsPlayer)
        {
            wallPaper = GetProcessWnd();
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

    // Update is called once per frame
    void Update()
    {

    }

    private void OnApplicationFocus(bool focus)
    {
        if (Application.platform == RuntimePlatform.WindowsPlayer)
        {
            t.text = Time.time.ToString();
            if (focus)
            {

            }
            else
            {

            }
        }

    }

    public static IntPtr GetProcessWnd()
    {
        IntPtr ptrWnd = IntPtr.Zero;
        uint pid = (uint)Process.GetCurrentProcess().Id;
        // 当前进程 ID         
        bool bResult = EnumWindows(new WNDENUMPROC(delegate (IntPtr hwnd, uint lParam)
        {
            uint id = 0;
            if (GetParent(hwnd) == IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, ref id);
                if (id == lParam)
                // 找到进程对应的主窗口句柄  
                {
                    ptrWnd = hwnd;
                    // 把句柄缓存起来     
                    SetLastError(0);
                    // 设置无错误       
                    return false;
                    // 返回 false 以终止枚举窗口       
                }
            }
            return true;
        }), pid);
        return (!bResult && Marshal.GetLastWin32Error() == 0) ? ptrWnd : IntPtr.Zero;
    }
      
}
