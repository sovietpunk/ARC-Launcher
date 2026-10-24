# ARC Launcher

**v0.1.0 · Windows · 命令行 · Offline 本地测试**

ARC Launcher 是一个面向 Minecraft Java Edition 的启动器项目。当前版本提供命令行工具，可识别本地游戏实例、检查启动所需文件，并使用明确的 Offline 测试身份启动游戏。

目前没有图形界面，也没有 Microsoft 正版在线登录。你需要自行准备完整的游戏副本和合适的 Java；ARC Launcher 不会自动下载游戏、Mod、资源或 Java。

## 已有功能

- 扫描本地版本，查看实例信息和游戏目录。
- 查询或打开 Mods、光影包、资源包和存档文件夹；文件夹不存在时不会自动创建。
- 检测 Java 的版本和架构，也可明确指定 `java.exe`。
- 检查客户端、libraries、assets、natives、模块路径和日志配置；缺少依赖时阻止启动。
- 解析版本继承及启动参数，保留 Fabric、Forge 的 Loader 配置。
- 使用 Offline 名称和确定性的离线 UUID 启动本地游戏。
- 查看启动参数预览、进程状态和退出码，捕获并保存脱敏日志。
- 阻止 ARC Launcher 对同一游戏目录重复启动；支持 JSON 输出。

### 已测试的游戏范围

以下为当前人工实测结果，包含使用者补充确认的进度：

| 游戏环境 | 已确认结果 |
| --- | --- |
| Minecraft 1.20.1 + Fabric | 已进入单人世界 |
| Minecraft 1.20.1 + Forge（Better MC） | 已进入 Better MC 主菜单 |

这些结果不代表所有 Minecraft 版本、Loader 版本或整合包均经过测试，也不代表 Better MC 已完成进入世界及长期游玩验证。历史自动化测试记录包含 87 项通过的断言，不能替代具体游戏环境的人工测试。

## 系统要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | 当前实现面向 Windows，已在 Windows 11 x64 环境测试 |
| 命令终端 | Windows PowerShell 或 PowerShell |
| 游戏文件 | 项目根目录下独立、完整的 `.minecraft` 游戏副本 |
| Java | 当前启动检查支持 Java 17 或 Java 21，主版本须符合游戏 JSON 要求，架构须与系统匹配；已测试的 1.20.1 环境使用 Java 17 |
| 源码构建工具 | `global.json` 指定的 .NET SDK，当前为 10.0.402，允许同一特性带的补丁更新 |

.NET SDK 用于构建 ARC Launcher，Java 用于运行 Minecraft，两者都需要。仅安装 .NET 运行时不能完成源码构建。其他 Windows 环境及 ARM64 尚未完成同等程度的实测，Linux 和 macOS 当前不支持。

## 安装与开始使用

### 当前可用：从源码运行

1. 从本仓库获取源码，解压或克隆到自己的工作目录。
2. 安装上述 .NET SDK，并准备已安装的合适 Java。
3. 在项目根目录准备独立的完整 `.minecraft` 副本，保留原始游戏目录。ARC Launcher 不会替你安装或补齐游戏文件。
4. 在包含 `ARC.Launcher.slnx` 的目录打开 PowerShell，执行：

```powershell
dotnet --list-sdks
dotnet build ARC.Launcher.slnx -m:1 -p:UseSharedCompilation=false
dotnet run --project CLI --no-build -- help
dotnet run --project CLI --no-build -- list
dotnet run --project CLI --no-build -- java list
```

这些命令使用系统中的 `dotnet`，不依赖开发者本机的工具目录。SDK 版本不符合要求时，请先按 `global.json` 配置环境。

游戏目录的基本结构如下；这只是布局说明，不是创建空文件夹后即可运行的安装包：

```text
项目根目录/
├─ ARC.Launcher.slnx
├─ Core/
├─ CLI/
└─ .minecraft/
   ├─ assets/
   ├─ libraries/
   └─ versions/
      └─ 实例名称/
         ├─ 实例名称.json
         └─ 实例名称.jar
```

