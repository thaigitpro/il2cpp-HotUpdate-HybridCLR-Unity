using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using HybridCLR;
using HybridCLR.Commands;
using HybridCLR.Installer;
using HybridCLR.Settings;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.UnityLinker;
using UnityEditorInternal;
using UnityEngine;

namespace HotPatchKit.Editor
{
    /// <summary>
    /// Dựng bản cài vá được + xuất bản bản vá.
    /// <list type="bullet">
    /// <item><see cref="BuildPlayer"/>: mã game (assembly <c>gameAssembly</c>) dịch sẵn trong bản cài như bản AOT; lúc dựng chèn ô vá vào
    /// đầu mỗi hàm (HotPatchWeaver) và lưu DLL gốc (chưa chèn) + bảng mã hàm vào kho bản gốc (thư mục khoá/base). Bản cài nhúng
    /// nền tảng, mã bản cài, phiên bản, khoá công khai (Resources/HotPatchKitBuild — chỉ tồn tại trong lúc dựng).</item>
    /// <item><see cref="Publish"/>: dịch mã game hiện tại, so với DLL gốc của từng bản cài trong kho, sinh bản vá HotPatchGen
    /// (HotPatchGenerator) — chỉ hàm / lớp đổi chạy thông dịch, còn lại vẫn mã máy — mã hoá + ký, ghi
    /// &lt;cdnDir&gt;/&lt;nền tảng&gt;/&lt;mã bản cài&gt;/{patch.bytes, version.json}.</item>
    /// </list>
    /// Dựng bằng File → Build thường (không qua đây) thì bản cài không chèn ô — không vá được.
    /// </summary>
    public static class HotPatchBuild
    {
        const string GenDir = "Assets/HotPatchKitBuild";
        const string GenRes = GenDir + "/Resources/HotPatchKitBuild";
        const string HybridclrRepo = "https://github.com/focus-creative-games/hybridclr";
        const string Il2cppPlusRepo = "https://github.com/focus-creative-games/il2cpp_plus";

        // ---------------- cài HybridCLR ----------------

        /// <summary>Bật HybridCLR (kho GitHub thay gitee) rồi cài il2cpp đã vá vào Library/HybridCLR (git clone hybridclr + il2cpp_plus
        /// đúng bản Unity); tạo file cấu hình + khoá nếu chưa có.</summary>
        public static bool InstallHybridCLR()
        {
            var cfg = HotPatchSettings.LoadOrCreate();
            ConfigureHybridCLR();
            var c = new InstallerController();
            if (!c.HasInstalledHybridCLR() || c.InstalledLibil2cppVersion != c.PackageVersion)
            {
                Debug.Log("[hotpatch] cài HybridCLR " + c.PackageVersion + " (hybridclr " + c.HybridclrLocalVersion + ", il2cpp_plus " + c.Il2cppPlusLocalVersion + ")");
                c.InstallDefaultHybridCLR();
            }
            var ok = c.HasInstalledHybridCLR();
            Debug.Log("[hotpatch] HybridCLR: " + (ok ? "đã cài ở " + SettingsUtil.LocalIl2CppDir : "CHƯA cài được"));
            if (!ok) return false;
            if (cfg.hardenNative) HotPatchNative.ApplyNativePatch();
            HotPatchKeys.Load(cfg, true);
            return true;
        }

        static void ConfigureHybridCLR()
        {
            var s = HybridCLRSettings.instance;
            if (s.enable && !s.useGlobalIl2cpp && s.hybridclrRepoURL == HybridclrRepo && s.il2cppPlusRepoURL == Il2cppPlusRepo) return;
            s.enable = true;
            s.useGlobalIl2cpp = false;
            // mặc định gitee.com (máy chủ ở Trung Quốc): ngoài Trung Quốc chậm / hay đứt
            s.hybridclrRepoURL = HybridclrRepo;
            s.il2cppPlusRepoURL = Il2cppPlusRepo;
            HybridCLRSettings.Save();
        }

        static AssemblyDefinitionAsset GameAsmdef(HotPatchConfig cfg)
        {
            var path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(cfg.gameAssembly);
            var a = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(path);
            return a ?? throw new BuildFailedException("[hotpatch] không thấy file .asmdef của assembly \"" + cfg.gameAssembly + "\" (cấu hình gameAssembly) — mã game phải nằm trong một asmdef riêng");
        }

