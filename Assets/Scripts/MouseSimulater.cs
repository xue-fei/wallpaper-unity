using System;

public class MouseSimulater
{
    #region DLLs
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(MouseEventFlag dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    // ��������˵��
    // VOID mouse_event(
    //     DWORD dwFlags,         // motion and click options
    //     DWORD dx,              // horizontal position or change
    //     DWORD dy,              // vertical position or change
    //     DWORD dwData,          // wheel movement
    //     ULONG_PTR dwExtraInfo  // application-defined information
    // );

    [Flags]
    enum MouseEventFlag : uint
    {
        Move = 0x0001,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        RightDown = 0x0008,
        RightUp = 0x0010,
        MiddleDown = 0x0020,
        MiddleUp = 0x0040,
        XDown = 0x0080,
        XUp = 0x0100,
        Wheel = 0x0800,
        VirtualDesk = 0x4000,
        Absolute = 0x8000
    }
    #endregion

    // Unity��Ļ��������½ǿ�ʼ������ΪX�ᣬ����ΪY��
    // Windows��Ļ��������Ͻǿ�ʼ������ΪX�ᣬ����ΪY��

    /// <summary>
    /// �ƶ���굽ָ��λ�ã�ʹ��Unity��Ļ���������Windows��Ļ���꣩
    /// </summary>
    public static bool MoveTo(float x, float y)
    {
        if (x < 0 || y < 0 || x > UnityEngine.Screen.width || y > UnityEngine.Screen.height)
            return true;

        if (!UnityEngine.Screen.fullScreen)
        {
            UnityEngine.Debug.LogError("ֻ����ȫ��״̬��ʹ�ã�");
            return false;
        }

        SetCursorPos((int)x, (int)(UnityEngine.Screen.height - y));
        return true;
    }

    // �������
    public static void LeftClick(float x = -1, float y = -1)
    {
        if (MoveTo(x, y))
        {
            mouse_event(MouseEventFlag.LeftDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseEventFlag.LeftUp, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // �Ҽ�����
    public static void RightClick(float x = -1, float y = -1)
    {
        if (MoveTo(x, y))
        {
            mouse_event(MouseEventFlag.RightDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseEventFlag.RightUp, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // �м�����
    public static void MiddleClick(float x = -1, float y = -1)
    {
        if (MoveTo(x, y))
        {
            mouse_event(MouseEventFlag.MiddleDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseEventFlag.MiddleUp, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // �������
    public static void LeftDown(float x = -1, float y = -1)
    {
        if (MoveTo(x, y))
        {
            mouse_event(MouseEventFlag.LeftDown, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // ���̧��
    public static void LeftUp(float x = -1, float y = -1)
    {
        if (MoveTo(x, y))
        {
            mouse_event(MouseEventFlag.LeftUp, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // �Ҽ�����
    public static void RightDown(float x = -1, float y = -1)
    {
        if (MoveTo(x, y))
        {
            mouse_event(MouseEventFlag.RightDown, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // �Ҽ�̧��
    public static void RightUp(float x = -1, float y = -1)
    {
        if (MoveTo(x, y))
        {
            mouse_event(MouseEventFlag.RightUp, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // �м�����
    public static void MiddleDown(float x = -1, float y = -1)
    {
        if (MoveTo(x, y))
        {
            mouse_event(MouseEventFlag.MiddleDown, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // �м�̧��
    public static void MiddleUp(float x = -1, float y = -1)
    {
        if (MoveTo(x, y))
        {
            mouse_event(MouseEventFlag.MiddleUp, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // ���ֹ���
    public static void ScrollWheel(float value)
    {
        mouse_event(MouseEventFlag.Wheel, 0, 0, (uint)value, UIntPtr.Zero);
    }
}