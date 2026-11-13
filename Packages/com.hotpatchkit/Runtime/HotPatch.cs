using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace HotPatchKit
{
    /// <summary>
    /// Ô vá hàm. Lúc dựng bản cài, mỗi hàm của assembly mã game được chèn ở đầu một phép kiểm (HotPatchWeaver):
    /// <code>if (HotPatch.On) { var f = HotPatch.Slots[id]; if (f != null &amp;&amp; !HotPatch.TakeSkip(id)) return f(this, args); }</code>
    /// Chưa có bản vá: On = false — mỗi lần gọi hàm chỉ thêm một lần đọc biến static + rẽ nhánh, hàm chạy mã máy như cũ. Có bản vá
    /// (assembly HotPatchGen, nạp qua HybridCLR — thông dịch): ô của các hàm đã sửa trỏ sang bản mới; hàm không đổi vẫn chạy mã máy.
    /// Không có static constructor: IL2CPP khỏi chèn phép kiểm khởi tạo lớp vào mọi hàm đã chèn.
    /// Mã game và bản vá sinh ra gọi các thành viên này theo tên — đổi tên / chữ ký là bản cài cũ không nhận bản vá mới.
    /// </summary>
    public static class HotPatch
    {
        /// <summary>Đã cài bản vá (Slots khác null).</summary>
        public static bool On;

        /// <summary>Ô vá theo mã hàm (bảng mã hàm ghi lúc dựng): (this hoặc null, tham số) → giá trị trả (đóng hộp) hoặc null.</summary>
        public static Func<object, object[], object>[] Slots;

        // bản vá gọi lại thân gốc của hàm có ô (override mới ở lớp con, phần "else" của bộ phân nhánh): bỏ qua ô đúng một lần
        [ThreadStatic] static int s_skip; // mã hàm + 1; 0 = không

        /// <summary>Bản vá cài: cấp ô (gọi trước khi gán ô nào).</summary>
        public static void Install(int slotCount)
        {
            if (Slots == null || Slots.Length < slotCount) Slots = new Func<object, object[], object>[slotCount];
        }

        /// <summary>Lần gọi hàm <paramref name="id"/> kế tiếp chạy thân gốc (không theo ô).</summary>
        public static void Skip(int id) => s_skip = id + 1;

        /// <summary>Phép kiểm ở đầu hàm (chỉ khi ô có bản vá): vừa xin <see cref="Skip"/> thì xoá cờ và chạy thân gốc.</summary>
        public static bool TakeSkip(int id)
        {
            if (s_skip != id + 1) return false;
            s_skip = 0;
            return true;
        }

        /// <summary>Biến thành viên mới (bản vá thêm vào lớp có sẵn — bản cài không có chỗ trong đối tượng): giá trị theo (đối tượng, mã
        /// biến); chưa gán = null (bản vá đổi thành giá trị mặc định của kiểu).</summary>
        public static object FieldGet(object owner, int id) => HotPatchFields.Get(owner, id);

        public static void FieldSet(object owner, int id, object value) => HotPatchFields.Set(owner, id, value);
    }

    /// <summary>Kho biến thành viên mới của bản vá: bảng yếu theo đối tượng (đối tượng bị thu gom thì giá trị đi theo).</summary>
    static class HotPatchFields
    {
        static readonly ConditionalWeakTable<object, Dictionary<int, object>> s_table = new ConditionalWeakTable<object, Dictionary<int, object>>();

        public static object Get(object owner, int id)
        {
            if (owner == null) throw new NullReferenceException();
            return s_table.TryGetValue(owner, out var d) && d.TryGetValue(id, out var v) ? v : null;
        }

        public static void Set(object owner, int id, object value)
        {
            if (owner == null) throw new NullReferenceException();
            s_table.GetValue(owner, _ => new Dictionary<int, object>())[id] = value;
        }
    }
}
