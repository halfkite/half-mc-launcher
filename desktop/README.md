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

## 当前进度（POC）

已实现并通过测试：

- 无边框窗口：标题栏拖动、边缘缩放、最小化/最大化/关闭，关闭前走与 Electron 相同的 `closeRequested` → `workspace.save` → `closeReady` 流程
- WebView2 虚拟主机 `https://app.local` 承载 `dist/`，注入与 Electron preload 相同签名的 `window.launcher`（`invoke` / `onEvent`）
- `data/config.json` 与 Electron 版完全兼容：保留未知字段，原子写入，损坏时备份
- 实例扫描：`versions` 根识别、`instanceId`（sha256 前 24 位）、加载器识别、损坏版本提示、隐藏实例、独立游戏目录判定
- 设置/外观/工作区/预设保存、目录登记与移除、离线账户（标准 OfflinePlayer UUID）、打开目录与网页
- 未接入的方法返回空数据或明确提示，而不是让界面崩溃

尚未接入（后续阶段）：游戏启动、加载器安装、资源下载、Java 检测、微软登录、Mod 管理、插件。

## 构建与打包

```text
npm run desktop        # 编译 C# 外壳并打包 dist-desktop/Half-Mc-Launcher-0.1.0-win-x64.zip
npm run desktop:test   # 运行实例扫描 / 配置读写的冒烟测试
```

要求：Windows + .NET Framework 4.8（系统自带）+ WebView2 运行时；构建需要 .NET SDK 8 或更高，
可用 `DOTNET_EXE` 指定 `dotnet.exe` 路径。

当前产物：解压后 2.39 MB，zip 0.72 MB。

## 约定

- 数据目录仍是可执行文件旁的 `data/`；`HALF_DATA_DIR`（兼容 `HAKF_DATA_DIR`）可覆盖，方便测试。
- 两个版本共用同一份 `data/config.json`，可以来回切换而不丢配置。
- `desktop.Smoke/` 是纯控制台测试工程，直接编译 `desktop/` 里的核心源文件，不需要图形界面。

## 下一阶段

1. 引入 `CmlLib.Core`（PCL 同款思路）：版本清单、libraries/natives/assets 下载、启动参数、Java 检测
2. 微软登录：WebView2 内嵌授权页或系统浏览器 + 本机回环回调，令牌用 DPAPI 加密
3. 加载器安装：Fabric/Quilt 走 JSON profile，Forge/NeoForge 调用 installer jar，OptiFine 解包
4. 资源下载：Modrinth / CurseForge
5. 单文件化（Costura 或 ILMerge）把 dll 收进 exe，进一步压缩到单文件约 1.4 MB
