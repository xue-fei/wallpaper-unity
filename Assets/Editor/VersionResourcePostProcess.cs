using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// ============================================================================
// 构建后处理：往 Unity 输出的 exe 里写入 RT_VERSION 版本资源（FileDescription 等）
//
// 为什么需要：
//   任务管理器「进程」页的「名称」列取自 exe 版本资源里的 FileDescription，
//   而 Unity 构建时该字段是空的，所以只显示文件名 WallPaper.exe。
//   写入 FileDescription 后，「进程」页会显示「Unity壁纸程序」。
//   （注意：「详细信息」页显示的是 exe 实际文件名，改不了，只能靠改名。）
//
// 触发方式：
//   1) 自动：每次 BuildPipeline.BuildPlayer 完成后自动执行（本文件实现 IPostprocessBuildWithReport）
//   2) 手动：菜单 工具 -> 给构建产物写入版本信息，可选任意已有 exe 打补丁
// ============================================================================

public class VersionResourcePostProcess : IPostprocessBuildWithReport
{
    // 取较大值，尽量排在其他后处理之后执行，避免版本资源被后续步骤覆盖
    public int callbackOrder { get { return 1000; } }

    public void OnPostprocessBuild(BuildReport report)
    {
        string exePath = report.summary.outputPath;
        if (string.IsNullOrEmpty(exePath)) return;
        if (!exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            VersionResourceWriter.Apply(exePath, "Unity壁纸程序", "WallPaper", "xuefei");
            Debug.Log("[VersionResource] 已写入版本资源 FileDescription=Unity壁纸程序 -> " + exePath);
        }
        catch (Exception e)
        {
            // 打补丁失败不能中断构建流程，只记警告
            Debug.LogWarning("[VersionResource] 写入版本资源失败: " + e.Message);
        }
    }

    [MenuItem("工具/给构建产物写入版本信息")]
    public static void PatchFromMenu()
    {
        string path = EditorUtility.OpenFilePanel("选择要写入版本信息的 exe", "", "exe");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            VersionResourceWriter.Apply(path, "Unity壁纸程序", "WallPaper", "xuefei");
            Debug.Log("[VersionResource] 已写入版本资源 -> " + path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VersionResource] 写入版本资源失败: " + e.Message);
            EditorUtility.DisplayDialog("写入失败", e.Message, "确定");
        }
    }
}