        // ---------------- dựng bản cài ----------------

        internal static bool Weaving => s_weave;
        static bool s_weave;
        static string s_archiveDir, s_archiveInfo, s_game;

        /// <summary>Dựng bản cài vá được (thay cho BuildPipeline.BuildPlayer). Development lấy từ <c>opts.options</c>.</summary>
        public static BuildReport BuildPlayer(BuildPlayerOptions opts)
        {
            var cfg = HotPatchSettings.LoadOrThrow();
            var target = opts.target;
            var group = BuildPipeline.GetBuildTargetGroup(target);
            var named = NamedBuildTarget.FromBuildTargetGroup(group);
            if (PlayerSettings.GetScriptingBackend(named) != ScriptingImplementation.IL2CPP)
            {
                PlayerSettings.SetScriptingBackend(named, ScriptingImplementation.IL2CPP);
                Debug.LogWarning("[hotpatch] " + target + ": đổi Scripting Backend sang IL2CPP (vá nóng chỉ chạy với IL2CPP)");
            }
            ConfigureHybridCLR();
            var ins = new InstallerController();
            if (!ins.HasInstalledHybridCLR()) throw new BuildFailedException("[hotpatch] HybridCLR chưa cài: Tools/HotPatchKit/Cài HybridCLR (hoặc hotpatch.ps1 -Install)");
            if (ins.InstalledLibil2cppVersion != ins.PackageVersion) throw new BuildFailedException("[hotpatch] gói HybridCLR đã đổi phiên bản: cài lại (Tools/HotPatchKit/Cài HybridCLR)");
            if (cfg.hardenNative) HotPatchNative.ApplyNativePatch();
            if (EditorUserBuildSettings.activeBuildTarget != target)
            {
                Debug.Log("[hotpatch] đổi nền tảng đang chọn " + EditorUserBuildSettings.activeBuildTarget + " → " + target);
                EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
            }
            GameAsmdef(cfg);
            var keys = HotPatchKeys.Load(cfg, true);
            var development = (opts.options & BuildOptions.Development) != 0;
            var s = HybridCLRSettings.instance;
            var oldDefs = s.hotUpdateAssemblyDefinitions;
            var oldNames = s.hotUpdateAssemblies;
            var oldDev = EditorUserBuildSettings.development;
            var aot = DheId(target, development, cfg);
            try
            {
                // mã game ở lại bản cài (AOT, có ô vá); HybridCLR vẫn bật để nạp bản vá — chỉ đổi trong bộ nhớ, file cấu hình HybridCLR giữ nguyên
                s.hotUpdateAssemblyDefinitions = new AssemblyDefinitionAsset[0];
                s.hotUpdateAssemblies = new string[0];
                EditorUserBuildSettings.development = development;
                s_weave = true;
                s_game = cfg.gameAssembly;
                s_archiveDir = Path.Combine(HotPatchKeys.BaseDir(cfg), PlatformName(target), aot);
                s_archiveInfo = InfoJson(target, aot, development, cfg, opts.locationPathName);
                PrebuildCommand.GenerateAll(); // (lượt dựng phụ để strip AOT cũng đi qua móc chèn ô)
                var version = Stamp();
                var meta = WriteBootResources(target, aot, version, keys, cfg);
                Debug.Log($"[hotpatch] dựng {target}: mã bản cài {aot}, phiên bản {version}, {(development ? "dev" : "phát hành")}; {meta}");
                var report = BuildPipeline.BuildPlayer(opts);
                if (report.summary.result == BuildResult.Succeeded)
                {
                    if (cfg.hardenNative) HotPatchNative.EncryptBuildMetadata(opts.locationPathName, target);
                    Debug.Log($"[hotpatch] bản cài {PlatformName(target)}/{aot} xong → {opts.locationPathName} (bản gốc: {s_archiveDir})");
                }
                return report;
            }
            finally
            {
                s.hotUpdateAssemblyDefinitions = oldDefs;
                s.hotUpdateAssemblies = oldNames;
                EditorUserBuildSettings.development = oldDev;
                s_weave = false;
                s_archiveDir = null;
                RemoveBootResources();
            }
        }

