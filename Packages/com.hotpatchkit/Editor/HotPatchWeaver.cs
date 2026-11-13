using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HotPatchKit.Editor
{
    /// <summary>
    /// Vá hàm kiểu DHE — bước dựng app: chèn vào đầu mỗi hàm của assembly mã game (sau khi Unity biên dịch, trước IL2CPP) phép kiểm ô vá
    /// (HotPatchKit.HotPatch):
    /// <code>
    /// if (HotPatch.On) { var f = HotPatch.Slots[id]; if (f != null &amp;&amp; !HotPatch.TakeSkip(id)) return (R)f(this, new object[] { a, b… }); }
    /// </code>
    /// và trả bảng mã hàm (thứ tự = mã) để lưu cùng DLL gốc (chưa chèn) của bản cài — bộ sinh bản vá so DLL mới với DLL gốc đó.
    /// Bỏ qua (không vá được — sửa thì phải phát hành bản cài): static constructor, hàm có tham số / trả ref / con trỏ, hàm của lớp
    /// struct / generic, hàm của lớp do trình biên dịch sinh (lambda, closure, iterator — đổi thì bản vá chép cả lớp và vá hàm tạo
    /// ra chúng).
    /// </summary>
    public static class HotPatchWeaver
    {
        public const string MapHeader = "# hotpatchkit-map 1";

        /// <summary>Assembly AOT chứa lớp HotPatch (ô vá) — mã game đã chèn ô và bản vá tham chiếu theo tên.</summary>
        public const string RuntimeAssembly = "HotPatchKit.Runtime";
        public const string RuntimeNamespace = "HotPatchKit";

        /// <summary>Tham chiếu tới assembly runtime: có sẵn trong module thì dùng, không thì thêm (mã game không cần tham chiếu HotPatchKit).</summary>
        public static AssemblyRef RuntimeRef(ModuleDef module) =>
            module.GetAssemblyRefs().FirstOrDefault(a => a.Name == RuntimeAssembly) ?? new AssemblyRefUser(RuntimeAssembly, new Version(0, 0, 0, 0));

        public static bool IsCompilerGenerated(TypeDef t)
        {
            for (var x = t; x != null; x = x.DeclaringType)
                if (x.Name.String.IndexOf('<') >= 0) return true;
            return false;
        }

        public static bool Eligible(MethodDef m)
        {
            if (!m.HasBody || m.Body.Instructions.Count == 0) return false;
            if (m.IsStaticConstructor || m.HasGenericParameters) return false;
            var t = m.DeclaringType;
            if (t.IsGlobalModuleType || t.IsValueType || IsCompilerGenerated(t)) return false;
            for (var x = t; x != null; x = x.DeclaringType)
                if (x.HasGenericParameters) return false;
            if (Unsupported(m.ReturnType)) return false;
            foreach (var p in m.Parameters)
                if (p.IsNormalMethodParameter && Unsupported(p.Type)) return false;
            return true;
        }

        static bool Unsupported(TypeSig s)
        {
            s = s.RemovePinnedAndModifiers();
            return s.IsByRef || s.IsPointer || s.IsFunctionPointer || s.ContainsGenericParameter;
        }

        public static string Key(MethodDef m) => m.FullName;

        public static bool IsValueType(TypeSig s)
        {
            s = s.RemovePinnedAndModifiers();
            if (s is GenericInstSig g) return g.GenericType.IsValueTypeSig;
            return s.IsValueType || s.IsPrimitive;
        }

        /// <summary>Chèn phép kiểm vào mọi hàm đủ điều kiện; trả danh sách khoá hàm theo mã (chỉ số).</summary>
        public static List<string> Weave(ModuleDef module)
        {
            var aot = RuntimeRef(module);
            var c = module.CorLibTypes;
            var hp = new TypeRefUser(module, RuntimeNamespace, "HotPatch", aot);
            var objArr = new SZArraySig(c.Object);
            var funcSig = new GenericInstSig(new ClassSig(new TypeRefUser(module, "System", "Func`3", c.AssemblyRef)), c.Object, objArr, c.Object);
            var funcType = new TypeSpecUser(funcSig);
            var refs = new Refs
            {
                On = new MemberRefUser(module, "On", new FieldSig(c.Boolean), hp),
                Slots = new MemberRefUser(module, "Slots", new FieldSig(new SZArraySig(funcSig)), hp),
                TakeSkip = new MemberRefUser(module, "TakeSkip", MethodSig.CreateStatic(c.Boolean, c.Int32), hp),
                Invoke = new MemberRefUser(module, "Invoke", MethodSig.CreateInstance(new GenericVar(2), new GenericVar(0), new GenericVar(1)), funcType),
                FuncSig = funcSig,
                Object = c.Object.TypeDefOrRef,
            };
            var methods = module.GetTypes().SelectMany(t => t.Methods).Where(Eligible).OrderBy(Key, StringComparer.Ordinal).ToList();
            var keys = new List<string>(methods.Count);
            for (var id = 0; id < methods.Count; id++)
            {
                Inject(methods[id], id, refs);
                keys.Add(Key(methods[id]));
            }
            return keys;
        }

        sealed class Refs
        {
            public MemberRef On, Slots, TakeSkip, Invoke;
            public TypeSig FuncSig;
            public ITypeDefOrRef Object;
        }

        static void Inject(MethodDef m, int id, Refs r)
        {
            var body = m.Body;
            body.SimplifyMacros(m.Parameters);
            body.SimplifyBranches();
            var il = body.Instructions;
            var first = il[0];
            var f = new Local(r.FuncSig, "__hp");
            body.Variables.Add(f);
            var pro = new List<Instruction>
            {
                OpCodes.Ldsfld.ToInstruction(r.On),
                OpCodes.Brfalse.ToInstruction(first),
                OpCodes.Ldsfld.ToInstruction(r.Slots),
                OpCodes.Ldc_I4.ToInstruction(id),
                OpCodes.Ldelem_Ref.ToInstruction(),
                OpCodes.Stloc.ToInstruction(f),
                OpCodes.Ldloc.ToInstruction(f),
                OpCodes.Brfalse.ToInstruction(first),
                OpCodes.Ldc_I4.ToInstruction(id),
                OpCodes.Call.ToInstruction(r.TakeSkip),
                OpCodes.Brtrue.ToInstruction(first),
                OpCodes.Ldloc.ToInstruction(f),
                m.IsStatic ? OpCodes.Ldnull.ToInstruction() : OpCodes.Ldarg.ToInstruction(m.Parameters[0]),
            };
            var ps = m.Parameters.Where(p => p.IsNormalMethodParameter).ToList();
            pro.Add(OpCodes.Ldc_I4.ToInstruction(ps.Count));
            pro.Add(OpCodes.Newarr.ToInstruction(r.Object));
            for (var i = 0; i < ps.Count; i++)
            {
                pro.Add(OpCodes.Dup.ToInstruction());
                pro.Add(OpCodes.Ldc_I4.ToInstruction(i));
                pro.Add(OpCodes.Ldarg.ToInstruction(ps[i]));
                if (IsValueType(ps[i].Type)) pro.Add(OpCodes.Box.ToInstruction(ps[i].Type.RemovePinnedAndModifiers().ToTypeDefOrRef()));
                pro.Add(OpCodes.Stelem_Ref.ToInstruction());
            }
            pro.Add(OpCodes.Callvirt.ToInstruction(r.Invoke));
            if (m.ReturnType.RemovePinnedAndModifiers().ElementType == ElementType.Void) pro.Add(OpCodes.Pop.ToInstruction());
            else pro.Add(OpCodes.Unbox_Any.ToInstruction(m.ReturnType.RemovePinnedAndModifiers().ToTypeDefOrRef()));
            pro.Add(OpCodes.Ret.ToInstruction());
            for (var i = pro.Count - 1; i >= 0; i--) il.Insert(0, pro[i]);
            body.OptimizeBranches();
            body.OptimizeMacros();
        }

        // ---------------- bảng mã hàm ----------------

        public static void WriteMap(string path, IList<string> keys)
        {
            var sb = new StringBuilder();
            sb.Append(MapHeader).Append('\n');
            for (var i = 0; i < keys.Count; i++) sb.Append(i).Append('\t').Append(keys[i]).Append('\n');
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static Dictionary<string, int> ReadMap(string path)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var tab = line.IndexOf('\t');
                map[line.Substring(tab + 1)] = int.Parse(line.Substring(0, tab));
            }
            return map;
        }
    }
}