/// <summary>
/// VERSIONINFO 资源写入器。
/// 用 Win32 的 BeginUpdateResource / UpdateResource / EndUpdateResource 直接改 PE 资源，
/// 不依赖任何外部工具（rcedit 等），因此可以完全在编辑器代码里完成。
/// </summary>
public static class VersionResourceWriter
{
    const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    const int RT_VERSION = 16;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LockResource(IntPtr hResData);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr BeginUpdateResource(string pFileName, [MarshalAs(UnmanagedType.Bool)] bool fDeleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName,
        ushort wLanguage, IntPtr lpData, uint cbData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EndUpdateResource(IntPtr hUpdate, [MarshalAs(UnmanagedType.Bool)] bool fDiscard);

    /// <summary>
    /// 给指定 exe 写入版本资源。
    /// </summary>
    public static void Apply(string exePath, string desc, string product, string company)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("找不到 exe: " + exePath);

        exePath = Path.GetFullPath(exePath);

        byte[] fixedInfo = ReadExistingFixedInfo(exePath);
        if (fixedInfo == null)
        {
            // 读不到现有 VS_FIXEDFILEINFO 就用一个最小可用的（VS_FFI_SIGNATURE 等）
            fixedInfo = new byte[52];
            fixedInfo[0] = 0xBD; fixedInfo[1] = 0x04; fixedInfo[2] = 0xEF; fixedInfo[3] = 0xFE;
        }

        string fileVersion = "2021.3.45.8411390";
        string productVersion = "2021.3.45f2c1 (8058fe21db2f)";
        string originalFilename = Path.GetFileName(exePath);
        string legalCopyright = "(c) 2005-2025 Unity Technologies. All rights reserved.";

        string[] keys = { "CompanyName", "FileDescription", "FileVersion", "InternalName",
                          "LegalCopyright", "OriginalFilename", "ProductName", "ProductVersion" };
        string[] vals = { company, desc, fileVersion, product,
                          legalCopyright, originalFilename, product, productVersion };

        // 同时写入 en-US(0x0409) 和 zh-CN(0x0804)，保证任意系统区域设置都能读到
        byte[] blobEn = BuildVersionResource(fixedInfo, "040904B0", keys, vals, 0x0409, 1200);
        byte[] blobZh = BuildVersionResource(fixedInfo, "080404B0", keys, vals, 0x0804, 1200);

        // fDeleteExistingResources = false：保留 Unity 写入的图标、清单(manifest)等其他资源
        IntPtr hUpdate = BeginUpdateResource(exePath, false);
        if (hUpdate == IntPtr.Zero)
            throw new InvalidOperationException(
                "BeginUpdateResource 失败 err=" + Marshal.GetLastWin32Error() + "（exe 可能正在运行或被占用）");

        try
        {
            if (!WriteOne(hUpdate, blobEn, 0x0409))
                throw new InvalidOperationException("UpdateResource(en) 失败 err=" + Marshal.GetLastWin32Error());

            if (!WriteOne(hUpdate, blobZh, 0x0804))
                throw new InvalidOperationException("UpdateResource(zh) 失败 err=" + Marshal.GetLastWin32Error());

            if (!EndUpdateResource(hUpdate, false))
            {
                int err = Marshal.GetLastWin32Error();
                // 提交失败，丢弃句柄避免留下半写状态
                EndUpdateResource(hUpdate, true);
                hUpdate = IntPtr.Zero;
                throw new InvalidOperationException("EndUpdateResource 失败 err=" + err
                    + "（exe 可能正在运行或被占用）");
            }
            hUpdate = IntPtr.Zero;   // 提交成功，句柄已释放
        }
        finally
        {
            if (hUpdate != IntPtr.Zero)
                EndUpdateResource(hUpdate, true);
        }
    }

