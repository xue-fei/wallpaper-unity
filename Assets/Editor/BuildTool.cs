using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public class BuildTool : Editor
{
    static string rootPath;

    [MenuItem("工具/打包WallPaper")]
    public static void RunBuildFront()
    {
        string nowTime = DateTime.Now.ToString("yyyy.MM.dd.HH.mm.ss");
        rootPath = Application.dataPath + "/../Build/Build" + nowTime + "/";
        if (!Directory.Exists(rootPath))
        {
            Directory.CreateDirectory(rootPath);
        }

        string sceneName = "WallPaper";
        if (!Directory.Exists(rootPath + sceneName + "/"))
        {
            Directory.CreateDirectory(rootPath + sceneName + "/");
        }
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.IL2CPP);
        BuildReport br = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/WallPaper.unity" },
            rootPath + sceneName + "/" + sceneName + ".exe", BuildTarget.StandaloneWindows64, BuildOptions.None);
        if (br.files.Length < 0)
        {
            throw new Exception("BuildPlayer failure: " + br.strippingInfo);
        }
        else
        {

        }
        System.Diagnostics.Process.Start(rootPath);
    }
}