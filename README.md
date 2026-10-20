# PCL2 原版游戏下载入口恢复工具

Restore the **"下载 → 原版游戏"** entry point in **Plain Craft Launcher 2 (PCL2) 2.99**.

[![Release](https://img.shields.io/github/v/release/NekoYume517/PCL2-VanillaDownload-Restore?label=release)](https://github.com/NekoYume517/PCL2-VanillaDownload-Restore/releases)
![Platform](https://img.shields.io/badge/platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-8.0--self--contained-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

---

## 问题

PCL2 2.99 打开「下载 → 原版游戏」只会报：

> 为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……

## 原因（一句话）

被拆掉的**不是下载功能**，而是页面对加载器的**绑定**：`PageDownloadInstall.LoaderInit()`
传给页面框架的 `RealLoader` 参数，被换成一个当场 `new` 出来的替身 `LoaderTask`（名字就叫
`"关闭下载"`），它的唯一动作是 `throw`。真正的下载链——版本列表拉取、json / libraries /
assets 下载——**一行都没删**，完好地躺在镜像里。

所以补丁只需要把「构造替身」那 48 字节 IL 换成一条 `ldsfld ModDownload::DlClientListLoader`。

完整逆向过程（IL 证据、地址、为何 `RestrictMcDownload` 改了没用）见 [ANALYSIS.md](ANALYSIS.md)。

## 下载

去 [Releases](https://github.com/NekoYume517/PCL2-VanillaDownload-Restore/releases) 下载：

| 文件 | 说明 |
| --- | --- |
| `PCL2VanillaRestore-win-x64.zip` | 自包含单文件，**目标机无需安装 .NET**，推荐 |
| `PCL2VanillaRestore-win-x86.zip` | 32 位 Windows 用 |
| `PCL2VanillaRestore-portable.exe` | 需要已安装 .NET 8 运行时，体积小 |

## 用法

三种方式任选：

```bat
:: 1) 输出到副本 "xxx (fixed).exe"，不动原文件
PCL2VanillaRestore.exe "Plain Craft Launcher 2.exe"

:: 2) 原地修改，自动备份为 .bak.exe
PCL2VanillaRestore.exe "Plain Craft Launcher 2.exe" --in-place

:: 3) 只分析不写盘，看看它找到了什么
PCL2VanillaRestore.exe "Plain Craft Launcher 2.exe" --dry
```

也可以**直接把启动器 exe 拖到工具图标上**；或者把工具和 exe 放在同一目录后双击运行（自动发现）。

### 输出示例

```
PCL2 原版游戏下载入口恢复器
  目标 : D:\Downloads\Plain Craft Launcher 2.exe
  输出 : D:\Downloads\Plain Craft Launcher 2 (fixed).exe

  SHA-256 : 6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386
  程序集  : Plain Craft Launcher 2 2.99.0.0  CLR v4.0.30319

  发现 1 处入口替换：
    · PageDownloadInstall::LoaderInit  (RVA 0x2DEB4)  替身名="关闭下载"
      段 IL_001A..IL_004A (48B)  -> ModDownload::DlClientListLoader

  站点 PageDownloadInstall::LoaderInit
    桩方法 : System.Void PCL.PageDownloadInstall/_Closure$__::_Lambda$__3-0()  (3 条指令，仅 throw)
    真实字段 : PCL.ModDownload::DlClientListLoader  token=0x0400040F
    异常处理块 : 0 个（等长替换不影响其偏移）
  ✓ PageDownloadInstall::LoaderInit  IL_001A 处 48B 已替换为 ldsfld DlClientListLoader + 43× nop

  已写出 : D:\Downloads\Plain Craft Launcher 2 (fixed).exe  (5,980,160 字节)

  —— 回读自检 ——
    ✓ PageDownloadInstall::LoaderInit  codeSize=94B(不变)  ldsfld=[DlClientListLoader]
    字节差异 : 41 处，区间 [0x2C0DA..0x2C109]，文件尺寸 5980160 -> 5980160
    结论 : PASS —— 原版游戏下载入口已接回
```

## 为什么是「等长」替换

补丁把 48 字节的替身构造段换成 `ldsfld`（5 字节）+ `nop × 43`，**总长度不变**。这样：

1. `codeSize` 不变 → 方法体之后的异常处理段（`MoreSects`）偏移不变
2. 所有分支目标偏移不变（工具会校验段内无段外跳入的分支）
3. 元数据表、文件尺寸、其余全部指令**一个字节都不动**

第 3 点尤其关键。PCL2 把 `PCLCS`、`MeloongCore`、`Newtonsoft.Json` 等依赖全部内嵌在 exe 的
4.2 MB 资源里（运行时才释放到磁盘）。如果用 Mono.Cecil 重写整个方法体，就会触发元数据重建，
然后立刻死在 `Failed to resolve assembly: 'PCLCS'` 上。等长字节手术完全绕开元数据重写，
Cecil 在这里**只用于读取和定位**。

## 兼容性

发现逻辑**不硬编码任何地址**，全程模式匹配，因此可用于同结构的其它版本 / 分支：

1. 全镜像扫描 `newobj LoaderTask\`2<…>` 调用点
2. 回溯其 `LoadDelegate` 实参的 `ldftn` 目标，判定是否为「只 `throw` 且含关键词」的桩
   （VB 编译器会缓存委托，桩常被包一层 `_Lambda$__R3-3` → 内部再 `Invoke` 到真桩，工具递归下钻）
3. 以最近的 `ldstr` 为段起点、`newobj` 结束处为段终点
4. 按 `LoaderTask<TIn,TOut>` 的泛型实参签名，在镜像里挑同签名的静态字段作为真实加载器
   （打分偏好 `DlClientListLoader` / 含 `Main` 的主加载器，压低具体源子加载器）
5. 校验分支与异常块边界 → 等长替换 → 回读自检

带**幂等检测**：对已修复的文件再跑一次，会提示「已直接引用 DlClientListLoader，无需修改」。

## 已知边界

- **不恢复整合包安装**。`ModModpack.ModpackInstall` 的两个重载是另一条独立的被拆路径
  （拖 zip 进主窗口 / 安装 `.mrpack`），和「原版游戏下载」不是一回事。需要的话可以加规则。
- **PCL 自动更新会覆盖回原版**，更新后重跑一次工具即可。
- 目标 exe 本来就没有数字签名，修改后不影响运行；若被 SmartScreen / 杀软拦，选「仍要运行」。
- 若启动器装在受保护目录（如 `C:\Program Files`），请手动「以管理员身份运行」本工具。

## 构建

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o ./artifacts
```

唯一 NuGet 依赖：[`Mono.Cecil` 0.11.6](https://www.nuget.org/packages/Mono.Cecil)（仅读取）。
推送 tag 会自动触发 [Release workflow](.github/workflows/release.yml) 构建并发布。

## License

MIT