        /// <summary>Thông tin nhúng vào bản cài + metadata AOT (DLL AOT đã strip của chính bản cài: generic của phần AOT mà bản vá dùng
        /// với kiểu bản cài chưa dịch sẵn — thiếu thì MissingMethodException "AOT generic method not instantiated").</summary>
        static string WriteBootResources(BuildTarget target, string aot, string version, HotPatchKeys keys, HotPatchConfig cfg)
        {
            RemoveBootResources();
            Directory.CreateDirectory(GenRes + "/aot");
            var boot = new HotPatchBootInfo
            {
                mode = "dhe", platform = PlatformName(target), aot = aot, version = version, pub = keys.ModulusBase64, exp = keys.Exponent,
                k = Convert.ToBase64String(HotPatchLoader.MaskKey(keys.Aes, aot)), harden = cfg.hardenNative,
            };
            File.WriteAllText(GenRes + "/boot.json", JsonUtility.ToJson(boot, true));
            var names = new SortedSet<string>(cfg.aotMetadata ?? new string[0], StringComparer.Ordinal);
            var refs = Path.Combine(Application.dataPath, HybridCLRSettings.instance.outputAOTGenericReferenceFile);
            if (File.Exists(refs))
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(refs), "\"([^\"]+)\\.dll\""))
                    names.Add(m.Groups[1].Value);
            var strip = SettingsUtil.GetAssembliesPostIl2CppStripDir(target);
            long bytes = 0;
            var n = 0;
            foreach (var name in names)
            {
                if (name == cfg.gameAssembly) continue;
                var src = Path.Combine(strip, name + ".dll");
                if (!File.Exists(src))
                {
                    Debug.LogWarning("[hotpatch] thiếu DLL AOT đã strip " + src + " (bỏ khỏi metadata AOT)");
                    continue;
                }
                var dll = File.ReadAllBytes(src);
                var packed = HotPatchPackage.Pack(cfg.hardenNative ? HotPatchNative.WrapImage(dll) : dll, keys.Aes);
                File.WriteAllBytes(GenRes + "/aot/" + name + ".bytes", packed);
                bytes += packed.Length;
                n++;
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return $"metadata AOT {n} DLL {bytes / 1048576.0:0.0} MB";
        }

        static void RemoveBootResources()
        {
            if (!Directory.Exists(GenDir)) return;
            AssetDatabase.DeleteAsset(GenDir);
            AssetDatabase.Refresh();
        }

        // ---------------- móc chèn ô vá ----------------

