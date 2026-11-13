using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Chuẩn bị project mẫu một lần: cảnh trống (Assets/Scenes/Main.unity) trong Build Settings, tên sản phẩm, IL2CPP, cửa sổ nhỏ.
/// Unity -batchmode -projectPath Sample -executeMethod SampleSetup.Prepare -quit</summary>
public static class SampleSetup
{
    public static void Prepare()
    {
        const string scene = "Assets/Scenes/Main.unity";
        System.IO.Directory.CreateDirectory("Assets/Scenes");
        var s = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        EditorSceneManager.SaveScene(s, scene);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scene, true) };
        PlayerSettings.companyName = "HotPatchKit";
        PlayerSettings.productName = "HotPatchSample";
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 640;
        PlayerSettings.defaultScreenHeight = 360;
        PlayerSettings.runInBackground = true;
        AssetDatabase.SaveAssets();
        Debug.Log("[sample] đã chuẩn bị project mẫu");
    }
}
