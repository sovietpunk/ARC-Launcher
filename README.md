# ARC Launcher · Offline 本地启动测试

Windows Minecraft Java Edition 启动核心。沿用 Core / CLI 分离架构，GUI 保持空目录。支持明确选择 Offline 本地单人游戏/开发测试身份；Microsoft 正版认证接口保留，完整 OAuth 暂未接入。Offline 不具备正版在线认证能力，不会绕过依赖检查。

## 项目结构

```text
ARC.Launcher.slnx                 # Desktop 分组预留
Directory.Build.props            # net10.0、nullable、警告视为错误
global.json                      # 稳定版 SDK 10.0.402
NuGet.Config                     # 无远程包源、无第三方依赖
Core/
  MinecraftRoot.cs               # 统一路径配置、边界及链接检查
  LocalInstanceService.cs        # 第一阶段只读扫描、有效 gameDir
  Models.cs                      # 原模型、兼容启动接口
  VersionResolver.cs             # 完整 JSON、继承合并、平台规则、旧参数
  JavaService.cs                 # Java 发现、版本/架构检测
  Authentication.cs             # 独立认证接口、内存会话和登录状态
  LaunchIdentity.cs             # Offline/Microsoft 身份及独立参数绑定器
  LaunchPlanner.cs               # 依赖检查、classpath/module path、预览
  LaunchService.cs               # ILaunchService、认证门禁、启动编排
  NativeExtractor.cs             # 独立运行目录内安全解压
  GameProcessService.cs          # ArgumentList、日志、退出码、运行锁
  SecretRedactor.cs              # 参数/日志脱敏
CLI/Program.cs                   # 显示和命令路由
GUI/                             # 空目录，Git 不跟踪空目录
Tests/Program.cs                 # 原 20 项断言、合成子进程入口
Tests/Phase2Tests.cs              # 第二阶段测试
Tests/OfflineTests.cs             # 离线 UUID、身份绑定、依赖与模式边界
docs/phase-1-verification.md      # 第一阶段历史核验
docs/phase-2-development.md       # 第二阶段实现、验收和限制
```

Core 不依赖 CLI，无控制台交互。所有游戏路径经过 `MinecraftRoot`；只有 CLI 的组合入口确定项目下 `.minecraft`。CLI 从可执行文件目录向上定位 Solution，不接受外部游戏目录参数。未来 Desktop 可直接调用 Core；当前不支持将 CLI 单独部署到项目目录外。

## 构建与测试

第一阶段经用户同意将稳定版 SDK 安装至项目 `.tools/dotnet`，没有修改系统 SDK。本阶段没有安装依赖或下载游戏文件。

