#nullable disable
// PCL2 原版游戏下载入口恢复器
//
// 用法：
//   双击运行（推荐给不熟悉命令行的用户）：
//     把本程序和 "Plain Craft Launcher 2.exe" 放在同一目录后双击，
//     程序会自动改写（原地修改并备份为 .bak.exe）并启动 PCL2。
//     同目录没找到时，会弹出文件选择窗口让你手动指认。
//   命令行：
//     PCL2VanillaRestore.exe [目标exe] [--in-place] [--out 路径] [--dry] [--no-launch] [--quiet]
//     也可以把 "Plain Craft Launcher 2.exe" 直接拖到本程序图标上。
//
// 原理（对 PCL 2.99 的逆向结论）：
//   PageDownloadInstall.LoaderInit() 里，原本传给 MyPageRight.PageLoaderInit 的
//   RealLoader 参数（真实版本列表加载器 ModDownload.DlClientListLoader）被换成了
//   一个当场 new 出来的替身 LoaderTask：
//       new LoaderTask<string, DlClientListResult>("关闭下载", <stub>, null, Normal)
//   该替身的 LoadDelegate 只有一个动作：
//       throw new Exception("为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能……")
//   而真正的下载链完好无损：
//       DlClientListLoader -> DlClientListMain -> DlClientList{Mojang,Bmclapi}Loader
//         -> DlClientListGet -> ModDownloadLib.McDownloadClientLoader
//         -> 版本 json / assets / libraries
//
//   因此补丁只需把「构造替身」那段 IL 换成一条 `ldsfld ModDownload::DlClientListLoader`。
//   为绝对安全，采用**等长替换**（ldsfld 5 字节 + nop 填充）：
//     · codeSize 不变 → 方法体后的异常处理段（MoreSects）偏移不变
//     · 所有分支目标偏移不变
//     · 元数据表、文件尺寸、其余指令一律不动
//   这样就不需要重写 .NET 元数据，绕开了 PCLCS / MeloongCore 等未随 exe 落盘的
//   依赖导致的 Cecil 整元数据重建失败问题。
//
//   发现过程完全基于模式匹配，不硬编码地址，因此可用于同结构的其它版本 / 分支。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class PclVanillaRestore
{
    const string LoaderTaskRoot = "PCL.ModLoader/LoaderTask`2";
    const string KillHintA = "为遵守相关法律法规";
    const string KillHintB = "不再提供 MC 下载";

    // 工具版本对齐其所支持的 PCL2 程序集版本。
    // 定位逻辑是模式匹配（不硬编码地址），因此对相邻版本通常也有效；
    // 这里只做提示：版本不等于已验证版本时给出 warning，但仍尝试。
    const string SupportedPclVersion = "2.99.0.0";

    static string ToolVersion()
    {
        var v = typeof(PclVanillaRestore).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            ?? typeof(PclVanillaRestore).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return v;
    }

    static bool _quiet;
    static bool _noLaunch;
    static void Log(string s) { if (!_quiet) Console.WriteLine(s); }
    static void Warn(string s) { if (!_quiet) Console.WriteLine("  ! " + s); }

    static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            // 用户可能把任意文件拖进来（原生 exe、损坏文件等），给出可读错误而非堆栈。
            // 注意：这里**不能**用 `is Mono.Cecil.XXX` 之类的类型判断 —— 那会让 JIT 在
            // 编译 Main 时就解析 Cecil 程序集；若它缺失，异常会在 try 建立之前抛出，
            // 顶层保护反而失效（实测为未捕获的 FileNotFoundException）。只比对类型名。
            Console.WriteLine($"  ✗ 失败：{ex.Message}");
            string tn = ex.GetType().FullName ?? "";
            if (tn.StartsWith("Mono.Cecil.AssemblyResolutionException", StringComparison.Ordinal)
                || tn.StartsWith("Mono.Cecil.ImageNotExecutableException", StringComparison.Ordinal)
                || tn.IndexOf("BadImageFormat", StringComparison.OrdinalIgnoreCase) >= 0)
                Console.WriteLine("    （目标不是可独立分析的托管程序集，或其依赖无法解析）");
            return 5;
        }
    }

    static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string target = null, outPath = null;
        bool inPlace = false, dry = false;
        _noLaunch = args.Contains("--no-launch");
        bool? forceAuto = args.Contains("--auto") ? true : (args.Contains("--no-auto") ? false : (bool?)null);
        _quiet = args.Contains("--quiet");

        // 双击（无参数）且处于交互桌面环境 => 自动模式：原地修改 + 备份 + 启动 PCL2。
        // 输出被重定向时（CI、管道、计划任务）不进入自动模式，避免弹窗挂死。
        bool autoMode = forceAuto ?? (args.Length == 0 && IsInteractiveDesktop());

        // banner 先于参数解析打印，确保 --help / 无目标 时也带着版本信息
        Console.WriteLine($"PCL2 原版游戏下载入口恢复器 v{ToolVersion()}  （适配 PCL2 {SupportedPclVersion}）");

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--in-place": inPlace = true; break;
                case "--dry": dry = true; break;
                case "--no-launch": _noLaunch = true; break;
                case "--auto": case "--no-auto": break;   // 已在上面解析
                case "--quiet": break;
                case "-h": case "--help": PrintUsage(); return 0;
                case "-o": case "--out": outPath = i + 1 < args.Length ? args[++i] : null; break;
                default:
                    if (target == null && (File.Exists(args[i]) || args[i].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                        target = args[i];
                    break;
            }
        }

        if (autoMode)
        {
            inPlace = true;
            Console.WriteLine("  自动模式：将原地修改（自动备份 .bak.exe）并在成功后启动 PCL2");
            Console.WriteLine("  （需要命令行用法请加 --help；只想检查不改动请加 --dry）");
        }

        target ??= AutoFindTarget();
        if (target == null && autoMode)
            target = PickFileViaDialog();

        if (target == null)
        {
            Console.WriteLine();
            PrintUsage();
            if (autoMode) PauseIfInteractive();
            return 3;
        }
        if (!File.Exists(target)) { Console.Error.WriteLine("文件不存在：" + target); PauseIfInteractive(); return 3; }

        outPath ??= inPlace ? target : NextTo(target, " (fixed)");

        Console.WriteLine("  目标 : " + target);
        Console.WriteLine("  输出 : " + outPath + (inPlace ? "   (原地修改，自动备份 .bak)" : ""));
        Console.WriteLine();

        // ================= 1. 只读定位 + 校验（自动模式下选错可重选）=================
        // 循环不变量：每轮都从「读文件」开始，using 作用域结束即释放 module，
        // 不会持有文件句柄（保证 --in-place / 自动模式的原地写盘可行）。
        ModuleDefinition module = null;
        List<TypeDefinition> allTypes = null;
        byte[] file = null;
        string ver = "";
        int rc2;

        try
        {
            while (true)
            {
                file = File.ReadAllBytes(target);
                module = ModuleDefinition.ReadModule(new MemoryStream(file));
                allTypes = module.GetTypes().ToList();
                Console.WriteLine($"  SHA-256 : {ShaBytes(file)}");
                var asmName = module.Assembly?.Name;
                Console.WriteLine($"  程序集  : {asmName?.Name} {asmName?.Version}  CLR {module.RuntimeVersion}");

                // ---- 版本对齐检查 ----
                if (!string.Equals(asmName?.Name, "Plain Craft Launcher 2", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine();
                    Console.WriteLine($"  ✗ 目标程序集名为 “{asmName?.Name}”，不是 Plain Craft Launcher 2。");
                    if (!autoMode)
                    {
                        Console.WriteLine("    为安全起见已中止。请确认选对了启动器 exe。");
                        return 4;
                    }
                    InfoBox($"你选中的文件不是 PCL2 本体（其程序集名为 “{asmName?.Name}”）。\n\n请重新选择 Plain Craft Launcher 2.exe。",
                            "选错文件了", Native.MB_ICONWARNING);
                    module.Dispose(); module = null;
                    target = PickFileViaDialog();
                    if (target == null) { Console.WriteLine("  已取消。"); return 4; }
                    Console.WriteLine("  目标 : " + target);
                    outPath = inPlace ? target : NextTo(target, " (fixed)");
                    Console.WriteLine("  输出 : " + outPath + (inPlace ? "   (原地修改，自动备份 .bak)" : ""));
                    Console.WriteLine();
                    continue;
                }

                ver = asmName?.Version?.ToString() ?? "";
                if (ver != SupportedPclVersion)
                {
                    Console.WriteLine($"  ! 目标版本 {ver} ≠ 已验证版本 {SupportedPclVersion}");
                    Console.WriteLine("    定位逻辑为模式匹配（不依赖硬编码地址），通常仍可尝试；");
                    Console.WriteLine("    若后续自检未通过，请勿使用该产物。");
                }
                Console.WriteLine();
                break;
            }

            rc2 = PatchAndSave(module, allTypes, file, target, outPath, inPlace, dry, autoMode);
            return rc2;
        }
        finally
        {
            module?.Dispose();
        }
    }

    // 定位 kill 点 → 校验 → 等长替换 → 写盘 → 回读自检；返回进程退出码
    static int PatchAndSave(ModuleDefinition module, List<TypeDefinition> allTypes, byte[] file,
                            string target, string outPath, bool inPlace, bool dry, bool autoMode)
    {
        var sites = FindKillSites(module, allTypes);
        if (sites.Count == 0)
        {
            var already = FindAlreadyRestored(allTypes);
            if (already != null)
                Console.WriteLine($"  未发现替身加载器；{already.Type.Name}::{already.Method.Name} 已直接引用 {already.RealField.Name}，看起来是已修复版或未被限制的版本。");
            else
                Console.WriteLine("  未发现「原版游戏下载」入口被替换的痕迹（可能该版本未做此限制）。");
            Console.WriteLine("  无需修改。");
            if (autoMode && !dry) LaunchPcl2(outPath);
            else PauseIfInteractive();
            return 0;
        }

        Console.WriteLine($"  发现 {sites.Count} 处入口替换：");
        foreach (var s in sites)
            Console.WriteLine($"    · {s.Type.Name}::{s.Method.Name}  (RVA 0x{s.Method.RVA:X})  替身名=\"{s.FakeName}\"  段 IL_{s.StartOff:X4}..IL_{s.EndOff:X4} ({s.Len}B)  -> {s.RealField?.DeclaringType.Name}::{s.RealField?.Name}");
        Console.WriteLine();

        foreach (var s in sites)
        {
            if (s.RealField == null)
            {
                Console.WriteLine($"  ! 无法为 {s.Type.Name}::{s.Method.Name} 找到真实加载器字段，跳过");
                continue;
            }
            Validate(s);
            Describe(s);
        }

        if (dry) { Console.WriteLine("\n--dry：仅分析，不写盘"); return 0; }

        // ================= 2. 等长字节手术 =================
        var outBuf = (byte[])file.Clone();
        int patched = 0;

        foreach (var s in sites.Where(x => x.RealField != null))
        {
            int bodyOff = (int)RvaToOff(file, (uint)s.Method.RVA);
            ushort hdr = BitConverter.ToUInt16(file, bodyOff);
            if ((hdr & 0x3) != 0x3) throw new Exception($"{s.Method.Name}: 不是 fat 方法头 (0x{hdr:X4})");
            int hdrSize = (hdr >> 12) * 4;
            uint codeSize = BitConverter.ToUInt32(file, bodyOff + 4);
            if (codeSize != (uint)s.Method.Body.CodeSize) throw new Exception($"{s.Method.Name}: codeSize 不一致");
            int codeStart = bodyOff + hdrSize;
            if (codeStart + codeSize > file.Length) throw new Exception("方法体越界");

            // 起点必须是 ldstr，终点必须是 newobj（5 字节 token 指令）
            if (file[codeStart + s.StartOff] != 0x72) throw new Exception($"{s.Method.Name}: 段起点非 ldstr");
            if (file[codeStart + s.EndOff - 5] != 0x73) throw new Exception($"{s.Method.Name}: 段终点非 newobj");
            if (s.Len < 5) throw new Exception($"{s.Method.Name}: 段长度不足");

            var repl = new List<byte> { 0x7E };                              // ldsfld
            repl.AddRange(BitConverter.GetBytes(s.RealField.MetadataToken.ToInt32()));
            while (repl.Count < s.Len) repl.Add(0x00);                       // nop 填充

            Array.Copy(repl.ToArray(), 0, outBuf, codeStart + s.StartOff, repl.Count);
            patched++;
            Console.WriteLine($"  ✓ {s.Type.Name}::{s.Method.Name}  IL_{s.StartOff:X4} 处 {s.Len}B 已替换为 ldsfld {s.RealField.Name} + {repl.Count - 5}× nop");
        }

        if (patched == 0) { Console.WriteLine("  没有可应用的补丁"); PauseIfInteractive(); return 2; }

        // ================= 3. 备份 + 写盘 =================
        if (inPlace)
        {
            var bak = NextTo(target, ".bak");
            if (!File.Exists(bak)) File.Copy(target, bak);
            Console.WriteLine($"  备份 : {bak}");
        }
        var tmp = outPath + ".tmp";
        File.WriteAllBytes(tmp, outBuf);
        File.Move(tmp, outPath, true);
        Console.WriteLine($"\n  已写出 : {outPath}  ({new FileInfo(outPath).Length:N0} 字节)");

        // ================= 4. 回读自检 =================
        int rc = Verify(outPath, sites, file, outBuf);

        // ================= 5. 自动模式下启动 =================
        if (autoMode)
        {
            Console.WriteLine();
            if (rc == 0)
            {
                // 文案必须与实际行为一致：非交互环境不会弹框、也不会启动，
                // 所以不能说「点确定后自动启动」。
                bool willLaunch = !_noLaunch && CanDialog();
                string msg = "补丁已完成，原版游戏下载入口已恢复。\n\n修复后的文件：\n" + outPath;
                if (willLaunch) msg += "\n\n点「确定」后将自动启动 PCL2。";
                else if (_noLaunch) msg += "\n\n（已按 --no-launch 要求，不自动启动）";
                else msg += "\n\n（当前环境非交互桌面，不会自动启动，请手动双击）";

                InfoBox(msg, "修复成功", Native.MB_ICONINFORMATION);
                if (willLaunch) LaunchPcl2(outPath);
                else PauseIfInteractive();
            }
            else
            {
                // 失败：留在控制台让人看到详细诊断
                InfoBox("补丁已写出，但回读自检未通过（退出码 " + rc + "）。\n\n" +
                        "请勿使用该文件，原文件已备份为 .bak.exe。\n\n" +
                        "详细输出见控制台窗口。",
                        "自检未通过", Native.MB_ICONERROR);
                PauseIfInteractive();
            }
        }
        else
        {
            PauseIfInteractive();
        }
        return rc;
    }

    static void PrintUsage()
    {
        Console.WriteLine("用法：PCL2VanillaRestore.exe [选项] \"Plain Craft Launcher 2.exe\"");
        Console.WriteLine("      也可直接把启动器 exe 拖到本程序图标上，或与本程序同目录后双击运行。");
        Console.WriteLine();
        Console.WriteLine("选项：");
        Console.WriteLine("  --in-place      原地修改（自动备份为 *.bak.exe）；默认输出 \"xxx (fixed).exe\"");
        Console.WriteLine("  -o, --out <路径> 指定输出文件");
        Console.WriteLine("  --dry           只分析并打印将要做的修改，不写盘");
        Console.WriteLine("  --quiet         精简输出");
        Console.WriteLine("  -h, --help      显示本帮助");
        Console.WriteLine();
        Console.WriteLine("退出码：0 成功/无需修改 · 2 自检未通过 · 3 未找到目标文件");
        Console.WriteLine("        4 目标不是 Plain Craft Launcher 2 · 5 分析失败（非托管 exe 等）");
    }

    // ---------------- 站点发现 ----------------
    sealed class Site
    {
        public TypeDefinition Type;
        public MethodDefinition Method;
        public MethodBody Body;
        public string FakeName;
        public int StartOff, EndOff, Len;
        public int StartIdx, EndIdx;          // 在快照数组中的下标，避免实时集合 IndexOf
        public FieldDefinition RealField;
        public MethodDefinition StubMethod;
        public Instruction[] Snapshot;
        public int EhOverlap;
    }

    static List<Site> FindKillSites(ModuleDefinition module, List<TypeDefinition> allTypes)
    {
        var list = new List<Site>();
        foreach (var t in allTypes)
        {
            foreach (var m in t.Methods)
            {
                if (m.Body == null) continue;
                var ins = m.Body.Instructions.ToArray();     // 快照，下标稳定
                for (int i = 0; i < ins.Length; i++)
                {
                    if (ins[i].OpCode != OpCodes.Newobj) continue;
                    var mr = (MethodReference)ins[i].Operand;
                    var dt = mr.DeclaringType;
                    var root = dt.IsGenericInstance ? ((GenericInstanceType)dt).ElementType.FullName : dt.FullName;
                    if (root != LoaderTaskRoot) continue;

                    // 该 LoaderTask 构造的 LoadDelegate 必须指向一个「只 throw」的桩方法
                    var stub = FindThrowStub(ins, i);
                    if (stub == null) continue;

                    int start = LastLdstrBefore(ins, i);
                    if (start < 0) continue;

                    int endOff = ins[i].Offset + ins[i].GetSize();
                    int startOff = ins[start].Offset;

                    var site = new Site
                    {
                        Type = t,
                        Method = m,
                        Body = m.Body,
                        FakeName = (string)ins[start].Operand,
                        StartOff = startOff,
                        EndOff = endOff,
                        Len = endOff - startOff,
                        StartIdx = start,
                        EndIdx = i,
                        StubMethod = stub,
                        Snapshot = ins,
                    };
                    site.RealField = PickRealLoaderField(module, allTypes, (GenericInstanceType)dt, m);
                    list.Add(site);
                }
            }
        }
        return list;
    }

    // 在 newobj LoaderTask 之前回溯，找到被压入的 Action<LoaderTask> 所引用的 ldftn -> 桩方法。
    // VB 编译器会缓存委托：ldftn 可能指向一个「包装方法」，其内部再 ldftn + newobj + callvirt Invoke
    // 到真正的 throw 桩，因此需要递归下钻。
    static MethodDefinition FindThrowStub(IList<Instruction> ins, int newobjIdx)
    {
        for (int i = newobjIdx - 1; i >= 0 && i >= newobjIdx - 60; i--)
        {
            if (ins[i].OpCode != OpCodes.Ldftn) continue;
            var hit = ResolveKillStub(((MethodReference)ins[i].Operand).Resolve(), 0);
            if (hit != null) return hit;
        }
        return null;
    }

    static MethodDefinition ResolveKillStub(MethodDefinition md, int depth)
    {
        if (md == null || depth > 4 || md.Body == null) return null;
        var b = md.Body.Instructions;
        bool hasThrow = b.Any(x => x.OpCode == OpCodes.Throw);
        bool hasKill = b.Any(x => x.OpCode == OpCodes.Ldstr && x.Operand is string s &&
                                  (s.Contains(KillHintA) || s.Contains(KillHintB)));
        if (hasThrow && hasKill && b.Count <= 24) return md;

        // 下钻一层：包装方法里的 ldftn 目标
        if (b.Count <= 24 && b.Any(x => x.OpCode == OpCodes.Callvirt && ((MethodReference)x.Operand)?.Name == "Invoke"))
            foreach (var x in b)
                if (x.OpCode == OpCodes.Ldftn)
                {
                    var inner = ResolveKillStub(((MethodReference)x.Operand).Resolve(), depth + 1);
                    if (inner != null) return inner;
                }
        return null;
    }

    static int LastLdstrBefore(IList<Instruction> ins, int idx)
    {
        for (int i = idx - 1; i >= 0 && i >= idx - 40; i--)
            if (ins[i].OpCode == OpCodes.Ldstr) return i;
        return -1;
    }

    static FieldDefinition PickRealLoaderField(ModuleDefinition module, List<TypeDefinition> allTypes,
                                               GenericInstanceType taskType, MethodDefinition host)
    {
        if (taskType.GenericArguments.Count != 2) return null;
        var a0 = taskType.GenericArguments[0].FullName;
        var a1 = taskType.GenericArguments[1].FullName;

        var cands = new List<FieldDefinition>();
        foreach (var t in allTypes)
            foreach (var f in t.Fields)
            {
                if (!f.IsStatic) continue;
                if (!(f.FieldType is GenericInstanceType g) || g.GenericArguments.Count != 2) continue;
                if (g.ElementType.FullName != LoaderTaskRoot) continue;
                if (g.GenericArguments[0].FullName != a0 || g.GenericArguments[1].FullName != a1) continue;
                cands.Add(f);
            }
        if (cands.Count == 0) return null;

        // 偏好：主加载器（名字含 Main / 或就是 DlClientListLoader），排除具体源子加载器
        int Score(FieldDefinition f)
        {
            int s = 0;
            var n = f.Name;
            if (n == "DlClientListLoader") s += 100;
            if (n.Contains("Main")) s += 50;
            if (n.Contains("Mojang") || n.Contains("Bmclapi") || n.Contains("Official") || n.Contains("Loader") && n.Contains("List") && n.Contains("Mirror")) s -= 30;
            if (n.StartsWith("Dl") && n.Contains("Client")) s += 20;
            return s;
        }
        return cands.OrderByDescending(Score).First();
    }

    static Site FindAlreadyRestored(List<TypeDefinition> allTypes)
    {
        var dl = allTypes.FirstOrDefault(x => x.FullName == "PCL.ModDownload");
        var real = dl?.Fields.FirstOrDefault(f => f.Name == "DlClientListLoader");
        if (real == null) return null;
        foreach (var t in allTypes)
            foreach (var m in t.Methods)
            {
                if (m.Body == null || m.Name != "LoaderInit") continue;
                if (m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld &&
                        ((FieldReference)i.Operand).Name == "DlClientListLoader"))
                    return new Site { Type = t, Method = m, RealField = real, FakeName = "(无)" };
            }
        return null;
    }

    // ---------------- 校验 ----------------
    static void Validate(Site s)
    {
        var ins = s.Snapshot;
        var inside = new HashSet<Instruction>(ins.Skip(s.StartIdx).Take(s.EndIdx - s.StartIdx + 1));
        foreach (var x in ins)
        {
            if (inside.Contains(x)) continue;
            if (x.Operand is Instruction t && inside.Contains(t) && (t.Offset > s.StartOff && t.Offset < s.EndOff))
                throw new Exception($"段外分支跳入段内：{x}");
            if (x.Operand is Instruction[] ts && ts.Any(y => inside.Contains(y) && y.Offset > s.StartOff && y.Offset < s.EndOff))
                throw new Exception($"分支表跳入段内：{x}");
        }
        // 等长替换不移动任何偏移，异常处理块天然安全；此处仅记录，供人工复核
        int ehOverlap = s.Body.ExceptionHandlers.Count(eh =>
            Range(s, eh.TryStart) || Range(s, eh.TryEnd) || Range(s, eh.HandlerStart) || Range(s, eh.HandlerEnd));
        s.EhOverlap = ehOverlap;
    }

    static bool Range(Site s, Instruction i) => i != null && i.Offset > s.StartOff && i.Offset < s.EndOff;

    static void Describe(Site s)
    {
        Console.WriteLine($"  站点 {s.Type.Name}::{s.Method.Name}");
        Console.WriteLine($"    桩方法 : {s.StubMethod?.FullName}  ({s.StubMethod?.Body?.Instructions.Count} 条指令，仅 throw)");
        Console.WriteLine($"    真实字段 : {s.RealField?.DeclaringType.FullName}::{s.RealField?.Name}  token=0x{s.RealField?.MetadataToken.ToInt32():X8}");
        Console.WriteLine($"    异常处理块 : {s.Body.ExceptionHandlers.Count} 个（等长替换不影响其偏移）");
    }

    static int Verify(string path, List<Site> sites, byte[] orig, byte[] now)
    {
        Console.WriteLine("\n  —— 回读自检 ——");
        bool allOk = true;
        using var chk = ModuleDefinition.ReadModule(path);
        var ct = chk.GetTypes().ToList();
        foreach (var s in sites.Where(x => x.RealField != null))
        {
            var t = ct.FirstOrDefault(x => x.FullName == s.Type.FullName);
            var m = t?.Methods.FirstOrDefault(x => x.Name == s.Method.Name && x.Parameters.Count == s.Method.Parameters.Count);
            if (m?.Body == null) { Console.WriteLine($"    ✗ {s.Method.Name}: 无法读取"); allOk = false; continue; }
            var strs = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToList();
            var flds = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldsfld).Select(i => ((FieldReference)i.Operand).Name).ToList();
            bool ok = !strs.Contains(s.FakeName) && flds.Contains(s.RealField.Name)
                      && m.Body.CodeSize == s.Body.CodeSize;
            Console.WriteLine($"    {(ok ? "✓" : "✗")} {t.Name}::{m.Name}  codeSize={m.Body.CodeSize}B(不变)  ldstr=[{string.Join(",", strs)}]  ldsfld=[{string.Join(",", flds)}]");
            allOk &= ok;
        }
        int diff = 0, lo = int.MaxValue, hi = -1;
        for (int i = 0; i < orig.Length; i++) if (orig[i] != now[i]) { diff++; if (i < lo) lo = i; if (i > hi) hi = i; }
        Console.WriteLine($"    字节差异 : {diff} 处，区间 [0x{lo:X}..0x{hi:X}]，文件尺寸 {orig.Length} -> {now.Length}");
        Console.WriteLine($"    结论 : {(allOk ? "PASS —— 原版游戏下载入口已接回" : "FAIL —— 请检查")}");
        return allOk ? 0 : 2;
    }

    // ---------------- 工具 ----------------
    static string AutoFindTarget()
    {
        // 本程序自身（用户可能把图标换成 PCL 后随手改名，务必排除）
        string self = Environment.ProcessPath;

        bool IsPclNamed(string f) =>
            f.IndexOf("Plain Craft Launcher", StringComparison.OrdinalIgnoreCase) >= 0
            || f.IndexOf("PCL", StringComparison.OrdinalIgnoreCase) >= 0;

        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var exes = Directory.EnumerateFiles(dir, "*.exe")
                .Where(f => !IsSelf(f, self))
                .Where(IsPclNamed)
                .ToList();

            // 1) 未处理过的本体
            var hit = exes.FirstOrDefault(f =>
                !f.Contains("(fixed)", StringComparison.OrdinalIgnoreCase)
                && !f.EndsWith(".bak.exe", StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;

            // 2) 只有已处理产物时，直接用它（双击即可启动，无需再补）
            hit = exes.FirstOrDefault(f => f.Contains("(fixed)", StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    static bool IsSelf(string candidate, string selfPath)
    {
        if (string.IsNullOrEmpty(selfPath)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(selfPath),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------- 交互辅助（P/Invoke，无 WinForms 依赖，x86/x64 通用）----------
    static class Native
    {
        public const uint MB_OK = 0, MB_ICONINFORMATION = 0x40, MB_ICONWARNING = 0x30, MB_ICONERROR = 0x10;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        // 返回共享该控制台的进程数；用于区分「双击新建的黑框」与「已打开的终端」
        [DllImport("kernel32.dll")]
        public static extern uint GetConsoleProcessList(uint[] processList, uint processListLength);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public string lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetOpenFileName(ref OPENFILENAME ofn);

        public const uint OFN_FILEMUSTEXIST = 0x1000, OFN_PATHMUSTEXIST = 0x800, OFN_HIDEREADONLY = 0x4;
    }

    static bool IsInteractiveDesktop()
    {
        try
        {
            // 输出被重定向（CI / 管道 / 计划任务）时不进入自动模式，避免弹窗挂死
            if (Console.IsOutputRedirected || Console.IsErrorRedirected) return false;
            return Environment.UserInteractive;
        }
        catch { return false; }
    }

    /// 弹模态提示框。**仅在真正可交互的桌面环境弹框**：
    /// 否则（CI、远程会话、输出被重定向、计划任务）模态框会永久阻塞无人应答的进程。
    static void InfoBox(string text, string caption, uint icon = Native.MB_ICONINFORMATION)
    {
        if (!CanDialog())
        {
            Console.WriteLine($"  [{caption}] {text.Replace("\n", " ").Replace("\r", "")}");
            return;
        }
        try
        {
            // MB_SETFOREGROUND(0x10000) 确保弹到前台，不被启动器主窗吞掉
            Native.MessageBoxW(IntPtr.Zero, text, caption, Native.MB_OK | icon | 0x10000);
        }
        catch { Console.WriteLine($"[{caption}] {text}"); }
    }

    static bool CanDialog()
    {
        try
        {
            if (Console.IsOutputRedirected || Console.IsErrorRedirected) return false;
            if (!Environment.UserInteractive) return false;
            return true;
        }
        catch { return false; }
    }

    /// 找不到本体时：先说明原因，再弹选择框让用户手动指认。
    /// 非交互环境（CI / 远程会话 / 输出被重定向）不弹框，直接返回 null，避免永久阻塞。
    static string PickFileViaDialog()
    {
        if (!CanDialog())
        {
            Console.WriteLine("  未找到 PCL2 本体，且当前环境无法弹出选择框。");
            Console.WriteLine("  请把本补丁与 PCL2 放在同一文件夹，或显式传入本体路径作为参数。");
            return null;
        }
        InfoBox(
            "此补丁需要和 PCL2 本体在同一个文件夹下使用。\n\n" +
            "如果本体存放在不同的文件夹，或者被改成了别的文件名，\n" +
            "请在接下来的选择框内选中 PCL2 本体（Plain Craft Launcher 2.exe）。",
            "没有在同目录找到 PCL2 本体", Native.MB_ICONWARNING);

        try
        {
            var buf = new string('\0', 1024);
            var titleBuf = new string('\0', 260);
            var ofn = new Native.OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<Native.OPENFILENAME>(),
                lpstrFilter = "PCL2 启动器 (*.exe)\0*.exe\0所有文件 (*.*)\0*.*\0\0",
                lpstrFile = buf,
                nMaxFile = buf.Length,
                lpstrFileTitle = titleBuf,
                nMaxFileTitle = titleBuf.Length,
                lpstrTitle = "请选择 PCL2 本体（Plain Craft Launcher 2.exe）",
                lpstrDefExt = "exe",
                Flags = (int)(Native.OFN_FILEMUSTEXIST | Native.OFN_PATHMUSTEXIST | Native.OFN_HIDEREADONLY),
                nFilterIndex = 1,
            };
            if (Native.GetOpenFileName(ref ofn))
            {
                int nul = ofn.lpstrFile.IndexOf('\0');
                string picked = nul >= 0 ? ofn.lpstrFile.Substring(0, nul) : ofn.lpstrFile;
                if (!string.IsNullOrWhiteSpace(picked) && File.Exists(picked))
                {
                    Console.WriteLine("  已选择 : " + picked);
                    return Path.GetFullPath(picked);
                }
            }
            Console.WriteLine("  已取消选择。");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  无法打开文件选择框：" + ex.Message);
        }
        return null;
    }

    static void LaunchPcl2(string exe)
    {
        if (_noLaunch) { Console.WriteLine("  已跳过自动启动（--no-launch）: " + exe); return; }
        // 非交互环境（CI / 远程会话 / 输出被重定向）不要拉起 GUI，只报告路径
        if (!CanDialog())
        {
            Console.WriteLine("  当前环境非交互桌面，跳过自动启动。请手动双击：\n    " + exe);
            return;
        }
        try
        {
            if (!File.Exists(exe)) { InfoBox("找不到要启动的文件：\n" + exe, "启动失败", Native.MB_ICONERROR); return; }
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetFullPath(exe),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe)),
                UseShellExecute = true,
            });
            Console.WriteLine("  已启动 : " + exe);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  启动失败：" + ex.Message);
            InfoBox("补丁已完成，但自动启动失败：\n" + ex.Message + "\n\n请手动双击：\n" + exe,
                    "启动失败", Native.MB_ICONWARNING);
        }
    }

    static void PauseIfInteractive()
    {
        if (!IsInteractiveDesktop()) return;
        // 只在「本进程独占控制台」时暂停 —— 即双击 / 拖放新建出来的黑框，
        // 否则用户看不到结果就闪退了。从已打开的终端里运行时不该拦人。
        if (!OwnsConsole()) return;
        Console.WriteLine();
        Console.Write("按任意键退出…");
        try { Console.ReadKey(true); } catch { }
    }

    static bool OwnsConsole()
    {
        try
        {
            var pids = new uint[4];
            uint n = Native.GetConsoleProcessList(pids, (uint)pids.Length);
            if (n == 0) return false;                       // 无控制台
            return n <= 1;                                  // 只有我们自己
        }
        catch { return false; }
    }

    static string NextTo(string path, string infix)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, name + infix + ext);
    }

    static long RvaToOff(byte[] file, uint rva)
    {
        int peSig = BitConverter.ToInt32(file, 0x3C);
        if (BitConverter.ToUInt32(file, peSig) != 0x00004550) throw new Exception("PE 签名无效");
        int coff = peSig + 4;
        ushort n = BitConverter.ToUInt16(file, coff + 2);
        int sec = coff + 20 + BitConverter.ToUInt16(file, coff + 16);
        for (int i = 0; i < n; i++)
        {
            int so = sec + i * 40;
            uint va = BitConverter.ToUInt32(file, so + 12);
            uint vsz = BitConverter.ToUInt32(file, so + 8);
            uint raw = BitConverter.ToUInt32(file, so + 16);
            uint ptr = BitConverter.ToUInt32(file, so + 20);
            if (rva >= va && rva < va + Math.Max(vsz, raw)) return ptr + (rva - va);
        }
        throw new Exception($"RVA 0x{rva:X} 未映射");
    }

    static string ShaBytes(byte[] data)
    {
        using var h = System.Security.Cryptography.SHA256.Create();
        return string.Concat(h.ComputeHash(data).Select(b => b.ToString("x2")));
    }
}
