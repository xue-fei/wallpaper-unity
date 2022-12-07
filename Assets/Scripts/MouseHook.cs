using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// ���ȫ�ֹ���
/// </summary>
public class MouseHook
{
    private const int WM_MOUSEMOVE = 0x200;
    private const int WM_LBUTTONDOWN = 0x201;
    private const int WM_RBUTTONDOWN = 0x204;
    private const int WM_MBUTTONDOWN = 0x207;
    private const int WM_LBUTTONUP = 0x202;
    private const int WM_RBUTTONUP = 0x205;
    private const int WM_MBUTTONUP = 0x208;
    private const int WM_LBUTTONDBLCLK = 0x203;
    private const int WM_RBUTTONDBLCLK = 0x206;
    private const int WM_MBUTTONDBLCLK = 0x209;

    /// <summary>
    /// ��
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public class POINT
    {
        public int x;
        public int y;
    }

    /// <summary>
    /// ���ӽṹ��
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public class MouseHookStruct
    {
        public POINT pt;
        public int hWnd;
        public int wHitTestCode;
        public int dwExtraInfo;
    }

    public const int WH_MOUSE_LL = 14; // mouse hook constant

    // װ�ù��ӵĺ���
    [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
    public static extern int SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hInstance, int threadId);

    // ж�¹��ӵĺ���
    [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
    public static extern bool UnhookWindowsHookEx(int idHook);

    // ��һ�����ҵĺ���
    [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
    public static extern int CallNextHookEx(int idHook, int nCode, Int32 wParam, IntPtr lParam);

    // ȫ�ֵ�����¼�
    public event MouseEventHandler OnMouseActivity;

    // ���ӻص�����
    public delegate int HookProc(int nCode, Int32 wParam, IntPtr lParam);

    // ������깳���¼�����
    private HookProc _mouseHookProcedure;
    private static int _hMouseHook = 0; // ��깳�Ӿ��

    /// <summary>
    /// ���캯��
    /// </summary>
    public MouseHook()
    {

    }

    /// <summary>
    /// ��������
    /// </summary>
    ~MouseHook()
    {
        Stop();
    }

    /// <summary>
    /// ����ȫ�ֹ���
    /// </summary>
    public void Start()
    {
        // ��װ��깳��
        if (_hMouseHook == 0)
        {
            // ����һ��HookProc��ʵ��.
            _mouseHookProcedure = new HookProc(MouseHookProc);
            IntPtr user32 = User32.LoadLibrary("user32.dll");
            _hMouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProcedure, user32, 0);

            //����װ��ʧ��ֹͣ����
            if (_hMouseHook == 0)
            {
                Stop();
                throw new Exception("SetWindowsHookEx failed.");
            }
        }
    }

    /// <summary>
    /// ֹͣȫ�ֹ���
    /// </summary>
    public void Stop()
    {
        bool retMouse = true;

        if (_hMouseHook != 0)
        {
            retMouse = UnhookWindowsHookEx(_hMouseHook);
            _hMouseHook = 0;
        }

        // ����ж�¹���ʧ��
        if (!(retMouse))
            throw new Exception("UnhookWindowsHookEx failed.");
    }

    /// <summary>
    /// ��깳�ӻص�����
    /// </summary>
    private int MouseHookProc(int nCode, Int32 wParam, IntPtr lParam)
    {
        // ��������ִ�ж����û�Ҫ����������Ϣ
        if ((nCode >= 0) && (OnMouseActivity != null))
        {
            MouseButtons button = MouseButtons.None;
            int clickCount = 0;

            switch (wParam)
            {
                case WM_LBUTTONDOWN:
                    button = MouseButtons.Left;
                    clickCount = 1;
                    break;
                case WM_LBUTTONUP:
                    button = MouseButtons.Left;
                    clickCount = 1;
                    break;
                case WM_LBUTTONDBLCLK:
                    button = MouseButtons.Left;
                    clickCount = 2;
                    break;
                case WM_RBUTTONDOWN:
                    button = MouseButtons.Right;
                    clickCount = 1;
                    break;
                case WM_RBUTTONUP:
                    button = MouseButtons.Right;
                    clickCount = 1;
                    break;
                case WM_RBUTTONDBLCLK:
                    button = MouseButtons.Right;
                    clickCount = 2;
                    break;
            }

            // �ӻص������еõ�������Ϣ
            MouseHookStruct MyMouseHookStruct = (MouseHookStruct)Marshal.PtrToStructure(lParam, typeof(MouseHookStruct));
            MouseEventArgs e = new MouseEventArgs(button, clickCount, MyMouseHookStruct.pt.x, MyMouseHookStruct.pt.y, 0);

            // ������Ҫ�����������Ļ�е��ƶ������ܹ��ڴ˴�����
            // ������Ҫ����ʵ�ʵ�x��y���ݲ�
            if (!System.Windows.Forms.Screen.PrimaryScreen.Bounds.Contains(e.X, e.Y))
            {
                //return 1;
            }

            OnMouseActivity(this, e);
        }

        // ������һ�ι���
        return CallNextHookEx(_hMouseHook, nCode, wParam, lParam);
    }
}