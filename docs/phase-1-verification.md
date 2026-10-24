# 第一阶段核验 · 2026-10-24

测试根目录：项目下 `.minecraft`。未访问或修改原始游戏目录；没有自动删除、迁移或覆盖游戏文件。源码目录原有 Core、CLI、GUI 直接复用。

| 实例 | Minecraft | 加载器 | 布局 |
|---|---|---|---|
| 1.20.1-Fabric 0.19.5 | 1.20.1 | Fabric | 隔离 |
| 1.20.1-Forge_47.4.23 | 1.20.1 | Forge | 隔离 |
| 1.21.11-Fabric 0.19.5 | 1.21.11 | Fabric | 隔离 |
| 最小的机械动力 | 1.20.1 | Forge | 隔离 |
| Better MC [FORGE] BMC4 | 1.20.1 | Forge | 隔离 |

基础结构完整：assets、libraries、versions 均存在；五个实例同名 JSON 可解析、JAR 均存在、对应 assets/indexes/5.json 或 29.json 存在。人工只读脚本按当前 Windows 的 OS allow/disallow 规则粗筛 libraries.downloads.artifact，五者缺失数均为 0。未筛选系统规则时缺失的 24/32 项属于其他平台声明，不能直接视为 Windows 缺失依赖。

此结果没有校验所有资产对象的哈希、JAR 内容、native classifiers、特性规则、Java 或 Forge 安装产物，不保证完整可启动。无下载或修复行为。

1.20.1-Forge_47.4.23 没有 saves、shaderpacks；path 返回不存在，不创建目录。其余四个实例的 mods、shaderpacks、resourcepacks、saves 均存在。

验证：Solution 构建 0 警告 / 0 错误；dotnet test 执行离线测试程序，20 项断言通过。实际 CLI 列出五个实例，并验证 info、中文 Mods 路径、方括号实例目录及缺失 Saves 的状态。open 已成功请求系统打开 Better MC 的 Mods 文件夹（退出 0）；打开普通 Forge 缺失的 Saves 时退出 1，再次确认该目录仍不存在。

安全：已创建 `.gitignore`，覆盖 `.minecraft/`、bin/obj、项目内 SDK、测试样本和常见敏感配置。当前工作目录尚不是 Git 仓库，未执行 git init、add、commit 或 push。
