using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace HotPatchKit
{
    /// <summary>Kết quả lần nạp bản vá của phiên chạy này.</summary>
    public sealed class HotPatchResult
    {
        /// <summary>Đã cài bản vá (hàm đã sửa chạy bản mới).</summary>
        public bool Installed;
        /// <summary>"cdn" / "kho" (bản đã tải lần trước) / null.</summary>
        public string Source;
        /// <summary>Phiên bản bản vá đã cài (yyyyMMdd-HHmmss-commit) hoặc null.</summary>
        public string Version;
        /// <summary>Nền tảng + mã bản cài (null: bản không vá nóng — editor, Mono, dựng thường).</summary>
        public string Platform, Aot;
        /// <summary>Lỗi khi nạp (bản vá hỏng / sai…) — app vẫn chạy mã của bản cài.</summary>
        public string Error;

        public override string ToString() =>
            Aot == null ? "bản không vá nóng" : Installed ? $"đã cài bản vá {Source} {Version} ({Platform}/{Aot})" : $"chạy mã bản cài ({Platform}/{Aot})" + (Error != null ? ": " + Error : "");
    }

    /// <summary>Thông tin nhúng vào bản cài lúc dựng (Resources/HotPatchKitBuild/boot.json): nền tảng, mã bản cài, phiên bản, khoá công khai,
    /// khoá AES (đã xáo).</summary>
    [Serializable]
    public sealed class HotPatchBootInfo
    {
        public string mode, platform, aot, version, pub, k;
        public int exp;
        public bool harden;
    }

    /// <summary>Nội dung version.json của một bản vá trên CDN.</summary>
    [Serializable]
    public sealed class HotPatchVersionInfo
    {
        public string mode, file, platform, aot, version, sha256, sig, built;
        public long size, dllSize;
    }

    /// <summary>
    /// Nạp bản vá lúc mở app: hỏi CDN <c>&lt;cdn&gt;/&lt;nền tảng&gt;/&lt;mã bản cài&gt;/version.json</c> (chờ tối đa checkTimeout giây);
    /// bản mới hơn bản đang có và đúng chữ ký thì tải, kiểm băm, lưu vào kho máy (persistentDataPath/HotPatchKit). Mất CDN thì dùng
    /// bản đã tải lần trước; không có thì chạy mã của bản cài — không bao giờ chặn người chơi. Gói nào cũng phải đúng chữ ký.
    /// Gọi trước khi chạy bất kỳ mã game nào (HotPatchBoot làm sẵn khi cấu hình autoBoot = true).
    /// <para>Dòng lệnh (thử nghiệm): <c>-hotpatch-cdn &lt;url&gt;</c> đổi gốc CDN, <c>-hotpatch-local</c> không hỏi CDN,
    /// <c>-hotpatch-cache &lt;thư mục&gt;</c> đổi kho máy.</para>
    /// </summary>
    public static class HotPatchLoader
    {
        public const string BootResource = "HotPatchKitBuild/boot";
        public const string AotResourceDir = "HotPatchKitBuild/aot";

        /// <summary>Dòng trạng thái cho màn chờ ("Đang tải bản cập nhật… 1.2 / 3.4 MB").</summary>
        public static string Status { get; private set; } = "";
        /// <summary>Tiến độ tải 0..1; -1 = không tải.</summary>
        public static float Progress { get; private set; } = -1;
        /// <summary>Kết quả (null: chưa chạy xong).</summary>
        public static HotPatchResult Result { get; private set; }

        static bool s_running, s_metaLoaded;

        sealed class Pkg
        {
            public string Source, Json;
            public HotPatchVersionInfo Info;
            public byte[] Bytes;
        }

        /// <summary>Thông tin bản cài nhúng lúc dựng; null = bản không vá nóng (editor, Mono, dựng không qua HotPatchKit).</summary>
        public static HotPatchBootInfo LoadBoot()
        {
            var t = Resources.Load<TextAsset>(BootResource);
            if (t == null) return null;
            var b = JsonUtility.FromJson<HotPatchBootInfo>(t.text);
            Resources.UnloadAsset(t);
            return b != null && b.mode == "dhe" && !string.IsNullOrEmpty(b.aot) ? b : null;
        }

        /// <summary>Xáo / gỡ xáo khoá AES nhúng trong bản cài (đối xứng; editor dùng cùng hàm).</summary>
        public static byte[] MaskKey(byte[] key, string aot)
        {
            byte[] m;
            using (var h = new SHA256Managed()) m = h.ComputeHash(Encoding.UTF8.GetBytes("hotpatchkit:" + aot));
            var r = new byte[key.Length];
            for (var i = 0; i < key.Length; i++) r[i] = (byte)(key[i] ^ m[i % m.Length]);
            return r;
        }

        /// <summary>Kiểm / tải / cài bản vá (coroutine). Chạy một lần mỗi phiên; xong thì <see cref="Result"/> có giá trị.
        /// <paramref name="cdnUrl"/> null = lấy từ cấu hình.</summary>
        public static IEnumerator Run(string cdnUrl = null)
        {
            if (Result != null) yield break;
            if (s_running)
            {
                while (Result == null) yield return null;
                yield break;
            }
            s_running = true;
            var t0 = Time.realtimeSinceStartup;
            var cfg = HotPatchConfig.Load() ?? new HotPatchConfig();
            var boot = LoadBoot();
            var r = new HotPatchResult();
            if (boot == null)
            {
                Finish(r);
                yield break;
            }
            r.Platform = boot.platform;
            r.Aot = boot.aot;
            var args = Environment.GetCommandLineArgs();
            var root = Arg(args, "-hotpatch-cdn") ?? cdnUrl ?? cfg.cdnUrl;
            if (Array.IndexOf(args, "-hotpatch-local") >= 0) root = null;
            var cacheDir = Path.Combine(Arg(args, "-hotpatch-cache") ?? Path.Combine(Application.persistentDataPath, "HotPatchKit"), boot.platform, boot.aot);
            byte[] aes;
            try
            {
                aes = MaskKey(Convert.FromBase64String(boot.k), boot.aot);
            }
            catch (Exception e)
            {
                r.Error = "thông tin bản cài hỏng: " + e.Message;
                Finish(r);
                yield break;
            }

            // (1) bản đã tải lần trước
            var cachedTask = Task.Run(() => LoadCached(cacheDir, boot));
            yield return new WaitUntil(() => cachedTask.IsCompleted);
            var cached = cachedTask.Status == TaskStatus.RanToCompletion ? cachedTask.Result : null;

            // (2) CDN
            Pkg remote = null;
            if (!string.IsNullOrEmpty(root))
            {
                Status = "Đang kiểm tra bản cập nhật…";
                var url = Url(root, boot, HotPatchPackage.VersionFile) + "?t=" + DateTime.UtcNow.Ticks;
                string json = null;
                using (var req = UnityWebRequest.Get(url))
                {
                    req.timeout = Math.Max(1, cfg.checkTimeout);
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success) json = req.downloadHandler.text;
                    else if (req.responseCode == 404) Debug.Log("[hotpatch] CDN chưa có bản vá cho bản cài này (" + boot.platform + "/" + boot.aot + ")");
                    else Debug.LogWarning("[hotpatch] không hỏi được " + url + ": " + req.error);
                }
                var info = json != null ? Parse(json) : null;
                if (json != null && !Valid(info, boot))
                    Debug.LogWarning("[hotpatch] version.json trên CDN sai chữ ký / không khớp bản cài: bỏ qua");
                else if (info != null && string.CompareOrdinal(info.version, boot.version) >= 0 && (cached == null || cached.Info.sha256 != info.sha256))
                {
                    Status = "Đang tải bản cập nhật…";
                    using (var req = UnityWebRequest.Get(Url(root, boot, HotPatchPackage.PatchFile) + "?v=" + info.sha256))
                    {
                        req.timeout = Math.Max(1, cfg.downloadTimeout);
                        var op = req.SendWebRequest();
                        while (!op.isDone)
                        {
                            Progress = req.downloadProgress;
                            if (info.size > 0) Status = $"Đang tải bản cập nhật… {req.downloadedBytes / 1048576.0:0.0} / {info.size / 1048576.0:0.0} MB";
                            yield return null;
                        }
                        Progress = -1;
                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            var p = new Pkg { Source = "cdn", Json = json, Info = info, Bytes = req.downloadHandler.data };
                            var check = Task.Run(() => HotPatchPackage.Sha256Hex(p.Bytes) == info.sha256);
                            yield return new WaitUntil(() => check.IsCompleted);
                            if (check.Status == TaskStatus.RanToCompletion && check.Result)
                            {
                                remote = p;
                                var save = Task.Run(() => SaveCache(cacheDir, p));
                                yield return new WaitUntil(() => save.IsCompleted);
                                if (save.Exception != null) Debug.LogWarning("[hotpatch] không lưu được vào kho máy: " + save.Exception.GetBaseException().Message);
                            }
                            else Debug.LogWarning("[hotpatch] gói tải về sai băm: bỏ qua");
                        }
                        else Debug.LogWarning("[hotpatch] không tải được gói: " + req.error);
                    }
                }
            }

            // (3) mới nhất trong các bản hợp lệ (bản vá phải dựng sau bản cài)
            var order = new List<Pkg>();
            foreach (var p in new[] { remote, cached })
                if (p != null && string.CompareOrdinal(p.Info.version, boot.version) >= 0 && !order.Contains(p)) order.Add(p);
            order.Sort((x, y) => string.CompareOrdinal(y.Info.version, x.Info.version));

            // (4) cài
            Status = "Đang khởi động…";
            foreach (var p in order)
            {
                if (!s_metaLoaded)
                {
                    s_metaLoaded = true;
                    yield return LoadAotMetadata(aes);
                }
                var unpack = Task.Run(() => HotPatchPackage.Unpack(p.Bytes, aes));
                yield return new WaitUntil(() => unpack.IsCompleted);
                try
                {
                    if (unpack.Status != TaskStatus.RanToCompletion) throw unpack.Exception?.GetBaseException() ?? new Exception("giải gói hỏng");
                    var asm = Assembly.Load(unpack.Result);
                    // HotPatch.On chỉ bật ở cuối Install: cài dở (lỗi giữa chừng) thì mọi ô vẫn bị bỏ qua
                    asm.GetType(HotPatchPackage.PatchNamespace + ".Installer", true).GetMethod("Install", BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
                    r.Installed = true;
                    r.Source = p.Source;
                    r.Version = p.Info.version;
                    r.Error = null;
                    break;
                }
                catch (Exception e)
                {
                    HotPatch.On = false;
                    r.Error = "không nạp được bản vá " + p.Source + " " + p.Info.version + ": " + (e.GetBaseException()?.Message ?? e.Message);
                    Debug.LogError("[hotpatch] " + r.Error);
                    TryDelete(cacheDir);
                }
            }
            Debug.Log("[hotpatch] " + r + $" — {(Time.realtimeSinceStartup - t0) * 1000:0} ms");
            Finish(r);
        }

        static void Finish(HotPatchResult r)
        {
            Status = "";
            Progress = -1;
            s_running = false;
            Result = r;
        }

        /// <summary>Gọi hàm vào game (cấu hình <c>entry</c>: "Namespace.Lớp.Hàm", static, không tham số) trong assembly mã game.</summary>
        public static void InvokeEntry(HotPatchConfig cfg = null)
        {
            cfg = cfg ?? HotPatchConfig.Load();
            if (cfg == null || string.IsNullOrEmpty(cfg.entry))
            {
                Debug.LogWarning("[hotpatch] cấu hình chưa có \"entry\" — không tự gọi vào game");
                return;
            }
            Assembly game = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == cfg.gameAssembly) game = a;
            if (game == null) throw new InvalidOperationException("[hotpatch] không thấy assembly mã game " + cfg.gameAssembly);
            var dot = cfg.entry.LastIndexOf('.');
            var type = game.GetType(cfg.entry.Substring(0, Math.Max(0, dot)), true);
            var m = type.GetMethod(cfg.entry.Substring(dot + 1), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                    ?? throw new MissingMethodException("[hotpatch] không thấy hàm static không tham số " + cfg.entry);
            m.Invoke(null, null);
        }

        /// <summary>Metadata bổ sung cho generic của phần AOT (DLL AOT đã strip của chính bản cài, nhúng lúc dựng) — trước khi nạp mã
        /// thông dịch.</summary>
        static IEnumerator LoadAotMetadata(byte[] aes)
        {
            var metas = Resources.LoadAll<TextAsset>(AotResourceDir);
            var n = 0;
            foreach (var m in metas)
            {
                var bytes = m.bytes;
                var name = m.name;
                Resources.UnloadAsset(m);
                var unpack = Task.Run(() => HotPatchPackage.Unpack(bytes, aes));
                yield return new WaitUntil(() => unpack.IsCompleted);
                if (unpack.Status != TaskStatus.RanToCompletion)
                {
                    Debug.LogError("[hotpatch] metadata AOT " + name + " hỏng: " + unpack.Exception?.GetBaseException().Message);
                    continue;
                }
#if !UNITY_EDITOR
                var err = HybridCLR.RuntimeApi.LoadMetadataForAOTAssembly(unpack.Result, HybridCLR.HomologousImageMode.SuperSet);
                if (err != HybridCLR.LoadImageErrorCode.OK) Debug.LogError("[hotpatch] metadata AOT " + name + ": " + err);
                else n++;
#endif
            }
            if (n > 0) Debug.Log("[hotpatch] metadata AOT: " + n + " DLL");
        }

        static HotPatchVersionInfo Parse(string json)
        {
            try
            {
                return JsonUtility.FromJson<HotPatchVersionInfo>(json);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static bool Valid(HotPatchVersionInfo v, HotPatchBootInfo boot) =>
            v != null && v.mode == "dhe" && (v.file ?? HotPatchPackage.PatchFile) == HotPatchPackage.PatchFile
            && v.platform == boot.platform && v.aot == boot.aot && !string.IsNullOrEmpty(v.version) && !string.IsNullOrEmpty(v.sha256)
            && HotPatchPackage.Verify(HotPatchPackage.SignedText(v.platform, v.aot, v.version, v.sha256), v.sig, boot.pub, boot.exp);

        static Pkg LoadCached(string dir, HotPatchBootInfo boot)
        {
            var jf = Path.Combine(dir, HotPatchPackage.VersionFile);
            var pf = Path.Combine(dir, HotPatchPackage.PatchFile);
            if (!File.Exists(jf) || !File.Exists(pf)) return null;
            var json = File.ReadAllText(jf);
            var info = Parse(json);
            if (!Valid(info, boot)) return null;
            var bytes = File.ReadAllBytes(pf);
            if (HotPatchPackage.Sha256Hex(bytes) != info.sha256) return null;
            return new Pkg { Source = "kho", Json = json, Info = info, Bytes = bytes };
        }

        static void SaveCache(string dir, Pkg p)
        {
            Directory.CreateDirectory(dir);
            // gói trước, json sau: hỏng giữa chừng thì json cũ không khớp băm gói mới → bỏ, lần sau tải lại
            File.WriteAllBytes(Path.Combine(dir, HotPatchPackage.PatchFile), p.Bytes);
            File.WriteAllText(Path.Combine(dir, HotPatchPackage.VersionFile), p.Json);
        }

        static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception)
            {
            }
        }

        static string Url(string root, HotPatchBootInfo boot, string file) => root.TrimEnd('/') + "/" + boot.platform + "/" + boot.aot + "/" + file;

        static string Arg(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
