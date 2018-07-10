using UnityEditor;
using UnityEngine;

public class OpenPath : MonoBehaviour
{
    [MenuItem("工具/打开PersistentDataPath")]
    static void OpenPersistentDataPath()
    {
        System.Diagnostics.Process.Start(@Application.persistentDataPath);
    }
}
