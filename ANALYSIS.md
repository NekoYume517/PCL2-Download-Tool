# 逆向分析：PCL2 2.99 原版游戏下载入口

本文记录对 `Plain Craft Launcher 2.exe` **v2.99.0.0** 的完整逆向过程与证据，
以及补丁的设计依据。用法见 [README.md](README.md)。

---

## 0. 样本信息

| 项 | 值 |
| --- | --- |
| 文件 | `Plain Craft Launcher 2.exe` |
| 大小 | 5,980,160 字节 |
| SHA-256 | `6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386` |
| 格式 | PE32 (`0x10B`)，Machine `0x014C` (i386)，3 个 section |
| 类型 | .NET 托管程序集，**IL-only**（CLI flags = 0x1） |
| 运行时 | `.NETFramework,Version=v4.8`，metadata `v4.0.30319` |
| 程序集 | `Plain Craft Launcher 2` `2.99.0.0`，无强名称（PublicKey = none） |
| 入口 | metadata token `0x06000EF7` |
| 语言 | 源码为 **VB.NET**（WPF，`Microsoft.VisualBasic` + `PresentationFramework`） |
| 签名 | **未签名**（`Get-AuthenticodeSignature` → `NotSigned`） |

镜像规模：TypeDef 665 / MethodDef 6339 / MemberRef 3200 / CustomAttribute 5186。
两个嵌入资源：`Plain Craft Launcher 2.g.resources`（764 KB，BAML + 图片）、
`PCL.Resources.resources`（3.48 MB，**内嵌依赖 DLL**）。

> 「未签名」这点很重要：修改字节不会破坏任何签名校验链，也不需要重新签名。

---

## 1. 定位：从 UI 文本反查代码

用户可见的症状是一句红字提示。逆向 WPF/VB 程序时，**UI 文案是最快的入口**。

先反编译整个程序集为 C# 工程（dnSpy / ILSpy / ilspycmd 任选）：

```bash
ilspycmd "Plain Craft Launcher 2.exe" -p -o ./src --decompile-baml
```

`--decompile-baml` 会把 XAML 页面一起还原出来，便于对照 UI 与代码。

全镜像搜索关键词 `原版`，命中左侧导航页的 XAML：

```xml
<!-- pages/pagedownload/pagedownloadleft.xaml -->
<local:MyListItem x:Name="ItemInstall" Tag="0" Title="原版游戏" ... />
```

`Tag="0"` 对应 `FormMain.PageSubType.Default`。再看 `PageDownloadLeft.PageGet()`：

```csharp
case FormMain.PageSubType.Default:
    if (ModMain.FrmDownloadInstall == null)
        ModMain.FrmDownloadInstall = new PageDownloadInstall();
    return ModMain.FrmDownloadInstall;
```

入口页面 = **`PCL.PageDownloadInstall`**。

---

## 2. 找到 kill 点

在 `PageDownloadInstall` 中搜索那句提示，命中三处（同一个桩）：

```csharp
// 闭包类里的真桩：3 条指令
internal void _Lambda$__3-0() {
    throw new Exception("为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……");
}

// VB 委托缓存包装
internal void _Lambda$__R3-3(LoaderTask<string, DlClientListResult> a0) {
    ((VB$AnonymousDelegate_10)/* 缓存 */ ...)();   // 内部 newobj + callvirt Invoke -> _Lambda$__3-0
}
```

关键在 `LoaderInit()`：

```csharp
public virtual void LoaderInit() {
    ControlInit();
    PageLoaderInit(
        LoadMinecraft, PanLoad, PanAllBack, null,
        new ModLoader.LoaderTask<string, DlClientListResult>(   // ← 替身
            "关闭下载",
            a0 => { throw new Exception("为遵守相关法律法规……"); }),
        a0 => { LoadMinecraft_OnFinish(); });
}
```

