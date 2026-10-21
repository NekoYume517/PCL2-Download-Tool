#nullable disable
// 整合包导入解锁 + 依赖解析（Cecil 全量重写路径）
//
// 为什么不能像「原版下载」那样做等长字节替换：
//   ModModpack 里两处 kill 的**方法体长度必然变化** —— 一处要把「只弹提示」换成
//   「选文件 + 起线程」，另一处要把「无条件 throw」换成「按 PackType 分派」。
//   所以只能走 Cecil 重写元数据。
//
// 为什么以前不能重写：
//   PCL 把 PCLCS / MeloongCore / MeloongCore.Wpf / Newtonsoft.Json 等依赖塞在
//   PCL.Resources.resources 里，运行时才释放；Cecil 写回时需要能解析这些程序集，
//   否则抛 AssemblyResolutionException。这里把它们解到临时目录喂给 AssemblyResolver。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class DepResolver
{
    // 从 PCL.Resources.resources 里把内嵌程序集解到临时目录。
    // 返回 null 表示这个 exe 没有内嵌资源（或不是 PCL），调用方应回退到纯读取。
    public static string Extract(ModuleDefinition module, out int count)
    {
        count = 0;
        var res = module.Resources.FirstOrDefault(r => r.Name == "PCL.Resources.resources") as EmbeddedResource;
        if (res == null) return null;

        string dir = Path.Combine(Path.GetTempPath(), "PCL2RestoreDeps", Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            using var s = res.GetResourceStream();
            using var rr = new ResourceReader(s);
            var e = rr.GetEnumerator();
            while (e.MoveNext())
            {
                if (!(e.Value is byte[] b) || b.Length < 2) continue;
                if (b[0] != 0x4D || b[1] != 0x5A) continue;          // 只挑 PE（MZ）
                File.WriteAllBytes(Path.Combine(dir, e.Key + ".dll"), b);
                count++;
            }
        }
        catch
        {
            TryDelete(dir);
            throw;
        }
        return dir;
    }

    public static void TryDelete(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { Directory.Delete(dir, true); } catch { }
    }
}

// 只在无法解析时才被调用；Cecil 写回只需要 AssemblyNameReference，
// 不需要真实的类型定义。
sealed class TolerantResolver : DefaultAssemblyResolver
{
    public TolerantResolver(string depsDir, string exeDir)
    {
        foreach (var d in new[] { depsDir, exeDir })
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) AddSearchDirectory(d);
    }

    public override AssemblyDefinition Resolve(AssemblyNameReference name)
    {
        try { return base.Resolve(name); }
        catch (AssemblyResolutionException)
        {
            return AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition(name.Name, name.Version ?? new Version(1, 0, 0, 0)),
                name.Name, ModuleKind.Dll);
        }
    }
}
static class ModpackPatch
{
    public sealed class Outcome
    {
        public bool EntryRebuilt;      // 无参 ModpackInstall()
        public bool DispatchRebuilt;   // 有参 ModpackInstall(...) 的 kill 分派
        public bool AlreadyPatched;    // 两处都已是真实现
        public List<string> Notes = new List<string>();
        public bool Changed => EntryRebuilt || DispatchRebuilt;
    }

    const string ModpackType = "PCL.ModModpack";
    const string InstallName = "ModpackInstall";
    const string KillA = "为遵守相关法律法规";

    // 与上游 InstallPack* 的参数形态一致：(File, Archive, BaseFolder [, InstanceName] [, Logo])
    static readonly (string Name, bool Name_, bool Logo)[] Dispatch = new[]
    {
        ("InstallPackCurseForge",   true,  true ),   // 0  CurseForge
        ("InstallPackHMCL",         false, false),   // 1  HMCL
        ("InstallPackMMC",          false, false),   // 2  MMC
        ("InstallPackMCBBS",        true,  false),   // 3  MCBBS
        ("InstallPackModrinth",     true,  true ),   // 4  Modrinth
        (null,                      false, false),   // 5  未知
        (null,                      false, false),   // 6
        (null,                      false, false),   // 7
        (null,                      false, false),   // 8
        ("InstallPackLauncherPack", false, false),   // 9  带启动器的压缩包
    };

    public static Outcome Apply(ModuleDefinition module)
    {
        var o = new Outcome();
        var t = module.GetType(ModpackType);
        if (t == null) { o.Notes.Add("未找到 PCL.ModModpack，跳过整合包解锁"); return o; }

        var entry = t.Methods.FirstOrDefault(m => m.Name == InstallName && m.Parameters.Count == 0);
        var full = t.Methods.FirstOrDefault(m => m.Name == InstallName && m.Parameters.Count == 3);

        if (entry == null || full == null)
        {
            o.Notes.Add("未找到 ModpackInstall 的预期重载，跳过整合包解锁");
            return o;
        }

        bool entryOk = IsEntryRestored(entry);
        bool dispatchOk = IsDispatchRestored(full);
        o.AlreadyPatched = entryOk && dispatchOk;

        if (!entryOk)
        {
            RebuildEntry(module, t, entry);
            o.EntryRebuilt = true;
        }
        if (!dispatchOk)
        {
            RebuildDispatch(module, t, full, o);
            o.DispatchRebuilt = true;
        }
        return o;
    }

