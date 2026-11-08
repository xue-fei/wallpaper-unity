using System;
using System.Runtime.InteropServices;
using UnityEngine;

// ============================================================================
// 桌面幻灯片守卫
//
// 作用：壁纸程序运行期间，关闭 Windows 桌面背景的「幻灯片放映 / Windows 聚焦」，
//       避免 Windows 周期性刷新桌面（Progman → WorkerW 重建）导致壁纸窗口被销毁。
//       程序退出时恢复用户原来的设置。
//
// 关键注册表：
//   HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers
//     BackgroundType：0=图片 1=纯色 2=幻灯片 3=Windows聚焦
//   HKCU\Software\xuefei\WallPaper
//     SavedBackgroundType：启动时记录的用户原值（退出时恢复用）
//
// 为什么用 P/Invoke 而不是 Microsoft.Win32.Registry：
//   Unity IL2CPP 走 .NET Standard 2.1，不含 Registry 类，必须用 advapi32。
// ============================================================================
public static class DesktopSlideshowGuard
{
    // ---------- BackgroundType 取值 ----------
    const int TYPE_PICTURE   = 0;
    const int TYPE_SOLID     = 1;
    const int TYPE_SLIDESHOW  = 2;
    const int TYPE_SPOTLIGHT = 3;

    // ---------- 注册表路径 ----------
    const string WALLPAPERS_KEY = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers";
    const string SETTINGS_KEY   = @"Software\xuefei\WallPaper";
    const string SAVED_VALUE    = "SavedBackgroundType";

    // ---------- Win32 常量 ----------
    const int HKEY_CURRENT_USER = unchecked((int)0x80000001);
    const int KEY_READ    = 0x20019;
    const int KEY_WRITE   = 0x20006;
    const uint REG_DWORD  = 4;
    const uint WM_SETTINGCHANGE = 0x001A;
    const uint SMTO_ABORTIFHUNG = 0x0002;
    // HWND_BROADCAST 是同步广播：退出路径要尽快返回，否则「点退出」会明显卡顿。
    // 注册表值在广播前已写入，Explorer 处理 WM_SETTINGCHANGE 通常 <10ms；
    // SMTO_ABORTIFHUNG 会跳过挂起窗口，200ms 超时不影响最终结果。
    const uint BROADCAST_TIMEOUT_MS = 200;
    static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

