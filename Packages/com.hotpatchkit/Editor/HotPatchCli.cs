using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HotPatchKit.Editor
{
    /// <summary>
    /// Lệnh batch (Unity -batchmode -projectPath … -buildTarget Win64|Android|iOS -executeMethod HotPatchKit.Editor.HotPatchCli.X) —
    /// Tools/hotpatch.ps1 gọi sẵn:
    /// <list type="bullet">
    /// <item><c>Install</c>: cài HybridCLR + tạo file cấu hình / khoá nếu chưa có.</item>
    /// <item><c>Build -hpOut &lt;đường dẫn&gt; [-hpDev] [-hpScenes a.unity,b.unity]</c>: dựng bản cài vá được (mặc định các cảnh đang bật
    /// trong Build Settings). Windows: -hpOut là thư mục thì thêm &lt;tên sản phẩm&gt;.exe.</item>
    /// <item><c>Publish [-hpCdn &lt;thư mục&gt;] [-hpBase mã,mã] [-hpBases N]</c>: sinh bản vá cho các bản cài trong kho.</item>
    /// <item><c>List</c>: in các bản cài trong kho.</item>
    /// </list>
    /// </summary>
    public static class HotPatchCli
    {
        public static void Install() => Run(HotPatchBuild.InstallHybridCLR);

        public static void Build() => Run(() =>
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var outPath = Arg("-hpOut") ?? throw new ArgumentException("cần -hpOut <đường dẫn bản dựng>");
            outPath = Path.GetFullPath(outPath);
            if ((target == BuildTarget.StandaloneWindows64 || target == BuildTarget.StandaloneWindows) && !outPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                outPath = Path.Combine(outPath, PlayerSettings.productName + ".exe");
            if (target == BuildTarget.StandaloneLinux64 && Path.GetExtension(outPath) == "")
                outPath = Path.Combine(outPath, PlayerSettings.productName + ".x86_64");
            var scenes = Arg("-hpScenes")?.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()
                         ?? EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) throw new ArgumentException("không có cảnh nào: bật cảnh trong Build Settings hoặc truyền -hpScenes");
            var report = HotPatchBuild.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes, locationPathName = outPath, target = target, targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                options = HasArg("-hpDev") ? BuildOptions.Development : BuildOptions.None,
            });
            Debug.Log($"[hotpatch] dựng: {report.summary.result}, {report.summary.totalErrors} lỗi, {report.summary.totalTime.TotalSeconds:0} s");
            return report.summary.result == BuildResult.Succeeded;
        });

        public static void Publish() => Run(() =>
        {
            var only = Arg("-hpBase")?.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var n = int.TryParse(Arg("-hpBases"), out var x) ? x : 0;
            return HotPatchBuild.Publish(EditorUserBuildSettings.activeBuildTarget, Arg("-hpCdn"), only, n);
        });

        public static void List() => Run(() =>
        {
            HotPatchBuild.LogBases(EditorUserBuildSettings.activeBuildTarget);
            return true;
        });

        static void Run(Func<bool> f)
        {
            var ok = false;
            try
            {
                ok = f();
            }
            catch (Exception e)
            {
                Debug.LogError("[hotpatch] lỗi: " + e);
            }
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        static string Arg(string name)
        {
            var a = Environment.GetCommandLineArgs();
            for (var i = 0; i + 1 < a.Length; i++)
                if (a[i] == name) return a[i + 1];
            return null;
        }

        static bool HasArg(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;
    }

    /// <summary>Menu Tools → HotPatchKit.</summary>
    static class HotPatchMenu
    {
        const string Root = "Tools/HotPatchKit/";

        [MenuItem(Root + "Tạo file cấu hình", false, 1)]
        static void CreateConfig()
        {
            HotPatchSettings.LoadOrCreate();
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<TextAsset>(HotPatchConfig.AssetPath);
        }

        [MenuItem(Root + "Cài HybridCLR", false, 2)]
        static void Install() => HotPatchBuild.InstallHybridCLR();

        [MenuItem(Root + "Dựng bản cài (dev)", false, 20)]
        static void BuildDev() => BuildMenu(true);

        [MenuItem(Root + "Dựng bản cài (phát hành)", false, 21)]
        static void BuildRelease() => BuildMenu(false);

        [MenuItem(Root + "Xuất bản bản vá (nền tảng đang chọn)", false, 40)]
        static void Publish()
        {
            var ok = HotPatchBuild.Publish(EditorUserBuildSettings.activeBuildTarget);
            EditorUtility.DisplayDialog("HotPatchKit", ok ? "Xong — xem Console, rồi đẩy thư mục cdnDir lên CDN." : "Có bản cài không vá được — xem Console.", "OK");
        }

        [MenuItem(Root + "Danh sách bản cài trong kho", false, 41)]
        static void List() => HotPatchBuild.LogBases(EditorUserBuildSettings.activeBuildTarget);

        [MenuItem(Root + "Mở thư mục khoá", false, 60)]
        static void OpenKeys()
        {
            var d = HotPatchKeys.KeyDir(HotPatchSettings.Load());
            Directory.CreateDirectory(d);
            EditorUtility.RevealInFinder(d);
        }

        static void BuildMenu(bool dev)
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
            {
                EditorUtility.DisplayDialog("HotPatchKit", "Chưa bật cảnh nào trong Build Settings.", "OK");
                return;
            }
            string path;
            if (target == BuildTarget.Android) path = EditorUtility.SaveFilePanel("Bản dựng", "", PlayerSettings.productName, "apk");
            else
            {
                path = EditorUtility.SaveFolderPanel("Thư mục bản dựng", "", "");
                if (!string.IsNullOrEmpty(path) && (target == BuildTarget.StandaloneWindows64 || target == BuildTarget.StandaloneWindows))
                    path = Path.Combine(path, PlayerSettings.productName + ".exe");
            }
            if (string.IsNullOrEmpty(path)) return;
            HotPatchBuild.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes, locationPathName = path, target = target, targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                options = dev ? BuildOptions.Development : BuildOptions.None,
            });
        }
    }
}