在项目根目录 PowerShell 执行：

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnet = Join-Path $PWD '.tools/dotnet/dotnet.exe'
& $dotnet build ARC.Launcher.slnx -m:1 -p:UseSharedCompilation=false
& $dotnet test ARC.Launcher.slnx --no-restore -m:1 -p:UseSharedCompilation=false
```

离线断言程序通过自定义 VSTest target 接入 `dotnet test`，失败返回非零码；不依赖 xUnit/MSTest，不提供 TRX/测试发现。第一阶段原 20 项与第二阶段原 67 项断言均保留，当前共 87 项通过。Windows junction 测试需要允许在 `.test-work` 创建目录联接；进程测试使用合成子进程。样本与缓存保留在被忽略的 `.test-work` 内。

## CLI 命令

| 命令 | 功能 |
|---|---|
| `help` | 帮助 |
| `list` / `scan` | 实例列表 / 扫描报告 |
| `info <实例名>` | 元数据和目录状态 |
| `path <实例名> <目录>` | 查询目录，不创建 |
| `open <实例名> <目录>` | 打开已存在目录，不创建 |
| `java list [--java <java.exe>]` | 检测 Java，支持检查明确指定的路径 |
| `launch check <实例名> [--java <java.exe>]` | 依赖、Java、认证准备检查 |
| `launch preview <实例名> [--java <java.exe>]` | 脱敏参数列表、路径与检查结果 |
| `launch run <实例名> [--java <java.exe>]` | 检查通过后启动并前台监督进程 |
| `launch status` | 只读查询 ARC 会话及退出码 |

`launch check/preview/run` 支持 `--offline --username <名称>`，两者必须同时明确指定。不传时仍使用 Microsoft 认证路径，尚未登录会阻塞。用户名限 1–16 位英文字母、数字或下划线；UUID 按 UTF-8 的 `OfflinePlayer:<名称>` 做 Java nameUUIDFromBytes 等价计算（MD5、RFC 4122 v3/variant 位），名称区分大小写，不使用随机 UUID。Offline 不会把 Authentication.IsAuthenticated 标成 true，报告另有 Identity.Kind 和 IsOnlineAuthenticated 区分。

目录类型：`game`、`mods`、`shaders`（或 `shaderpacks`）、`resourcepacks`、`saves`。全部支持 `--json`；info、scan、java、check、preview、status 始终输出 JSON。成功启动后的 `run --json` 输出逐行 JSON 事件：started、log、exited；准备阻塞时输出单个检查报告。日志事件可能先于 started 到达。退出码：0 成功，1 参数/解析/访问错误，2 准备未通过/目录不存在/未发现 Java，3 游戏退出码非零；游戏原始退出码保留在状态中。open 缺失目录返回 1。

```powershell
& $dotnet run --project CLI --no-build -- list
& $dotnet run --project CLI --no-build -- info 'Better MC [FORGE] BMC4'
& $dotnet run --project CLI --no-build -- path '最小的机械动力' mods
& $dotnet run --project CLI --no-build -- java list --json
& $dotnet run --project CLI --no-build -- launch check '1.20.1-Fabric 0.19.5' --json
& $dotnet run --project CLI --no-build -- launch preview '1.20.1-Forge_47.4.23' --json
# 替换为已安装的真实 Java 路径；不会自动安装 Java
& $dotnet run --project CLI --no-build -- launch check '1.20.1-Fabric 0.19.5' --java 'C:\Program Files\Java\jdk-17\bin\java.exe'
& $dotnet run --project CLI --no-build -- launch run '1.20.1-Fabric 0.19.5'
& $dotnet run --project CLI --no-build -- launch status --json

