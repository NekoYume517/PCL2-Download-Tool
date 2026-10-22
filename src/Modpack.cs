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
//
// 注意：Cecil 写回时还会为「带默认值的参数」解析其常量类型（MetadataBuilder.GetConstantType），
// 而 PCL 的方法签名里有 `ThreadPriority` 这类 mscorlib 枚举。目标 exe 是 .NET Framework 程序集，
// 所以必须让 resolver 能看见 .NET Framework 的 mscorlib —— 否则会抛
// `Failed to resolve System.Threading.ThreadPriority`。
// 自包含单文件发布时进程目录里没有 mscorlib.dll，这个坑只有在发布产物上才暴露。
sealed class TolerantResolver : DefaultAssemblyResolver
{
    static readonly string[] FrameworkDirs =
    {
        @"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8",
        @"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2",
        @"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.1",
        @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319",
        @"C:\Windows\Microsoft.NET\Framework\v4.0.30319",
    };

    public TolerantResolver(string depsDir, string exeDir)
    {
        foreach (var d in new[] { depsDir, exeDir })
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) AddSearchDirectory(d);
        foreach (var d in FrameworkDirs)
            if (Directory.Exists(d)) AddSearchDirectory(d);
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
    //
    // 原始结构（Cecil 实测，2.99.0.0）：
    //   try {                                       // TryStart=27, TryEnd=2E5
    //       try { ... 解析包类型 ... }              // 6D..284（catch）
    //       ...
    //       leave IL_2DA                            // ← 正常完成路径，共 14 条 leave
    //       catch { ... throw ... }                 // handler [284, 2DA)
    //       IL_2DA: ldstr kill / newobj / throw     // ← 无条件 throw，**位于 try 内部**
    //   } finally { if (Archive != null) Dispose; } // handler [2E5, 方法末尾)
    //
    // kill 段同时是：所有 leave 的落点、内层 catch 的 HandlerEnd、且位于外层 try 内部。
    //
    // 三个必须同时满足的约束（每一个都靠实测踩出来）：
    //
    // ① 分派块必须插在 **kill 原来的位置**（try 内部），不能追加到方法末尾。
    //    追加到末尾 = 落在 finally handler 里 → `ret` 出现在 finally 中 → 非法 IL
    //    （CLR 报「公共语言运行时检测到无效的程序」）。
    //
    // ② 即使挪出 finally、放到 finally 之后，也是错的：那时 ZipArchive 已被 finally
    //    释放，InstallPack* 拿到的是已 Dispose 的对象 →
    //    ObjectDisposedException「无法访问已释放的对象」。
    //
    // ③ 所以必须照 VB 编译器处理 `Try...Finally` 里 `Return` 的套路来：
    //       在 try 内算出结果 → stloc 到局部变量 → leave 到 finally 之后
    //       → finally 先执行 → 落到 retStub: ldloc + ret
    //    同时把 finally 的 HandlerEnd 明确设为 retStub（原值为 null，会一路延伸）。
    //
    // catch 的 HandlerEnd 仍指向 kill 原位置（= 分派入口），保持原独占语义。
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

        var killOff = kill.Offset;

        // 结果局部变量：不能复用 V_0（原方法里它是别的用途），新加一个同返回类型的
        var vResult = new VariableDefinition(m.ReturnType);
        body.Variables.Add(vResult);
        if (body.InitLocals == false) body.InitLocals = true;

        // ---- ① 在 kill 之前插入分派块（仍在 try 内部）----
        var dispatchEntry = Instruction.Create(OpCodes.Ldloc, body.Variables[4]);   // PackType
        var swInstr = Instruction.Create(OpCodes.Switch, new Instruction[Dispatch.Length]);
        il.InsertBefore(kill, dispatchEntry);
        il.InsertBefore(kill, swInstr);

