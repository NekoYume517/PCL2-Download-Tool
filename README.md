# PCL2 下载入口恢复工具

Restore the **「下载 → 原版游戏」** and **「导入整合包」** entry points in **Plain Craft Launcher 2 (PCL2) 2.99**.

[![Release](https://img.shields.io/github/v/release/NekoYume517/PCL2-Download-Tool?label=release)](https://github.com/NekoYume517/PCL2-Download-Tool/releases)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-8.0--self--contained-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

---

## 问题

PCL2 2.99 里两个入口都被关掉了，点进去只会报：

> 为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……

1. **下载 → 原版游戏**：版本列表加载不出来
2. **版本选择 → 导入整合包**：只弹一条提示，连选文件的框都不出

## 原因（一句话）

被拆掉的**不是功能**，而是**入口对真实实现的绑定**：

**原版游戏**：`PageDownloadInstall.LoaderInit()` 传给页面框架的 `RealLoader` 参数，被换成一个当场
`new` 出来的替身 `LoaderTask`（名字就叫 `"关闭下载"`），唯一动作是 `throw`。真正的下载链——
版本列表拉取、json / libraries / assets 下载——**一行都没删**。

**导入整合包**：`ModModpack` 里有两道独立的锁：

- `ModpackInstall()`（无参）整段被换成「弹一条 Hint 然后 return」，真实现是
  `Dialogs.SelectFile` 选文件 → `RunInThread(ModpackInstall(File))`
- `ModpackInstall(string,string,string)` 在解析完包类型之后无条件 `ldstr + newobj + throw`，
  真实现是按 `PackType` 分派到 `InstallPack{CurseForge,HMCL,MMC,MCBBS,Modrinth,LauncherPack,Compress}`

所以两道锁都得开——只开第一道，选完文件就会撞上第二道。

完整逆向过程（IL 证据、地址、为何 `RestrictMcDownload` 改了没用）见 [ANALYSIS.md](ANALYSIS.md)。

## 版本与自更新

- 工具版本 5 段 `a.b.c.d.e`：前 4 段 = 最新适配的 PCL2 版本，第 5 段 = 工具修订号。
- 启动时读取目标 PCL 的程序集版本：在「已验证列表」内直接打补丁；不在列表内则提示，并自动查询 GitHub latest release——若有适配该 PCL 版本的新补丁会询问是否打开下载页（只提示，不静默下载）。\n## 两种补法，各有理由

| 入口 | 手法 | 原因 |
| --- | --- | --- |
| 原版游戏 | **等长字节替换**（48B → `ldsfld` + 43×`nop`） | 方法体长度不变 → codeSize / 分支偏移 / 异常处理边界 / 元数据全不动，最安全 |
| 导入整合包 | **Cecil 重写元数据** | 要恢复「选文件 + 起线程」和「switch 分派」，方法体长度必然变化，没法等长 |

Cecil 重写的老大难是 PCL 把 `PCLCS` / `MeloongCore` / `Newtonsoft.Json` 等依赖塞在
`PCL.Resources.resources` 里、运行时才释放，写回时会 `AssemblyResolutionException`。
本工具在重写前先把这些内嵌依赖解到临时目录喂给 `AssemblyResolver`，写完立即删除。

重写后还会做**悬空引用审计**：任何跳转 / switch 目标 / 异常处理边界若指向已删除的指令就中止
（这类错误写进 exe 后会变成运行时崩溃，不能只靠"能启动"判断）。

## 下载

去 [Releases](https://github.com/NekoYume517/PCL2-Download-Tool/releases) 下载：

| 文件 | 说明 |
| --- | --- |
| `PCL2VanillaRestore-win-x64.zip` | 自包含单文件，**目标机无需安装 .NET**，推荐 |
| `PCL2VanillaRestore-win-x86.zip` | 32 位 Windows 用 |
| `PCL2VanillaRestore-portable.exe` | 需要已安装 .NET 8 运行时，体积小 |
## 用法

### 方式一：双击（推荐给不熟悉命令行的用户）

> ⚠️ 修复前请**先完全退出 PCL2**，否则它的文件被自己占用，无法改写（补丁会弹窗提醒）。

把本程序和 `Plain Craft Launcher 2.exe` 放在**同一个文件夹**，然后**双击本程序**：

1. 自动原地改写启动器（先把原件备份为 `*.bak.exe`）
2. 弹窗提示修复结果
3. 点「确定」后**自动启动 PCL2** —— 原版下载和导入整合包同时可用

**全程不出现任何命令行黑框**（程序是 GUI 子系统）。找不到本体时会弹窗说明，并给出文件选择框让你手动指认本体位置；选错文件也会提示并允许重选。程序图标沿用了 PCL2 本体的图标，在文件夹里一眼能认出来。

诊断细节会写到同目录的 **`PCL2补丁日志.txt`**，出问题时把它发给维护者即可。

### 方式二：命令行

```bat
:: 输出到副本 "xxx (fixed).exe"，不动原文件
PCL2VanillaRestore.exe "Plain Craft Launcher 2.exe"

:: 原地修改，自动备份为 .bak.exe
PCL2VanillaRestore.exe "Plain Craft Launcher 2.exe" --in-place

:: 只分析不写盘，看看它找到了什么
PCL2VanillaRestore.exe "Plain Craft Launcher 2.exe" --dry
```

也可以**直接把启动器 exe 拖到本程序图标上**（等同双击，会自动改写并启动）。

| 选项 | 说明 |
| --- | --- |
| `--in-place` | 原地修改（自动备份 `*.bak.exe`）；默认输出 `xxx (fixed).exe` |
| `--dry` | 只分析并打印将要做的修改，不写盘 |
| `--auto` / `--no-auto` | 强制开启 / 关闭自动模式（默认：双击或拖放时开启） |
| `--no-launch` | 修复后不自动启动 PCL2 |
| `-o, --out <路径>` | 指定输出文件 |
| `--quiet` | 精简输出 |
| `-h, --help` | 显示帮助 |

退出码：`0` 成功/无需修改 · `2` 自检未通过 · `3` 未找到目标文件 · `4` 目标不是 PCL2 · `5` 分析失败（非托管 exe 等） · `6` PCL2 正在运行（文件被占用） · `7` 写盘失败（权限/占用）

> 自动模式在**非交互环境**（CI、远程会话、输出被重定向）下会自动降级：
> 不弹任何模态框、不拉起 GUI，只在控制台打印结果并快速返回，因此可安全用于脚本。

### 输出示例

```
PCL2 原版游戏下载入口恢复器 v2.99.0.1.1  （已验证适配: 2.99.0.0 / 2.99.0.1）
  目标 : D:\Downloads\Plain Craft Launcher 2.exe
  输出 : D:\Downloads\Plain Craft Launcher 2.exe   (原地修改，自动备份 .bak)

  SHA-256 : 6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386
  程序集  : Plain Craft Launcher 2 2.99.0.0  CLR v4.0.30319

  发现 1 处「原版游戏下载」入口替换：
    · PageDownloadInstall::LoaderInit  (RVA 0x2DEB4)  替身名="关闭下载"
      段 IL_001A..IL_004A (48B)  -> ModDownload::DlClientListLoader

  站点 PageDownloadInstall::LoaderInit
    桩方法 : System.Void PCL.PageDownloadInstall/_Closure$__::_Lambda$__3-0()  (3 条指令，仅 throw)
    真实字段 : PCL.ModDownload::DlClientListLoader  token=0x0400040F
    异常处理块 : 0 个（等长替换不影响其偏移）
  发现「导入整合包」入口被关闭：
    · ModModpack::ModpackInstall()  —— 整段替换为只弹提示，真实现是「选文件 + 起线程安装」
    · ModModpack::ModpackInstall(string,string,string)  —— 解析完包类型后无条件 throw，真实现是按类型分派

  ✓ PageDownloadInstall::LoaderInit  IL_001A 处 48B 已替换为 ldsfld DlClientListLoader + 43× nop
  备份 : D:\Downloads\Plain Craft Launcher 2.bak.exe
  正在解锁整合包导入（需重写元数据，可能稍慢）……
    已解出 14 个内嵌依赖用于元数据解析
    · kill 桩 IL_02DA 已替换为分派，重定向 16 处分支/异常边界
    整合包解锁完成：5,980,160 → 5,975,040 字节

  已写出 : D:\Downloads\Plain Craft Launcher 2.exe  (5,975,040 字节)

  —— 回读自检 ——
    ✓ PageDownloadInstall::LoaderInit  codeSize=94B(不变)  ldstr=[]  ldsfld=[DlClientListLoader]
    ✓ 整合包入口 ModpackInstall()  codeSize=94B  含 RunInThread=True
    ✓ 整合包分派 ModpackInstall(3参)  codeSize=895B  含 switch=True
    字节差异 : 5689476 处，区间 [0x97..0x5B2BFF]，文件尺寸 5,980,160 -> 5,975,040
    结论 : PASS —— 原版游戏下载 + 整合包导入 入口均已接回
```

## 原版下载为什么走「等长」

补丁把 48 字节的替身构造段换成 `ldsfld`（5 字节）+ `nop × 43`，**总长度不变**。这样：

1. `codeSize` 不变 → 方法体之后的异常处理段（`MoreSects`）偏移不变
2. 所有分支目标偏移不变（工具会校验段内无段外跳入的分支）
3. 元数据表、文件尺寸、其余全部指令**一个字节都不动**

这条路径上 Cecil 只用于读取和定位，**不参与写盘**，所以最稳。

## 整合包为什么必须走 Cecil 重写

要恢复的两段真实现（选文件 + 起线程 / switch 分派）都比被替换掉的替身**长得多**，
等长替换不可能。于是必须重写元数据，而 PCL2 把 `PCLCS`、`MeloongCore`、`Newtonsoft.Json`
等依赖全部内嵌在 exe 的 4.2 MB 资源里（运行时才释放到磁盘），直接写回会立刻死在
`Failed to resolve assembly: 'PCLCS'`。

解决办法：写回前先从 `PCL.Resources.resources` 把内嵌程序集解到临时目录（实测 14 个），
喂给 Cecil 的 `AssemblyResolver`，写完立即删掉临时目录。**解出的依赖不会留在用户机器上。**

### 三层嵌套 try 的坑（这个版本踩得最惨）

被替换掉的那条 `ldstr` 位于**外层 `try` 内部**，同时是内层 `catch` 的 `HandlerEnd`，
也是 14 条 `leave` 的落点。三个约束每一个都是靠实测踩出来的：

1. **分派块必须插在 kill 原来的位置（`try` 内部）**。追加到方法末尾 → 落在 `finally`
   handler 里 → `ret` 出现在 finally 中 → CLR 报「公共语言运行时检测到无效的程序」。
2. **但也不能插在 `finally` 之后**。那时 `ZipArchive` 已被 `finally` 释放，
   `InstallPack*` 拿到的是已 Dispose 的对象 → `ObjectDisposedException`
   「无法访问已释放的对象」。
3. **所以必须照 VB 编译器处理 `Try...Finally` 里 `Return` 的套路**：
   `try` 内算出结果 → `stloc` 到局部变量 → `leave` 到 finally 之后
   → finally 先执行 → 落到 `ldloc + ret`。
   同时把 `finally` 的 `HandlerEnd` 从 `null`（一路延伸到方法末尾）收窄到 `ret` 块之前。

最终 EH 结构（实测）：

```
Finally  try 0143..0276   h 0276..0282
Catch    try 006D..0284   h 0284..02DA
Finally  try 0027..03AD   h 03AD..03B7     ← HandlerEnd 明确收窄
```

### 分支 / 异常边界的坑

被删掉的那条 `ldstr` 不只是普通指令——它同时是 `try` 正常结束的 `leave` 目标和外层
`catch` 的 `HandlerEnd`。直接 `Remove` 会留下悬空引用，exe 能启动、但一走到对应路径就崩。
工具会把**所有指向它的分支、switch 目标、四个异常处理边界**统一改指向新的分派入口
（本次实测重定向 16 处），再删除原指令。

写盘后还有一道**悬空引用审计**：任何跳转 / switch 目标 / 异常处理边界若指向已删除的指令，
立即中止并报错，不写出文件。

## 版本策略

**工具版本号前三段 = 其所适配的 PCL2 版本号**，末位是工具自身修订号（加特性 / 修 bug 时递增，
不影响适配的 PCL 版本）。例如 `2.99.0.1.1` 表示「适配 PCL2 2.99.0.1 的第 1 版工具」——前 4 段跟随 PCL 版本，第 5 段是工具自己的修订号。

工具启动时会打印适配版本，并在运行时校验目标：

```
PCL2 原版游戏下载入口恢复器 v2.99.0.1.1  （已验证适配: 2.99.0.0 / 2.99.0.1）
  程序集  : Plain Craft Launcher 2 2.99.0.0  CLR v4.0.30319
```

- 目标**程序集名不是** `Plain Craft Launcher 2` → 直接中止（退出码 `4`），不会误改别的程序
- 目标**版本 ≠ 已验证版本** → 打印警告但仍尝试（定位是模式匹配的，相邻版本通常有效）；
  此时若末尾自检未通过（退出码 `2`），就不要使用产物

## 兼容性

发现逻辑**不硬编码任何地址**，全程模式匹配，因此可用于同结构的其它版本 / 分支：

1. 全镜像扫描 `newobj LoaderTask\`2<…>` 调用点
2. 回溯其 `LoadDelegate` 实参的 `ldftn` 目标，判定是否为「只 `throw` 且含关键词」的桩
   （VB 编译器会缓存委托，桩常被包一层 `_Lambda$__R3-3` → 内部再 `Invoke` 到真桩，工具递归下钻）
3. 以最近的 `ldstr` 为段起点、`newobj` 结束处为段终点
4. 按 `LoaderTask<TIn,TOut>` 的泛型实参签名，在镜像里挑同签名的静态字段作为真实加载器
   （打分偏好 `DlClientListLoader` / 含 `Main` 的主加载器，压低具体源子加载器）
5. 校验分支与异常块边界 → 等长替换 → 回读自检

整合包那两处同样不硬编码：入口按「有没有调用 `ModBase.RunInThread`」判定，分派按
「有没有 `switch` 且还带 kill 字符串」判定。

带**幂等检测**：对已修复的文件再跑一次，会提示入口已是真实现、无需修改。

## 已知边界

- 整合包解锁**只针对 2.99 这一版的 kill 形态**（两道锁）。若上游改了结构，工具会打印
  「未找到 kill 桩」并跳过，不会写出半成品。
- **PCL 自动更新会覆盖回原版**，更新后重跑一次工具即可。
- 目标 exe 本来就没有数字签名，修改后不影响运行；若被 SmartScreen / 杀软拦，选「仍要运行」。
- 若启动器装在受保护目录（如 `C:\Program Files`），请手动「以管理员身份运行」本工具。

## 构建

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o ./artifacts
```

唯一 NuGet 依赖：[`Mono.Cecil` 0.11.6](https://www.nuget.org/packages/Mono.Cecil)。
推送 tag 会自动触发 [Release workflow](.github/workflows/release.yml) 构建并发布。

## License

MIT
