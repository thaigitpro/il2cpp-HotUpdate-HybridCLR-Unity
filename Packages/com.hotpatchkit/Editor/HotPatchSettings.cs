using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEngine;

namespace HotPatchKit.Editor
{
    /// <summary>Đọc / tạo file cấu hình <see cref="HotPatchConfig.AssetPath"/> (editor đọc thẳng file, không qua Resources).</summary>
    public static class HotPatchSettings
    {
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        static string FilePath => Path.Combine(ProjectRoot, HotPatchConfig.AssetPath);

        /// <summary>Cấu hình hiện tại; chưa có file → null.</summary>
        public static HotPatchConfig Load()
        {
            var p = FilePath;
            return File.Exists(p) ? JsonUtility.FromJson<HotPatchConfig>(File.ReadAllText(p)) : null;
        }

        /// <summary>Cấu hình hiện tại; chưa có file → lỗi dựng kèm hướng dẫn.</summary>
        public static HotPatchConfig LoadOrThrow()
        {
            var c = Load() ?? throw new BuildFailedException("[hotpatch] chưa có " + HotPatchConfig.AssetPath + " — menu Tools/HotPatchKit/Tạo file cấu hình");
            if (string.IsNullOrEmpty(c.gameAssembly)) throw new BuildFailedException("[hotpatch] cấu hình thiếu \"gameAssembly\"");
            return c;
        }

        /// <summary>Có file thì đọc, chưa có thì tạo file mặc định (thư mục khoá theo tên sản phẩm).</summary>
        public static HotPatchConfig LoadOrCreate()
        {
            var c = Load();
            if (c != null) return c;
            c = new HotPatchConfig { keyDir = "%USERPROFILE%/.hotpatchkit/" + SafeName(PlayerSettings.productName) };
            // đoán assembly mã game: asmdef đầu tiên trong Assets (trừ Editor / test)
            var guess = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies)
                .Where(a => a.sourceFiles.Length > 0 && a.sourceFiles.All(f => f.StartsWith("Assets/", StringComparison.Ordinal)) && a.name != "Assembly-CSharp" && a.name != "Assembly-CSharp-firstpass")
                .Select(a => a.name).FirstOrDefault();
            if (guess != null) c.gameAssembly = guess;
            Save(c);
            Debug.Log("[hotpatch] tạo " + HotPatchConfig.AssetPath + " (gameAssembly = " + c.gameAssembly + ") — sửa \"entry\" và \"cdnUrl\" cho đúng project");
            return c;
        }

