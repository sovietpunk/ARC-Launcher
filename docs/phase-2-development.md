# 第二阶段：启动核心 · 2026-10-24

本文记录新增 Offline 支持之前的第二阶段验收状态。当前实现允许用户明确选择本地 Offline 测试身份，最新状态以 README 和 offline-startup-verification.md 为准。

## 实现与调用链

复用第一阶段 Solution、根配置、实例服务和启动请求模型；Core 不调用 Console，不依赖 CLI。新增 ILaunchService；原 IMinecraftLaunchService 由其继承并保留 LaunchAsync 兼容入口。

```text
CLI java list
  → JavaService.DiscoverAsync
    → 常见路径/JavaSoft 注册表/测试 runtime
    → InspectAsync → java -XshowSettings:properties -version

CLI launch check / preview
  → ILaunchService.CheckAsync / PreviewAsync
    → VersionResolver.Resolve（完整 JSON、父链、循环检测）
    → JavaService.DiscoverAsync（精确主版本与 OS 架构）
    → IAuthenticationService.GetStateAsync
    → LaunchPlanner.Build
      → LocalInstanceService.GetDirectory（有效 gameDir）
      → VersionRules（Windows、架构、特性、条件参数）
      → Maven/客户端/资产/日志/module path 存在性检查
      → 路径占位符展开 → 脱敏 LaunchPreview / LaunchCheck

CLI launch run
  → ILaunchService.RunAsync
    → 同上 PrepareAsync；CanLaunch=false 时抛结构化 LaunchBlockedException
    → IAuthenticationService.GetSessionAsync（必须有效正版会话）
    → 仅内存注入真实凭据
    → GameProcessService.Start
      → 所有权检查 + gameDir 文件锁 + 上次 PID/开始时间检查
      → 独立 GUID 运行目录
      → NativeExtractor.Extract（CreateNew、ZIP 边界和 exclude）
      → ProcessStartInfo.ArgumentList + WorkingDirectory
      → 并发 stdout/stderr → SecretRedactor → CLI
      → WaitForExitAsync → status.json + 原始退出码 → 释放锁

CLI launch status → GameProcessService.Status（只读，不创建目录）
```

原始启动参数保留在 internal PreparedLaunch 中，不暴露给预览 JSON；预览脱敏不影响实际参数。凭据不写入锁、状态、日志缓存或磁盘。MinecraftSession 覆盖 ToString 并忽略敏感 JSON 属性。CLI 只组装 UnavailableAuthenticationService；没有读取其他启动器账号配置，也没有离线身份或令牌输入通道。

未来认证实现应独立承担：应用身份与用户授权、Microsoft/Xbox/Minecraft 登录、权益与 Profile 验证、刷新与过期管理。GetStateAsync 返回可公开状态；GetSessionAsync 只在需要实际启动时返回短期内存凭据。认证模块不得把 access/refresh token 放入状态文本或异常文本。

版本解析保留未知 JSON 字段。父子 game/jvm 数组按顺序拼接，Maven 同一 artifact/classifier 的子版本覆盖父版本；旧式 minecraftArguments 支持引号和空值，并在占位符替换前分词。旧参数与现代参数混合继承有测试覆盖。缺失父版本、循环、越界路径及未解析占位符不能继续运行。

