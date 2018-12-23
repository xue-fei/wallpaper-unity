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

    #region 钩子相关
    public delegate int HookProc(int nCode, int wParam, IntPtr lParam);
    private static int hHook = 0;
    public const int WH_KEYBOARD_LL = 13;

    //LowLevel键盘截获，如果是WH_KEYBOARD＝2，并不能对系统键盘截取，会在你截取之前获得键盘。 
    private static HookProc KeyBoardHookProcedure;
    //键盘Hook结构函数 
    [StructLayout(LayoutKind.Sequential)]
    public class KeyBoardHookStruct
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public int dwExtraInfo;
    }
    //设置钩子 
    [DllImport("user32.dll")]
    public static extern int SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hInstance, int threadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
    //抽掉钩子 
    public static extern bool UnhookWindowsHookEx(int idHook);

    [DllImport("user32.dll")]
    //调用下一个钩子 
    public static extern int CallNextHookEx(int idHook, int nCode, int wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern int GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string name);
    #endregion

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    public static extern void Keybd_event(byte bvk,//虚拟键值 ESC键对应的是27
                                            byte bScan,//0
                                            int dwFlags,//0为按下，1按住，2释放
                                            int dwExtraInfo);


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
            wallPaper = FindWindow("WallPaper", null);
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

            GetProcessWnd();
        }
    }

    //这里可以添加自己想要的信息处理 
    private int KeyBoardHookProc(int nCode, int wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            KeyBoardHookStruct kbh = (KeyBoardHookStruct)Marshal.PtrToStructure(lParam, typeof(KeyBoardHookStruct));

            if (kbh.vkCode == 91) // 截获左win(开始菜单键) 
            {
                return 1;
            }

            if (kbh.vkCode == 92)// 截获右win 
            {
                return 1;
            }

        }

        return CallNextHookEx(hHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 安装键盘钩子 
    /// </summary>
    public void Hook()
    {
        if (hHook == 0)
        {
            KeyBoardHookProcedure = new HookProc(KeyBoardHookProc);
            hHook = SetWindowsHookEx(WH_KEYBOARD_LL, KeyBoardHookProcedure, wallPaper, 0);
            //如果设置钩子失败. 
            if (hHook == 0)
            {
                Hook_Clear();
            }
        }
    }

    //取消钩子事件 
    public static void Hook_Clear()
    {
        bool retKeyboard = true;
        if (hHook != 0)
        {
            retKeyboard = UnhookWindowsHookEx(hHook);
            hHook = 0;
        }
        //如果去掉钩子失败. 
        if (!retKeyboard)
        {
            UnityEngine.Debug.Log("UnhookWindowsHookEx failed.");
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
        if (Application.platform == RuntimePlatform.WindowsPlayer)
        {
            t.text += " focus:" + focus;
            if (focus)
            {
                //Hook(); 
            }
            else
            {
                //Hook_Clear();
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

    public delegate bool WNDENUMPROC(IntPtr hwnd, uint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(WNDENUMPROC lpEnumFunc, uint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, ref uint lpdwProcessId);
    [DllImport("kernel32.dll")]
    public static extern void SetLastError(uint dwErrCode);

}
