using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace HotPatchKit.Editor
{
    /// <summary>
    /// Vá hàm kiểu DHE — bước xuất bản: so mã game mới với mã game gốc (chưa chèn) của bản cài, sinh assembly HotPatchGen (HybridCLR nạp,
    /// thông dịch) chỉ chứa phần khác:
    /// <list type="bullet">
    /// <item>hàm có sẵn đổi thân → hàm static HotPatchGen.Code (tham số đầu = this) + hàm gọi HotPatchGen.Inv gắn vào ô HotPatch.Slots[mã];</item>
    /// <item>hàm mới trên lớp có sẵn → hàm static (gọi thẳng; delegate qua lớp bọc); override mới → bộ phân nhánh trong ô của hàm được
    /// override (lớp con mới override thì gọi bản mới, không thì thân gốc — HotPatch.Skip);</item>
    /// <item>lớp mới → chép nguyên; lớp do trình biên dịch sinh (lambda / closure / iterator) đổi → chép cả lớp, hàm dùng phần đổi của
    /// nó được vá theo;</item>
    /// <item>biến thành viên mới trên lớp có sẵn → HotPatch.FieldGet/FieldSet (bảng yếu theo đối tượng); biến static mới → lớp giữ hộ
    /// HotPatchGen.S_*, khởi tạo bằng phần tương ứng của static constructor mới.</item>
    /// </list>
    /// Không vá được (báo lỗi, phải phát hành bản cài): đổi lớp cha / interface / kiểu biến, đổi khởi tạo của biến static có sẵn, hàm
    /// không có ô (tham số ref/out…), override mới của hàm phần AOT, constructor mới trên lớp có sẵn, lấy địa chỉ biến mới.
    /// </summary>
    public sealed class HotPatchGenerator
    {
        public sealed class Result
        {
            public byte[] Dll;
            public int SlotCount;
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Report = new List<string>();
            public int ChangedMethods, NewMethods, NewTypes, ReplacedTypes, ClonedCompilerTypes, NewInstanceFields, NewStaticFields, NewOverrides;

            public string Summary =>
                $"hàm đổi {ChangedMethods}, hàm mới {NewMethods}, lớp mới {NewTypes}, lớp thay cả lớp {ReplacedTypes}, lớp lambda chép {ClonedCompilerTypes}, biến mới {NewInstanceFields} (+{NewStaticFields} static), override mới {NewOverrides}";
        }

        public static Result Generate(ModuleDefMD baseModule, ModuleDefMD newModule, Dictionary<string, int> slotMap, string patchName = HotPatchKit.HotPatchPackage.PatchNamespace)
        {
            var g = new HotPatchGenerator(baseModule, newModule, slotMap, patchName);
            try
            {
                g.Analyze();
                if (g._r.Errors.Count == 0) g.Emit();
                if (g._r.Errors.Count == 0) g.Write();
            }
            catch (Exception e)
            {
                g._r.Errors.Add("lỗi bộ sinh: " + e);
            }
            return g._r;
        }

        readonly ModuleDefMD _base, _new;
        readonly Dictionary<string, int> _slots;
        readonly string _name;
        readonly Result _r = new Result();

        readonly Dictionary<string, TypeDef> _baseTypes = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
        readonly Dictionary<string, MethodDef> _baseMethods = new Dictionary<string, MethodDef>(StringComparer.Ordinal);
        readonly Dictionary<string, FieldDef> _baseFields = new Dictionary<string, FieldDef>(StringComparer.Ordinal);

        // ---- kết quả phân tích (đối tượng của module mới) ----
        readonly HashSet<TypeDef> _cloneTypes = new HashSet<TypeDef>();             // chép nguyên vào bản vá
        readonly HashSet<TypeDef> _newTypes = new HashSet<TypeDef>();               // không có trong bản gốc
        readonly HashSet<TypeDef> _replaced = new HashSet<TypeDef>();               // có sẵn nhưng đổi lớp cha / interface → thay cả lớp
        readonly HashSet<MethodDef> _changed = new HashSet<MethodDef>();            // hàm có sẵn đổi (có ô)
        readonly List<MethodDef> _added = new List<MethodDef>();                    // hàm mới trên lớp có sẵn
        readonly HashSet<MethodDef> _addedSet = new HashSet<MethodDef>();
        readonly HashSet<MethodDef> _unpatchable = new HashSet<MethodDef>();          // Propagate: hàm cần vá mà không có ô (đã báo lỗi)
        readonly Dictionary<FieldDef, int> _newInstanceField = new Dictionary<FieldDef, int>();
        readonly Dictionary<FieldDef, FieldDef> _newStaticField = new Dictionary<FieldDef, FieldDef>(); // → biến của lớp giữ hộ
        readonly Dictionary<TypeDef, List<FieldDef>> _newStaticsByType = new Dictionary<TypeDef, List<FieldDef>>();
        // override mới của hàm có ô: mã ô → (lớp, hàm mới)
        readonly Dictionary<int, List<MethodDef>> _overridesBySlot = new Dictionary<int, List<MethodDef>>();
        readonly Dictionary<int, MethodDef> _slotOwner = new Dictionary<int, MethodDef>();  // mã ô → hàm (module mới) giữ ô
        // hàm virtual mới (newslot) trên lớp có sẵn: gốc → mọi bản cài đặt (gốc + override, kể cả trong lớp chép)
        readonly Dictionary<MethodDef, List<MethodDef>> _newVirtualImpls = new Dictionary<MethodDef, List<MethodDef>>();
        readonly Dictionary<MethodDef, MethodDef> _newVirtualRootOf = new Dictionary<MethodDef, MethodDef>();

        // ---- bản vá ----
        ModuleDefUser _patch;
        Importer _imp;
        AssemblyRef _gameRef, _aotRef;
        TypeDefUser _code, _inv, _installer;
        readonly Dictionary<TypeDef, TypeDef> _cloneOf = new Dictionary<TypeDef, TypeDef>();
        readonly Dictionary<MethodDef, MethodDef> _cloneMethod = new Dictionary<MethodDef, MethodDef>();
        readonly Dictionary<FieldDef, FieldDef> _cloneField = new Dictionary<FieldDef, FieldDef>();
        readonly Dictionary<MethodDef, MethodDef> _staticOf = new Dictionary<MethodDef, MethodDef>();   // hàm có sẵn đổi / hàm mới → hàm static
        readonly Dictionary<MethodDef, MethodDef> _dispatchOf = new Dictionary<MethodDef, MethodDef>(); // gốc virtual mới → bộ phân nhánh
        readonly Dictionary<MethodDef, TypeDef> _binderOf = new Dictionary<MethodDef, TypeDef>();
        readonly Dictionary<TypeDef, ITypeDefOrRef> _typeRef = new Dictionary<TypeDef, ITypeDefOrRef>();
        readonly Dictionary<TypeDef, TypeDefUser> _holder = new Dictionary<TypeDef, TypeDefUser>();
        MemberRef _hpFieldGet, _hpFieldSet, _hpSkip, _hpInstall, _hpOn, _hpSlots, _funcCtor;
        TypeSig _funcSig;
        int _seq;

        HotPatchGenerator(ModuleDefMD b, ModuleDefMD n, Dictionary<string, int> slots, string name)
        {
            _base = b;
            _new = n;
            _slots = slots;
            _name = name;
            _r.SlotCount = slots.Count == 0 ? 0 : slots.Values.Max() + 1;
        }

        void Error(string s) => _r.Errors.Add(s);

        static bool IsCg(TypeDef t) => HotPatchWeaver.IsCompilerGenerated(t);

        static bool IsPrivImpl(TypeDef t) => t.DeclaringType == null && t.Name.String.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal);

        // =====================================================================================================================
        // so sánh
        // =====================================================================================================================

        // ---- lớp lambda dùng chung "<>c" (lambda không bắt biến, kèm ô cache delegate "<>9__N_M"): ghép theo NỘI DUNG chứ không theo
        // tên — thêm một lambda thì số thứ tự trong tên các lambda sau nó dời đi (vd 4.600 hàm tạo đối tượng của GeneratedClasses,
        // 6.300 hàm đọc/ghi thuộc tính của FastAccessors). Lambda có thân y như trong bản cài → dùng luôn bản AOT; chỉ lambda mới / sửa
        // mới vào bản vá (lớp "<>c" chép một phần). ----
        readonly Dictionary<object, string> _canon = new Dictionary<object, string>();   // thành viên "<>c" (cả hai module) → tên chuẩn
        readonly HashSet<MethodDef> _lambdaNew = new HashSet<MethodDef>();                // lambda không có trong bản cài
        readonly Dictionary<MethodDef, MethodDef> _lambdaMatch = new Dictionary<MethodDef, MethodDef>(); // lambda mới → lambda bản cài cùng nội dung
        readonly Dictionary<TypeDef, TypeDefUser> _lambdaClone = new Dictionary<TypeDef, TypeDefUser>(); // "<>c" → bản chép một phần
        readonly Dictionary<TypeDef, FieldDef> _lambdaCloneInst = new Dictionary<TypeDef, FieldDef>();   // → "<>9" của bản chép

        static bool IsLambdaCache(TypeDef t) => t.DeclaringType != null && t.Name == "<>c";

        static string LambdaSuffix(MethodDef m)
        {
            var i = m.Name.String.IndexOf(">b__", StringComparison.Ordinal);
            return i < 0 ? null : m.Name.String.Substring(i + 4);
        }

        static string CacheSuffix(FieldDef f) => f.Name.String.StartsWith("<>9__", StringComparison.Ordinal) ? f.Name.String.Substring(5) : null;

        static MethodDef LambdaOfCache(FieldDef f)
        {
            var s = CacheSuffix(f);
            return s == null ? null : f.DeclaringType.Methods.FirstOrDefault(m => LambdaSuffix(m) == s);
        }

        void ComputeCanon(ModuleDef mod)
        {
            var keys = new List<(MethodDef m, string key)>();
            foreach (var t in mod.GetTypes().Where(IsLambdaCache))
                foreach (var m in t.Methods.Where(x => !x.IsConstructor && x.HasBody))
                    keys.Add((m, SigKey(m) + "\n" + Norm(m)));
            foreach (var (m, key) in keys)
            {
                using (var h = System.Security.Cryptography.SHA1.Create())
                    _canon[m] = "λ " + m.DeclaringType.DeclaringType.FullName + " " + Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(key)));
            }
            foreach (var t in mod.GetTypes().Where(IsLambdaCache))
                foreach (var f in t.Fields)
                {
                    if (f.Name == "<>9") _canon[f] = "λ9 " + t.DeclaringType.FullName;
                    else if (LambdaOfCache(f) is MethodDef lm && _canon.TryGetValue(lm, out var c)) _canon[f] = "λc " + c;
                }
        }

        string Norm(MethodDef m)
        {
            if (!m.HasBody) return "-";
            var b = m.Body;
            var il = b.Instructions;
            var idx = new Dictionary<Instruction, int>(il.Count);
            for (var i = 0; i < il.Count; i++) idx[il[i]] = i;
            var sb = new StringBuilder();
            sb.Append(b.InitLocals ? '1' : '0').Append('|');
            foreach (var l in b.Variables) sb.Append(l.Type.FullName).Append(';');
            sb.Append('\n');
            foreach (var ins in il)
            {
                sb.Append(ins.OpCode.Code).Append(' ');
                AppendOperand(sb, ins.Operand, idx);
                sb.Append('\n');
            }
            foreach (var eh in b.ExceptionHandlers)
            {
                sb.Append("eh ").Append(eh.HandlerType).Append(' ')
                    .Append(Ix(idx, eh.TryStart)).Append(' ').Append(Ix(idx, eh.TryEnd)).Append(' ')
                    .Append(Ix(idx, eh.HandlerStart)).Append(' ').Append(Ix(idx, eh.HandlerEnd)).Append(' ')
                    .Append(Ix(idx, eh.FilterStart)).Append(' ').Append(eh.CatchType?.FullName).Append('\n');
            }
            return sb.ToString();
        }

        static int Ix(Dictionary<Instruction, int> idx, Instruction i) => i == null ? -1 : idx.TryGetValue(i, out var v) ? v : -2;

        void AppendOperand(StringBuilder sb, object op, Dictionary<Instruction, int> idx)
        {
            if (op != null && _canon.TryGetValue(op, out var canon))
            {
                sb.Append(canon);
                return;
            }
            switch (op)
            {
                case null: break;
                case Instruction t: sb.Append('@').Append(Ix(idx, t)); break;
                case Instruction[] ts: foreach (var t in ts) sb.Append('@').Append(Ix(idx, t)).Append(','); break;
                case Local l: sb.Append('L').Append(l.Index); break;
                case Parameter p: sb.Append('A').Append(p.Index); break;
                case IFullName f: sb.Append(f.FullName); break;
                case string s: sb.Append('"').Append(s.Replace("\n", "\\n")).Append('"'); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case IFormattable n: sb.Append(n.ToString(null, CultureInfo.InvariantCulture)); break;
                default: sb.Append(op); break;
            }
        }

        static string FieldShape(TypeDef t) =>
            string.Join(";", t.Fields.Select(f => (f.IsStatic ? "s " : "") + f.Name + ":" + f.FieldType.FullName + (f.HasFieldRVA ? "#" + Convert.ToBase64String(f.InitialValue ?? new byte[0]) : "")));

        // =====================================================================================================================
        // phân tích
        // =====================================================================================================================

        void Analyze()
        {
            foreach (var t in _base.GetTypes())
            {
                _baseTypes[t.FullName] = t;
                foreach (var m in t.Methods) _baseMethods[m.FullName] = m;
                foreach (var f in t.Fields) _baseFields[f.FullName] = f;
            }
            ComputeCanon(_base);
            ComputeCanon(_new);
            var newTypes = _new.GetTypes().Where(t => !t.IsGlobalModuleType).ToList();

            // 1) lớp mới / lớp do trình biên dịch sinh
            foreach (var t in newTypes)
            {
                if (IsPrivImpl(t)) continue; // từng biến / hàm: lấy của bản cài nếu có, không thì chép theo nhu cầu
                if (!_baseTypes.TryGetValue(t.FullName, out var bt))
                {
                    _newTypes.Add(t);
                    _cloneTypes.Add(t);
                    continue;
                }
                if (IsCg(t) && !IsLambdaCache(t))
                {
                    if (!SameCompilerType(t, bt)) _cloneTypes.Add(t);
                }
            }
            // 1b) lớp có sẵn đổi lớp cha / interface / loại kiểu: đối tượng AOT không đổi được → thay cả lớp (chép nguyên; mọi chỗ
            // dùng lớp đó được vá theo). Lớp con của lớp bị thay cũng bị thay (lớp cha của nó đổi).
            foreach (var t in newTypes)
            {
                if (IsCg(t) || IsPrivImpl(t) || _cloneTypes.Contains(t)) continue;
                if (StructureChanged(t, _baseTypes[t.FullName], out var why))
                {
                    _replaced.Add(t);
                    _r.Report.Add($"thay cả lớp {t.FullName}: {why}");
                }
            }
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var t in newTypes)
                {
                    if (_replaced.Contains(t) || _newTypes.Contains(t) || IsCg(t) || IsPrivImpl(t)) continue;
                    for (var b = t.BaseType as TypeDef; b != null && b.Module == _new; b = b.BaseType as TypeDef)
                        if (_replaced.Contains(b))
                        {
                            _replaced.Add(t);
                            _r.Report.Add($"thay cả lớp {t.FullName}: lớp cha {b.FullName} bị thay");
                            grew = true;
                            break;
                        }
                }
            }
            foreach (var t in _replaced) _cloneTypes.Add(t);
            // lớp lồng trong lớp chép: chép theo
            foreach (var t in newTypes)
                for (var x = t.DeclaringType; x != null; x = x.DeclaringType)
                    if (_cloneTypes.Contains(x) && !IsPrivImpl(t))
                    {
                        _cloneTypes.Add(t);
                        break;
                    }

            // 1c) lớp lambda dùng chung "<>c" (của lớp không bị chép): ghép từng lambda với bản cài theo nội dung
            foreach (var t in newTypes.Where(x => IsLambdaCache(x) && !_cloneTypes.Contains(x)))
            {
                var byCanon = new Dictionary<string, MethodDef>(StringComparer.Ordinal);
                if (_baseTypes.TryGetValue(t.FullName, out var bt))
                    foreach (var bm in bt.Methods)
                        if (_canon.TryGetValue(bm, out var bc)) byCanon[bc] = bm;
                foreach (var m in t.Methods.Where(x => !x.IsConstructor && x.HasBody))
                {
                    if (byCanon.TryGetValue(_canon[m], out var bm)) _lambdaMatch[m] = bm;
                    else _lambdaNew.Add(m);
                }
            }
            _r.Report.Add($"lambda dùng chung: {_lambdaMatch.Count} giữ bản AOT, {_lambdaNew.Count} mới / sửa");

            // 2) lớp có sẵn: biến mới, hàm đổi / mới
            foreach (var t in newTypes)
            {
                if (_cloneTypes.Contains(t) || IsCg(t) || IsPrivImpl(t)) continue;
                var bt = _baseTypes[t.FullName];
                CheckRemovedOverrides(t, bt);
                foreach (var f in t.Fields)
                {
                    if (_baseFields.TryGetValue(f.FullName, out var bf))
                    {
                        if (bf.IsStatic != f.IsStatic) Error($"biến {f.FullName}: đổi static / thành viên");
                        // kiểu của biến là lớp bị thay: đối tượng mới không cất vào ô kiểu cũ được → như biến mới
                        if (!MentionsClone(f.FieldType)) continue;
                    }
                    if (f.IsLiteral) continue; // const: giá trị đã nằm thẳng ở chỗ dùng
                    // biến mới, hoặc biến đổi kiểu (mọi hàm dùng nó cũng đổi IL — tên đầy đủ của biến có kiểu) → kho phụ / lớp giữ hộ
                    if (f.IsStatic)
                    {
                        if (!_newStaticsByType.TryGetValue(t, out var l)) _newStaticsByType[t] = l = new List<FieldDef>();
                        l.Add(f);
                        _r.NewStaticFields++;
                    }
                    else
                    {
                        _newInstanceField[f] = _newInstanceField.Count;
                        _r.NewInstanceFields++;
                    }
                }
                foreach (var m in t.Methods)
                {
                    if (_baseMethods.TryGetValue(m.FullName, out var bm))
                    {
                        if (bm.IsVirtual != m.IsVirtual) Error($"hàm {m.FullName}: đổi virtual");
                        if (m.IsStaticConstructor) continue; // xử lý riêng (biến static)
                        if (Norm(m) != Norm(bm)) MarkChanged(m, "đổi thân");
                    }
                    else
                    {
                        _added.Add(m);
                        _addedSet.Add(m);
                    }
                }
                CheckStaticCtor(t, bt);
            }

            // 3) lan: lớp lambda đổi → hàm dùng phần đổi của nó (và lớp lambda khác dùng nó) theo
            Propagate(newTypes);

            // 4) phân loại hàm mới
            foreach (var m in _added)
            {
                if (m.IsConstructor && !m.IsStatic) Error($"constructor mới {m.FullName}: chưa hỗ trợ (cần bản cài mới)");
                else if (m.IsStaticConstructor) { }
                else if (m.IsVirtual && !m.IsNewSlot) ClassifyOverride(m);
                else if (m.IsVirtual)
                {
                    _newVirtualImpls[m] = new List<MethodDef> { m };
                    _newVirtualRootOf[m] = m;
                }
            }
            // override của hàm virtual mới trong lớp chép (lớp mới): không thể là override thật (bản cài không có ô đó) → bộ phân nhánh
            foreach (var t in _cloneTypes.Where(t => !IsCg(t)))
                foreach (var m in t.Methods.Where(x => x.IsVirtual && !x.IsNewSlot))
                {
                    var o = FindOverridden(t, m);
                    if (o != null && _newVirtualRootOf.TryGetValue(o, out var root))
                    {
                        _newVirtualImpls[root].Add(m);
                        _newVirtualRootOf[m] = root;
                    }
                }

            _r.NewTypes = _newTypes.Count(t => !IsCg(t));
            _r.ReplacedTypes = _replaced.Count;
            _r.ClonedCompilerTypes = _cloneTypes.Count(IsCg);
            _r.NewMethods = _added.Count;
            _r.ChangedMethods = _changed.Count;
        }

        void MarkChanged(MethodDef m, string why)
        {
            if (_changed.Contains(m) || _addedSet.Contains(m)) return;
            if (!_slots.ContainsKey(m.FullName))
            {
                Error($"hàm {m.FullName} ({why}): bản cài không có ô vá (tham số ref/out, static constructor…) — cần bản cài mới");
                return;
            }
            _changed.Add(m);
        }

        static bool StructureChanged(TypeDef t, TypeDef bt, out string why)
        {
            why = null;
            if ((t.BaseType?.FullName ?? "") != (bt.BaseType?.FullName ?? "")) why = $"đổi lớp cha {bt.BaseType?.FullName} → {t.BaseType?.FullName}";
            else if (string.Join(",", t.Interfaces.Select(i => i.Interface.FullName).OrderBy(x => x)) != string.Join(",", bt.Interfaces.Select(i => i.Interface.FullName).OrderBy(x => x)))
                why = "đổi interface";
            else if (t.IsValueType != bt.IsValueType || t.IsInterface != bt.IsInterface) why = "đổi loại kiểu";
            return why != null;
        }

        void CheckRemovedOverrides(TypeDef t, TypeDef bt)
        {
            foreach (var bm in bt.Methods)
                if (bm.IsVirtual && !bm.IsNewSlot && !t.Methods.Any(m => m.FullName == bm.FullName))
                    Error($"bỏ override {bm.FullName}: chưa hỗ trợ (cần bản cài mới)");
        }

        bool SameCompilerType(TypeDef t, TypeDef bt)
        {
            if (FieldShape(t) != FieldShape(bt)) return false;
            if ((t.BaseType?.FullName ?? "") != (bt.BaseType?.FullName ?? "")) return false;
            if (t.Methods.Count != bt.Methods.Count) return false;
            foreach (var m in t.Methods)
            {
                if (!_baseMethods.TryGetValue(m.FullName, out var bm)) return false;
                if (Norm(m) != Norm(bm)) return false;
            }
            return true;
        }

        /// <summary>Lớp lambda chép: mọi hàm / lớp lambda khác đang dùng nó phải dùng bản chép (bản trong app là mã cũ).</summary>
        void Propagate(List<TypeDef> types)
        {
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var t in types)
                {
                    if (IsPrivImpl(t) || IsLambdaCache(t)) continue; // "<>c": ghép từng lambda theo nội dung (bước 1c)
                    var cg = IsCg(t);
                    if (cg && _cloneTypes.Contains(t)) continue;
                    if (!cg && (_cloneTypes.Contains(t))) continue;
                    foreach (var m in t.Methods)
                    {
                        if (!m.HasBody || !UsesClone(m)) continue;
                        if (cg)
                        {
                            _cloneTypes.Add(t);
                            foreach (var n in types)
                                for (var x = n.DeclaringType; x != null; x = x.DeclaringType)
                                    if (x == t) _cloneTypes.Add(n);
                            changed = true;
                            break;
                        }
                        if (!_changed.Contains(m) && !_addedSet.Contains(m) && !_unpatchable.Contains(m))
                        {
                            MarkChanged(m, "dùng lambda / closure đã đổi");
                            // hàm không có ô vá (ref/out, static constructor…): MarkChanged chỉ ghi lỗi, không thêm vào _changed — trước đây
                            // vẫn đặt changed = true nên vòng này lặp mãi (mỗi vòng thêm lỗi trùng). Nhớ lại để vòng sau bỏ qua.
                            if (_changed.Contains(m)) changed = true;
                            else _unpatchable.Add(m);
                        }
                    }
                }
            }
        }

        /// <summary>Hàm có nhắc tới lớp được chép (lambda đổi, lớp bị thay, lớp mới) ở bất kỳ đâu: chữ ký của chính nó, toán hạng (kể cả
        /// kiểu generic, chữ ký của hàm / biến được gọi), biến cục bộ, kiểu catch — bản trong app dùng lớp cũ nên phải vá theo.</summary>
        bool UsesClone(MethodDef m)
        {
            if (MentionsClone(m.MethodSig)) return true;
            foreach (var ins in m.Body.Instructions)
                if (OperandMentionsClone(ins.Operand)) return true;
            foreach (var l in m.Body.Variables)
                if (MentionsClone(l.Type)) return true;
            foreach (var eh in m.Body.ExceptionHandlers)
                if (eh.CatchType != null && TypeMentionsClone(eh.CatchType)) return true;
            return false;
        }

        bool IsCloneDef(TypeDef td) => td != null && td.Module == _new && _cloneTypes.Contains(td);

        bool TypeMentionsClone(ITypeDefOrRef t) =>
            t is TypeDef td ? IsCloneDef(td) : t is TypeSpec ts && MentionsClone(ts.TypeSig);

        bool MentionsClone(MethodSig sig)
        {
            if (sig == null) return false;
            if (MentionsClone(sig.RetType)) return true;
            foreach (var p in sig.Params)
                if (MentionsClone(p)) return true;
            return false;
        }

        bool MentionsClone(TypeSig s)
        {
            while (s != null)
            {
                switch (s)
                {
                    case GenericInstSig g:
                        if (MentionsClone(g.GenericType)) return true;
                        foreach (var a in g.GenericArguments)
                            if (MentionsClone(a)) return true;
                        return false;
                    case TypeDefOrRefSig tdr:
                        return TypeMentionsClone(tdr.TypeDefOrRef);
                    case NonLeafSig nl:
                        s = nl.Next;
                        continue;
                    default:
                        return false;
                }
            }
            return false;
        }

        bool OperandMentionsClone(object op)
        {
            switch (op)
            {
                case MethodDef lm when _lambdaNew.Contains(lm): return true;
                case FieldDef lf when IsLambdaCache(lf.DeclaringType) && LambdaOfCache(lf) is MethodDef l2 && _lambdaNew.Contains(l2): return true;
                case ITypeDefOrRef t: return TypeMentionsClone(t);
                case MethodDef md: return IsCloneDef(md.DeclaringType) || MentionsClone(md.MethodSig);
                case FieldDef fd: return IsCloneDef(fd.DeclaringType) || MentionsClone(fd.FieldType);
                case MemberRef mr:
                    if (mr.Class is ITypeDefOrRef c && TypeMentionsClone(c)) return true;
                    return mr.IsMethodRef ? MentionsClone(mr.MethodSig) : MentionsClone(mr.FieldSig?.Type);
                case MethodSpec ms:
                    if (OperandMentionsClone(ms.Method)) return true;
                    if (ms.GenericInstMethodSig != null)
                        foreach (var a in ms.GenericInstMethodSig.GenericArguments)
                            if (MentionsClone(a)) return true;
                    return false;
                case MethodSig sig: return MentionsClone(sig);
                default: return false;
            }
        }

        /// <summary>Static constructor của lớp có sẵn: chỉ được thêm khởi tạo cho biến static mới; khởi tạo của biến có sẵn phải y nguyên.</summary>
        void CheckStaticCtor(TypeDef t, TypeDef bt)
        {
            var nc = t.Methods.FirstOrDefault(m => m.IsStaticConstructor);
            var bc = bt.Methods.FirstOrDefault(m => m.IsStaticConstructor);
            if (nc == null && bc == null) return;
            var ns = Segments(nc, out var nok);
            var bs = Segments(bc, out var bok);
            if (!nok || !bok)
            {
                if (nc != null && bc != null && Norm(nc) == Norm(bc)) return;
                Error($"static constructor {t.FullName}: đổi mà không tách được theo từng biến — cần bản cài mới");
                return;
            }
            var newStatics = _newStaticsByType.TryGetValue(t, out var l) ? new HashSet<FieldDef>(l) : new HashSet<FieldDef>();
            var bOther = bs.Where(s => s.Target == null || !newStatics.Contains(s.Target)).Select(s => s.Key).ToList();
            var nOther = ns.Where(s => s.Target == null || !newStatics.Contains(s.Target)).Select(s => s.Key).ToList();
            if (!bOther.SequenceEqual(nOther)) Error($"static constructor {t.FullName}: đổi khởi tạo của biến static có sẵn — cần bản cài mới");
        }

        sealed class Segment
        {
            public int Start, End; // [Start, End]
            public FieldDef Target; // stsfld cuối (của lớp này) hoặc null
            public string Key;
        }

        /// <summary>Tách thân static constructor thành các đoạn kết thúc khi ngăn xếp về 0 (thường mỗi đoạn = khởi tạo một biến).</summary>
        List<Segment> Segments(MethodDef m, out bool ok)
        {
            ok = true;
            var list = new List<Segment>();
            if (m == null || !m.HasBody) return list;
            if (m.Body.HasExceptionHandlers)
            {
                ok = false;
                return list;
            }
            var il = m.Body.Instructions;
            var depth = 0;
            var start = 0;
            for (var i = 0; i < il.Count; i++)
            {
                var ins = il[i];
                if (ins.OpCode.Code == Code.Ret && i == il.Count - 1 && depth == 0) break;
                ins.CalculateStackUsage(false, out var push, out var pop);
                depth += push - pop;
                if (depth < 0)
                {
                    ok = false;
                    return list;
                }
                if (depth != 0) continue;
                if (ins.OpCode.FlowControl == FlowControl.Branch || ins.OpCode.FlowControl == FlowControl.Cond_Branch)
                {
                    // nhánh ra khỏi đoạn: đoạn có vòng lặp / rẽ nhánh ở mức câu lệnh — không tách
                    var target = ins.Operand as Instruction;
                    var ti = target == null ? -1 : il.IndexOf(target);
                    if (ti < start || ti > i + 1)
                    {
                        ok = false;
                        return list;
                    }
                }
                var seg = new Segment { Start = start, End = i, Target = ins.OpCode.Code == Code.Stsfld ? ins.Operand as FieldDef : null };
                var sb = new StringBuilder();
                var idx = new Dictionary<Instruction, int>();
                for (var k = 0; k < il.Count; k++) idx[il[k]] = k - start;
                for (var k = start; k <= i; k++)
                {
                    sb.Append(il[k].OpCode.Code).Append(' ');
                    AppendOperand(sb, il[k].Operand, idx);
                    sb.Append('\n');
                }
                seg.Key = sb.ToString();
                list.Add(seg);
                start = i + 1;
            }
            return list;
        }

        /// <summary>Override mới trên lớp có sẵn: hàm cha (gần nhất) phải có ô — hoặc là hàm virtual mới của bản vá.</summary>
        void ClassifyOverride(MethodDef m)
        {
            var o = FindOverridden(m.DeclaringType, m);
            if (o == null)
            {
                Error($"override mới {m.FullName}: hàm được override nằm ngoài assembly mã game (phần AOT) — cần bản cài mới");
                return;
            }
            if (_newVirtualRootOf.TryGetValue(o, out var root) || (_addedSet.Contains(o) && o.IsNewSlot && (root = o) != null))
            {
                if (!_newVirtualImpls.ContainsKey(root))
                {
                    _newVirtualImpls[root] = new List<MethodDef> { root };
                    _newVirtualRootOf[root] = root;
                }
                _newVirtualImpls[root].Add(m);
                _newVirtualRootOf[m] = root;
                return;
            }
            if (_addedSet.Contains(o))
            {
                // override mới của một override mới khác: gom về cùng ô của hàm gốc
                ClassifyOverrideChain(m, o);
                return;
            }
            if (!_slots.TryGetValue(o.FullName, out var slot))
            {
                Error($"override mới {m.FullName}: hàm được override {o.FullName} không có ô vá — cần bản cài mới");
                return;
            }
            if (!_overridesBySlot.TryGetValue(slot, out var l)) _overridesBySlot[slot] = l = new List<MethodDef>();
            l.Add(m);
            _slotOwner[slot] = o;
            _r.NewOverrides++;
        }

        void ClassifyOverrideChain(MethodDef m, MethodDef viaNewOverride)
        {
            foreach (var kv in _overridesBySlot)
                if (kv.Value.Contains(viaNewOverride))
                {
                    kv.Value.Add(m);
                    _r.NewOverrides++;
                    return;
                }
            Error($"override mới {m.FullName}: chuỗi override mới chưa hỗ trợ");
        }

        /// <summary>Hàm virtual gần nhất trong các lớp cha (cùng module) mà <paramref name="m"/> override; null nếu nằm ngoài assembly mã game.</summary>
        MethodDef FindOverridden(TypeDef t, MethodDef m)
        {
            var sig = SigKey(m);
            for (var bt = t.BaseType; bt != null;)
            {
                var td = bt as TypeDef;
                if (td == null || td.Module != _new) return null;
                foreach (var x in td.Methods)
                    if (x.IsVirtual && x.Name == m.Name && SigKey(x) == sig) return x;
                bt = td.BaseType;
            }
            return null;
        }

        static string SigKey(MethodDef m) => m.MethodSig.RetType.FullName + "(" + string.Join(",", m.MethodSig.Params.Select(p => p.FullName)) + ")";

        HashSet<MethodDef> _overriddenInNew;

        /// <summary>Hàm virtual có lớp nào trong module mới override.</summary>
        bool IsOverriddenInNew(MethodDef m)
        {
            if (!m.IsVirtual) return false;
            if (_overriddenInNew == null)
            {
                _overriddenInNew = new HashSet<MethodDef>();
                foreach (var t in _new.GetTypes())
                    foreach (var x in t.Methods)
                        if (x.IsVirtual && !x.IsNewSlot)
                        {
                            var o = FindOverridden(t, x);
                            if (o != null) _overriddenInNew.Add(o);
                        }
            }
            return _overriddenInNew.Contains(m);
        }

        // =====================================================================================================================
        // sinh bản vá
        // =====================================================================================================================

        sealed class Mapper : ImportMapper
        {
            readonly HotPatchGenerator _g;
            public Mapper(HotPatchGenerator g) => _g = g;

            public override ITypeDefOrRef Map(ITypeDefOrRef source) =>
                source is TypeDef td && td.Module == _g._new ? _g.MapType(td) : null;

            public override IField Map(FieldDef source) => source.Module == _g._new ? _g.MapFieldRef(source) : null;

            public override IMethod Map(MethodDef source) => source.Module == _g._new ? _g.MapMethodRef(source) : null;
        }

        void Emit()
        {
            var corlib = new AssemblyRefUser(_new.CorLibTypes.AssemblyRef);
            _patch = new ModuleDefUser(_name + ".dll", Guid.NewGuid(), corlib) { Kind = ModuleKind.Dll, RuntimeVersion = _new.RuntimeVersion };
            var asm = new AssemblyDefUser(_name, new Version(1, 0, 0, 0));
            asm.Modules.Add(_patch);
            _imp = new Importer(_patch, ImporterOptions.TryToUseDefs, default, new Mapper(this));
            _gameRef = new AssemblyRefUser(_new.Assembly);
            _aotRef = new AssemblyRefUser(HotPatchWeaver.RuntimeRef(_new));

            var c = _patch.CorLibTypes;
            var hp = new TypeRefUser(_patch, HotPatchWeaver.RuntimeNamespace, "HotPatch", _aotRef);
            _funcSig = new GenericInstSig(new ClassSig(new TypeRefUser(_patch, "System", "Func`3", c.AssemblyRef)), c.Object, new SZArraySig(c.Object), c.Object);
            _funcCtor = new MemberRefUser(_patch, ".ctor", MethodSig.CreateInstance(c.Void, c.Object, c.IntPtr), new TypeSpecUser(_funcSig));
            _hpFieldGet = new MemberRefUser(_patch, "FieldGet", MethodSig.CreateStatic(c.Object, c.Object, c.Int32), hp);
            _hpFieldSet = new MemberRefUser(_patch, "FieldSet", MethodSig.CreateStatic(c.Void, c.Object, c.Int32, c.Object), hp);
            _hpSkip = new MemberRefUser(_patch, "Skip", MethodSig.CreateStatic(c.Void, c.Int32), hp);
            _hpInstall = new MemberRefUser(_patch, "Install", MethodSig.CreateStatic(c.Void, c.Int32), hp);
            _hpOn = new MemberRefUser(_patch, "On", new FieldSig(c.Boolean), hp);
            _hpSlots = new MemberRefUser(_patch, "Slots", new FieldSig(new SZArraySig(_funcSig)), hp);

            _code = NewType(_name, "Code");
            _inv = NewType(_name, "Inv");
            _installer = NewType(_name, "Installer");

            // 1) vỏ các lớp chép (để tham chiếu chéo được), rồi biến / hàm, rồi thân
            foreach (var t in _cloneTypes.OrderBy(t => t.FullName, StringComparer.Ordinal)) CloneTypeShell(t);
            foreach (var t in _cloneTypes) CloneTypeMembers(t);
            foreach (var g in _lambdaNew.GroupBy(m => m.DeclaringType)) LambdaClone(g.Key, g.ToList());
            // 2) biến static mới → lớp giữ hộ
            foreach (var kv in _newStaticsByType)
            {
                var h = Holder(kv.Key);
                foreach (var f in kv.Value)
                {
                    var nf = new FieldDefUser(f.Name, new FieldSig(_imp.Import(f.FieldType)), FieldAttributes.Public | FieldAttributes.Static);
                    h.Fields.Add(nf);
                    _newStaticField[f] = nf;
                }
            }
            // 3) hàm static cho hàm đổi / hàm mới (vỏ trước)
            foreach (var m in _changed.Concat(_added).Where(m => !m.IsStaticConstructor).OrderBy(m => m.FullName, StringComparer.Ordinal))
                _staticOf[m] = StaticShell(m);
            // 4) bộ phân nhánh của hàm virtual mới
            foreach (var kv in _newVirtualImpls) _dispatchOf[kv.Key] = NewVirtualDispatch(kv.Key, kv.Value);
            // 5) thân: lớp chép, hàm static, khởi tạo biến static mới
            foreach (var kv in _cloneMethod.ToList()) // (chép thân có thể thêm hàm <PrivateImplementationDetails>)
                if (kv.Key.HasBody && kv.Value.Body == null) kv.Value.Body = CopyBody(kv.Key, kv.Value);
            foreach (var kv in _staticOf) kv.Value.Body = CopyBody(kv.Key, kv.Value);
            foreach (var kv in _newStaticsByType) HolderStaticCtor(kv.Key, kv.Value);
            foreach (var kv in _dispatchOf) FillNewVirtualDispatch(kv.Key, _newVirtualImpls[kv.Key], kv.Value);
            // 6) ô: hàm gọi + cài đặt
            var install = new MethodDefUser("Install", MethodSig.CreateStatic(c.Void), MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            _installer.Methods.Add(install);
            var il = new List<Instruction> { OpCodes.Ldc_I4.ToInstruction(_r.SlotCount), OpCodes.Call.ToInstruction(_hpInstall) };
            var slotIds = new SortedSet<int>(_changed.Select(m => _slots[m.FullName]).Concat(_overridesBySlot.Keys));
            foreach (var id in slotIds)
            {
                var inv = SlotInvoker(id);
                il.Add(OpCodes.Ldsfld.ToInstruction(_hpSlots));
                il.Add(OpCodes.Ldc_I4.ToInstruction(id));
                il.Add(OpCodes.Ldnull.ToInstruction());
                il.Add(OpCodes.Ldftn.ToInstruction(inv));
                il.Add(OpCodes.Newobj.ToInstruction(_funcCtor));
                il.Add(OpCodes.Stelem_Ref.ToInstruction());
            }
            il.Add(OpCodes.Ldc_I4_1.ToInstruction());
            il.Add(OpCodes.Stsfld.ToInstruction(_hpOn));
            il.Add(OpCodes.Ret.ToInstruction());
            install.Body = Body(il);
            _r.Report.Add($"ô vá: {slotIds.Count} ({_changed.Count} hàm đổi, {_overridesBySlot.Count} ô có override mới)");
        }

        TypeDefUser NewType(string ns, string name, TypeDef baseType = null)
        {
            var t = new TypeDefUser(ns, name, _patch.CorLibTypes.Object.TypeDefOrRef)
            {
                Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            };
            _patch.Types.Add(t);
            return t;
        }

        static CilBody Body(List<Instruction> il, params Local[] locals)
        {
            var b = new CilBody { InitLocals = true };
            foreach (var l in locals) b.Variables.Add(l);
            foreach (var i in il) b.Instructions.Add(i);
            b.SimplifyBranches();
            b.OptimizeBranches();
            b.OptimizeMacros();
            return b;
        }

        // ---------------- tham chiếu ----------------

        ITypeDefOrRef MapType(TypeDef t)
        {
            if (_cloneOf.TryGetValue(t, out var c)) return c;
            if (_typeRef.TryGetValue(t, out var r)) return r;
            if (IsPrivImpl(t) || (t.DeclaringType != null && IsPrivImpl(t.DeclaringType)))
            {
                // <PrivateImplementationDetails>: kiểu có trong bản cài thì dùng, không thì chép
                if (!_baseTypes.ContainsKey(t.FullName)) return ClonePrivImplType(t);
            }
            else if (!_baseTypes.ContainsKey(t.FullName) && !_cloneTypes.Contains(t))
                throw new InvalidOperationException("kiểu không có trong bản cài mà không chép: " + t.FullName);
            IResolutionScope scope = t.DeclaringType != null ? (IResolutionScope)MapType(t.DeclaringType) : _gameRef;
            r = new TypeRefUser(_patch, t.DeclaringType != null ? UTF8String.Empty : t.Namespace, t.Name, scope);
            _typeRef[t] = r;
            return r;
        }

        IField MapFieldRef(FieldDef f)
        {
            if (_cloneField.TryGetValue(f, out var cf)) return cf;
            if (_newStaticField.TryGetValue(f, out var hf)) return hf;
            var owner = f.DeclaringType;
            // ô cache delegate của lambda đã ghép: ô của lambda cùng nội dung trong bản cài (tên số thứ tự có thể khác)
            if (IsLambdaCache(owner) && !_cloneTypes.Contains(owner) && LambdaOfCache(f) is MethodDef lam && _lambdaMatch.TryGetValue(lam, out var bl))
                return new MemberRefUser(_patch, "<>9__" + LambdaSuffix(bl), new FieldSig(_imp.Import(f.FieldType)), MapType(owner));
            if (IsPrivImpl(owner) && !_baseFields.ContainsKey(f.FullName)) return ClonePrivImplField(f);
            if (!_baseFields.ContainsKey(f.FullName) && !_cloneTypes.Contains(owner))
                throw new InvalidOperationException("biến không có trong bản cài: " + f.FullName);
            return new MemberRefUser(_patch, f.Name, new FieldSig(_imp.Import(f.FieldType)), MapType(owner));
        }

        IMethod MapMethodRef(MethodDef m)
        {
            if (_cloneMethod.TryGetValue(m, out var cm)) return cm;
            if (_lambdaMatch.TryGetValue(m, out var bm)) return new MemberRefUser(_patch, bm.Name, _imp.Import(m.MethodSig), MapType(m.DeclaringType));
            if (_addedSet.Contains(m) && _staticOf.TryGetValue(m, out var sm)) return sm;
            var owner = m.DeclaringType;
            if (IsPrivImpl(owner) && !_baseMethods.ContainsKey(m.FullName)) return ClonePrivImplMethod(m);
            var sig = _imp.Import(m.MethodSig);
            return new MemberRefUser(_patch, m.Name, sig, MapType(owner));
        }

        // ---------------- <PrivateImplementationDetails> theo nhu cầu ----------------

        TypeDefUser _privImpl;
        readonly Dictionary<TypeDef, TypeDef> _privTypes = new Dictionary<TypeDef, TypeDef>();

        TypeDef PrivImplHost()
        {
            if (_privImpl != null) return _privImpl;
            _privImpl = new TypeDefUser("", "<PrivateImplementationDetails>" + _name, _patch.CorLibTypes.Object.TypeDefOrRef)
            {
                Attributes = TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Class,
            };
            _patch.Types.Add(_privImpl);
            return _privImpl;
        }

        ITypeDefOrRef ClonePrivImplType(TypeDef t)
        {
            if (_privTypes.TryGetValue(t, out var c)) return c;
            var host = PrivImplHost();
            var n = new TypeDefUser("", t.Name, _imp.Import(t.BaseType)) { Attributes = (t.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedAssembly };
            if (t.ClassLayout != null) n.ClassLayout = new ClassLayoutUser(t.ClassLayout.PackingSize, t.ClassLayout.ClassSize);
            host.NestedTypes.Add(n);
            _privTypes[t] = n;
            _cloneOf[t] = n;
            return n;
        }

        IField ClonePrivImplField(FieldDef f)
        {
            if (_cloneField.TryGetValue(f, out var cf)) return cf;
            var n = new FieldDefUser(f.Name, new FieldSig(_imp.Import(f.FieldType)), f.Attributes);
            if (f.HasFieldRVA) n.InitialValue = f.InitialValue;
            PrivImplHost().Fields.Add(n);
            _cloneField[f] = n;
            return n;
        }

        IMethod ClonePrivImplMethod(MethodDef m)
        {
            if (_cloneMethod.TryGetValue(m, out var cm)) return cm;
            var n = new MethodDefUser(m.Name, _imp.Import(m.MethodSig), m.ImplAttributes, m.Attributes);
            PrivImplHost().Methods.Add(n);
            _cloneMethod[m] = n;
            n.Body = CopyBody(m, n);
            return n;
        }

        // ---------------- "<>c" chép một phần ----------------

        void LambdaClone(TypeDef t, List<MethodDef> lambdas)
        {
            var c = _patch.CorLibTypes;
            var parts = new List<string>();
            var outer = t;
            for (var x = t; x != null; x = x.DeclaringType)
            {
                parts.Insert(0, x.Name);
                outer = x;
            }
            var n = new TypeDefUser(outer.Namespace, string.Join("__", parts) + "_patch", c.Object.TypeDefOrRef)
            {
                Attributes = TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            };
            _patch.Types.Add(n);
            var inst = new FieldDefUser("<>9", new FieldSig(new ClassSig(n)), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly);
            n.Fields.Add(inst);
            var ctor = new MethodDefUser(".ctor", MethodSig.CreateInstance(c.Void), MethodImplAttributes.IL | MethodImplAttributes.Managed,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            n.Methods.Add(ctor);
            ctor.Body = Body(new List<Instruction>
            {
                OpCodes.Ldarg_0.ToInstruction(), OpCodes.Call.ToInstruction(new MemberRefUser(_patch, ".ctor", MethodSig.CreateInstance(c.Void), c.Object.TypeDefOrRef)),
                OpCodes.Ret.ToInstruction(),
            });
            var cctor = new MethodDefUser(".cctor", MethodSig.CreateStatic(c.Void), MethodImplAttributes.IL | MethodImplAttributes.Managed,
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            n.Methods.Add(cctor);
            cctor.Body = Body(new List<Instruction> { OpCodes.Newobj.ToInstruction(ctor), OpCodes.Stsfld.ToInstruction(inst), OpCodes.Ret.ToInstruction() });
            foreach (var m in lambdas)
            {
                var nm = new MethodDefUser(m.Name, _imp.Import(m.MethodSig), m.ImplAttributes, m.Attributes);
                foreach (var p in m.ParamDefs) nm.ParamDefs.Add(new ParamDefUser(p.Name, p.Sequence, p.Attributes));
                n.Methods.Add(nm);
                _cloneMethod[m] = nm;
                foreach (var f in t.Fields.Where(f => LambdaOfCache(f) == m))
                {
                    var nf = new FieldDefUser(f.Name, new FieldSig(_imp.Import(f.FieldType)), f.Attributes);
                    n.Fields.Add(nf);
                    _cloneField[f] = nf;
                }
            }
            _lambdaClone[t] = n;
            _lambdaCloneInst[t] = inst;
        }

        // ---------------- lớp chép ----------------

        void CloneTypeShell(TypeDef t)
        {
            if (_cloneOf.ContainsKey(t)) return;
            var nestedInClone = t.DeclaringType != null && _cloneTypes.Contains(t.DeclaringType);
            if (nestedInClone) CloneTypeShell(t.DeclaringType);
            string ns = t.Namespace, name = t.Name;
            var attrs = t.Attributes;
            if (t.DeclaringType != null && !nestedInClone)
            {
                // lớp lồng trong lớp có sẵn (lambda của lớp đó): ra lớp ngoài cùng, đổi tên cho khỏi trùng
                var outer = t;
                var parts = new List<string>();
                for (var x = t; x != null; x = x.DeclaringType)
                {
                    parts.Insert(0, x.Name);
                    outer = x;
                }
                ns = outer.Namespace;
                name = string.Join("__", parts);
                attrs = (attrs & ~TypeAttributes.VisibilityMask) | TypeAttributes.Public;
            }
            var n = new TypeDefUser(ns, name) { Attributes = attrs };
            if (t.ClassLayout != null) n.ClassLayout = new ClassLayoutUser(t.ClassLayout.PackingSize, t.ClassLayout.ClassSize);
            if (nestedInClone) ((TypeDef)_cloneOf[t.DeclaringType]).NestedTypes.Add(n);
            else _patch.Types.Add(n);
            _cloneOf[t] = n;
        }

        void CloneTypeMembers(TypeDef t)
        {
            var n = (TypeDef)_cloneOf[t];
            if (t.BaseType != null) n.BaseType = _imp.Import(t.BaseType);
            foreach (var i in t.Interfaces) n.Interfaces.Add(new InterfaceImplUser(_imp.Import(i.Interface)));
            foreach (var f in t.Fields)
            {
                var nf = new FieldDefUser(f.Name, new FieldSig(_imp.Import(f.FieldType)), f.Attributes);
                if (f.HasFieldRVA) nf.InitialValue = f.InitialValue;
                if (f.HasConstant) nf.Constant = new ConstantUser(f.Constant.Value, f.Constant.Type);
                n.Fields.Add(nf);
                _cloneField[f] = nf;
            }
            foreach (var m in t.Methods)
            {
                var attrs = m.Attributes;
                // override hàm virtual mới của lớp có sẵn: bản cài không có ô đó → hàm thường, gọi qua bộ phân nhánh
                if (m.IsVirtual && !m.IsNewSlot && _newVirtualRootOf.ContainsKey(m)) attrs = (attrs & ~MethodAttributes.Virtual) & ~MethodAttributes.Final;
                var nm = new MethodDefUser(m.Name, _imp.Import(m.MethodSig), m.ImplAttributes, attrs);
                foreach (var p in m.ParamDefs) nm.ParamDefs.Add(new ParamDefUser(p.Name, p.Sequence, p.Attributes));
                n.Methods.Add(nm);
                _cloneMethod[m] = nm;
                foreach (var o in m.Overrides) nm.Overrides.Add(new MethodOverride(_imp.Import(o.MethodBody) as IMethodDefOrRef, _imp.Import(o.MethodDeclaration) as IMethodDefOrRef));
            }
            foreach (var p in t.Properties)
            {
                var np = new PropertyDefUser(p.Name, (PropertySig)_imp.Import(p.PropertySig), p.Attributes);
                foreach (var g in p.GetMethods) np.GetMethods.Add(_cloneMethod[g]);
                foreach (var s in p.SetMethods) np.SetMethods.Add(_cloneMethod[s]);
                n.Properties.Add(np);
            }
            foreach (var e in t.Events)
            {
                var ne = new EventDefUser(e.Name, _imp.Import(e.EventType), e.Attributes);
                if (e.AddMethod != null) ne.AddMethod = _cloneMethod[e.AddMethod];
                if (e.RemoveMethod != null) ne.RemoveMethod = _cloneMethod[e.RemoveMethod];
                if (e.InvokeMethod != null) ne.InvokeMethod = _cloneMethod[e.InvokeMethod];
                n.Events.Add(ne);
            }
        }

        // ---------------- hàm static thay cho hàm có sẵn đổi / hàm mới ----------------

        MethodDefUser StaticShell(MethodDef m)
        {
            var ps = new List<TypeSig>();
            if (!m.IsStatic) ps.Add(MapType(m.DeclaringType).ToTypeSig());
            foreach (var p in m.MethodSig.Params) ps.Add(_imp.Import(p));
            var sig = MethodSig.CreateStatic(_imp.Import(m.MethodSig.RetType), ps.ToArray());
            var name = $"{m.DeclaringType.Name}__{m.Name}_{_seq++}";
            var s = new MethodDefUser(name, sig, MethodImplAttributes.IL | MethodImplAttributes.Managed,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            _code.Methods.Add(s);
            return s;
        }

        /// <summary>Bộ phân nhánh cho hàm virtual mới (newslot) trên lớp có sẵn: bản cài không có ô vtable → kiểm kiểu, lớp sâu nhất trước.</summary>
        MethodDefUser NewVirtualDispatch(MethodDef root, List<MethodDef> impls)
        {
            var ps = new List<TypeSig> { MapType(root.DeclaringType).ToTypeSig() };
            foreach (var p in root.MethodSig.Params) ps.Add(_imp.Import(p));
            var d = new MethodDefUser($"{root.DeclaringType.Name}__{root.Name}_virt{_seq++}", MethodSig.CreateStatic(_imp.Import(root.MethodSig.RetType), ps.ToArray()),
                MethodImplAttributes.IL | MethodImplAttributes.Managed, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            _code.Methods.Add(d);
            return d;
        }

        void FillNewVirtualDispatch(MethodDef root, List<MethodDef> impls, MethodDef d)
        {
            var il = new List<Instruction>();
            var argc = root.MethodSig.Params.Count;
            foreach (var impl in impls.Where(x => x != root).OrderByDescending(x => Depth(x.DeclaringType)))
            {
                var next = OpCodes.Nop.ToInstruction();
                il.Add(OpCodes.Ldarg_0.ToInstruction());
                il.Add(OpCodes.Isinst.ToInstruction(MapType(impl.DeclaringType)));
                il.Add(OpCodes.Brfalse.ToInstruction(next));
                il.Add(OpCodes.Ldarg_0.ToInstruction());
                il.Add(OpCodes.Castclass.ToInstruction(MapType(impl.DeclaringType)));
                for (var i = 1; i <= argc; i++) il.Add(OpCodes.Ldarg.ToInstruction(d.Parameters[i]));
                il.Add(OpCodes.Call.ToInstruction(_cloneMethod.TryGetValue(impl, out var cm) ? (IMethod)cm : _staticOf[impl]));
                il.Add(OpCodes.Ret.ToInstruction());
                il.Add(next);
            }
            for (var i = 0; i <= argc; i++) il.Add(OpCodes.Ldarg.ToInstruction(d.Parameters[i]));
            il.Add(OpCodes.Call.ToInstruction(_staticOf[root]));
            il.Add(OpCodes.Ret.ToInstruction());
            d.Body = Body(il);
        }

        static int Depth(TypeDef t)
        {
            var n = 0;
            for (var b = t.BaseType as TypeDef; b != null; b = b.BaseType as TypeDef) n++;
            return n;
        }

        /// <summary>Hàm gọi gắn vào ô: (object this, object[] tham số) → bản mới (hoặc bộ phân nhánh override mới).</summary>
        MethodDef SlotInvoker(int id)
        {
            var c = _patch.CorLibTypes;
            var inv = new MethodDefUser("I" + id, MethodSig.CreateStatic(c.Object, c.Object, new SZArraySig(c.Object)),
                MethodImplAttributes.IL | MethodImplAttributes.Managed, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
            _inv.Methods.Add(inv);
            var il = new List<Instruction>();
            var owner = _slotOwner.TryGetValue(id, out var o) ? o : _changed.First(m => _slots[m.FullName] == id);
            if (_overridesBySlot.TryGetValue(id, out var ovs))
                foreach (var ov in ovs.OrderByDescending(x => Depth(x.DeclaringType)))
                {
                    var next = OpCodes.Nop.ToInstruction();
                    il.Add(OpCodes.Ldarg_0.ToInstruction());
                    il.Add(OpCodes.Isinst.ToInstruction(MapType(ov.DeclaringType)));
                    il.Add(OpCodes.Brfalse.ToInstruction(next));
                    EmitUnpackCall(il, ov, MapType(ov.DeclaringType), _staticOf[ov], false);
                    il.Add(next);
                }
            // mặc định: bản mới của chính hàm giữ ô, không thì thân gốc (bỏ ô một lần)
            var ownerNew = _changed.Contains(owner) ? owner : null;
            if (ownerNew != null) EmitUnpackCall(il, owner, MapType(owner.DeclaringType), _staticOf[owner], false);
            else
            {
                il.Add(OpCodes.Ldc_I4.ToInstruction(id));
                il.Add(OpCodes.Call.ToInstruction(_hpSkip));
                EmitUnpackCall(il, owner, MapType(owner.DeclaringType), MapMethodRef(owner), true);
            }
            inv.Body = Body(il);
            return inv;
        }

        /// <summary>Tháo (this, object[]) → gọi <paramref name="target"/> → đóng hộp giá trị trả.</summary>
        void EmitUnpackCall(List<Instruction> il, MethodDef shape, ITypeDefOrRef selfType, IMethod target, bool instanceCall)
        {
            if (!shape.IsStatic)
            {
                il.Add(OpCodes.Ldarg_0.ToInstruction());
                il.Add(OpCodes.Castclass.ToInstruction(selfType));
            }
            var ps = shape.MethodSig.Params;
            for (var i = 0; i < ps.Count; i++)
            {
                il.Add(OpCodes.Ldarg_1.ToInstruction());
                il.Add(OpCodes.Ldc_I4.ToInstruction(i));
                il.Add(OpCodes.Ldelem_Ref.ToInstruction());
                il.Add(OpCodes.Unbox_Any.ToInstruction(_imp.Import(ps[i]).ToTypeDefOrRef()));
            }
            il.Add(OpCodes.Call.ToInstruction(target));
            var ret = shape.MethodSig.RetType.RemovePinnedAndModifiers();
            if (ret.ElementType == ElementType.Void) il.Add(OpCodes.Ldnull.ToInstruction());
            else if (HotPatchWeaver.IsValueType(ret)) il.Add(OpCodes.Box.ToInstruction(_imp.Import(ret).ToTypeDefOrRef()));
            il.Add(OpCodes.Ret.ToInstruction());
        }

        // ---------------- biến static mới ----------------

        TypeDefUser Holder(TypeDef t)
        {
            if (_holder.TryGetValue(t, out var h)) return h;
            h = NewType(_name, "S_" + t.FullName.Replace('.', '_').Replace('/', '_'));
            _holder[t] = h;
            return h;
        }

        void HolderStaticCtor(TypeDef t, List<FieldDef> fields)
        {
            var cctor = t.Methods.FirstOrDefault(m => m.IsStaticConstructor);
            if (cctor == null) return;
            var segs = Segments(cctor, out var ok);
            if (!ok) return; // đã báo lỗi ở bước phân tích nếu cần
            var want = new HashSet<FieldDef>(fields);
            var h = Holder(t);
            var c = _patch.CorLibTypes;
            var nc = new MethodDefUser(".cctor", MethodSig.CreateStatic(c.Void), MethodImplAttributes.IL | MethodImplAttributes.Managed,
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            // dựng thân tạm chỉ gồm các đoạn khởi tạo biến mới, rồi chép như một thân hàm bình thường
            var tmp = new MethodDefUser("tmp", MethodSig.CreateStatic(_new.CorLibTypes.Void), cctor.ImplAttributes, MethodAttributes.Static) { Body = new CilBody { InitLocals = cctor.Body.InitLocals } };
            foreach (var l in cctor.Body.Variables) tmp.Body.Variables.Add(new Local(l.Type, l.Name));
            var map = new Dictionary<Instruction, Instruction>();
            var picked = new List<Instruction>();
            foreach (var s in segs.Where(s => s.Target != null && want.Contains(s.Target)))
                for (var k = s.Start; k <= s.End; k++) picked.Add(cctor.Body.Instructions[k]);
            foreach (var ins in picked)
            {
                var x = new Instruction(ins.OpCode, ins.Operand is Local l ? tmp.Body.Variables[l.Index] : ins.Operand);
                map[ins] = x;
                tmp.Body.Instructions.Add(x);
            }
            foreach (var x in tmp.Body.Instructions)
                if (x.Operand is Instruction ti) x.Operand = map.TryGetValue(ti, out var mt) ? mt : x;
            tmp.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            h.Methods.Add(nc);
            nc.Body = CopyBody(tmp, nc);
        }

        // ---------------- chép thân hàm ----------------

        /// <summary>Chép thân <paramref name="src"/> (module mới) sang <paramref name="dst"/> (bản vá), đổi mọi tham chiếu.</summary>
        CilBody CopyBody(MethodDef src, MethodDef dst)
        {
            var sb = src.Body;
            var b = new CilBody { InitLocals = sb.InitLocals };
            var locals = new Dictionary<Local, Local>();
            foreach (var l in sb.Variables)
            {
                var nl = new Local(_imp.Import(l.Type), l.Name);
                b.Variables.Add(nl);
                locals[l] = nl;
            }
            Local tmpObj = null;
            Local TmpObj()
            {
                if (tmpObj == null) b.Variables.Add(tmpObj = new Local(_patch.CorLibTypes.Object, "__hpv"));
                return tmpObj;
            }
            var first = new Dictionary<Instruction, Instruction>(); // lệnh gốc → lệnh đầu tiên sinh ra (đích nhảy)
            var fix = new List<Instruction>();                      // lệnh có toán hạng là lệnh khác (sửa sau)
            var srcParams = src.Parameters;
            var dstParams = dst.Parameters;
            Parameter P(Parameter p) => dstParams[p.Index];

            Instruction nextIns = null;
            for (var i = 0; i < sb.Instructions.Count; i++)
            {
                var ins = sb.Instructions[i];
                nextIns = i + 1 < sb.Instructions.Count ? sb.Instructions[i + 1] : null;
                var start = b.Instructions.Count;
                Emit(ins);
                first[ins] = b.Instructions[start];
            }
            // chỉ đích là lệnh của hàm gốc mới phải đổi (nhánh do bộ sinh tự thêm đã trỏ đúng lệnh mới)
            foreach (var x in fix)
            {
                if (x.Operand is Instruction t && first.TryGetValue(t, out var nt)) x.Operand = nt;
                else if (x.Operand is Instruction[] ts) x.Operand = ts.Select(t2 => first.TryGetValue(t2, out var n2) ? n2 : t2).ToArray();
            }
            foreach (var eh in sb.ExceptionHandlers)
            {
                b.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType)
                {
                    TryStart = first[eh.TryStart],
                    TryEnd = eh.TryEnd == null ? null : first[eh.TryEnd],
                    HandlerStart = first[eh.HandlerStart],
                    HandlerEnd = eh.HandlerEnd == null ? null : first[eh.HandlerEnd],
                    FilterStart = eh.FilterStart == null ? null : first[eh.FilterStart],
                    CatchType = eh.CatchType == null ? null : _imp.Import(eh.CatchType),
                });
            }
            // nhánh ngắn chép nguyên có thể xa quá 127 byte (lệnh thay biến mới dài hơn): ra dạng dài rồi rút lại chỗ được
            b.SimplifyBranches();
            b.OptimizeBranches();
            return b;

            void Add(Instruction x)
            {
                b.Instructions.Add(x);
                if (x.Operand is Instruction || x.Operand is Instruction[]) fix.Add(x);
            }

            void Emit(Instruction ins)
            {
                var op = ins.Operand;
                switch (op)
                {
                    case Local l:
                        Add(new Instruction(ins.OpCode, locals[l]));
                        return;
                    case Parameter p:
                        Add(new Instruction(ins.OpCode, P(p)));
                        return;
                    case Instruction _:
                    case Instruction[] _:
                        Add(new Instruction(ins.OpCode, op));
                        return;
                    case FieldDef fd when fd.Module == _new && _newInstanceField.TryGetValue(fd, out var fid):
                        EmitNewInstanceField(ins, fd, fid);
                        return;
                    case FieldDef lf when lf.Name == "<>9" && IsLambdaCache(lf.DeclaringType) && !_cloneTypes.Contains(lf.DeclaringType):
                        // "<>9" (đối tượng của lớp lambda) đi cặp với ldftn ngay sau: lambda mới → đối tượng của bản chép một phần
                        var useClone = nextIns?.Operand is MethodDef nx && _lambdaNew.Contains(nx);
                        Add(new Instruction(ins.OpCode, useClone ? (IField)_lambdaCloneInst[lf.DeclaringType] : MapFieldRef(lf)));
                        return;
                    case IMethod im when im.MDToken.Table == dnlib.DotNet.MD.Table.Method && im is MethodDef md && md.Module == _new:
                        EmitMethodRef(ins, md);
                        return;
                    case ITypeDefOrRef t:
                        Add(new Instruction(ins.OpCode, _imp.Import(t)));
                        return;
                    case IField f:
                        Add(new Instruction(ins.OpCode, _imp.Import(f)));
                        return;
                    case IMethod m:
                        Add(new Instruction(ins.OpCode, _imp.Import(m)));
                        return;
                    case MethodSig ms:
                        Add(new Instruction(ins.OpCode, _imp.Import(ms)));
                        return;
                    default:
                        Add(new Instruction(ins.OpCode, op));
                        return;
                }
            }

            void EmitNewInstanceField(Instruction ins, FieldDef fd, int fid)
            {
                var ft = fd.FieldType.RemovePinnedAndModifiers();
                var vt = HotPatchWeaver.IsValueType(ft);
                var tref = _imp.Import(ft).ToTypeDefOrRef();
                switch (ins.OpCode.Code)
                {
                    case Code.Ldfld:
                        Add(OpCodes.Ldc_I4.ToInstruction(fid));
                        Add(OpCodes.Call.ToInstruction(_hpFieldGet));
                        if (!vt)
                        {
                            Add(OpCodes.Castclass.ToInstruction(tref));
                            return;
                        }
                        // chưa gán → giá trị mặc định của kiểu
                        var has = OpCodes.Unbox_Any.ToInstruction(tref);
                        var end = OpCodes.Nop.ToInstruction();
                        Add(OpCodes.Dup.ToInstruction());
                        Add(OpCodes.Brtrue.ToInstruction(has));
                        Add(OpCodes.Pop.ToInstruction());
                        EmitDefault(ft, tref);
                        Add(OpCodes.Br.ToInstruction(end));
                        Add(has);
                        Add(end);
                        return;
                    case Code.Stfld:
                        if (vt) Add(OpCodes.Box.ToInstruction(tref));
                        Add(OpCodes.Stloc.ToInstruction(TmpObj()));
                        Add(OpCodes.Ldc_I4.ToInstruction(fid));
                        Add(OpCodes.Ldloc.ToInstruction(TmpObj()));
                        Add(OpCodes.Call.ToInstruction(_hpFieldSet));
                        return;
                    default:
                        throw new NotSupportedException($"lấy địa chỉ biến mới {fd.FullName} ({ins.OpCode}) trong {src.FullName}");
                }
            }

            void EmitDefault(TypeSig ft, ITypeDefOrRef tref)
            {
                switch (ft.ElementType)
                {
                    case ElementType.I8:
                    case ElementType.U8: Add(OpCodes.Ldc_I8.ToInstruction(0L)); return;
                    case ElementType.R4: Add(OpCodes.Ldc_R4.ToInstruction(0f)); return;
                    case ElementType.R8: Add(OpCodes.Ldc_R8.ToInstruction(0d)); return;
                    case ElementType.Boolean:
                    case ElementType.Char:
                    case ElementType.I1:
                    case ElementType.U1:
                    case ElementType.I2:
                    case ElementType.U2:
                    case ElementType.I4:
                    case ElementType.U4: Add(OpCodes.Ldc_I4_0.ToInstruction()); return;
                    default:
                        var l = new Local(_imp.Import(ft), "__hpd");
                        b.Variables.Add(l);
                        Add(OpCodes.Ldloca.ToInstruction(l));
                        Add(OpCodes.Initobj.ToInstruction(tref));
                        Add(OpCodes.Ldloc.ToInstruction(l));
                        return;
                }
            }

            void EmitMethodRef(Instruction ins, MethodDef md)
            {
                var code = ins.OpCode.Code;
                // hàm virtual mới (hoặc override của nó) → bộ phân nhánh
                if (_newVirtualRootOf.TryGetValue(md, out var root) && (code == Code.Callvirt || (code == Code.Call && md == root)))
                {
                    Add(OpCodes.Call.ToInstruction(_dispatchOf[root]));
                    return;
                }
                // hàm có sẵn (đã vá theo) mà chữ ký nhắc tới lớp chép — lớp bị thay cả lớp / lớp mới: bản cài không có hàm mang chữ ký đó
                // (cùng tên lớp nhưng là kiểu của bản vá) → tham chiếu thường sẽ MissingMethodException lúc chạy (vd mediator get_amultPanel
                // trả panel đổi lớp cha). Gọi thẳng hàm static của bản vá như hàm mới; hàm virtual đang bị override thì không gọi thẳng được
                // (bỏ qua ô ảo) — bước kiểm sau khi ghi sẽ báo lỗi.
                var viaStatic = _addedSet.Contains(md)
                                || (_changed.Contains(md) && MentionsClone(md.MethodSig) && !IsOverriddenInNew(md));
                if (viaStatic && _staticOf.TryGetValue(md, out var st))
                {
                    switch (code)
                    {
                        case Code.Call:
                        case Code.Callvirt:
                            Add(OpCodes.Call.ToInstruction(st));
                            return;
                        case Code.Ldftn when md.IsStatic:
                            Add(OpCodes.Ldftn.ToInstruction(st));
                            return;
                        case Code.Ldftn:
                        case Code.Ldvirtftn:
                            // delegate tới hàm thành viên mới: [đối tượng] → lớp bọc giữ đối tượng, ldftn hàm của lớp bọc
                            if (code == Code.Ldvirtftn) Add(OpCodes.Pop.ToInstruction());
                            var bnd = Binder(md);
                            Add(OpCodes.Newobj.ToInstruction(bnd.FindDefaultConstructor() ?? bnd.Methods.First(x => x.IsConstructor)));
                            Add(OpCodes.Ldftn.ToInstruction(bnd.Methods.First(x => x.Name == "Invoke")));
                            return;
                        case Code.Newobj:
                            throw new NotSupportedException("constructor mới " + md.FullName);
                    }
                }
                // gọi không virtual (base.M()) tới hàm có ô đang có override mới → bỏ ô một lần (khỏi vòng về override mới)
                if (code == Code.Call && md.IsVirtual && !md.IsStatic && _slots.TryGetValue(md.FullName, out var sid) && _overridesBySlot.ContainsKey(sid))
                {
                    if (_changed.Contains(md))
                    {
                        Add(OpCodes.Call.ToInstruction(_staticOf[md]));
                        return;
                    }
                    Add(OpCodes.Ldc_I4.ToInstruction(sid));
                    Add(OpCodes.Call.ToInstruction(_hpSkip));
                }
                Add(new Instruction(ins.OpCode, MapMethodRef(md)));
            }
        }

        /// <summary>Lớp bọc cho delegate tới hàm thành viên mới (hàm static trong bản vá): giữ đối tượng, Invoke gọi hàm static.</summary>
        TypeDef Binder(MethodDef md)
        {
            if (_binderOf.TryGetValue(md, out var t)) return t;
            var c = _patch.CorLibTypes;
            var bt = new TypeDefUser(_name, "Bind" + _seq++, c.Object.TypeDefOrRef)
            {
                Attributes = TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            };
            _patch.Types.Add(bt);
            var self = new FieldDefUser("self", new FieldSig(c.Object), FieldAttributes.Public);
            bt.Fields.Add(self);
            var objCtor = new MemberRefUser(_patch, ".ctor", MethodSig.CreateInstance(c.Void), c.Object.TypeDefOrRef);
            var ctor = new MethodDefUser(".ctor", MethodSig.CreateInstance(c.Void, c.Object), MethodImplAttributes.IL | MethodImplAttributes.Managed,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
            bt.Methods.Add(ctor);
            ctor.Body = Body(new List<Instruction>
            {
                OpCodes.Ldarg_0.ToInstruction(), OpCodes.Call.ToInstruction(objCtor),
                OpCodes.Ldarg_0.ToInstruction(), OpCodes.Ldarg_1.ToInstruction(), OpCodes.Stfld.ToInstruction(self),
                OpCodes.Ret.ToInstruction(),
            });
            var ps = md.MethodSig.Params.Select(p => _imp.Import(p)).ToArray();
            var invoke = new MethodDefUser("Invoke", MethodSig.CreateInstance(_imp.Import(md.MethodSig.RetType), ps), MethodImplAttributes.IL | MethodImplAttributes.Managed,
                MethodAttributes.Public | MethodAttributes.HideBySig);
            bt.Methods.Add(invoke);
            var il = new List<Instruction>
            {
                OpCodes.Ldarg_0.ToInstruction(), OpCodes.Ldfld.ToInstruction(self), OpCodes.Castclass.ToInstruction(MapType(md.DeclaringType)),
            };
            for (var i = 1; i <= ps.Length; i++) il.Add(OpCodes.Ldarg.ToInstruction(invoke.Parameters[i]));
            il.Add(OpCodes.Call.ToInstruction(_staticOf[md]));
            il.Add(OpCodes.Ret.ToInstruction());
            invoke.Body = Body(il);
            _binderOf[md] = bt;
            return bt;
        }

        // =====================================================================================================================
        // ghi
        // =====================================================================================================================

        sealed class Logger : ILogger
        {
            public readonly List<string> Errors = new List<string>();

            public void Log(object sender, LoggerEvent loggerEvent, string format, params object[] args)
            {
                if (loggerEvent == LoggerEvent.Error) Errors.Add(string.Format(format, args));
            }

            public bool IgnoresEvent(LoggerEvent loggerEvent) => loggerEvent != LoggerEvent.Error;
        }

        void Write()
        {
            var log = new Logger();
            var opts = new ModuleWriterOptions(_patch) { Logger = log };
            using (var ms = new MemoryStream())
            {
                _patch.Write(ms, opts);
                foreach (var e in log.Errors) Error("ghi bản vá: " + e);
                if (log.Errors.Count == 0) _r.Dll = ms.ToArray();
            }
            if (_r.Dll != null) VerifyGameRefs(_r.Dll);
            _r.Report.Add($"bản vá {(_r.Dll?.Length ?? 0) / 1024.0:0.0} KB: {_r.Summary}");
        }

        /// <summary>
        /// Kiểm bản vá đã ghi: mọi tham chiếu hàm / biến sang mã game của bản cài phải có trong bản cài. Tham chiếu mà chữ ký nhắc tới kiểu
        /// định nghĩa trong chính bản vá (lớp chép cùng tên lớp cũ) thì bản cài không có → lúc chạy MissingMethodException / MissingField.
        /// Báo lỗi ngay khi sinh thay vì để người chơi gặp.
        /// </summary>
        void VerifyGameRefs(byte[] dll)
        {
            var mod = ModuleDefMD.Load(dll);
            var game = _gameRef.Name.String;
            var bad = new List<string>();
            foreach (var mr in mod.GetMemberRefs())
            {
                var scope = mr.DeclaringType?.ScopeType;
                if (!(scope is TypeRef tr) || tr.DefinitionAssembly?.Name != game) continue;
                var local = mr.IsMethodRef
                    ? HasLocalType(mr.MethodSig.RetType) || mr.MethodSig.Params.Any(HasLocalType)
                    : HasLocalType(mr.FieldSig?.Type);
                if (local) bad.Add(mr.FullName);
            }
            foreach (var b in bad.Distinct().Take(40)) Error("bản vá tham chiếu " + b + ": chữ ký có lớp của bản vá, bản cài không có — lỗi bộ sinh");
        }

        static bool HasLocalType(TypeSig s)
        {
            for (; s != null; s = s.Next)
            {
                if (s is TypeDefOrRefSig td && td.TypeDefOrRef is TypeDef) return true;
                if (s is GenericInstSig gi && (HasLocalType(gi.GenericType) || gi.GenericArguments.Any(HasLocalType))) return true;
            }
            return false;
        }
    }
}