Forge 本地 JSON 中的 `-p`、`--add-modules`、`--add-opens`、`--add-exports`、ignoreList、mergeModules、libraryDirectory、launchTarget 和 FML 版本参数均被保留；Fabric 的 KnotClient 和 FabricMcEmu 参数保留。module path 从这些原始 JVM 参数解析并验证，没有另造 Loader 启动模板。BootstrapLauncher 对 classpath/模块的使用可参见其[上游源码](https://github.com/McModLauncher/bootstraplauncher/blob/main/src/main/java/cpw/mods/bootstraplauncher/BootstrapLauncher.java)。

## 验收结果

稳定版 SDK 10.0.402；未升级 SDK、无新增 PackageReference、无联网依赖安装。构建命令与 README 一致：单节点、关闭共享编译。build 成功，0 警告/0 错误。

`dotnet test` 使用原有离线断言方式：原 20 项继续通过；新增 47 项，总 67 项通过。测试覆盖完整继承、父客户端 JAR、Maven 覆盖、缺失父/循环、旧/现代混合参数、规则顺序、架构、Java 版本解析、classpath/module path、认证门禁、未知占位符、DLL 解压/exclude/ZIP 穿越、缓存所有权、参数/日志脱敏、中文/空格/方括号 ArgumentList、工作目录、异步双日志流、退出码、重复启动和退出后的再次启动。目录联接测试在授权的沙箱外执行，仅写 `.test-work`。

实际 Minecraft 副本验收：

| 检查项 | Fabric 1.20.1 | Forge 1.20.1 |
|---|---|---|
| 实例名 | 1.20.1-Fabric 0.19.5 | 1.20.1-Forge_47.4.23 |
| mainClass | net.fabricmc.loader.impl.launch.knot.KnotClient | cpw.mods.bootstraplauncher.BootstrapLauncher |
| RequiredJavaMajor | 17 | 17 |
| 检查文件引用数 | 3636 | 3657 |
| module path 项数 | 0 | 8，均存在 |
| Loader 参数 | FabricMcEmu 保留 | forgeclient、add-opens 等保留 |
| preview / run 退出码 | 2 / 2 | 2 / 2 |
| CanLaunch | false | false |

两者均返回 JavaMissing、MissingDependency（assets/log_configs/client-1.12.xml）和 AuthenticationRequired。java list 未在已支持发现范围内找到 Java；可通过 --java 提供任意已安装路径，但仍验证主版本和架构，不会接受不合适的 Java。没有自动安装 Java、补齐日志配置或调用认证网络接口。

实际 `launch run` 已执行并在准备阶段正确阻止；`launch status` 返回空会话。真实测试根下没有 `.arc-launcher` 缓存，GUI 仍为空。未实际启动 Minecraft，进程服务的合成测试不能替代后续真实游戏验收。

## 已知限制与下一步

本阶段仅 Windows，精确支持 Java 17/21。Java 发现是明确范围搜索，不扫描磁盘或正式游戏目录。依赖检查为存在且非空，不做 SHA1、预期大小、所有 ZIP/JAR 结构或 Mod 兼容性验证；natives ZIP 的解压安全在实际 run 时检查。日志配置按 Mojang 常见 assets/log_configs 布局查找，不搜索其他启动器缓存。

实例隔离采用第一阶段启发式规则。可选 features 当前均关闭；未提供自定义分辨率、quick play、demo 参数入口。不接受外部 JVM @argfile。版本 JSON 属于可执行启动配置，不将任意恶意 JVM 插件视为隔离沙箱；本服务的路径边界保护文件准备操作，Java 游戏代码本身具有用户进程权限。

运行锁只保护 ARC 启动的同一规范化 gameDir，目录联接被拒绝；不查找其他启动器进程。CLI 前台监督期间可记录退出码；若 CLI 被强制终止，状态查询仅识别存活或 ExitedUnknown。父进程在创建子进程后、写入 PID 之前被强制杀死的极小窗口还需要未来独立监督进程解决。没有自动删除、缓存清理或停止命令。

缓存只在通过准备检查的明确 run 内创建，所有权标记和 CreateNew 防止覆盖未知缓存和已存在 natives 目录。stdout/stderr 在行边界脱敏后交给调用者，不保存原始输出；操作系统进程命令行仍承载 Minecraft 必需参数，不属于应用预览/日志。

后续真实启动验收依次需要：用户提供合适 Java、确认可信日志配置补齐方案、实现并接入正版认证。然后检查 Fabric/Forge 窗口、菜单、日志与退出行为。任何依赖下载或补齐写入另行取得用户确认。当前没有 Git Commit。
