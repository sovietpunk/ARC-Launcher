# Offline 启动验收 · 2026-10-24/25（Asia/Shanghai）

## 实现

保留 Microsoft 的 IAuthenticationService/MinecraftSession，未实现 OAuth，未读取其他启动器账号或私有凭据。新增 LaunchIdentity、OfflineIdentity、MicrosoftLaunchIdentity、IdentityArgumentBinder；版本/JVM 参数构建与账号身份分离。LaunchRequest 通过可选 OfflineIdentity 明确选择本地测试模式，缺省仍走 Microsoft 身份检查。

UUID 为 UTF-8 编码 `OfflinePlayer:<username>` 的 MD5，按 Java UUID.nameUUIDFromBytes 设置 RFC 4122 v3/variant 位，再以正常字节顺序输出 32 位十六进制；避免 .NET Guid(byte[]) 的混合端序。参考[Prism Launcher 上游实现](https://github.com/PrismLauncher/PrismLauncher/blob/develop/launcher/minecraft/auth/MinecraftAccount.cpp)，并用固定参考向量测试。用户名区分大小写，限 1–16 位英文字母、数字或下划线。

Offline 的参数是明确未认证的协议占位值：accessToken=0、session=-、clientId/xuid 为空、userType=legacy。Authentication.IsAuthenticated 与 Identity.IsOnlineAuthenticated 均为 false；不会冒充正版凭据。只用于本地单人和开发测试，阻止 JSON 参数自动连接多人服务器/Realms；这不是网络隔离功能，游戏/Mod 自身仍可能访问在线服务。日志中出现未认证 HTTP 401 不代表拥有在线认证能力，也不必然导致单人主菜单失败。

CLI 新增 `launch run <实例名> --offline --username <名称>`，check/preview 同样支持这组参数；两项必须同时出现。Java、库、资产、native、module path 和日志检查均保留。日志配置声明 SHA1 时进行校验，不下载、不忽略缺失文件。stdout/stderr 脱敏后实时输出并记录在单次运行目录的 launch.ndjson。

## 环境与依赖

用户提供的既有 Java 路径经执行 -XshowSettings:properties -version 验证：

- java-runtime-beta：17.0.15，amd64；本次两次游戏运行均使用它。
- java-runtime-delta：21.0.7，amd64；未用于 1.20.1 实际运行。

仅执行用户指定的外部 Java 可执行文件；游戏工作目录、参数路径和生成缓存均位于测试 `.minecraft`。没有搜索或修改原始游戏目录、复制账号凭据、安装或下载 Java。

用户自行将 client-1.12.xml 放入测试 assets/log_configs 后，实测 SHA1 为 `bd65e7d2e3c237be76cfbef4c2405033d7f91521`，匹配 Fabric/Forge JSON。两者 Check.CanLaunch=true，Issues=[]，仍明确标记 Offline/未在线认证。

## 实际运行结果

| 项目 | Fabric 1.20.1 | Forge 1.20.1 |
|---|---|---|
| 实例 | 1.20.1-Fabric 0.19.5 | 1.20.1-Forge_47.4.23 |
| 启动时间（UTC+8） | 10 月 24 日 23:58:19 | 10 月 25 日 00:05:13 |
| RunId | 765e7d35c011461c9c556bf8bb9797d0 | 0244c1188c9a43ab88c24dab7261dc8a |
| PID | 42652 | 13752 |
| 检查文件引用 | 3636 | 3657 |
| Offline 用户名 | ARC_Test | ARC_Test |
| Offline UUID | c5367cf7896e3fd28047fb1b58c2a7e1 | c5367cf7896e3fd28047fb1b58c2a7e1 |
| 进程创建 | 成功 | 成功 |
| 主菜单视觉确认 | **成功** | **未确认** |
| 退出码验收 | 未退出，尚无退出码 | 未退出，尚无退出码 |

Fabric：使用 computer-use 技能观察实际窗口，主菜单可见“单人游戏”“多人游戏”“Minecraft Realms”“选项”“退出游戏”；窗口及左下角显示 Minecraft 1.20.1/Fabric。截图保存在项目 `.tools/verification/fabric-1.20.1-menu.jpg`（被 Git 忽略）。没有进入存档或服务器。

Forge：ModLauncher 确认 Java 17.0.15、Windows amd64，并以 forgeclient/FML 47.4.23 启动；日志继续出现纹理 atlas 与 shader 初始化。它证明进程和渲染资源加载，不足以证明主菜单已经可见。用户物理 Esc 停止 Computer Use 后，立即停止窗口操作，没有继续点击、截图或关闭游戏。

最后只读状态文件中两个进程都记录为 Running/ExitCode=null。没有强制终止任何真实游戏。进程服务的 MainMenuState 仍为 Unverified，因为服务只监督进程与日志，不根据日志猜测菜单；Fabric 的视觉成功是本次外部验收记录，二者语义分开。

脱敏启动日志位于测试根下：

```text
.arc-launcher/v1/runs/765e7d35c011461c9c556bf8bb9797d0/launch.ndjson
.arc-launcher/v1/runs/0244c1188c9a43ab88c24dab7261dc8a/launch.ndjson
```

游戏自身可在明确 run 的测试实例中生成 logs、缓存与配置；ARC 没有修改现有版本 JSON、Mod 或存档，没有下载依赖。用户提供的日志文件也未被覆盖。GUI 保持空目录，未执行 Commit。

## 构建与回归

dotnet build 成功，0 警告/0 错误。最终 dotnet test 共 **87 项断言通过**；保留原有 67 项，新增确定性 UUID、大小写和字节顺序、非法名称、身份参数绑定、Microsoft/Offline 模式分离、离线不访问认证服务、保留 Java/库/资产/native/日志检查、拒绝自动多人连接、阻塞无副作用、脱敏日志持久化和进程不等于主菜单成功等验证。测试仍无第三方 NuGet 依赖。

## 调用链与复现

```text
CLI --offline --username
  → OfflineIdentity（确定性 UUID、Offline 标记）
  → LaunchService.PrepareAsync
    → JavaService + VersionResolver + LaunchPlanner（全部依赖检查）
  → IdentityArgumentBinder.Bind（不依赖 OAuth 或具体版本）
  → NativeExtractor + GameProcessService
    → ProcessStartInfo.ArgumentList / 实例 gameDir
    → 异步 stdout/stderr → SecretRedactor → launch.ndjson / CLI
```

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/cli-home'
$dotnet = Join-Path $PWD '.tools/dotnet/dotnet.exe'
$java17 = Join-Path $env:APPDATA '.minecraft/runtime/java-runtime-beta/bin/java.exe'
& $dotnet run --project CLI --no-build -- launch run '1.20.1-Fabric 0.19.5' --offline --username ARC_Test --java $java17
& $dotnet run --project CLI --no-build -- launch run '1.20.1-Forge_47.4.23' --offline --username ARC_Test --java $java17
& $dotnet run --project CLI --no-build -- launch status --json
```

同一 gameDir 正在运行时重复启动会被阻止。后续需在用户愿意继续窗口测试时确认 Forge 主菜单，并正常退出两者以验收真实退出码；然后再开展 Microsoft 正版认证模块。
