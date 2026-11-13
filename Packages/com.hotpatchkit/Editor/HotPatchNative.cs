using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HybridCLR;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace HotPatchKit.Editor
{
    /// <summary>
    /// Gia cố bản cài IL2CPP (tuỳ chọn, cấu hình <c>hardenNative</c>): che phần .NET ở hai chỗ engine đọc bằng mã máy (il2cpp /
    /// HybridCLR), nên bản dump cũng không ra DLL .NET đọc thẳng được.
    /// <list type="bullet">
    /// <item><b>global-metadata.dat</b> (tên lớp / hàm / chuỗi của bản cài): mã hoá khi dựng, giải mã lúc nạp trong
    /// <c>vm/MetadataLoader.cpp</c> → Il2CppDumper không đọc được cấu trúc.</item>
    /// <item><b>ảnh DLL bản vá / metadata AOT</b>: bọc định dạng riêng, giải mã tại chỗ trong <c>hybridclr/metadata/RawImageBase.cpp</c>
    /// ngay trước khi đọc như PE → byte giao cho interpreter không phải DLL chuẩn.</item>
    /// </list>
    /// Mã C++ chèn vào cây il2cpp cục bộ (Library/HybridCLR, không theo git) qua <see cref="ApplyNativePatch"/>; lệnh cài và mỗi lần dựng
    /// tự áp lại. Khoá xáo (128-bit) nằm trong thư mục khoá (obf-key.txt) và được nung vào mã máy lúc dựng. Khoá nằm trong app nên chỉ
    /// nâng mức khó đọc ngang phần IL2CPP, không phải bảo mật tuyệt đối.
    /// Đoạn neo khớp đúng HybridCLR v9.0.3 / il2cpp_plus v6000.3.x — bản khác báo lỗi "không thấy đoạn để vá" thì sửa các hằng ở cuối file.
    /// APK Android: global-metadata.dat nằm trong APK — chưa mã hoá (chỉ bọc DLL bản vá).
    /// </summary>
    public static class HotPatchNative
    {
        // nhãn 8 byte đầu: "HPKT" + loại (2) + phiên bản (1) + 0; byte thân xáo bắt đầu từ offset 8, chỉ số keystream tính từ 0
        const byte C4Image = (byte)'I', C5Image = (byte)'M'; // ảnh DLL vá / metadata AOT
        const byte C4Meta = (byte)'M', C5Meta = (byte)'D';   // global-metadata.dat
        const byte Version = 1;

        // ---------------- khoá xáo (thư mục khoá, nung vào mã máy lúc dựng) ----------------

        static string KeyPath() => Path.Combine(HotPatchKeys.KeyDir(HotPatchSettings.Load()), "obf-key.txt");

        static (ulong k0, ulong k1) LoadKey()
        {
            var path = KeyPath();
            if (File.Exists(path))
            {
                ulong k0 = 0, k1 = 0;
                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (t.StartsWith("k0=", StringComparison.OrdinalIgnoreCase)) k0 = Convert.ToUInt64(t.Substring(3), 16);
                    else if (t.StartsWith("k1=", StringComparison.OrdinalIgnoreCase)) k1 = Convert.ToUInt64(t.Substring(3), 16);
                }
                if (k0 != 0 || k1 != 0) return (k0, k1);
            }
            var b = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            var nk0 = BitConverter.ToUInt64(b, 0);
            var nk1 = BitConverter.ToUInt64(b, 8);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, $"k0={nk0:X16}\nk1={nk1:X16}\n");
            Debug.LogWarning("[hotpatch] tạo khoá xáo mới " + path + " — SAO LƯU cả thư mục khoá. Đổi khoá thì bản cài / bản vá cũ không khớp nhau nữa.");
            return (nk0, nk1);
        }

        /// <summary>Dấu khoá xáo (tính vào mã runtime: đổi khoá thì bản cài cũ không nhận bản vá mới).</summary>
        public static string KeyFingerprint()
        {
            var (k0, k1) = LoadKey();
            return HotPatchPackage.Sha256Hex(Encoding.UTF8.GetBytes(k0.ToString("X16") + k1.ToString("X16"))).Substring(0, 12);
        }

        // ---------------- keystream (khớp từng byte với hotpatchkit_crypt.h) ----------------

        static ulong Sm64(ulong x)
        {
            unchecked
            {
                x += 0x9E3779B97F4A7C15UL;
                x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
                x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
                return x ^ (x >> 31);
            }
        }

        static ulong Ks64(ulong b, ulong k0, ulong k1)
        {
            unchecked { return Sm64(k0 ^ (b * 0x9E3779B97F4A7C15UL)) ^ Sm64(k1 + b); }
        }

        static void Transform(byte[] dst, int dstOff, byte[] src, int srcOff, int n, ulong k0, ulong k1)
        {
            ulong cur = 0;
            for (var i = 0; i < n; i++)
            {
                if ((i & 7) == 0) cur = Ks64((ulong)(i >> 3), k0, k1);
                dst[dstOff + i] = (byte)(src[srcOff + i] ^ (byte)(cur >> ((i & 7) * 8)));
            }
        }

        static byte[] Wrap(byte[] data, byte c4, byte c5)
        {
            var (k0, k1) = LoadKey();
            var outp = new byte[8 + data.Length];
            outp[0] = (byte)'H'; outp[1] = (byte)'P'; outp[2] = (byte)'K'; outp[3] = (byte)'T';
            outp[4] = c4; outp[5] = c5; outp[6] = Version; outp[7] = 0;
            Transform(outp, 8, data, 0, data.Length, k0, k1);
            return outp;
        }

        static bool HasMagic(byte[] p, byte c4, byte c5) =>
            p != null && p.Length > 8 && p[0] == 'H' && p[1] == 'P' && p[2] == 'K' && p[3] == 'T' && p[4] == c4 && p[5] == c5;

        // ---------------- dùng lúc dựng / xuất bản ----------------

        /// <summary>DLL (bản vá / metadata AOT) → định dạng ảnh riêng; RawImageBase.cpp giải mã tại chỗ trước khi đọc PE.
        /// Gọi trước HotPatchPackage.Pack.</summary>
        public static byte[] WrapImage(byte[] dll) => Wrap(dll, C4Image, C5Image);

        /// <summary>Mã hoá global-metadata.dat của bản dựng tại chỗ (nếu chưa). MetadataLoader.cpp giải mã lúc nạp.</summary>
        public static bool EncryptMetadataFile(string path)
        {
            var data = File.ReadAllBytes(path);
            if (HasMagic(data, C4Meta, C5Meta)) return false; // đã mã hoá
            File.WriteAllBytes(path, Wrap(data, C4Meta, C5Meta));
            return true;
        }

        /// <summary>Tìm global-metadata.dat trong thư mục bản dựng (Windows / project iOS: file rời) rồi mã hoá. APK Android nằm trong zip
        /// — chưa xử lý (báo).</summary>
        public static void EncryptBuildMetadata(string outputPath, BuildTarget target)
        {
            var dir = Directory.Exists(outputPath) ? outputPath : Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { Debug.LogWarning("[hotpatch] không thấy thư mục bản dựng " + outputPath); return; }
            // Windows: <app>_Data/il2cpp_data/Metadata/; dự án Xcode iOS: Data/Managed/Metadata/
            var found = Directory.GetFiles(dir, "global-metadata.dat", SearchOption.AllDirectories)
                .Where(f => f.Replace('\\', '/').Contains("/Metadata/global-metadata.dat")).ToList();
            if (found.Count == 0)
            {
                Debug.LogWarning($"[hotpatch] không thấy global-metadata.dat trong {dir} ({target}) — bỏ qua mã hoá metadata"
                                 + (target == BuildTarget.Android ? " (APK Android: metadata nằm trong APK, chưa hỗ trợ)" : ""));
                return;
            }
            foreach (var f in found)
                Debug.Log("[hotpatch] " + (EncryptMetadataFile(f) ? "mã hoá" : "đã mã hoá") + " metadata " + f.Substring(dir.Length).TrimStart('\\', '/')
                          + $" ({new FileInfo(f).Length / 1048576.0:0.0} MB)");
        }

        /// <summary>Mã hoá metadata của một bản ĐÃ dựng (không dựng lại): Unity -batchmode -buildTarget iOS
        /// -executeMethod HotPatchKit.Editor.HotPatchNative.EncryptMetadataCli -hpOut &lt;thư mục bản dựng&gt;</summary>
        public static void EncryptMetadataCli()
        {
            var a = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(a, "-hpOut");
            var ok = i >= 0 && i + 1 < a.Length;
            if (ok) EncryptBuildMetadata(Path.GetFullPath(a[i + 1]), EditorUserBuildSettings.activeBuildTarget);
            else Debug.LogError("[hotpatch] cần -hpOut <thư mục bản dựng>");
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        // ---------------- vá cây il2cpp cục bộ (Library, không theo git) ----------------

        static string Libil2cppDir()
        {
            var dir = Path.Combine(SettingsUtil.LocalIl2CppDir, "libil2cpp");
            if (!File.Exists(Path.Combine(dir, "vm", "MetadataLoader.cpp")))
                throw new BuildFailedException("[hotpatch] không thấy libil2cpp ở " + dir + " — cài HybridCLR trước (Tools/HotPatchKit hoặc hotpatch.ps1 -Install)");
            return dir;
        }

        /// <summary>Chèn mã giải mã vào cây il2cpp cục bộ (idempotent): header khoá + MetadataLoader.cpp (metadata) + RawImageBase.cpp
        /// (ảnh vá). Chạy sau khi cài HybridCLR và trước mỗi lần dựng; cài lại HybridCLR thì áp lại.</summary>
        public static void ApplyNativePatch()
        {
            var lib = Libil2cppDir();
            var (k0, k1) = LoadKey();
            WriteCryptHeader(lib, k0, k1);
            PatchMetadataLoader(Path.Combine(lib, "vm", "MetadataLoader.cpp"));
            PatchRawImageBase(Path.Combine(lib, "hybridclr", "metadata", "RawImageBase.cpp"));
        }

        static void WriteCryptHeader(string lib, ulong k0, ulong k1)
        {
            var h = CryptHeader.Replace("__K0__", "0x" + k0.ToString("X16")).Replace("__K1__", "0x" + k1.ToString("X16"));
            var path = Path.Combine(lib, "hotpatchkit_crypt.h");
            if (!File.Exists(path) || File.ReadAllText(path) != h) File.WriteAllText(path, h);
        }

        static void PatchMetadataLoader(string path)
        {
            var src = File.ReadAllText(path).Replace("\r\n", "\n"); // file gốc CRLF; neo dùng \n (trình biên dịch không quan tâm)
            if (src.Contains("hotpatchkit_crypt.h")) return; // đã vá
            var s = src;
            s = ReplaceOnce(path, s, "#include \"utils/Logging.h\"",
                "#include \"utils/Logging.h\"\n#include \"../hotpatchkit_crypt.h\"\n#include <cstdlib>\n#include <cstring>");
            s = ReplaceOnce(path, s, MetaLoadOriginal, MetaLoadPatched);
            s = ReplaceOnce(path, s, MetaUnloadOriginal, "    free(fileBuffer);");
            File.WriteAllText(path, s);
            Debug.Log("[hotpatch] đã vá libil2cpp/vm/MetadataLoader.cpp (giải mã metadata)");
        }

        static void PatchRawImageBase(string path)
        {
            var src = File.ReadAllText(path).Replace("\r\n", "\n");
            if (src.Contains("hotpatchkit_crypt.h")) return; // đã vá
            var s = src;
            s = ReplaceOnce(path, s, "#include \"RawImage.h\"", "#include \"RawImage.h\"\n#include \"../../hotpatchkit_crypt.h\"");
            s = ReplaceOnce(path, s, RawImageOriginal, RawImagePatched);
            File.WriteAllText(path, s);
            Debug.Log("[hotpatch] đã vá libil2cpp/hybridclr/metadata/RawImageBase.cpp (giải mã ảnh DLL vá)");
        }

        static string ReplaceOnce(string path, string src, string from, string to)
        {
            var i = src.IndexOf(from, StringComparison.Ordinal);
            if (i < 0) throw new BuildFailedException("[hotpatch] " + path + ": không thấy đoạn để vá (bản HybridCLR / il2cpp khác?) — tắt hardenNative hoặc sửa neo trong HotPatchNative.cs\n--- cần ---\n" + from);
            if (src.IndexOf(from, i + from.Length, StringComparison.Ordinal) >= 0)
                throw new BuildFailedException("[hotpatch] " + path + ": đoạn để vá xuất hiện nhiều lần");
            return src.Substring(0, i) + to + src.Substring(i + from.Length);
        }

        // ---- đoạn gốc / đoạn thay (khớp đúng bản HybridCLR v9.0.3 / il2cpp_plus v6000.3.x) ----

        const string MetaLoadOriginal =
            "    void* fileBuffer = utils::MemoryMappedFile::Map(handle);\n\n" +
            "    os::File::Close(handle, &error);\n" +
            "    if (error != 0)\n" +
            "    {\n" +
            "        utils::MemoryMappedFile::Unmap(fileBuffer);\n" +
            "        fileBuffer = NULL;\n" +
            "        return NULL;\n" +
            "    }\n\n" +
            "    return fileBuffer;";

        const string MetaLoadPatched =
            "    int metaErr = 0;\n" +
            "    int64_t metaLen64 = os::File::GetLength(handle, &metaErr);\n" +
            "    void* fileBuffer = utils::MemoryMappedFile::Map(handle);\n\n" +
            "    os::File::Close(handle, &error);\n" +
            "    if (error != 0 || metaErr != 0 || metaLen64 <= 0)\n" +
            "    {\n" +
            "        if (fileBuffer) utils::MemoryMappedFile::Unmap(fileBuffer);\n" +
            "        return NULL;\n" +
            "    }\n\n" +
            "    size_t metaLen = (size_t)metaLen64;\n" +
            "    const unsigned char* metaSrc = (const unsigned char*)fileBuffer;\n" +
            "    void* metaResult;\n" +
            "    if (hpk::has_magic(metaSrc, metaLen, 'M', 'D'))\n" +
            "    {\n" +
            "        size_t metaOut = metaLen - 8;\n" +
            "        unsigned char* metaBuf = (unsigned char*)malloc(metaOut);\n" +
            "        hpk::decrypt(metaBuf, metaSrc + 8, metaOut);\n" +
            "        metaResult = metaBuf;\n" +
            "    }\n" +
            "    else\n" +
            "    {\n" +
            "        unsigned char* metaBuf = (unsigned char*)malloc(metaLen);\n" +
            "        memcpy(metaBuf, metaSrc, metaLen);\n" +
            "        metaResult = metaBuf;\n" +
            "    }\n" +
            "    utils::MemoryMappedFile::Unmap(fileBuffer);\n" +
            "    return metaResult;";

        const string MetaUnloadOriginal =
            "    bool success = il2cpp::utils::MemoryMappedFile::Unmap(fileBuffer);\n" +
            "    NO_UNUSED_WARNING(success);\n" +
            "    IL2CPP_ASSERT(success);";

        const string RawImageOriginal =
            "    _imageData = (const byte*)rawImageData;\n" +
            "    _imageLength = uint32_t(length);\n" +
            "    _ptrRawDataEnd = _imageData + length;";

        const string RawImagePatched =
            "    unsigned char* hpkImg = (unsigned char*)rawImageData; // bộ đệm heap do image sở hữu (CopyBytes) — giải mã tại chỗ an toàn\n" +
            "    size_t hpkLen = length;\n" +
            "    if (hpk::has_magic(hpkImg, hpkLen, 'I', 'M'))\n" +
            "    {\n" +
            "        hpkLen -= 8;\n" +
            "        hpk::decrypt(hpkImg, hpkImg + 8, hpkLen); // đích sau nguồn 8 byte, quét xuôi: không đè\n" +
            "    }\n" +
            "    _imageData = (const byte*)hpkImg;\n" +
            "    _imageLength = uint32_t(hpkLen);\n" +
            "    _ptrRawDataEnd = _imageData + hpkLen;";

        const string CryptHeader =
@"// hotpatchkit_crypt.h — giải mã metadata + ảnh DLL vá lúc chạy (HotPatchKit, gia cố bản cài).
// Sinh bởi HotPatchKit.Editor.HotPatchNative.ApplyNativePatch (khoá nung từ <thư mục khoá>/obf-key.txt) — ĐỪNG sửa tay.
#pragma once
#include <stdint.h>
#include <stddef.h>

namespace hpk
{
    static const uint64_t K0 = __K0__ULL;
    static const uint64_t K1 = __K1__ULL;

    static inline uint64_t sm64(uint64_t x)
    {
        x += 0x9E3779B97F4A7C15ULL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9ULL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBULL;
        return x ^ (x >> 31);
    }

    static inline uint64_t ks64(uint64_t b)
    {
        return sm64(K0 ^ (b * 0x9E3779B97F4A7C15ULL)) ^ sm64(K1 + b);
    }

    // dst[i] = src[i] ^ keystream[i]; quét xuôi, cho phép dst = src - k (giải mã tại chỗ, bỏ nhãn đầu)
    static inline void decrypt(unsigned char* dst, const unsigned char* src, size_t n)
    {
        uint64_t cur = 0;
        for (size_t i = 0; i < n; i++)
        {
            if ((i & 7) == 0) cur = ks64(i >> 3);
            dst[i] = (unsigned char)(src[i] ^ (unsigned char)(cur >> ((i & 7) * 8)));
        }
    }

    static inline bool has_magic(const unsigned char* p, size_t n, unsigned char c4, unsigned char c5)
    {
        return n > 8 && p[0] == 'H' && p[1] == 'P' && p[2] == 'K' && p[3] == 'T' && p[4] == c4 && p[5] == c5;
    }
}
";
    }
}