    // 真实现会调用 ModBase.RunInThread；替身只有 newobj closure + Hint + ret
    static bool IsEntryRestored(MethodDefinition m)
    {
        if (m.Body == null) return false;
        return m.Body.Instructions.Any(i => i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference mr && mr.Name == "RunInThread");
    }

    // 真实现是 switch 分派；替身是 ldstr + newobj + throw
    static bool IsDispatchRestored(MethodDefinition m)
    {
        if (m.Body == null) return false;
        if (m.Body.Instructions.Any(i => i.OpCode == OpCodes.Switch)) return true;
        return !m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr &&
            i.Operand is string s && s.Contains(KillA));
    }

    // ---------------- 1) 无参 ModpackInstall() ----------------
    // 真实现（上游 ModModpack.vb）：
    //   Dim File = Dialogs.SelectFile("选择整合包压缩文件", False, filter:=...).FirstOrDefault()
    //   If String.IsNullOrEmpty(File) Then Return
    //   RunInThread(Sub() Try ModpackInstall(File) Catch ex ... End Try)
    static void RebuildEntry(ModuleDefinition module, TypeDefinition modpack, MethodDefinition m)
    {
        var closure = module.GetType(ModpackType + "/_Closure$__0-0");
        if (closure == null) throw new Exception("找不到 ModModpack/_Closure$__0-0，无法重建无参 ModpackInstall");
        var ctor = closure.Methods.First(x => x.IsConstructor && x.Parameters.Count == 0);
        var lambda = closure.Methods.First(x => x.Name == "_Lambda$__0");
        var fFile = closure.Fields.First(x => x.Name == "$VB$Local_File");

        // 复用二进制里已有的成员引用（PCL 自身在 ModJava / PageInstanceExport 里用过 SelectFile）
        var refs = CollectRefs(module);

        var body = m.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.InitLocals = true;
        body.MaxStackSize = 4;

        var vClosure = new VariableDefinition(closure);
        var vFile = new VariableDefinition(module.TypeSystem.String);
        body.Variables.Add(vClosure);
        body.Variables.Add(vFile);

        var il = body.GetILProcessor();
        var ret = Instruction.Create(OpCodes.Ret);

        il.Append(Instruction.Create(OpCodes.Newobj, ctor));
        il.Append(Instruction.Create(OpCodes.Stloc, vClosure));
        il.Append(Instruction.Create(OpCodes.Ldstr, "选择整合包压缩文件"));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));                       // multiselect = False
        il.Append(Instruction.Create(OpCodes.Ldnull));                         // defaultDirectory = Nothing
        il.Append(Instruction.Create(OpCodes.Ldstr, refs.FilterString));
        il.Append(Instruction.Create(OpCodes.Call, refs.OneOfFromString));
        il.Append(Instruction.Create(OpCodes.Newobj, refs.NullableOneOfCtor));
        il.Append(Instruction.Create(OpCodes.Call, refs.SelectFile));
        il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
        il.Append(Instruction.Create(OpCodes.Call, refs.ElementAtOrDefaultString));
        il.Append(Instruction.Create(OpCodes.Stloc, vFile));
        il.Append(Instruction.Create(OpCodes.Ldloc, vFile));
        il.Append(Instruction.Create(OpCodes.Call, refs.IsNullOrEmpty));
        il.Append(Instruction.Create(OpCodes.Brtrue, ret));
        il.Append(Instruction.Create(OpCodes.Ldloc, vClosure));
        il.Append(Instruction.Create(OpCodes.Ldloc, vFile));
        il.Append(Instruction.Create(OpCodes.Stfld, fFile));
        il.Append(Instruction.Create(OpCodes.Ldloc, vClosure));
        il.Append(Instruction.Create(OpCodes.Ldftn, lambda));
        il.Append(Instruction.Create(OpCodes.Newobj, refs.ActionCtor));
        il.Append(Instruction.Create(OpCodes.Call, refs.RunInThread));
        il.Append(ret);
    }

    // ---------------- 2) 有参 ModpackInstall 的分派 ----------------
    static void RebuildDispatch(ModuleDefinition module, TypeDefinition modpack, MethodDefinition m, Outcome o)
    {
        var body = m.Body;
        var ins = body.Instructions;
        var il = body.GetILProcessor();

        // 定位 kill 段：ldstr(kill) / newobj Exception / throw
        var kill = ins.FirstOrDefault(i => i.OpCode == OpCodes.Ldstr &&
                                           i.Operand is string s && s.Contains(KillA));
        if (kill == null)
        {
            o.Notes.Add("未找到整合包 kill 桩（可能已被其它补丁处理），跳过分派重建");
            return;
        }
        var newobj = kill.Next;
        var thr = newobj?.Next;
        if (newobj == null || newobj.OpCode != OpCodes.Newobj || thr == null || thr.OpCode != OpCodes.Throw)
            throw new Exception("整合包 kill 桩结构不符（预期 ldstr/newobj/throw）");

        // 关键：这个 ldstr 是 try 正常结束时的落点（leave 目标），也是外层 catch 的 HandlerEnd。
        // 直接 Remove 会留下悬空引用（写回后跳转到垃圾偏移）。必须先改指向。
        var killOff = kill.Offset;

        // 先构建分派块（追加在方法末尾），拿到入口指令后再重定向
        var swSlot = Instruction.Create(OpCodes.Nop);
        il.Append(Instruction.Create(OpCodes.Ldloc, body.Variables[4]));    // PackType
        il.Append(swSlot);

        var jump = new Instruction[Dispatch.Length];
        for (int i = 0; i < Dispatch.Length; i++)
        {
            var (name, withName, withLogo) = Dispatch[i];
            if (name == null) continue;
            var target = modpack.Methods.FirstOrDefault(x => x.Name == name);
            if (target == null) { o.Notes.Add($"缺少 {name}，该项将走默认分支"); continue; }

            var seq = new List<Instruction>
            {
                Instruction.Create(OpCodes.Ldarg_0),                 // File
                Instruction.Create(OpCodes.Ldloc, body.Variables[1]),// Archive
                Instruction.Create(OpCodes.Ldloc, body.Variables[2]),// ArchiveBaseFolder
            };
            if (withName) seq.Add(Instruction.Create(OpCodes.Ldarg_1));
            if (withLogo) seq.Add(Instruction.Create(OpCodes.Ldarg_2));
            seq.Add(Instruction.Create(OpCodes.Call, target));
            seq.Add(Instruction.Create(OpCodes.Ret));

            jump[i] = seq[0];
            foreach (var x in seq) il.Append(x);
        }

        // 默认分支：InstallPackCompress(File, Archive)
        var defTarget = modpack.Methods.FirstOrDefault(x => x.Name == "InstallPackCompress");
        if (defTarget == null) throw new Exception("缺少 InstallPackCompress，无法构建默认分支");
        var defEntry = Instruction.Create(OpCodes.Ldarg_0);
        il.Append(defEntry);
        il.Append(Instruction.Create(OpCodes.Ldloc, body.Variables[1]));
        il.Append(Instruction.Create(OpCodes.Call, defTarget));
        il.Append(Instruction.Create(OpCodes.Ret));

        for (int i = 0; i < jump.Length; i++) if (jump[i] == null) jump[i] = defEntry;
        il.Replace(swSlot, Instruction.Create(OpCodes.Switch, jump));

        // 重定向所有指向 kill 的分支 / 异常处理边界到分派入口
        var dispatchEntry = swSlot.Previous;   // Ldloc PackType
        int redirected = 0;
        foreach (var i in ins)
        {
            if (i.Operand is Instruction t && t == kill) { i.Operand = dispatchEntry; redirected++; }
            else if (i.Operand is Instruction[] ts)
                for (int k = 0; k < ts.Length; k++)
                    if (ts[k] == kill) { ts[k] = dispatchEntry; redirected++; }
        }
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.TryStart == kill) { eh.TryStart = dispatchEntry; redirected++; }
            if (eh.TryEnd == kill) { eh.TryEnd = dispatchEntry; redirected++; }
            if (eh.HandlerStart == kill) { eh.HandlerStart = dispatchEntry; redirected++; }
            if (eh.HandlerEnd == kill) { eh.HandlerEnd = dispatchEntry; redirected++; }
        }
        o.Notes.Add($"kill 桩 IL_{killOff:X4} 已替换为分派，重定向 {redirected} 处分支/异常边界");

        // 现在才安全删除
        il.Remove(thr);
        il.Remove(newobj);
        il.Remove(kill);
    }

    // ---------------- 成员引用 ----------------
    sealed class Refs
    {
        public MethodReference SelectFile;
        public MethodReference OneOfFromString;
        public MethodReference NullableOneOfCtor;
        public MethodReference ElementAtOrDefaultString;
        public MethodReference IsNullOrEmpty;
        public MethodReference ActionCtor;
        public MethodReference RunInThread;
        public string FilterString;
    }

    const string OneOfFilter =
        "MeloongCore.OneOf`2<System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>,System.String>";

    static Refs CollectRefs(ModuleDefinition module)
    {
        var r = new Refs
        {
            // 用 string 形态的 filter（Dialogs 会原样塞进 OpenFileDialog.Filter）
            FilterString = "整合包文件(*.rar;*.zip;*.mrpack)|*.rar;*.zip;*.mrpack",
        };

        var memberRefs = module.GetMemberReferences().OfType<MethodReference>().ToList();

        r.SelectFile = memberRefs.FirstOrDefault(x =>
            x.DeclaringType.FullName == "MeloongCore.Wpf.Dialogs" && x.Name == "SelectFile");

        r.OneOfFromString = memberRefs.FirstOrDefault(x =>
            x.DeclaringType.FullName == OneOfFilter && x.Name == "op_Implicit" &&
            x.Parameters.Count == 1 && x.Parameters[0].ParameterType is GenericParameter gp && gp.Position == 1);

        r.NullableOneOfCtor = memberRefs.FirstOrDefault(x =>
            x.DeclaringType.FullName == "System.Nullable`1<" + OneOfFilter + ">" && x.Name == ".ctor");

        var elemAt = memberRefs.FirstOrDefault(x =>
            x.DeclaringType.FullName == "System.Linq.Enumerable" && x.Name == "ElementAtOrDefault");
        if (elemAt != null)
        {
            var gim = new GenericInstanceMethod(elemAt);
            gim.GenericArguments.Add(module.TypeSystem.String);
            r.ElementAtOrDefaultString = gim;
        }

        r.IsNullOrEmpty = memberRefs.FirstOrDefault(x =>
            x.DeclaringType.FullName == "System.String" && x.Name == "IsNullOrEmpty");

        r.ActionCtor = memberRefs.FirstOrDefault(x =>
            x.DeclaringType.FullName == "System.Action" && x.Name == ".ctor");

        var run = module.GetType("PCL.ModBase")?.Methods
            .FirstOrDefault(x => x.Name == "RunInThread" && x.Parameters.Count == 1);
        if (run != null) r.RunInThread = module.ImportReference(run);

        var missing = new List<string>();
        if (r.SelectFile == null) missing.Add("Dialogs.SelectFile");
        if (r.OneOfFromString == null) missing.Add("OneOf.op_Implicit(string)");
        if (r.NullableOneOfCtor == null) missing.Add("Nullable<OneOf>::.ctor");
        if (r.ElementAtOrDefaultString == null) missing.Add("Enumerable.ElementAtOrDefault");
        if (r.IsNullOrEmpty == null) missing.Add("String.IsNullOrEmpty");
        if (r.ActionCtor == null) missing.Add("Action::.ctor");
        if (r.RunInThread == null) missing.Add("ModBase.RunInThread");
        if (missing.Count > 0)
            throw new Exception("缺少重建所需的成员引用：" + string.Join(", ", missing) +
                                "（该版本可能改动过大，请提 issue 附上 PCL 版本号）");
        return r;
    }

    // ---------------- 自检 ----------------
    // 重写元数据最怕的是「分支/异常边界指向已删除的指令」—— 那种 exe 能启动，
    // 但一走到对应路径就崩。这里把所有引用做一次完整性扫描。
    public static List<string> Audit(MethodDefinition m)
    {
        var bad = new List<string>();
        if (m.Body == null) return bad;
        var set = new HashSet<Instruction>(m.Body.Instructions);
        foreach (var i in m.Body.Instructions)
        {
            if (i.Operand is Instruction t && !set.Contains(t))
                bad.Add($"{m.Name}: IL_{i.Offset:X4} 跳转到已删除的指令");
            else if (i.Operand is Instruction[] ts)
                foreach (var x in ts) if (!set.Contains(x))
                    bad.Add($"{m.Name}: IL_{i.Offset:X4} switch 含已删除目标");
        }
        foreach (var eh in m.Body.ExceptionHandlers)
        {
            if (eh.TryStart != null && !set.Contains(eh.TryStart)) bad.Add($"{m.Name}: TryStart 悬空");
            if (eh.TryEnd != null && !set.Contains(eh.TryEnd)) bad.Add($"{m.Name}: TryEnd 悬空");
            if (eh.HandlerStart != null && !set.Contains(eh.HandlerStart)) bad.Add($"{m.Name}: HandlerStart 悬空");
            if (eh.HandlerEnd != null && !set.Contains(eh.HandlerEnd)) bad.Add($"{m.Name}: HandlerEnd 悬空");
        }
        return bad;
    }
}
