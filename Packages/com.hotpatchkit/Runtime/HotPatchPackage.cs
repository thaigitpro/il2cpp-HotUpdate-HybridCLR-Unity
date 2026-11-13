using System;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace HotPatchKit
{
    /// <summary>
    /// Định dạng gói bản vá — app và công cụ xuất bản (editor) dùng chung.
    /// <list type="bullet">
    /// <item>File gói: "HPKG" | định dạng (1) | IV 16 byte | AES-256-CBC(PKCS7)(gzip(DLL)) — tải từ CDN không mở thẳng được bằng
    /// ILSpy / dnSpy.</item>
    /// <item>Chữ ký: RSA PKCS#1 v1.5 SHA-256 trên <see cref="SignedText"/> (nền tảng, mã bản cài, phiên bản, băm SHA-256 của file gói).
    /// Khoá bí mật chỉ nằm ở máy xuất bản (ngoài repo), app chỉ có khoá công khai: ai sửa được CDN / đường truyền mà không có khoá bí
    /// mật thì không đưa được mã lạ vào app. Khoá AES nằm trong app nên chỉ để khó đọc, không thay được chữ ký.</item>
    /// </list>
    /// Kiểm chữ ký tự làm bằng BigInteger (không qua lớp RSA của .NET: lớp đó tìm thuật toán qua CryptoConfig / reflection, dễ bị IL2CPP
    /// strip mất ở bản phát hành).
    /// </summary>
    public static class HotPatchPackage
    {
        /// <summary>Tên file gói bản vá trên CDN.</summary>
        public const string PatchFile = "patch.bytes";
        /// <summary>Tên file mô tả bản vá trên CDN (phiên bản, băm, chữ ký).</summary>
        public const string VersionFile = "version.json";
        /// <summary>Namespace / tên assembly của bản vá sinh ra; bộ nạp gọi <c>HotPatchGen.Installer.Install()</c>.</summary>
        public const string PatchNamespace = "HotPatchGen";

        static readonly byte[] Magic = { (byte)'H', (byte)'P', (byte)'K', (byte)'G' };
        const byte Format = 1;

        // DigestInfo của SHA-256 (RFC 8017 §9.2)
        static readonly byte[] Sha256DigestInfo = { 0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20 };

        /// <summary>Nội dung được ký: gắn chữ ký với đúng nền tảng + bản cài + phiên bản + file gói.</summary>
        public static string SignedText(string platform, string aot, string version, string sha256) =>
            "HPK1\n" + platform + "\n" + aot + "\n" + version + "\n" + sha256;

        public static string Sha256Hex(byte[] data)
        {
            using (var h = new SHA256Managed())
            {
                var d = h.ComputeHash(data);
                var sb = new StringBuilder(d.Length * 2);
                foreach (var b in d) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>DLL → file gói (editor, lúc dựng / xuất bản).</summary>
        public static byte[] Pack(byte[] dll, byte[] aesKey)
        {
            byte[] zipped;
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(dll, 0, dll.Length);
                zipped = ms.ToArray();
            }
            var iv = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(iv);
            using (var aes = NewAes(aesKey, iv))
            using (var enc = aes.CreateEncryptor())
            {
                var body = enc.TransformFinalBlock(zipped, 0, zipped.Length);
                var pkg = new byte[Magic.Length + 1 + iv.Length + body.Length];
                Buffer.BlockCopy(Magic, 0, pkg, 0, Magic.Length);
                pkg[Magic.Length] = Format;
                Buffer.BlockCopy(iv, 0, pkg, Magic.Length + 1, iv.Length);
                Buffer.BlockCopy(body, 0, pkg, Magic.Length + 1 + iv.Length, body.Length);
                return pkg;
            }
        }

        /// <summary>File gói → DLL. Sai định dạng / khoá → ngoại lệ (người gọi kiểm chữ ký + băm trước).</summary>
        public static byte[] Unpack(byte[] pkg, byte[] aesKey)
        {
            const int head = 4 + 1 + 16;
            if (pkg == null || pkg.Length <= head) throw new InvalidDataException("gói rỗng");
            for (var i = 0; i < Magic.Length; i++)
                if (pkg[i] != Magic[i]) throw new InvalidDataException("không phải gói HotPatchKit");
            if (pkg[Magic.Length] != Format) throw new InvalidDataException("định dạng gói " + pkg[Magic.Length] + " chưa hỗ trợ");
            var iv = new byte[16];
            Buffer.BlockCopy(pkg, Magic.Length + 1, iv, 0, 16);
            byte[] zipped;
            using (var aes = NewAes(aesKey, iv))
            using (var dec = aes.CreateDecryptor())
                zipped = dec.TransformFinalBlock(pkg, head, pkg.Length - head);
            using (var src = new MemoryStream(zipped))
            using (var gz = new GZipStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                gz.CopyTo(dst);
                return dst.ToArray();
            }
        }

        static SymmetricAlgorithm NewAes(byte[] key, byte[] iv)
        {
            if (key == null || key.Length != 32) throw new ArgumentException("khoá AES phải 32 byte");
            return new AesManaged { KeySize = 256, BlockSize = 128, Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7, Key = key, IV = iv };
        }

        /// <summary>Chữ ký (base64) có đúng là của khoá bí mật trên <paramref name="text"/> không (khoá công khai: modulus base64 + số mũ).</summary>
        public static bool Verify(string text, string signatureBase64, string modulusBase64, int exponent)
        {
            try
            {
                if (string.IsNullOrEmpty(signatureBase64) || string.IsNullOrEmpty(modulusBase64)) return false;
                var n = FromBigEndian(Convert.FromBase64String(modulusBase64));
                var k = (int)((Bits(n) + 7) / 8);
                var sig = Convert.FromBase64String(signatureBase64);
                if (sig.Length != k) return false;
                var s = FromBigEndian(sig);
                if (s >= n) return false;
                var em = ToBigEndian(BigInteger.ModPow(s, exponent, n), k);
                var expected = EncodePkcs1(Encoding.UTF8.GetBytes(text), k);
                var diff = 0;
                for (var i = 0; i < k; i++) diff |= em[i] ^ expected[i];
                return diff == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>EMSA-PKCS1-v1_5 (SHA-256): 00 01 FF..FF 00 | DigestInfo | băm — dài <paramref name="k"/> byte.</summary>
        public static byte[] EncodePkcs1(byte[] message, int k)
        {
            byte[] hash;
            using (var h = new SHA256Managed()) hash = h.ComputeHash(message);
            var t = new byte[Sha256DigestInfo.Length + hash.Length];
            Buffer.BlockCopy(Sha256DigestInfo, 0, t, 0, Sha256DigestInfo.Length);
            Buffer.BlockCopy(hash, 0, t, Sha256DigestInfo.Length, hash.Length);
            if (k < t.Length + 11) throw new ArgumentException("khoá RSA quá ngắn");
            var em = new byte[k];
            em[0] = 0x00;
            em[1] = 0x01;
            for (var i = 2; i < k - t.Length - 1; i++) em[i] = 0xFF;
            em[k - t.Length - 1] = 0x00;
            Buffer.BlockCopy(t, 0, em, k - t.Length, t.Length);
            return em;
        }

        public static BigInteger FromBigEndian(byte[] be)
        {
            var le = new byte[be.Length + 1]; // byte 0 cuối: luôn dương
            for (var i = 0; i < be.Length; i++) le[i] = be[be.Length - 1 - i];
            return new BigInteger(le);
        }

        public static byte[] ToBigEndian(BigInteger v, int length)
        {
            var le = v.ToByteArray();
            var be = new byte[length];
            for (var i = 0; i < le.Length && i < length; i++) be[length - 1 - i] = le[i];
            return be;
        }

        public static long Bits(BigInteger v)
        {
            long n = 0;
            while (v > 0)
            {
                v >>= 1;
                n++;
            }
            return n;
        }
    }
}