    static bool WriteOne(IntPtr hUpdate, byte[] blob, ushort langId)
    {
        IntPtr pData = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, pData, blob.Length);
            return UpdateResource(hUpdate, new IntPtr(RT_VERSION), new IntPtr(1),
                langId, pData, (uint)blob.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(pData);
        }
    }

    static void WriteU16(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)(value >> 8);
    }

    // 读取现有 RT_VERSION 里的 VS_FIXEDFILEINFO（52 字节，固定在资源偏移 40 处）
    static byte[] ReadExistingFixedInfo(string exePath)
    {
        IntPtr hMod = LoadLibraryEx(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (hMod == IntPtr.Zero) return null;
        try
        {
            IntPtr hRes = FindResource(hMod, new IntPtr(1), new IntPtr(RT_VERSION));
            if (hRes == IntPtr.Zero) return null;
            uint size = SizeofResource(hMod, hRes);
            if (size < 92) return null;
            IntPtr hData = LoadResource(hMod, hRes);
            if (hData == IntPtr.Zero) return null;
            IntPtr p = LockResource(hData);
            if (p == IntPtr.Zero) return null;
            byte[] buf = new byte[size];
            Marshal.Copy(p, buf, 0, (int)size);
            byte[] fixedInfo = new byte[52];
            Array.Copy(buf, 40, fixedInfo, 0, 52);
            return fixedInfo;
        }
        finally
        {
            FreeLibrary(hMod);
        }
    }

    // 构造一个「键 = 字符串值」的 VALUE 节点（无子节点）
    static byte[] BuildStringValue(string key, string value)
    {
        byte[] keyBytes = Encoding.Unicode.GetBytes(key + "\0");
        byte[] valBytes = Encoding.Unicode.GetBytes(value + "\0");
        int pos = 6 + keyBytes.Length;
        pos += (4 - (pos % 4)) % 4;
        int total = pos + valBytes.Length;

        byte[] buf = new byte[total];
        WriteU16(buf, 0, (ushort)total);
        WriteU16(buf, 2, (ushort)(value.Length + 1));   // 文本值：字符数（含结尾 null）
        WriteU16(buf, 4, 1);                            // wType = text
        Array.Copy(keyBytes, 0, buf, 6, keyBytes.Length);
        Array.Copy(valBytes, 0, buf, pos, valBytes.Length);
        return buf;
    }

    // 构造一个带子节点的块（value 可为 null）
    static byte[] BuildBlock(string key, byte[] value, ushort wValueLength, ushort wType, byte[][] children)
    {
        byte[] keyBytes = Encoding.Unicode.GetBytes(key + "\0");
        int pos = 6 + keyBytes.Length;
        pos += (4 - (pos % 4)) % 4;
        int valueOffset = pos;
        if (value != null && value.Length > 0) pos += value.Length;
        int padBeforeChildren = (children.Length > 0) ? (4 - (pos % 4)) % 4 : 0;
        pos += padBeforeChildren;
        int childrenOffset = pos;

        int childrenTotal = 0;
        for (int i = 0; i < children.Length; i++)
        {
            if (i > 0) childrenTotal += (4 - (childrenTotal % 4)) % 4;
            childrenTotal += children[i].Length;
        }
        pos += childrenTotal;
        int total = pos;

        byte[] buf = new byte[total];
        WriteU16(buf, 0, (ushort)total);
        WriteU16(buf, 2, wValueLength);
        WriteU16(buf, 4, wType);
        Array.Copy(keyBytes, 0, buf, 6, keyBytes.Length);
        if (value != null && value.Length > 0)
            Array.Copy(value, 0, buf, valueOffset, value.Length);

        int cpos = childrenOffset;
        for (int i = 0; i < children.Length; i++)
        {
            if (i > 0) cpos += (4 - (cpos % 4)) % 4;
            Array.Copy(children[i], 0, buf, cpos, children[i].Length);
            cpos += children[i].Length;
        }
        return buf;
    }

    // VarFileInfo / Translation 节点（二进制值：langID + codepage）
    static byte[] BuildTranslationNode(ushort langId, ushort codepage)
    {
        byte[] keyBytes = Encoding.Unicode.GetBytes("Translation\0");
        byte[] valBytes = new byte[]
        {
            (byte)(langId & 0xFF), (byte)(langId >> 8),
            (byte)(codepage & 0xFF), (byte)(codepage >> 8)
        };
        int pos = 6 + keyBytes.Length;
        pos += (4 - (pos % 4)) % 4;
        int total = pos + valBytes.Length;

        byte[] buf = new byte[total];
        WriteU16(buf, 0, (ushort)total);
        WriteU16(buf, 2, 4);    // 二进制值：字节数
        WriteU16(buf, 4, 0);    // wType = binary
        Array.Copy(keyBytes, 0, buf, 6, keyBytes.Length);
        Array.Copy(valBytes, 0, buf, pos, valBytes.Length);
        return buf;
    }

    static byte[] BuildVersionResource(byte[] fixedInfo, string langKey,
        string[] keys, string[] values, ushort langId, ushort codepage)
    {
        var valueNodes = new List<byte[]>();
        for (int i = 0; i < keys.Length; i++)
            valueNodes.Add(BuildStringValue(keys[i], values[i]));

        // 层级：VS_VERSION_INFO → StringFileInfo → <langKey> → VALUE...
        byte[] langBlock = BuildBlock(langKey, null, 0, 1, valueNodes.ToArray());
        byte[] stringBlock = BuildBlock("StringFileInfo", null, 0, 1, new[] { langBlock });
        byte[] varBlock = BuildBlock("VarFileInfo", null, 0, 1,
            new[] { BuildTranslationNode(langId, codepage) });

        return BuildBlock("VS_VERSION_INFO", fixedInfo, 52, 0, new[] { stringBlock, varBlock });
    }
}