对照上游源码（[Meloong-Git/PCL](https://github.com/Meloong-Git/PCL)）可见，原本这里传的
是 `ModDownload.DlClientListLoader`。也就是说 **UI 侧的绑定被换成了替身**，
而 `LoadMinecraft_OnFinish` 回调（读 `DlClientListLoader.Output.Value["versions"]`
→ `ShowMinecraftVersionList()`）**完全没改**：

```csharp
private void LoadMinecraft_OnFinish() {
    MinecraftVersions = (JArray)ModDownload.DlClientListLoader.Output.Value["versions"].DeepClone();
    ShowMinecraftVersionList();
    TrySelectWaitingVersion();
}
```

这就是「只拆绑定点、不拆功能」的典型手法——改动面最小，也最容易恢复。

### 对应的 IL（权威证据）

`ilspycmd -t PCL.PageDownloadInstall -il` 输出，`LoaderInit` RVA `0x2DEB4`，codeSize 94 B：

```
IL_0000: ldarg.0
IL_0001: call   PCL.PageDownloadInstall::ControlInit()
IL_0006: ldarg.0
IL_0007: ldarg.0
IL_0008: callvirt get_LoadMinecraft()          ; LoaderUi
IL_000d: ldarg.0
IL_000e: callvirt get_PanLoad()                ; PanLoader
IL_0013: ldarg.0
IL_0014: callvirt get_PanAllBack()             ; PanContent
IL_0019: ldnull                                ; PanAlways
IL_001a: ldstr   "关闭下载"                     ; ★ 段起点：替身 Name
IL_001f: ldsfld  _Closure$__::$IR3-3           ; 缓存的委托
IL_0024: brfalse.s IL_002d
IL_0026: ldsfld  _Closure$__::$IR3-3
IL_002b: br.s    IL_0043
IL_002d: ldsfld  _Closure$__::$I
IL_0032: ldftn   _Closure$__::_Lambda$__R3-3   ; 包装方法
IL_0038: newobj  Action`1::.ctor(...)
IL_003d: dup
IL_003e: stsfld  _Closure$__::$IR3-3
IL_0043: ldnull                                ; InputDelegate
IL_0044: ldc.i4.2                              ; ThreadPriority
IL_0045: newobj  LoaderTask`2::.ctor(...)      ; ★ 段终点：构造替身
IL_004a: ldarg.0
IL_004b: ldftn   PageDownloadInstall::_Lambda$__R3-4   ; FinishedInvoke（未动）
IL_0051: newobj  Action`1::.ctor(...)
IL_0056: ldnull                                ; InputInvoke
IL_0057: ldc.i4.1                              ; AutoRun = True
IL_0058: call    MyPageRight::PageLoaderInit<string, DlClientListResult>(...)
IL_005d: ret
```

替换段 = `IL_001A..IL_004A`，共 **48 字节**。

---

## 3. 确认下游链路完好

恢复之前必须确认「接回去就能用」。逐层验证：

```
ModDownload.DlClientListLoader                       LoaderTask<string, DlClientListResult>
  └─ DlClientListMain                                按 Settings("ToolDownloadVersion") 选源
       ├─ DlClientListMojangLoader
       │    https://launchermeta.mojang.com/mc/game/version_manifest.json
       │    （校验 versions 长度 >= 200，测速决定是否优先官方源）
       └─ DlClientListBmclapiLoader
            https://bmclapi2.bangbang93.com/mc/game/version_manifest.json
            （还会 merge 本地 Cache\download.json 补版本）
            └─ DlClientListGet(id)                   返回该版本 json 的 url
                 └─ ModDownloadLib.McDownloadClientLoader(id, jsonUrl, versionFolder)
                      ├─ LoaderDownload "下载原版 json 文件"
                      ├─ LoaderCombo  "下载原版支持库文件"    → libraries
                      └─ LoaderCombo  "下载原版资源文件"      → asset index + assets
```

`DlClientListGet` 内部用的是 `DlClientListLoader.WaitForExit(...)`，
即**它自己会拉起主加载器**——所以只要 `DlClientListLoader` 不是替身，整条链自动通。

另外 `PageDownloadLeft.Refresh()` 里也仍在 `Start()` 一批真实加载器
（`DlClientListLoader` / OptiFine / Forge / NeoForge / LiteLoader / Fabric / …），
说明**只有 Install 页面的绑定被单独替换**，其余都还是原样。

结论：接回一处 `ldsfld`，版本列表页、下载页、修改版本页三处同时恢复。

---

## 4. 关于 `ModSecret.RestrictMcDownload`（常见误解）

网上流传的修法是「改 `Modules/ModSecret.vb` 第 18 行 `RestrictMcDownload = True` 为 `False`」。
那是**上游源码**层面的做法。在这个已编译的二进制里：

```il
.field public static literal bool RestrictMcDownload = bool(true)
```

`literal` = `public const`，是**编译期常量，会被直接内联到每个使用点**。
全镜像搜索 `RestrictMcDownload` 只有 **1 处命中**，就是它自己的字段声明——
没有任何 `ldsfld` 引用它。

所以：把这个字段值改成 `false`，对运行时**零影响**。必须直接改 IL 调用点。

（工具仍会顺手把常量翻成 `false`，仅为与上游语义一致，不影响行为。）

---

## 5. 补丁设计：为什么必须「等长」

最直觉的做法是用 Mono.Cecil 重写整个方法体。**这条路在本样本上行不通**：

```
Mono.Cecil.AssemblyResolutionException:
  Failed to resolve assembly: 'PCLCS, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
   at Mono.Cecil.MetadataBuilder.GetConstantType(...)
   at Mono.Cecil.MetadataBuilder.AddParameter(...)
   at Mono.Cecil.ModuleWriter.BuildMetadata(...)
```

原因：PCL2 把 `PCLCS`、`MeloongCore`、`MeloongCore.Wpf`、`Newtonsoft.Json`、`NAudio`、
`CacheCow.*`、`Imazen.WebP`、`ThrottleDebounce`、`PCLCS`、`libwebp64` 等依赖
**全部内嵌**在 `PCL.Resources.resources` 里（运行时才释放到磁盘）。
Cecil 写盘时会重建整个元数据树，需要解析所有引用程序集，于是必然失败。

解法：**等长字节手术**。只替换那 48 字节，长度保持不变：

```
ldsfld  PCL.ModDownload::DlClientListLoader   ; 0x7E + token 0x0400040F  = 5 字节
nop × 43                                       ; 0x00 × 43
```

长度不变带来三个强性质：

1. **`codeSize` 不变** → 方法体之后的异常处理段（`CorILMethod_MoreSects`）偏移不变
2. **所有分支目标偏移不变** → 无需重定位任何 `br` / `switch`
3. **元数据表、文件尺寸、其余全部指令一个字节不动** → 不需要重写元数据，绕开上面的失败

Cecil 在这里**降级为只读的解码器**（定位 RVA、token、校验指令语义），写入走手写 PE 偏移。

### 安全前置校验

替换前必须确认段是「自包含」的：

- 段内不得有来自段外的分支跳入（`br` 目标 / `switch` 表项）
- 段的边界不得落在异常处理块的 `TryStart/TryEnd/HandlerStart/HandlerEnd` 上
- 段的起点字节必须是 `0x72`（`ldstr`），终点前 5 字节必须是 `0x73`（`newobj`）
- `codeSize` / `maxStack` 必须与 Cecil 解码结果一致（用于验证 fat 头解析方向正确）

本样本 `LoaderInit` 的 `ExceptionHandlers.Count == 0`，且段内分支（`brfalse.s IL_002d`、
`br.s IL_0043`）全部指向段内，安全。

### IL 头解析的一个坑

ECMA-335 II.25.4.1 的 **fat 方法头是 16 位**：

```
bit  0..11 : Flags   (CorILMethod_FatFormat = 0x3, MoreSects = 0x8, InitLocals = 0x10)
bit 12..15 : Size    (头长度，单位 4 字节)
```

本样本原始字 = `0x3013` → Flags 低 2 位 = `0x3`（fat）、InitLocals 置位、无 MoreSects、
Size = 3 → 头长 12 B。随后 `MaxStack(u16)=9` / `CodeSize(u32)=0x5E=94` / `LocalVarSig(u32)=0`。

（我第一版把 Flags 和 Size 的位置弄反了，用「`codeSize` 与 Cecil 解码是否一致」这条
交叉校验立刻暴露了问题——这类校验断言值得写，别省。）

---

## 6. 验证

三层，缺一不可：

### 6.1 静态回读

重新用 Cecil 打开产物，确认：

```
ldstr  = []                     (替身名 "关闭下载" 已消失)
ldsfld = [DlClientListLoader]   (真实加载器已接上)
codeSize = 94B                  (不变)
PageLoaderInit 调用点 = 1       (未破坏)
字节差异 = 41 处，区间 [0x2C0DA..0x2C109]
文件尺寸 5980160 -> 5980160
```

### 6.2 JIT 校验（关键）

静态检查证明不了 IL 合法。用 .NET Framework 强制编译被改方法——
JIT 会做完整的栈平衡 / 类型 / token 校验：

```powershell
# 必须用系统 PowerShell（.NET Framework 4.x），PCL2 是 Fx 程序集
$a  = [Reflection.Assembly]::LoadFrom("Plain Craft Launcher 2 (fixed).exe")
$m  = $a.GetType("PCL.PageDownloadInstall").GetMethod("LoaderInit", [Type[]]@())
[System.Runtime.CompilerServices.RuntimeHelpers]::PrepareMethod($m.MethodHandle)
# → 无异常 = IL 合法
```

### 6.3 实跑

在隔离目录启动修复后的 exe，检查日志：

```
00:02:22.799  [ModNet] 发起网络请求（GET, https://launchermeta.mojang.com/mc/game/version_manifest.json）
00:02:23.483  [ModDownload] Mojang 官方源加载耗时：688ms，可优先使用官方源
00:02:23.485  加载器 DlClientList Mojang 状态改变：Finished
00:02:24.265  加载器 DlClientList Main   状态改变：Finished
```

日志中**不再出现**「为遵守相关法律法规」，也没有替身名「关闭下载」。

UI 侧（`PrintWindow` 抓屏，无视 Z 序）确认「下载 → 原版游戏」已渲染出完整版本列表：
最新版本 26.3 / 26.4-snapshot-5，以及正式版 / 预览版 / 远古版 / 愚人节版四个分组。

---

## 7. 其它 kill 点（本工具未处理）

同一句提示在全镜像出现 5 处，分属 4 个语义位置：

| 位置 | 表现 | 是否处理 |
| --- | --- | --- |
| `PageDownloadInstall._Closure$__._Lambda$__3-0` | 原版游戏下载入口 | ✅ 本工具目标 |
| `ModModpack.ModpackInstall()` 无参重载 | `Hint` 红字，拖 zip 进主窗口 | ❌ 独立路径 |
| `ModModpack.ModpackInstall(file,…)` | 识别完包格式（mcbbs/mmc/modrinth/…）后 `throw` | ❌ 整合包安装主路径 |
| `FormMain` 拖放的 `catch` 分支 | 对上述 throw 的特判显示 | ❌ 纯显示，无害 |
| `FormMain.ShowUpdateLog` | 更新日志文案 | ❌ 纯文本，无害 |

整合包安装需要**下载对应原版 json**，逻辑上和原版下载相关，但代码路径完全独立
（在 `ModModpack` 里，且是把整段安装流程替换成 throw，不是换绑定）。
恢复它需要重建被删的原始实现，而不是改一个引用——因此本工具明确不做，
并在 README 的「已知边界」里声明。

---

## 8. 复现命令速查

```bash
# 反编译为 C# 工程（含 XAML）
ilspycmd "Plain Craft Launcher 2.exe" -p -o ./src --decompile-baml

# 只反编译单个类型
ilspycmd "Plain Craft Launcher 2.exe" -t PCL.PageDownloadInstall

# 导出全镜像 IL（定位 kill 点、核对偏移）
ilspycmd "Plain Craft Launcher 2.exe" -il > full.il

# 列内嵌资源（确认依赖 DLL 被塞在资源里）
ilspycmd "Plain Craft Launcher 2.exe" --list-resources

# 静态元数据（不执行、不加载 CLR）
#   REA MCP: inspect_managed_artifact / inspect_managed_members

# 找 UI 文案对应的 XAML
grep -rn "原版游戏" ./src
```

---

## 9. 免责声明

本项目是**针对本地样本的互操作性研究与技术恢复工具**，用于说明：
「仅替换一处绑定」的功能禁用，在二进制层面是可逆的。

- 仅处理用户自己持有的本地文件，不绕过任何运行时保护、签名校验或授权检查
  （本样本本来就没有签名）。
- Minecraft 游戏文件的下载源为 Mojang 官方与 BMCLAPI 公开镜像，工具不含任何
  账号、授权或验证逻辑。
- 请自行确认你对相关游戏内容的使用符合 Mojang Studios 的使用条款与所在地区的法律。
- 与 PCL / Meloong、Mojang、Microsoft 均无隶属或背书关系。