        var leaveToRet = new List<Instruction>();   // 稍后统一指向 retStub
        void EmitCase(MethodDefinition target, bool withName, bool withLogo, int slot)
        {
            var first = Instruction.Create(OpCodes.Ldarg_0);                     // File
            il.InsertBefore(kill, first);
            il.InsertBefore(kill, Instruction.Create(OpCodes.Ldloc, body.Variables[1])); // Archive
            il.InsertBefore(kill, Instruction.Create(OpCodes.Ldloc, body.Variables[2])); // BaseFolder
            if (withName) il.InsertBefore(kill, Instruction.Create(OpCodes.Ldarg_1));
            if (withLogo) il.InsertBefore(kill, Instruction.Create(OpCodes.Ldarg_2));
            il.InsertBefore(kill, Instruction.Create(OpCodes.Call, target));
            il.InsertBefore(kill, Instruction.Create(OpCodes.Stloc, vResult));
            var lv = Instruction.Create(OpCodes.Leave, kill);   // 占位，稍后改为 retStub
            il.InsertBefore(kill, lv);
            leaveToRet.Add(lv);
            swInstr.Operand = (Instruction[])swInstr.Operand;
            ((Instruction[])swInstr.Operand)[slot] = first;
        }

        for (int i = 0; i < Dispatch.Length; i++)
        {
            var (name, withName, withLogo) = Dispatch[i];
            if (name == null) continue;
            var target = modpack.Methods.FirstOrDefault(x => x.Name == name);
            if (target == null) { o.Notes.Add($"缺少 {name}，该项将走默认分支"); continue; }
            EmitCase(target, withName, withLogo, i);
        }

        // 默认分支：InstallPackCompress(File, Archive)
        var defTarget = modpack.Methods.FirstOrDefault(x => x.Name == "InstallPackCompress");
        if (defTarget == null) throw new Exception("缺少 InstallPackCompress，无法构建默认分支");
        var defFirst = Instruction.Create(OpCodes.Ldarg_0);
        il.InsertBefore(kill, defFirst);
        il.InsertBefore(kill, Instruction.Create(OpCodes.Ldloc, body.Variables[1]));
        il.InsertBefore(kill, Instruction.Create(OpCodes.Call, defTarget));
        il.InsertBefore(kill, Instruction.Create(OpCodes.Stloc, vResult));
        var defLeave = Instruction.Create(OpCodes.Leave, kill);
        il.InsertBefore(kill, defLeave);
        leaveToRet.Add(defLeave);

        var table = (Instruction[])swInstr.Operand;
        for (int i = 0; i < table.Length; i++) if (table[i] == null) table[i] = defFirst;

