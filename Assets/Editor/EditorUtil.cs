using UnityEditor;
using UnityEngine;

public class EditorUtil : MonoBehaviour
{
    [MenuItem("工具/打开沙盒文件夹")]
    static void OpenPersistentDataPath()
    {
        System.Diagnostics.Process.Start(@Application.persistentDataPath);
    }

    [MenuItem("工具/清除登录信息")]
    static void ClearPlayerPrefs()
    {
        PlayerPrefs.DeleteAll();
    }
}
 