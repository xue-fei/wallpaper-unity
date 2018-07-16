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

    public Text t;

    public int ResWidth;//窗口宽度
    public int ResHeight;//窗口高度
    IntPtr wallPaper;
    void Main()
    {
        ResWidth = Screen.width;
        ResHeight = Screen.height;
        //Screen.SetResolution(ResWidth, ResHeight, true, 30);

        if (Application.platform == RuntimePlatform.WindowsPlayer)
        {
            wallPaper = FindWindow("WallPaper", null);
            IntPtr progman = FindWindow("Progman", null);

            IntPtr result = IntPtr.Zero;
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

    // Use this for initialization
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    { 

    }

    private void OnApplicationFocus(bool focus)
    {
        t.text += " focus:" + focus; 
        if(focus)
        {
            //ShowWindow(wallPaper, 0);
        }
    }

}
