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
        public bool UiRestored;        // 版本选择页「导入整合包」列表项重新挂回面板
        public bool DownloadLeftRestored; // 下载页「下载游戏」入口恢复可见
        public bool AlreadyPatched;    // 所有处都已是真实现
        public List<string> Notes = new List<string>();
        public bool Changed => EntryRebuilt || DispatchRebuilt || UiRestored || DownloadLeftRestored;
    }

    const string ModpackType = "PCL.ModModpack";
    const string InstallName = "ModpackInstall";
    const string KillA = "为遵守相关法律法规";
    const string SelectLeftType = "PCL.PageSelectLeft";
    const string SelectLeftUiMethod = "McFolderListUI";
    const string ImportItemTitle = "导入整合包";
    const string DownloadLeftType = "PCL.PageDownloadLeft";

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
        var ui = FindUiSite(module, out bool uiMissingAdd);
        bool uiOk = ui == null || !uiMissingAdd;   // 找不到该 UI 方法时视为不适用

        o.AlreadyPatched = entryOk && dispatchOk && uiOk;

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
        if (!uiOk)
        {
            RestoreUiItem(ui, o);
            o.UiRestored = true;
        }

        // ④ 下载页「下载游戏」入口（.1 新增）：
        //    PageDownloadLeft..ctor 里 ItemInstall.Visibility = Collapsed、
        //    LabResource.Visibility = Collapsed、PageID = 2、ItemMod.Checked = true，
        //    打开"下载"直接落在 Mod 页，侧栏看不到「下载游戏」分组（整合包在其下）。
        //    判据：.ctor 里存在 get_ItemInstall → ldc.i4.2 → set_Visibility。
        var dl = module.GetType(DownloadLeftType)?.Methods.FirstOrDefault(x => x.Name == ".ctor");
        if (dl?.Body != null && DownloadsLeftCollapsed(dl))
        {
            RestoreDownloadLeft(dl, o);
            o.DownloadLeftRestored = true;
        }
        return o;
    }

    // 检测 PageDownloadLeft..ctor 是否折叠了「下载游戏」入口
    public static bool DownloadsLeftCollapsed(MethodDefinition ctor)
    {
        var ins = ctor.Body.Instructions.ToList();
        for (int i = 0; i + 2 < ins.Count; i++)
        {
            if (ins[i].OpCode == OpCodes.Callvirt && ins[i].Operand is MethodReference g &&
                g.Name == "get_ItemInstall" &&
                (ins[i + 1].OpCode.Code == Code.Ldc_I4 && (int)ins[i + 1].Operand == 2 ||
                 ins[i + 1].OpCode.Code == Code.Ldc_I4_2) &&                    // Visibility.Collapsed
                ins[i + 2].OpCode == OpCodes.Callvirt &&
                ins[i + 2].Operand is MethodReference s && s.Name == "set_Visibility")
                return true;
        }
        return false;
    }

    // 等长修改：ldc.i4.2 → ldc.i4.0（Collapsed → Visible），PageID 同理；
    // ItemMod 选中那段直接 nop（默认选中页回到 0 = 下载游戏）。
    static void RestoreDownloadLeft(MethodDefinition ctor, Outcome o)
    {
        var ins = ctor.Body.Instructions.ToList();
        var il = ctor.Body.GetILProcessor();
        int visFixed = 0, pageFixed = 0, checkedNop = 0;

        for (int i = 0; i + 2 < ins.Count; i++)
        {
            // get_ItemInstall / get_LabResource → ldc.i4.2 → set_Visibility
            // 注意 ldc.i4.2 是短格式，OpCode.Code 是 Ldc_I4_2 而不是 Ldc_I4（踩过）
            if (ins[i].OpCode == OpCodes.Callvirt && ins[i].Operand is MethodReference g &&
                (g.Name == "get_ItemInstall" || g.Name == "get_LabResource") &&
                (ins[i + 1].OpCode.Code == Code.Ldc_I4 && (int)ins[i + 1].Operand == 2 ||
                 ins[i + 1].OpCode.Code == Code.Ldc_I4_2) &&
                ins[i + 2].OpCode == OpCodes.Callvirt && ins[i + 2].Operand is MethodReference s &&
                s.Name == "set_Visibility")
            {
                ins[i + 1].OpCode = OpCodes.Ldc_I4_0;          // Visibility.Visible
                visFixed++;
            }
            // PageID = 2 → 0（等长：ldc.i4.2 → ldc.i4.0，stfld 不动）
            if (ins[i].OpCode == OpCodes.Stfld && ins[i].Operand is FieldReference f &&
                f.Name == "PageID" && i > 0 &&
                (ins[i - 1].OpCode.Code == Code.Ldc_I4 && (int)ins[i - 1].Operand == 2 ||
                 ins[i - 1].OpCode.Code == Code.Ldc_I4_2))
            {
                ins[i - 1].OpCode = OpCodes.Ldc_I4_0;
                pageFixed++;
            }
            // ItemMod.Checked = true 段（ldarg.0 → get_ItemMod → ldc.i4.1 → set_Checked）→ 全 nop
            // ★ 必须连开头的 ldarg.0 一起 nop，否则 ret 时栈上残留一个 this →
            //   InvalidProgramException「公共语言运行时检测到无效的程序」（实测踩过）
            if (ins[i].OpCode == OpCodes.Callvirt && ins[i].Operand is MethodReference gm &&
                gm.Name == "get_ItemMod" &&
                i + 2 < ins.Count &&
                ins[i + 1].OpCode.Code == Code.Ldc_I4_1 &&
                ins[i + 2].OpCode == OpCodes.Callvirt && ins[i + 2].Operand is MethodReference sc &&
                sc.Name == "set_Checked")
            {
                if (i > 0 && ins[i - 1].OpCode == OpCodes.Ldarg_0)
                {
                    ins[i - 1].OpCode = OpCodes.Nop; ins[i - 1].Operand = null;
                }
                ins[i].OpCode = OpCodes.Nop; ins[i].Operand = null;
                ins[i + 1].OpCode = OpCodes.Nop; ins[i + 1].Operand = null;
                ins[i + 2].OpCode = OpCodes.Nop; ins[i + 2].Operand = null;
                checkedNop++;
            }
        }
        o.Notes.Add($"下载页入口恢复：{visFixed} 处 Visibility 改回 Visible，" +
                    $"{pageFixed} 处 PageID 改回 0，{checkedNop} 处默认选中已清除");
    }

    // ---------------- 3) 版本选择页的「导入整合包」列表项 ----------------
    //
    // 2.99.0.1 换了一种更"干净"的关闭方式：不再改 ModpackInstall（那两处 kill 原样
    // 保留，兼容旧补丁），而是把 PageSelectLeft.McFolderListUI 里构建「导入整合包」
    // 列表项之后的那句 `PanList.Children.Add(item)` 删掉 —— 控件照常创建、Click
    // 照常绑定，但永远不会被加进面板，UI 上自然就看不到入口了。
    //
    // 症状与 .0 不同：.0 点「导入整合包」会弹红字；.1 里**压根没有这个按钮**。
    //
    // 补丁 = 把那句 Add 插回去。定位方式：
    //   ldstr "导入整合包" → 向后找该条目的 add_Click → 在其后插入
    //     ldloc item / ldsfld FrmSelectLeft / callvirt get_PanList
    //     / callvirt get_Children / callvirt Add
    // 引用全部取自同方法里已存在的调用，不新造签名。
    public sealed class UiSite
    {
        public TypeDefinition Type;
        public MethodDefinition Method;
        public Instruction Anchor;             // add_Click（插入点在其后）
        public VariableDefinition ItemLocal;   // 列表项局部变量
        public MethodReference PanListGet, ChildrenGet, Add;
        public FieldReference FrmSelectLeft;
        public bool MissingAdd;                // true = 已被藏起来（需要补回）
    }

    public static UiSite FindUiSite(ModuleDefinition module, out bool missingAdd)
    {
        missingAdd = false;
        var t = module.GetType(SelectLeftType);
        var m = t?.Methods.FirstOrDefault(x => x.Name == SelectLeftUiMethod);
        if (m?.Body == null) return null;

        var ins = m.Body.Instructions;
        var titleIdx = -1;
        for (int i = 0; i < ins.Count; i++)
            if (ins[i].OpCode == OpCodes.Ldstr && (string)ins[i].Operand == ImportItemTitle)
            { titleIdx = i; break; }
        if (titleIdx < 0) return null;   // 该版本没有这个入口项，不适用

        // 向后找本条目的 add_Click（在合理窗口内）
        int clickIdx = -1;
        for (int k = titleIdx; k < Math.Min(titleIdx + 60, ins.Count); k++)
            if (ins[k].OpCode == OpCodes.Callvirt && ins[k].Operand is MethodReference mr &&
                mr.Name == "add_Click" && mr.DeclaringType.Name == "MyListItem")
            { clickIdx = k; break; }
        if (clickIdx < 0) return null;

        var addItem = ins.Select(x => x.Operand).OfType<MethodReference>()
            .FirstOrDefault(x => x.Name == "Add" &&
                                 x.DeclaringType.FullName == "System.Windows.Controls.UIElementCollection");
        var panList = ins.Select(x => x.Operand).OfType<MethodReference>()
            .FirstOrDefault(x => x.Name == "get_PanList");
        var children = ins.Select(x => x.Operand).OfType<MethodReference>()
            .FirstOrDefault(x => x.Name == "get_Children" &&
                                 x.DeclaringType.FullName.EndsWith("Panel"));
        var frm = ins.Select(x => x.Operand).OfType<FieldReference>()
            .FirstOrDefault(x => x.Name == "FrmSelectLeft");
        if (addItem == null || panList == null || children == null || frm == null)
        {
            missingAdd = true;
            return null;
        }

        // 列表项局部变量：VB 对象初始化器的模式是
        //   newobj MyListItem → dup → 一长串 set_X（中间夹着 ldstr "导入整合包"）→ stloc N
        // 即 stloc 在 title **之后**（实测 IL_0E73），不能只在 title 之前找。
        // 稳妥做法：从 newobj 起向后扫，第一个 stloc / stloc.N 即列表项；
        // 扫描上限定为 add_Click（如有）或 title+60，避免吃进别的对象赋值。
        VariableDefinition itemLocal = null;
        Instruction stlocInstr = null;
        {
            int newObjIdx = -1;
            for (int k = titleIdx; k >= Math.Max(0, titleIdx - 40); k--)
                if (ins[k].OpCode == OpCodes.Newobj && ins[k].Operand is MethodReference mr &&
                    mr.DeclaringType.Name == "MyListItem")
                { newObjIdx = k; break; }

            int scanEnd = clickIdx >= 0 ? clickIdx : Math.Min(titleIdx + 60, ins.Count);
            for (int j = newObjIdx + 1; newObjIdx >= 0 && j < scanEnd; j++)
            {
                var op = ins[j].OpCode;
                if (op.Code >= Code.Stloc_0 && op.Code <= Code.Stloc_3)
                {
                    itemLocal = m.Body.Variables.FirstOrDefault(v => v.Index == op.Code - Code.Stloc_0);
                    stlocInstr = ins[j];
                    break;
                }
                if (op == OpCodes.Stloc || op == OpCodes.Stloc_S)
                {
                    itemLocal = m.Body.Variables.FirstOrDefault(v => v.Index == ((VariableDefinition)ins[j].Operand).Index);
                    stlocInstr = ins[j];
                    break;
                }
            }
        }
        if (itemLocal == null) { missingAdd = true; return null; }

        // 确认确实缺 Add。两个版本的指令顺序不同（.0 是 Add 在前 Click 在后，.1 相反），
        // 按"位置/顺序"判断必然踩坑 —— 2.99.0.0 曾被误插一条 Add 直接改坏（实测）。
        //
        // 也不能按"类型"数：方法里还有文件夹条目的列表项（同样是 MyListItem 类型），
        // 实测 .0 有 3 个 MyListItem 型 Add、.1 有 2 个，个数都不为 0，数不出来。
        //
        // 决定性判据：**列表项自己的那个局部变量**有没有被某个 Add 压栈。
        //   itemLocal = ldstr"导入整合包"之前那个 newobj MyListItem 的 stloc 目标（.0/.1 都是 V_1）
        //   2.99.0.0：get_Children → ldloc V_1 → Add        → 已挂回
        //   2.99.0.1：整个方法没有任何 ldloc V_1 → Add      → 被藏起来了
        // 用"变量索引"匹配而不是类型，避开文件夹条目的同名类型干扰。
        // 回溯窗口：Add 之前**同一条语句**内。VB 的调用形态有两种：
        //   .0：PanList.Children.Add(item)      → get_Children / ldloc / Add（紧邻）
        //   补丁后：ldloc / ldsfld / get_PanList / get_Children / Add（ldloc 在 4 条之前）
        // 所以从 Add 往前扫到"语句边界"（stloc / 前一条 Add 后的 pop / 分支目标）为止，
        // 窗口内出现过 itemLocal 的 ldloc 即命中。窗口固定取 12 条，实测足够。
        bool hasAdd = false;
        for (int k = 0; k < ins.Count; k++)
        {
            if (!(ins[k].OpCode == OpCodes.Callvirt && ins[k].Operand is MethodReference ma &&
                  ma.Name == "Add" && ma.DeclaringType.FullName == "System.Windows.Controls.UIElementCollection"))
                continue;
            for (int j = k - 1; j >= 0 && j >= k - 12; j--)
            {
                var op = ins[j].OpCode;
                if (op == OpCodes.Ldloc || op == OpCodes.Ldloc_S ||
                    (op.Code >= Code.Ldloc_0 && op.Code <= Code.Ldloc_3))
                {
                    int li = op.Code >= Code.Ldloc_0 && op.Code <= Code.Ldloc_3
                        ? op.Code - Code.Ldloc_0
                        : ((VariableDefinition)ins[j].Operand).Index;
                    if (itemLocal != null && li == itemLocal.Index) { hasAdd = true; break; }
                }
                // 语句边界：撞到别的语句开头就停（stloc 说明进入了上一条赋值语句）
                if (op == OpCodes.Stloc || op == OpCodes.Stloc_S ||
                    (op.Code >= Code.Stloc_0 && op.Code <= Code.Stloc_3))
                    break;
            }
            if (hasAdd) break;
        }
        missingAdd = !hasAdd;

        return new UiSite
        {
            Type = t, Method = m, Anchor = stlocInstr, ItemLocal = itemLocal,
            PanListGet = panList, ChildrenGet = children, Add = addItem, FrmSelectLeft = frm,
            MissingAdd = missingAdd,
        };
    }

    static void RestoreUiItem(UiSite ui, Outcome o)
    {
        // ★ 实测结论：光把 Add 插回去不够 —— 插在 add_Click 之后（.1 原顺序延长）时
        //   IL 合法但按钮不出现；必须复刻 .0 原生的语句顺序：
        //     stloc.1 → Children.Add(item) → pop → ldloc.1 → Click 绑定 → add_Click
        //   即 **Click 绑定段整体搬到 Add 之后**。做法：把 [ldloc.1 .. add_Click] 这段
        //   指令块先摘下来，在 Add/pop 插好后再接回去（指令对象原样复用，分支自洽）。
        var body = ui.Method.Body;
        var il = body.GetILProcessor();

        // ★ 实测结论（transplant2 实验对照）：
        //   · 插在 add_Click 之后 → IL 合法但按钮不出现
        //   · 插在 stloc（对象初始化器收尾）之后 → 与 .0 原生顺序一致（Add 在 Click 绑定之前），
        //     用户实测按钮出现。Anchor 就是 FindUiSite 里记录的 stloc 指令。
        var addSeq = new List<Instruction>
        {
            // 顺序照抄 .0 原生 IL：容器链先压栈（ldsfld → get_PanList → get_Children），
            // 最后才压 item —— 与编译器输出逐指令一致（压栈顺序颠倒虽然语义等价，
            // 但实测按钮不出现，原因未明；别再"优化"它）
            Instruction.Create(OpCodes.Ldsfld, ui.FrmSelectLeft),
            Instruction.Create(OpCodes.Callvirt, ui.PanListGet),
            Instruction.Create(OpCodes.Callvirt, ui.ChildrenGet),
            Instruction.Create(OpCodes.Ldloc, ui.ItemLocal),
            Instruction.Create(OpCodes.Callvirt, ui.Add),
            Instruction.Create(OpCodes.Pop),   // Add 返回 int，必须弹掉
        };
        var cursor = ui.Anchor;   // stloc.1
        foreach (var x in addSeq) { il.InsertAfter(cursor, x); cursor = x; }

        o.Notes.Add($"版本选择页「{ImportItemTitle}」列表项已重新挂回面板（stloc 后插 {addSeq.Count} 条，复刻 .0 原生顺序）");
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