        /// <summary>Sau khi Unity biên dịch mã người chơi, trước IL2CPP: lưu DLL mã game gốc vào kho bản gốc rồi chèn ô vá vào chính file đó.</summary>
        internal static void WeavePlayerScripts()
        {
            if (!s_weave) return;
            try
            {
                // Unity 6: có callback này thì bản dựng chép assembly vào Temp/StagingArea/Data/Managed cho nó sửa, UnityLinker đọc từ đó
                var dll = Path.GetFullPath("Temp/StagingArea/Data/Managed/" + s_game + ".dll");
                if (!File.Exists(dll))
                {
                    // Android / nền tảng khác: assembly staging ở đường khác — tìm trong Temp/StagingArea
                    var staging = Path.GetFullPath("Temp/StagingArea");
                    var found = Directory.Exists(staging) ? Directory.GetFiles(staging, s_game + ".dll", SearchOption.AllDirectories) : new string[0];
                    if (found.Length == 0) throw new BuildFailedException("[hotpatch] không thấy " + s_game + ".dll trong Temp/StagingArea để chèn ô vá");
                    dll = found[0];
                }
                var pdb = Path.ChangeExtension(dll, ".pdb");
                var raw = File.ReadAllBytes(dll);
                var t0 = DateTime.Now;
                var probe = ModuleDefMD.Load(raw, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
                if (probe.GetMemberRefs().Any(m => m.Name == "On" && m.DeclaringType?.FullName == HotPatchWeaver.RuntimeNamespace + ".HotPatch"))
                {
                    Debug.Log("[hotpatch] " + s_game + " đã chèn ô vá (bỏ qua)");
                    return;
                }
                // kèm PDB (dòng mã trong stack trace bản dev); dnlib không ghi lại được PDB thì bỏ PDB (xoá bản cũ: lệch offset IL)
                List<string> keys;
                byte[] woven, wovenPdb = null;
                try
                {
                    (keys, woven, wovenPdb) = WeaveModule(raw, File.Exists(pdb) ? File.ReadAllBytes(pdb) : null);
                }
                catch (ModuleWriterException e) when (File.Exists(pdb))
                {
                    Debug.LogWarning("[hotpatch] không ghi lại được PDB của " + s_game + " (" + e.Message + ") — bỏ PDB");
                    (keys, woven, wovenPdb) = WeaveModule(raw, null);
                }
                if (s_archiveDir != null)
                {
                    Directory.CreateDirectory(s_archiveDir);
                    File.WriteAllBytes(Path.Combine(s_archiveDir, s_game + ".dll"), raw);
                    HotPatchWeaver.WriteMap(Path.Combine(s_archiveDir, s_game + ".hotmap.txt"), keys);
                    File.WriteAllText(Path.Combine(s_archiveDir, "info.json"), s_archiveInfo);
                }
                File.WriteAllBytes(dll, woven);
                if (wovenPdb != null) File.WriteAllBytes(pdb, wovenPdb);
                else if (File.Exists(pdb)) File.Delete(pdb);
                Debug.Log($"[hotpatch] chèn ô vá: {keys.Count} hàm, {raw.Length / 1048576.0:0.00} → {woven.Length / 1048576.0:0.00} MB" + (wovenPdb == null ? " (không PDB)" : "")
                          + $", {(DateTime.Now - t0).TotalMilliseconds:0} ms");
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception e)
            {
                // không để Unity dựng tiếp ra bản cài không vá được
                throw new BuildFailedException("[hotpatch] chèn ô vá lỗi: " + e);
            }
        }

        static (List<string> keys, byte[] dll, byte[] pdb) WeaveModule(byte[] raw, byte[] pdb)
        {
            var module = ModuleDefMD.Load(raw, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
            if (pdb != null) module.LoadPdb(pdb);
            var keys = HotPatchWeaver.Weave(module);
            var opts = new ModuleWriterOptions(module);
            using (var ms = new MemoryStream())
            using (var ps = pdb != null ? new MemoryStream() : null)
            {
                if (ps != null)
                {
                    opts.WritePdb = true;
                    opts.PdbFileName = s_game + ".pdb";
                    opts.PdbStream = ps;
                }
                module.Write(ms, opts);
                return (keys, ms.ToArray(), ps?.ToArray());
            }
        }

        // ---------------- xuất bản bản vá ----------------

        sealed class Base
        {
            public string Dir, Aot, Built, Runtime, Dev, Harden;
        }

        /// <summary>
        /// Sinh bản vá cho nền tảng <paramref name="target"/> (phải là nền tảng đang chọn): với từng bản cài trong kho (mặc định
        /// keepBases bản mới nhất cùng runtime; <paramref name="only"/> = chỉ các mã đó) so mã game hiện tại với DLL gốc, ghi
        /// &lt;cdn&gt;/&lt;nền tảng&gt;/&lt;mã&gt;/. Bản cài dev / phát hành tự dịch mã game đúng kiểu của nó. Trả false nếu có bản cài không vá được.
        /// </summary>
        public static bool Publish(BuildTarget target, string cdnDir = null, ICollection<string> only = null, int maxBases = 0)
        {
            var cfg = HotPatchSettings.LoadOrThrow();
            var keys = HotPatchKeys.Load(cfg, false);
            ConfigureHybridCLR();
            cdnDir = string.IsNullOrEmpty(cdnDir) ? HotPatchSettings.CdnDir(cfg) : Path.GetFullPath(cdnDir);
            if (maxBases <= 0) maxBases = Math.Max(1, cfg.keepBases);
            var runtime = RuntimeId(target, cfg);
            var all = ListBases(cfg, target);
            var bases = all.Where(b => only != null && only.Count > 0 ? only.Contains(b.Aot) : b.Runtime == runtime).OrderByDescending(b => b.Built, StringComparer.Ordinal)
                .Take(only != null && only.Count > 0 ? int.MaxValue : maxBases).ToList();
            if (bases.Count == 0)
            {
                Debug.LogError($"[hotpatch] kho bản gốc không có bản cài {PlatformName(target)} nào " + (only != null && only.Count > 0 ? "có mã " + string.Join(",", only) : "cùng runtime " + runtime)
                               + " — dựng bản cài qua HotPatchKit trước");
                return false;
            }
            var okAll = true;
            foreach (var group in bases.GroupBy(b => b.Dev))
            {
                var development = group.Key == "True";
                ModuleDefMD newModule;
                try
                {
                    newModule = ModuleDefMD.Load(CompileGame(target, development, cfg));
                }
                catch (Exception e)
                {
                    Debug.LogError("[hotpatch] dịch mã game lỗi: " + e.Message);
                    return false;
                }
                foreach (var b in group)
                {
                    // mã game mới có thể gọi lớp / hàm phần AOT mà bản cài không có → không vá, phải phát hành bản cài mới
                    if (b.Runtime != runtime)
                    {
                        okAll = false;
                        Debug.LogError($"[hotpatch] {b.Aot} (dựng {b.Built}): KHÔNG vá được — runtime của bản cài ({b.Runtime}) khác hiện tại ({runtime}): "
                                       + "mã phần AOT (mọi assembly trừ mã game), link.xml, Unity, HybridCLR hoặc hardenNative đã đổi từ lúc dựng bản đó — cần bản cài mới");
                        continue;
                    }
                    var game = cfg.gameAssembly;
                    var baseModule = ModuleDefMD.Load(File.ReadAllBytes(Path.Combine(b.Dir, game + ".dll")));
                    var map = HotPatchWeaver.ReadMap(Path.Combine(b.Dir, game + ".hotmap.txt"));
                    var r = HotPatchGenerator.Generate(baseModule, newModule, map);
                    foreach (var line in r.Report) Debug.Log("[hotpatch] " + b.Aot + ": " + line);
                    if (r.Errors.Count > 0 || r.Dll == null)
                    {
                        okAll = false;
                        Debug.LogError($"[hotpatch] {b.Aot} (dựng {b.Built}): KHÔNG vá được — {r.Errors.Count} lỗi (cần bản cài mới):");
                        foreach (var e in r.Errors.Take(60)) Debug.LogError("[hotpatch]   " + e.Split('\n')[0]);
                        continue;
                    }
                    if (r.ChangedMethods + r.NewMethods + r.NewTypes + r.ReplacedTypes + r.NewOverrides == 0)
                    {
                        Debug.Log($"[hotpatch] {b.Aot}: mã game không khác bản cài — bỏ qua");
                        continue;
                    }
                    var pkg = HotPatchPackage.Pack(b.Harden == "True" ? HotPatchNative.WrapImage(r.Dll) : r.Dll, keys.Aes);
                    var platform = PlatformName(target);
                    var info = new HotPatchVersionInfo
                    {
                        mode = "dhe", file = HotPatchPackage.PatchFile, platform = platform, aot = b.Aot, version = Stamp(),
                        sha256 = HotPatchPackage.Sha256Hex(pkg), size = pkg.Length, dllSize = r.Dll.Length, built = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                    };
                    info.sig = keys.Sign(HotPatchPackage.SignedText(info.platform, info.aot, info.version, info.sha256));
                    var dir = Path.Combine(cdnDir, platform, b.Aot);
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, HotPatchPackage.PatchFile), pkg);
                    File.WriteAllText(Path.Combine(dir, HotPatchPackage.VersionFile), JsonUtility.ToJson(info, true) + "\n");
                    Debug.Log($"[hotpatch] xuất bản {dir}: bản vá {r.Dll.Length / 1024.0:0.0} KB → gói {pkg.Length / 1024.0:0.0} KB, phiên bản {info.version} ({r.Summary})");
                }
            }
            return okAll;
        }