继承版本的客户端 JAR 可能来自父版本。具体所需文件以 `launch check` 的结果为准。

### 选择实例与 Java

先查看 `list` 的输出，再执行下面这段代码。按提示输入完整实例名称和已有 `java.exe` 路径；路径含空格、中文或方括号时无需自行拆分。

```powershell
$instance = Read-Host '请输入 list 中的完整实例名称'
$java = Read-Host '请输入已有 java.exe 的完整路径，不要额外输入引号'

dotnet run --project CLI --no-build -- info "$instance"
dotnet run --project CLI --no-build -- java list --java "$java"
dotnet run --project CLI --no-build -- launch check "$instance" --offline --username Player --java "$java"
```

`Player` 是示例名称，可改为自己的本地测试名称：长度为 1–16 个字符，仅允许英文字母、数字和下划线。名称区分大小写；同一名称会得到相同离线 UUID，更换名称会改变离线身份。

### 预览与启动游戏

确认检查结果中的 `CanLaunch` 为 `true`、`Issues` 为空后，再执行：

```powershell
dotnet run --project CLI --no-build -- launch preview "$instance" --offline --username Player --java "$java"
dotnet run --project CLI --no-build -- launch run "$instance" --offline --username Player --java "$java"
```

这两条命令沿用上一节的 `$instance` 和 `$java`。`run` 会再次检查依赖，并在当前终端持续显示日志，等待游戏退出；游戏工作目录为该实例的有效游戏目录。请从游戏菜单正常退出以便记录退出码。

Offline 仅用于本地单人游戏与开发测试，**不提供 Microsoft 正版在线认证能力**。它不会验证或取得正版账号权益，也不能作为进入要求正版认证的服务器或 Realms 的登录方式。不加 `--offline --username` 时会走尚未接入的 Microsoft 认证路径并报告阻塞。

### 查询状态与打开文件夹

下面的状态查询可在另一个位于项目根目录的 PowerShell 中执行：

```powershell
dotnet run --project CLI --no-build -- launch status --json
```

以下命令沿用前面设置的 `$instance`：

```powershell
dotnet run --project CLI --no-build -- path "$instance" mods
dotnet run --project CLI --no-build -- open "$instance" mods
dotnet run --project CLI --no-build -- open "$instance" shaders
dotnet run --project CLI --no-build -- open "$instance" resourcepacks
dotnet run --project CLI --no-build -- open "$instance" saves
```

`shaders` 对应 `shaderpacks` 文件夹。打开命令不会创建缺失目录。

### 未来发布版：与源码运行区分

当前使用方式以源码运行流程为准；源码尚未提供可直接脱离项目目录使用的独立安装器或发布包约定。不要把编译输出中的 EXE 单独复制出去当作完整安装包。

如果未来发布包提供 `ARC.Launcher.CLI.exe`，命令形态预计如下。**这是未来发布版示意，当前不作为 v0.1.0 的安装步骤，也不表示已有可下载发布包。**

```powershell
# 仅适用于未来明确提供该可执行文件的发布包
.\ARC.Launcher.CLI.exe help
.\ARC.Launcher.CLI.exe list
```

未来发布包的目录布局、是否内置 .NET 运行时及安装步骤，以对应版本的发布说明为准。源码运行命令中的 `dotnet run --project CLI --no-build --` 不能直接套用到没有源码的发布目录。

## 命令速查

| 命令 | 用途 |
| --- | --- |
| `help` | 显示帮助 |
| `list` / `scan` | 列出实例 / 输出扫描报告 |
| `info <实例名>` | 查看实例详情及目录状态 |
| `path <实例名> <目录>` | 查询目录路径 |
| `open <实例名> <目录>` | 打开已有目录 |
| `java list [--java <路径>]` | 检测 Java |
| `launch check <实例名>` | 检查启动条件 |
| `launch preview <实例名>` | 预览脱敏参数 |
| `launch run <实例名>` | 启动并监督游戏进程 |
| `launch status` | 查看 ARC Launcher 会话状态 |