    // ---------- P/Invoke ----------
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern int RegOpenKeyEx(IntPtr hKey, string lpSubKey, uint ulOptions, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern int RegCreateKeyEx(IntPtr hKey, string lpSubKey, uint reserved, string lpClass,
        uint dwOptions, int samDesired, IntPtr lpSecurityAttributes, out IntPtr phkResult, out uint lpdwDisposition);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern int RegQueryValueEx(IntPtr hKey, string lpValueName, IntPtr lpReserved,
        out uint lpType, IntPtr lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern int RegSetValueEx(IntPtr hKey, string lpValueName, uint reserved, uint dwType,
        byte[] lpData, uint cbData);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern int RegDeleteValue(IntPtr hKey, string lpValueName);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern int RegCloseKey(IntPtr hKey);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    // 防止退出路径重复恢复（Update / OnApplicationQuit / OnDestroy 可能都会走到）
    static bool s_Restored;

    /// <summary>
    /// 壁纸启动时调用：先还原上次异常退出残留的设置，再记录当前值，必要时关闭幻灯片。
    /// </summary>
    public static void OnWallpaperStart()
    {
        if (Application.isEditor) return;   // 编辑器下不动系统设置
        s_Restored = false;

        try
        {
            // 1) 上次异常退出（有残留保存值）→ 先还原，避免用户设置丢失
            int saved = ReadDWord(SETTINGS_KEY, SAVED_VALUE, -1);
            if (saved >= 0)
            {
                WriteDWord(WALLPAPERS_KEY, "BackgroundType", saved);
                NotifyExplorer();
                Debug.Log("[SlideshowGuard] 检测到上次未还原，已恢复 BackgroundType=" + saved);
            }

            // 2) 记录当前背景类型
            int current = ReadDWord(WALLPAPERS_KEY, "BackgroundType", TYPE_PICTURE);
            WriteDWord(SETTINGS_KEY, SAVED_VALUE, current);
            Debug.Log("[SlideshowGuard] 已记录当前 BackgroundType=" + current
                + "（" + TypeName(current) + "）");

            // 3) 幻灯片 / Windows 聚焦 → 关闭（改为图片）
            if (current == TYPE_SLIDESHOW || current == TYPE_SPOTLIGHT)
            {
                WriteDWord(WALLPAPERS_KEY, "BackgroundType", TYPE_PICTURE);
                NotifyExplorer();
                Debug.Log("[SlideshowGuard] 已关闭系统桌面幻灯片（原类型=" + TypeName(current) + "）");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[SlideshowGuard] 启动处理异常: " + e.Message);
        }
    }

    /// <summary>
    /// 壁纸关闭时调用：恢复用户原来的背景类型。
    /// </summary>
    public static void OnWallpaperStop()
    {
        if (Application.isEditor) return;
        if (s_Restored) return;
        s_Restored = true;

        try
        {
            int saved = ReadDWord(SETTINGS_KEY, SAVED_VALUE, -1);
            if (saved < 0)
            {
                Debug.Log("[SlideshowGuard] 无保存值，跳过恢复");
                return;
            }

            WriteDWord(WALLPAPERS_KEY, "BackgroundType", saved);
            DeleteDWord(SETTINGS_KEY, SAVED_VALUE);
            NotifyExplorer();
            Debug.Log("[SlideshowGuard] 已恢复 BackgroundType=" + saved
                + "（" + TypeName(saved) + "）");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[SlideshowGuard] 恢复异常: " + e.Message);
        }
    }

    static string TypeName(int t)
    {
        switch (t)
        {
            case TYPE_PICTURE:   return "图片";
            case TYPE_SOLID:     return "纯色";
            case TYPE_SLIDESHOW:  return "幻灯片";
            case TYPE_SPOTLIGHT: return "Windows聚焦";
            default:             return "未知(" + t + ")";
        }
    }

    // ============================================================================
    // 注册表读写
    // ============================================================================

    static int ReadDWord(string keyPath, string valueName, int defaultValue)
    {
        IntPtr hKey;
        if (RegOpenKeyEx((IntPtr)HKEY_CURRENT_USER, keyPath, 0, KEY_READ, out hKey) != 0)
            return defaultValue;
        try
        {
            uint type;
            uint size = 4;
            IntPtr pData = Marshal.AllocHGlobal(4);
            try
            {
                if (RegQueryValueEx(hKey, valueName, IntPtr.Zero, out type, pData, ref size) == 0
                    && type == REG_DWORD && size == 4)
                {
                    byte[] buf = new byte[4];
                    Marshal.Copy(pData, buf, 0, 4);
                    return BitConverter.ToInt32(buf, 0);
                }
            }
            finally { Marshal.FreeHGlobal(pData); }
            return defaultValue;
        }
        finally { RegCloseKey(hKey); }
    }

    static bool WriteDWord(string keyPath, string valueName, int value)
    {
        IntPtr hKey;
        uint disposition;
        if (RegCreateKeyEx((IntPtr)HKEY_CURRENT_USER, keyPath, 0, null, 0,
                KEY_WRITE, IntPtr.Zero, out hKey, out disposition) != 0)
            return false;
        try
        {
            byte[] data = BitConverter.GetBytes(value);
            return RegSetValueEx(hKey, valueName, 0, REG_DWORD, data, 4) == 0;
        }
        finally { RegCloseKey(hKey); }
    }

    static bool DeleteDWord(string keyPath, string valueName)
    {
        IntPtr hKey;
        if (RegOpenKeyEx((IntPtr)HKEY_CURRENT_USER, keyPath, 0, KEY_WRITE, out hKey) != 0)
            return false;
        try { return RegDeleteValue(hKey, valueName) == 0; }
        finally { RegCloseKey(hKey); }
    }

    /// <summary>
    /// 广播设置变更，让 Explorer 立即重新读取背景设置（否则要等下次切换/重启 Explorer）
    /// </summary>
    static void NotifyExplorer()
    {
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero,
            "Wallpapers", SMTO_ABORTIFHUNG, BROADCAST_TIMEOUT_MS, out _);
    }
}