        /// <summary>Dịch assembly mã game cho nền tảng (kiểu dev / phát hành) bằng HybridCLR — để so với bản gốc.</summary>
        static byte[] CompileGame(BuildTarget target, bool development, HotPatchConfig cfg)
        {
            var s = HybridCLRSettings.instance;
            var oldDefs = s.hotUpdateAssemblyDefinitions;
            var oldNames = s.hotUpdateAssemblies;
            try
            {
                s.hotUpdateAssemblyDefinitions = new[] { GameAsmdef(cfg) };
                s.hotUpdateAssemblies = new string[0];
                CompileDllCommand.CompileDll(target, development);
            }
            finally
            {
                s.hotUpdateAssemblyDefinitions = oldDefs;
                s.hotUpdateAssemblies = oldNames;
            }
            var path = Path.Combine(SettingsUtil.GetHotUpdateDllsOutputDirByTarget(target), cfg.gameAssembly + ".dll");
            if (!File.Exists(path)) throw new BuildFailedException("[hotpatch] chưa dịch được " + path);
            return File.ReadAllBytes(path);
        }

        static List<Base> ListBases(HotPatchConfig cfg, BuildTarget target)
        {
            var dir = Path.Combine(HotPatchKeys.BaseDir(cfg), PlatformName(target));
            if (!Directory.Exists(dir)) return new List<Base>();
            return Directory.GetDirectories(dir).Select(d =>
            {
                var info = File.Exists(Path.Combine(d, "info.json")) ? File.ReadAllText(Path.Combine(d, "info.json")) : "";
                return new Base
                {
                    Dir = d, Aot = Path.GetFileName(d), Built = JsonField(info, "built") ?? "", Runtime = JsonField(info, "runtime") ?? "",
                    Dev = JsonField(info, "development") ?? "", Harden = JsonField(info, "harden") ?? "False",
                };
            }).ToList();
        }