目录类型为 `game`、`mods`、`shaders`（或 `shaderpacks`）、`resourcepacks`、`saves`。`launch check/preview/run` 可加 `--java <路径>`；当前本地测试需同时加 `--offline --username <名称>`。

所有命令支持 `--json`。部分查询本来就输出 JSON；`run --json` 启动成功后输出逐行的进程、日志和退出事件，适合脚本读取。

## 常见问题

### 为什么找不到游戏实例？

当前程序只使用项目根目录下的 `.minecraft`，不会自动接管其他启动器的游戏目录，也没有自定义游戏根目录的 CLI 参数。检查游戏副本的位置和 `versions` 内的同名版本 JSON；运行 `scan` 查看诊断信息。

### 为什么找不到 Java，或者提示版本不匹配？

Java 检测只搜索环境变量、常见安装位置等已支持范围，未必找到任意位置的便携运行时。使用 `java list --java` 检查已有可执行文件，再用 `--java` 指定；Java 主版本和架构仍须匹配要求，不会因手动指定而跳过检查。

### 为什么离线启动仍会报告缺少依赖？

Offline 只改变测试身份，不补齐或跳过游戏文件。根据 `Issues` 中的路径检查原有完整安装，并在独立游戏副本中准备所需文件。日志配置（例如 `client-1.12.xml`）缺失或其声明的 SHA1 不匹配也会阻止启动；删除 JSON 中的配置并不能作为完整安装验证。

### 出现 AuthenticationRequired 怎么办？

Microsoft 正版在线认证尚未提供。仅做本地单人测试时，同时指定 `--offline --username Player`；不要把访问令牌或其他启动器的账号配置粘贴给程序。

### 显示“进程创建成功”，是否表示已经进入游戏？

不一定。`Running` 表示进程存活，主菜单或单人世界必须另外观察确认。`MainMenuState=Unverified` 不会因为资源加载日志或退出码为 0 就自动变成成功；本 README 的实测结果来自人工确认。

### 去哪里查看日志？

启动时终端会显示日志位置。ARC Launcher 的脱敏日志保存在测试游戏根目录的 `.arc-launcher/v1/runs/<运行编号>/launch.ndjson`，同目录的 `status.json` 记录进程状态和退出码。`ExitedUnknown` 表示无法取得最终退出码，不等于正常退出。

### 提示同一个游戏目录已经在运行怎么办？

先检查已有游戏窗口及 `launch status`，正常退出原会话后再启动。重复启动保护只针对 ARC Launcher 的会话，不会识别所有其他启动器的游戏进程。

## 已知限制

- 当前只有 CLI，没有 GUI；不提供 Microsoft 登录、游戏下载、依赖自动修复、下载源管理或整合包导入。
- 仅上述 Minecraft 1.20.1 环境有明确的人工测试结论，不保证任意版本、Mod 组合或整合包可用。
- 实例隔离目录通过现有文件夹推断，未读取其他启动器的隔离配置；启动前可用 `info` 确认路径。
- 不接受外部 JVM 参数文件；未提供自定义分辨率或快捷加入游戏的参数入口。
- 依赖检查主要验证文件存在且非空，额外检查已声明的日志配置 SHA1；尚未对所有资源和库做完整哈希、大小及内容校验。
- 没有跨终端停止游戏、自动清理缓存或独立后台监督服务。终端意外结束可能影响退出码记录。
- 扫描、查询和预览不创建游戏运行目录；明确执行 `run` 后，Minecraft 和 Mod 会在测试副本中产生配置、日志及缓存。Offline 不等于禁止游戏或 Mod 访问网络。

请在独立游戏副本中测试并保留重要数据的备份。仓库不包含 Minecraft 游戏文件、存档或账号凭据。