        public static void Save(HotPatchConfig c)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonUtility.ToJson(c, true) + "\n", new UTF8Encoding(false));
            AssetDatabase.ImportAsset(HotPatchConfig.AssetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>Thư mục ra của bản vá (tuyệt đối).</summary>
        public static string CdnDir(HotPatchConfig c)
        {
            var d = Environment.ExpandEnvironmentVariables(string.IsNullOrEmpty(c.cdnDir) ? "HotPatchCDN" : c.cdnDir);
            return Path.GetFullPath(Path.IsPathRooted(d) ? d : Path.Combine(ProjectRoot, d));
        }

        static string SafeName(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s ?? "")
                sb.Append(ch < 128 && (char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_') ? ch : '_');
            return sb.Length > 0 ? sb.ToString() : "project";
        }
    }

    /// <summary>
    /// Khoá của project — trong thư mục khoá (cấu hình keyDir / biến môi trường HOTPATCHKIT_KEYDIR), NGOÀI repo:
    /// <list type="bullet">
    /// <item><c>sign-private.xml</c>: khoá ký RSA-3072 (bí mật). App chỉ có khoá công khai — mất file này thì mọi bản cài đã phát không
    /// nhận bản vá nào nữa.</item>
    /// <item><c>aes.key</c>: khoá AES-256 bọc gói (nằm cả trong app, chỉ để khó đọc).</item>
    /// <item><c>obf-key.txt</c>: khoá xáo của phần gia cố (khi bật hardenNative).</item>
    /// <item><c>base/&lt;nền tảng&gt;/&lt;mã bản cài&gt;/</c>: kho bản gốc — DLL mã game chưa chèn + bảng mã hàm của từng bản cài. Mất bản gốc
    /// của bản cài nào thì bản đó không vá được.</item>
    /// </list>
    /// Sao lưu cả thư mục. Không commit, không đưa lên CDN.
    /// </summary>
    public sealed class HotPatchKeys
    {
        public string Dir, PrivateXml, ModulusBase64;
        public int Exponent;
        public byte[] Aes;

        public static string KeyDir(HotPatchConfig cfg)
        {
            var d = Environment.GetEnvironmentVariable("HOTPATCHKIT_KEYDIR");
            if (string.IsNullOrEmpty(d)) d = cfg?.keyDir;
            if (string.IsNullOrEmpty(d)) d = "%USERPROFILE%/.hotpatchkit/" + PlayerSettings.productName;
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(d));
        }

        public static string BaseDir(HotPatchConfig cfg) => Path.Combine(KeyDir(cfg), "base");

        /// <summary>Đọc khoá; <paramref name="create"/> = tạo khi chưa có (lần dựng đầu). Không có và không tạo → lỗi.</summary>
        public static HotPatchKeys Load(HotPatchConfig cfg, bool create)
        {
            var dir = KeyDir(cfg);
            var priv = Path.Combine(dir, "sign-private.xml");
            var aes = Path.Combine(dir, "aes.key");
            if (!File.Exists(priv) || !File.Exists(aes))
            {
                if (!create || File.Exists(priv) != File.Exists(aes))
                    throw new BuildFailedException("[hotpatch] thiếu khoá trong " + dir + " (sign-private.xml / aes.key) — chép lại từ bản sao lưu; "
                                                   + "tạo khoá mới thì các bản cài đã phát KHÔNG nhận bản vá nữa");
                Directory.CreateDirectory(dir);
                using (var rsa = new RSACryptoServiceProvider(3072)) File.WriteAllText(priv, rsa.ToXmlString(true));
                var k = new byte[32];
                using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(k);
                File.WriteAllText(aes, BitConverter.ToString(k).Replace("-", "") + "\n");
                Debug.LogWarning("[hotpatch] tạo khoá mới trong " + dir + " — SAO LƯU thư mục này (mất khoá ký = các bản cài đã phát không nhận bản vá nữa). "
                                 + "Không commit, không đưa lên CDN.");
            }
            var xml = File.ReadAllText(priv);
            var hex = File.ReadAllText(aes).Trim();
            var key = new byte[hex.Length / 2];
            for (var i = 0; i < key.Length; i++) key[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            if (key.Length != 32) throw new BuildFailedException("[hotpatch] " + aes + " phải là 64 ký tự hex (32 byte)");
            var e = Convert.FromBase64String(XmlField(xml, "Exponent"));
            var exp = 0;
            foreach (var b in e) exp = (exp << 8) | b;
            return new HotPatchKeys { Dir = dir, PrivateXml = xml, ModulusBase64 = XmlField(xml, "Modulus"), Exponent = exp, Aes = key };
        }

        /// <summary>Ký <paramref name="text"/> (RSA PKCS#1 v1.5 SHA-256, base64) rồi tự kiểm lại bằng khoá công khai.</summary>
        public string Sign(string text)
        {
            var n = HotPatchPackage.FromBigEndian(Convert.FromBase64String(ModulusBase64));
            var d = HotPatchPackage.FromBigEndian(Convert.FromBase64String(XmlField(PrivateXml, "D")));
            var k = (int)((HotPatchPackage.Bits(n) + 7) / 8);
            var m = HotPatchPackage.FromBigEndian(HotPatchPackage.EncodePkcs1(Encoding.UTF8.GetBytes(text), k));
            var sig = Convert.ToBase64String(HotPatchPackage.ToBigEndian(System.Numerics.BigInteger.ModPow(m, d, n), k));
            if (!HotPatchPackage.Verify(text, sig, ModulusBase64, Exponent)) throw new InvalidOperationException("[hotpatch] khoá ký hỏng (ký xong không kiểm lại được)");
            return sig;
        }

        static string XmlField(string xml, string name)
        {
            var a = xml.IndexOf("<" + name + ">", StringComparison.Ordinal);
            var b = xml.IndexOf("</" + name + ">", StringComparison.Ordinal);
            if (a < 0 || b < a) throw new FormatException("khoá bí mật thiếu " + name);
            return xml.Substring(a + name.Length + 2, b - a - name.Length - 2).Trim();
        }
    }
}