# 用户提供的已安装 Java，仅执行其 java.exe；游戏工作目录仍是测试副本
$java17 = Join-Path $env:APPDATA '.minecraft/runtime/java-runtime-beta/bin/java.exe'
& $dotnet run --project CLI --no-build -- launch check '1.20.1-Fabric 0.19.5' --offline --username ARC_Test --java $java17 --json
& $dotnet run --project CLI --no-build -- launch run '1.20.1-Fabric 0.19.5' --offline --username ARC_Test --java $java17
& $dotnet run --project CLI --no-build -- launch run '1.20.1-Forge_47.4.23' --offline --username ARC_Test --java $java17
```

## 启动与认证边界

完整读取版本 JSON，父子 JVM/game 参数拼接，子库按 Maven 组/名称/classifier/扩展名覆盖父版本。保留 Loader 配置，不套用固定 Fabric/Forge 参数模板；仅无 JVM 配置的旧版本添加最小 classpath/natives 参数。按 Windows、Java 架构、OS/feature 规则筛选。当前所有可选 feature 关闭，不启用 demo。旧版参数先分词再替换路径占位符，实际进程使用 `ArgumentList`。

Java 发现范围为 JAVA_HOME/JDK_HOME、PATH、常见厂商安装目录、用户 `.jdks`、JavaSoft 注册表、测试根目录 runtime（深度最多 5）。使用 `-XshowSettings:properties -version` 确认版本及架构，精确选择 JSON 要求的 Java 17/21 并匹配 OS 架构。其他 Java 主版本报不支持；没有 javaVersion 时按明确 Minecraft 版本推断，不可识别则阻塞。`--java` 仍需满足版本/架构要求。发现范围不保证涵盖所有任意位置的便携 Java，可明确指定路径。检测与启动均移除 JAVA_TOOL_OPTIONS、_JAVA_OPTIONS、JDK_JAVA_OPTIONS、CLASSPATH，避免外部环境悄悄注入参数。

没有现成的正版认证代码。`IAuthenticationService` 的未来实现必须完成正版登录、Minecraft 权益和 Profile 验证；`MinecraftSession` 仅在内存保存凭据，敏感属性不参与 JSON 序列化。Microsoft 路径仍绑定 `UnavailableAuthenticationService`，不接收用户提供的令牌或 UUID，运行前检查登录状态及会话到期时间。

`OfflineIdentity` 与 `MicrosoftLaunchIdentity` 通过 `IdentityArgumentBinder` 绑定同一套版本占位符，版本参数构建器不读取账号或执行 OAuth。Offline 以确定性 UUID、明确的未认证占位值（accessToken 为 0、session 为 -、clientId/xuid 为空、userType 为 legacy）启动，不冒充正版令牌。仅面向本地单人游戏和开发测试；拒绝版本参数自动连接服务器/Realms，不宣称能进入正版在线服务器。不会为了离线启动移除 Java、libraries、assets、natives、日志配置检查；日志配置若声明 SHA1，也必须匹配。

`check`/`preview`/`status` 不创建运行缓存。只有准备通过的明确 `run` 会创建测试根目录下 `.arc-launcher/v1`，含 ARC 所有权标记、运行锁、每次独立的 GUID 目录、natives 与不含凭据的 status.json。缓存所有权不符时拒绝写入，不覆盖用户目录。DLL 解压拒绝 ZIP 穿越和冲突，保留 exclude 规则，不复用已有 natives 输出目录。没有自动清理命令；确认会话退出后，可人工清理 ARC 所有的单次运行目录，锁文件和所有权标记建议保留。

同一规范化 gameDir 的 ARC 会话以文件锁互斥，锁元数据记录 PID 和进程开始时间；父 CLI 意外退出后，再次启动仍检查上次游戏 PID，避免仅因文件锁释放而重复启动。状态查询从缓存读取，结合 PID/开始时间识别进程；未被正常监督完成的退出显示 `ExitedUnknown`，不编造退出码。只保护 ARC 发起的会话，不识别其他启动器的游戏进程。

stdout/stderr 并行异步读取，已知凭据和常见令牌模式在交给 CLI 前脱敏，持久化到单次运行目录的 `launch.ndjson`，不持久化原始日志。状态记录 LogPath/LogCaptureState。预览与日志脱敏不改变实际传给游戏的 Loader 参数。run 默认前台等待退出；不提供守护进程或跨 CLI 的停止命令。

`Running` 只表示进程创建并存活。`MainMenuState=Unverified` 是独立字段，不会因窗口标题、退出码 0 或资源加载日志自动改成主菜单成功；实际主菜单需单独观察，验收结果记录在开发文档中。

## 当前验收与阻塞

用户提供的 Java 17.0.15 / 21.0.7 已检测为 x64；选择 --java 可以使用这些现有运行时，无需安装。用户已补齐 `assets/log_configs/client-1.12.xml`，SHA1 与两份版本 JSON 一致。Fabric、Forge 的 Offline 准备检查均通过：Fabric 已视觉确认进入主菜单；Forge 已创建进程并加载渲染资源，用户按 Esc 停止窗口自动化后，未继续确认其主菜单。详见 [Offline 验收记录](docs/offline-startup-verification.md)。

依赖验证检查当前平台所需的客户端、库、module path、native archive、日志配置、资产索引及资源对象是否存在且非空；日志配置额外校验 JSON 声明的 SHA1，尚未对全部库/资源校验哈希、大小或 JAR 内部内容。Forge 安装器全部产物及 Mod 兼容性仍需最终游戏运行验证。实例隔离继续使用第一阶段目录证据启发式规则，不读取其他启动器的私有配置或凭据。

不自动下载、修复、迁移、覆盖现有版本 JSON、Mod 或存档；未开发 GUI、下载源、整合包导入，也未执行 Git Commit。`.gitignore` 排除游戏副本、构建输出、SDK、测试样本和常见敏感配置。

开发顺序：先补齐用户提供的日志配置，在 Offline 模式依次验收 Fabric、Forge 主菜单及退出行为，再实现 Microsoft 正版认证。用户已选择本次不下载依赖；需要联网或进一步补齐时必须另行获得明确授权。