        /// <summary>In danh sách bản cài trong kho bản gốc của nền tảng (mới nhất trước) + có cùng runtime với mã hiện tại không.</summary>
        public static void LogBases(BuildTarget target)
        {
            var cfg = HotPatchSettings.LoadOrThrow();
            var runtime = RuntimeId(target, cfg);
            var cdn = HotPatchSettings.CdnDir(cfg);
            var sb = new StringBuilder($"[hotpatch] bản cài {PlatformName(target)} trong kho {HotPatchKeys.BaseDir(cfg)} (runtime hiện tại {runtime}):\n");
            foreach (var b in ListBases(cfg, target).OrderByDescending(b => b.Built, StringComparer.Ordinal))
            {
                var v = Path.Combine(cdn, PlatformName(target), b.Aot, HotPatchPackage.VersionFile);
                var patched = File.Exists(v) ? JsonField(File.ReadAllText(v), "version") : "-";
                sb.Append($"  {b.Aot}  dựng {b.Built}  {(b.Dev == "False" ? "phát hành" : "dev")}  {(b.Runtime == runtime ? "vá được" : "runtime khác — không vá được")}  bản vá: {patched}\n");
            }
            Debug.Log(sb.ToString());
        }

        // ---------------- mã bản cài / runtime ----------------

        /// <summary>Tên thư mục nền tảng trên CDN — trùng với tên nhúng trong bản cài.</summary>
        public static string PlatformName(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows64: return "win64";
                case BuildTarget.StandaloneWindows: return "win32";
                case BuildTarget.StandaloneOSX: return "macos";
                case BuildTarget.StandaloneLinux64: return "linux64";
                case BuildTarget.Android: return "android";
                case BuildTarget.iOS: return "ios";
                default: return target.ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// Mã runtime: băm mã nguồn mọi assembly người chơi TRỪ mã game (phần AOT — gồm HotPatchKit, HybridCLR, package), các file
        /// runtimeIdExtraFiles (link.xml…), bản Unity, bản HybridCLR, nền tảng, gia cố. Bản cài khác runtime với mã hiện tại thì không vá
        /// được (bản vá có thể gọi phần AOT mà bản cài không có). Bỏ qua phần HybridCLR sinh theo mã game (AOTGenericReferences, link.xml).
        /// </summary>
        public static string RuntimeId(BuildTarget target, HotPatchConfig cfg)
        {
            var s = HybridCLRSettings.instance;
            var skip = new[]
            {
                NormDir("Assets/" + Path.GetDirectoryName(s.outputAOTGenericReferenceFile)), NormDir("Assets/" + Path.GetDirectoryName(s.outputLinkFile)), NormDir(GenDir),
            };
            var files = new List<string>();
            foreach (var a in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                if (a.name == cfg.gameAssembly) continue;
                files.AddRange(a.sourceFiles.Where(f => !skip.Any(d => d.Length > 1 && f.Replace('\\', '/').StartsWith(d, StringComparison.OrdinalIgnoreCase))));
                var asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(a.name);
                if (!string.IsNullOrEmpty(asmdef)) files.Add(asmdef);
            }
            foreach (var f in cfg.runtimeIdExtraFiles ?? new string[0])
                if (File.Exists(Physical(f))) files.Add(f);
            return HashFiles(files, "hotpatchkit 1", "unity " + Application.unityVersion, "hybridclr " + new InstallerController().PackageVersion, "target " + target,
                "harden " + (cfg.hardenNative ? HotPatchNative.KeyFingerprint() : "off"));
        }

        /// <summary>Mã bản cài: runtime + toàn bộ mã nguồn mã game + cờ development — mỗi bản cài một mã, bản vá sinh riêng theo DLL gốc
        /// của đúng bản đó.</summary>
        public static string DheId(BuildTarget target, bool development, HotPatchConfig cfg)
        {
            var game = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).FirstOrDefault(a => a.name == cfg.gameAssembly)
                       ?? throw new BuildFailedException("[hotpatch] không thấy assembly mã game " + cfg.gameAssembly);
            var files = game.sourceFiles.ToList();
            var asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(cfg.gameAssembly);
            if (!string.IsNullOrEmpty(asmdef)) files.Add(asmdef);
            return HashFiles(files, "dhe", "runtime " + RuntimeId(target, cfg), "development " + development);
        }

