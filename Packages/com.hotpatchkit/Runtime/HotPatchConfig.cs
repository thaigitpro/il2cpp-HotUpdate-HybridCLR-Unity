using System;
using UnityEngine;

namespace HotPatchKit
{
    /// <summary>
    /// Cấu hình HotPatchKit của project: <c>Assets/HotPatchKit/Resources/HotPatchKit.json</c> (menu Tools → HotPatchKit → Tạo file cấu
    /// hình). App đọc phần chạy (gameAssembly, entry, autoBoot, cdnUrl, thời gian chờ); editor đọc cả phần dựng / xuất bản.
    /// </summary>
    [Serializable]
    public sealed class HotPatchConfig
    {
        /// <summary>Đường dẫn Resources của file cấu hình (không đuôi).</summary>
        public const string ResourcePath = "HotPatchKit";
        /// <summary>Đường dẫn file cấu hình trong project.</summary>
        public const string AssetPath = "Assets/HotPatchKit/Resources/HotPatchKit.json";

        // ---------------- phần chạy (app) ----------------

        /// <summary>Tên assembly mã game (tên trong file .asmdef) — mọi hàm của nó vá được. Phần còn lại của app là AOT, không vá được.</summary>
        public string gameAssembly = "Game";

        /// <summary>Hàm static không tham số của mã game, chạy SAU khi cài bản vá: "Namespace.Lớp.Hàm". Trống = không tự gọi.</summary>
        public string entry = "";

        /// <summary>true: app tự kiểm / tải / cài bản vá lúc mở rồi gọi <see cref="entry"/> (HotPatchBoot). false: project tự gọi
        /// <c>HotPatchLoader.Run</c>.</summary>
        public bool autoBoot = true;

        /// <summary>Gốc CDN bản vá: app hỏi <c>&lt;cdnUrl&gt;/&lt;nền tảng&gt;/&lt;mã bản cài&gt;/version.json</c>. Trống = không hỏi CDN
        /// (chỉ dùng bản đã tải trước đó). Dòng lệnh <c>-hotpatch-cdn &lt;url&gt;</c> ghi đè.</summary>
        public string cdnUrl = "";

        /// <summary>Chờ CDN trả version.json tối đa (giây) — quá thì chạy tiếp bằng bản đã có.</summary>
        public int checkTimeout = 5;

        /// <summary>Tải gói bản vá tối đa (giây).</summary>
        public int downloadTimeout = 180;

        // ---------------- phần dựng / xuất bản (editor) ----------------

        /// <summary>Thư mục khoá + kho bản gốc (ngoài repo, phải sao lưu). Nhận biến môi trường dạng %USERPROFILE%. Biến môi trường
        /// HOTPATCHKIT_KEYDIR ghi đè.</summary>
        public string keyDir = "";

        /// <summary>Thư mục ra của bản vá (tương đối với thư mục project hoặc tuyệt đối) — đẩy nội dung thư mục này lên CDN.</summary>
        public string cdnDir = "HotPatchCDN";

        /// <summary>Không chỉ rõ bản cài thì vá cho N bản cài mới nhất cùng runtime trong kho.</summary>
        public int keepBases = 5;

        /// <summary>File ngoài mã nguồn mà đổi thì bản cài cũ không vá được nữa (thêm vào mã runtime). Mã nguồn của mọi assembly AOT
        /// (mọi assembly trừ <see cref="gameAssembly"/>) đã tự tính.</summary>
        public string[] runtimeIdExtraFiles = { "Assets/link.xml" };

        /// <summary>DLL AOT nhúng làm metadata bổ sung (generic của phần AOT mà bản vá dùng với kiểu bản cài chưa dịch sẵn). Hợp với
        /// danh sách HybridCLR tự tính.</summary>
        public string[] aotMetadata = { "mscorlib", "System", "System.Core", "UnityEngine.CoreModule" };

        /// <summary>Gia cố: mã hoá global-metadata.dat + bọc DLL bản vá bằng định dạng riêng, giải mã trong il2cpp (vá cây il2cpp cục
        /// bộ). Mới kiểm với Unity 6000.3 + HybridCLR 9.0.3; bản khác có thể phải sửa đoạn neo trong HotPatchNative.cs.</summary>
        public bool hardenNative = false;

        /// <summary>Đọc cấu hình từ Resources (app + editor). Không có file → null.</summary>
        public static HotPatchConfig Load()
        {
            var t = Resources.Load<TextAsset>(ResourcePath);
            if (t == null) return null;
            var c = JsonUtility.FromJson<HotPatchConfig>(t.text);
            Resources.UnloadAsset(t);
            return c;
        }
    }
}
