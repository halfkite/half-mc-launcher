# Half Mc Launcher · C# 桌面外壳（重构分支）

这个目录是 `refactor/desktop` 分支的核心：用 **.NET Framework 4.8 + WebView2** 替换 Electron 外壳，
继续复用 `src/` 里的 Vue 3 界面，目标是把发行包从 88 MB 降到几 MB。

## 为什么是这套组合

| 方案 | 包体 | 依赖 | 结论 |
|---|---|---|---|
| Electron（现状） | 88 MB | 无 | 大，但开箱即用 |
| **.NET Framework 4.8 + WebView2** | **2.4 MB** | Win10 1903+ 自带 .NET FW 4.8，Win10 1803+ 自带 WebView2 | 采用 |
| Tauri / Rust | ~12 MB | 需 MSVC 工具链 | 本机无编译器，且 XMCL 无 Rust 替代 |
| Java / JavaFX | 30 MB+ | 需要 JRE | 最重 |

界面（`src/`、`shared/`）完全不动；宿主只负责窗口、WebView2、数据路径和 IPC 桥。

## 当前进度

已实现并通过测试：

- 无边框窗口：标题栏拖动、边缘缩放、最小化/最大化/关闭，关闭前走与 Electron 相同的 `closeRequested` → `workspace.save` → `closeReady` 流程
- WebView2 虚拟主机 `https://app.local` 承载 `dist/`，注入与 Electron preload 相同签名的 `window.launcher`（`invoke` / `onEvent`）
- `data/config.json` 与 Electron 版完全兼容：保留未知字段，原子写入，损坏时备份
- 实例扫描：`versions` 根识别、`instanceId`（sha256 前 24 位）、加载器识别、损坏版本提示、隐藏实例、独立游戏目录判定
- 设置/外观/工作区/预设保存、目录登记与移除、离线账户（标准 OfflinePlayer UUID）、打开目录与网页
- **Java 检测**：PATH、`JAVA_HOME`、常见安装目录与用户搜索目录，`-XshowSettings:properties` 读取版本/架构
- **启动游戏**：CmlLib.Core 构建启动参数（离线会话、内存、Java、natives），进程输出接入日志与任务中心，支持结束进程
- **下载页**：Mojang 版本清单、原版安装、任务进度与取消；游戏版本选定后折叠为一行，下载页默认先显示 Mod
- **加载器**：Fabric / Quilt / Forge / NeoForge / OptiFine / LiteLoader 版本列表与图标；安装支持前四种（Forge/NeoForge 调用官方安装器，需要 Java）
- **OptiFine 组件**：任意加载器安装时可勾选，安装在流程末尾调用 OptiFine 官方安装器（图形界面，需要 Java）
- **资源下载**：Modrinth 与 CurseForge 搜索、版本列表、依赖解析、哈希校验安装；CurseForge 密钥用 DPAPI 加密保存
- **Mod 管理**：读取 JAR 描述与图标、导入、启用/禁用、移到回收站
- **目录选择**：Windows 现代 `IFileOpenDialog`（FOS_PICKFOLDERS），默认打开程序所在目录
- 未处理异常写入 `data/logs/crash.log` 并提示，窗口不再直接消失
- 默认主题为浅色 + 淡蓝（`#74b5ff`）

尚未接入：LiteLoader 安装、微软登录、实例删除/复制、插件系统。

无界面自检命令（结果写在 `data/` 下）：

```text
Half Mc Launcher.exe --selftest          # 单文件资源与依赖解析
Half Mc Launcher.exe --check-versions    # Mojang 版本清单
Half Mc Launcher.exe --check-downloads   # Modrinth 搜索 / 依赖 / 下载 / 模组图标
```

## 构建与打包

```text
npm run desktop         # 编译并输出单文件 dist-desktop/Half-Mc-Launcher-0.1.0-r6.exe
npm run desktop:launch  # 同上，构建完成后直接启动新版本
npm run desktop:test    # 运行实例扫描 / 配置读写 / Java 检测 / 启动参数构建的冒烟测试
```

要求：Windows + .NET Framework 4.8（系统自带）+ WebView2 运行时；构建需要 .NET SDK 8 或更高，
可用 `DOTNET_EXE` 指定 `dotnet.exe` 路径。

当前产物：**单文件 2.14 MB**，没有附带 DLL。分发时只发这一个 exe，首次运行会在旁边生成 `data/`
（配置、日志、WebView2 用户数据、解压出来的前端资源与 `WebView2Loader.dll`）。

单文件是怎么来的：

- 托管依赖（CmlLib、Newtonsoft.Json、System.Text.Json 等 22 个程序集）由 Costura.Fody 压缩后嵌入 exe，
  运行时通过程序集解析钩子加载；
- 前端 `dist/` 以 `web/...` 资源嵌入，启动时解压到 `data/web`，WebView2 用虚拟主机映射该目录；
- `WebView2Loader.dll` 是原生 DLL，同样嵌入并在启动时解压到 `data/native`，先 `SetDllDirectory` 再加载；
- DPI 感知改为程序内调用（原来依赖 `App.config`，单文件不再带 `.config`）。

无界面自检：`"Half Mc Launcher.exe" --selftest`，结果写入 `data/selftest.txt`，用于确认真实发行包里
资源与依赖都能解析（本次已验证：native/web/newtonsoft/cmllib/webview2 全部为 True）。

冒烟测试支持两个可选环境变量：`HALF_TEST_JAVA` 指定真实的 java.exe，`HALF_TEST_MINECRAFT` 指定真实
`.minecraft`（默认使用内置的合成版本描述，不触碰真实游戏目录）。

## 约定

- 数据目录仍是可执行文件旁的 `data/`；`HALF_DATA_DIR`（兼容 `HAKF_DATA_DIR`）可覆盖，方便测试。
- 两个版本共用同一份 `data/config.json`，可以来回切换而不丢配置。
- `desktop.Smoke/` 是纯控制台测试工程，直接编译 `desktop/` 里的核心源文件，不需要图形界面。

## 下一阶段

1. 微软登录：WebView2 内嵌授权页或系统浏览器 + 本机回环回调，令牌用 DPAPI 加密
2. 加载器安装：Fabric/Quilt 走 JSON profile，Forge/NeoForge 调用 installer jar，OptiFine 解包
3. 资源下载：Modrinth / CurseForge
4. 下载页：Mojang 版本清单与安装任务