        static string NormDir(string d) => d.Replace('\\', '/').TrimEnd('/') + "/";

        static string Physical(string path) => Path.GetFullPath(FileUtil.GetPhysicalPath(path));

        static string HashFiles(IEnumerable<string> files, params string[] extra)
        {
            var rel = files.Select(f => f.Replace('\\', '/')).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
            using (var h = SHA256.Create())
            {
                void Add(string s)
                {
                    var b = Encoding.UTF8.GetBytes(s + "\n");
                    h.TransformBlock(b, 0, b.Length, null, 0);
                }
                foreach (var e in extra) Add(e);
                foreach (var r in rel)
                {
                    Add("file " + r);
                    var p = Physical(r);
                    Add(File.Exists(p) ? File.ReadAllText(p).Replace("\r\n", "\n") : "(thiếu)"); // CRLF / LF tuỳ máy (git autocrlf)
                }
                h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var sb = new StringBuilder();
                foreach (var b in h.Hash) sb.Append(b.ToString("x2"));
                return sb.ToString().Substring(0, 12);
            }
        }

        static string InfoJson(BuildTarget target, string aot, bool development, HotPatchConfig cfg, string output)
        {
            string J(string x) => (x ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "{\n" + $"  \"platform\": \"{PlatformName(target)}\",\n  \"aot\": \"{aot}\",\n  \"runtime\": \"{RuntimeId(target, cfg)}\",\n" +
                   $"  \"development\": \"{development}\",\n  \"harden\": \"{cfg.hardenNative}\",\n  \"commit\": \"{GitCommit()}\",\n" +
                   $"  \"built\": \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",\n  \"out\": \"{J(output)}\"\n" + "}\n";
        }

        static string JsonField(string json, string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json ?? "", "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Phiên bản: thời điểm + commit git (so theo chuỗi — tăng theo thời gian).</summary>
        static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + GitCommit();

        static string GitCommit()
        {
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "rev-parse --short HEAD")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = HotPatchSettings.ProjectRoot,
                });
                var s = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                return string.IsNullOrEmpty(s) || p.ExitCode != 0 ? "nogit" : s;
            }
            catch (Exception)
            {
                return "nogit";
            }
        }
    }

    /// <summary>Móc dựng: chèn ô vá vào mã game sau khi Unity biên dịch mã người chơi (chỉ khi dựng qua HotPatchBuild).</summary>
    sealed class HotPatchWeaveHook : IPostBuildPlayerScriptDLLs
    {
        public int callbackOrder => 1000;

        public void OnPostBuildPlayerScriptDLLs(BuildReport report) => HotPatchBuild.WeavePlayerScripts();
    }

    /// <summary>Không strip HotPatchKit.Runtime và mã game: bản vá có thể gọi bất kỳ hàm nào của chúng, kể cả hàm bản cài chưa dùng tới.</summary>
    sealed class HotPatchLinker : IUnityLinkerProcessor
    {
        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            var cfg = HotPatchSettings.Load();
            var path = Path.Combine(HotPatchSettings.ProjectRoot, "Temp", "HotPatchKit", "link.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var game = HotPatchBuild.Weaving && cfg != null && !string.IsNullOrEmpty(cfg.gameAssembly)
                ? $"  <assembly fullname=\"{cfg.gameAssembly}\" preserve=\"all\" />\n" : "";
            File.WriteAllText(path, "<linker>\n  <assembly fullname=\"" + HotPatchWeaver.RuntimeAssembly + "\" preserve=\"all\" />\n" + game + "</linker>\n");
            return path;
        }
    }
}