        // ---- ② 重定向：分支 / catch HandlerEnd 指向分派入口 ----
        int redirected = 0, handlerFixed = 0;
        foreach (var i in ins)
        {
            if (i == dispatchEntry || i == swInstr) continue;
            if (i.Operand is Instruction t && t == kill) { i.Operand = dispatchEntry; redirected++; }
            else if (i.Operand is Instruction[] ts)
                for (int k = 0; k < ts.Length; k++)
                    if (ts[k] == kill) { ts[k] = dispatchEntry; redirected++; }
        }
        // 上面把 leaveToRet 里的占位也改成了 dispatchEntry，这里改正回来（下面统一指向 retStub）
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.TryStart == kill) { eh.TryStart = dispatchEntry; redirected++; }
            if (eh.TryEnd == kill) { eh.TryEnd = dispatchEntry; redirected++; }
            if (eh.HandlerStart == kill) { eh.HandlerStart = dispatchEntry; redirected++; }
            if (eh.HandlerEnd == kill)
            {
                // catch 的 handler 上界 = 分派入口（保持原独占语义，不吞 finally）
                eh.HandlerEnd = dispatchEntry;
                handlerFixed++;
            }
        }

        // ---- ③ 方法末尾追加 retStub: ldloc result / ret ----
        var retStub = Instruction.Create(OpCodes.Ldloc, vResult);
        il.Append(retStub);
        il.Append(Instruction.Create(OpCodes.Ret));

        // leave 全部指向 retStub（从 try 内 leave 出去 → 先执行 finally → 再落到这里）
        foreach (var lv in leaveToRet) lv.Operand = retStub;

        // finally 的 HandlerEnd 原本是 null（一路到方法末尾），必须收窄到 retStub 之前，
        // 否则 retStub 会落在 finally handler 里 —— `ret` 在 finally 中非法。
        int handlerClosed = 0;
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.HandlerEnd == null && eh.HandlerStart != null)
            {
                eh.HandlerEnd = retStub;
                handlerClosed++;
            }
        }

        o.Notes.Add($"kill 桩 IL_{killOff:X4} 已替换为分派，重定向 {redirected} 处分支/Try 边界" +
                    (handlerFixed > 0 ? $"；{handlerFixed} 处 catch HandlerEnd 指向分派入口" : "") +
                    (handlerClosed > 0 ? $"；{handlerClosed} 处 finally 收窄到 retStub 之前（结果经 leave 带出）" : ""));

        // 最后删除 kill 三条指令（此时已无任何引用指向它们）
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
    // 重写元数据最怕两类错误，它们都表现为「exe 能启动，但走到对应路径就崩」或
    // 「CLR 直接拒绝加载」：
    //   1) 分支 / switch / EH 边界指向已删除的指令（悬空引用）
    //   2) EH 区域互相重叠（CLR 要求 try 之间不能重叠）、或 try 块里没有 ret/leave 收尾
    // 第 2 类尤其阴险：它不会在写盘时报错，只会在运行时报
    // 「公共语言运行时检测到无效的程序」—— 实测踩过一次。
    public static List<string> Audit(MethodDefinition m)
    {
        var bad = new List<string>();
        if (m.Body == null) return bad;
        var list = m.Body.Instructions.ToList();
        var set = new HashSet<Instruction>(list);
        var order = new Dictionary<Instruction, int>();
        for (int i = 0; i < list.Count; i++) order[list[i]] = i;

        foreach (var i in list)
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
            if (bad.Count > 0) continue;

            // 区间必须朝向正确：TryStart < TryEnd，HandlerStart < HandlerEnd（HandlerEnd 可为空=到方法末尾）
            if (eh.TryStart != null && eh.TryEnd != null && order[eh.TryStart] >= order[eh.TryEnd])
                bad.Add($"{m.Name}: {eh.HandlerType} 的 try 区间为空或倒置");
            if (eh.HandlerStart != null && eh.HandlerEnd != null && order[eh.HandlerStart] >= order[eh.HandlerEnd])
                bad.Add($"{m.Name}: {eh.HandlerType} 的 handler 区间为空或倒置");

            // try 与 handler 不能交叠
            if (eh.TryStart != null && eh.TryEnd != null && eh.HandlerStart != null && eh.HandlerEnd != null)
            {
                int ts = order[eh.TryStart], te = order[eh.TryEnd];
                int hs = order[eh.HandlerStart], he = order[eh.HandlerEnd];
                if (hs < te && ts < he)
                    bad.Add($"{m.Name}: {eh.HandlerType} 的 try 与 handler 区间重叠");
            }
        }

        // 任意两个受保护区的相对关系只允许三种：相离、严格包含（嵌套）。
        // 「交叉」（有交叠但互不包含）非法 —— CLR 会拒绝加载。
        // 注意嵌套本身完全合法：原件里就有三层嵌套
        // （外层 finally 27..2E5 ⊃ 内层 catch 6D..284 ⊃ 最内层 finally 143..276），
        // 所以不能简单地判「有重叠就报错」——只报交叉。
        var tries = m.Body.ExceptionHandlers
            .Where(h => h.TryStart != null && h.TryEnd != null)
            .Select(h => new { h, s = order[h.TryStart], e = order[h.TryEnd] })
            .OrderBy(x => x.s).ToList();
        for (int i = 0; i < tries.Count; i++)
            for (int j = i + 1; j < tries.Count; j++)
            {
                var A = tries[i]; var B = tries[j];
                bool disjoint = A.e <= B.s || B.e <= A.s;
                bool nested = (A.s <= B.s && B.e <= A.e) || (B.s <= A.s && A.e <= B.e);
                if (!disjoint && !nested)
                    bad.Add($"{m.Name}: 两个受保护区交叉（非嵌套）" +
                            $" IL_{A.h.TryStart.Offset:X4}..IL_{A.h.TryEnd.Offset:X4} 与 " +
                            $"IL_{B.h.TryStart.Offset:X4}..IL_{B.h.TryEnd.Offset:X4}");
            }

        return bad;
    }
}
