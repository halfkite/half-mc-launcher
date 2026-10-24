# Half Mc Launcher

面向 Windows 的 Minecraft Java 启动器。基于 **.NET Framework 4.8 + WebView2 + Vue 3**，发行形态是约 2.2 MB 的单文件 exe，依赖、前端资源与 WebView2Loader 全部内嵌。

当前版本：**0.1.0-r110**（开发中，尚未签名）

- 桌面外壳与构建说明：[desktop/README.md](desktop/README.md)
- 代码签名与发布：[docs/CODE-SIGNING.md](docs/CODE-SIGNING.md)
- 许可证：[MIT](LICENSE)

## 功能

- **实例**：多目录扫描、分组、收藏、保护、隔离游戏目录、每实例内存 / Java 覆盖
- **下载**：原版、Fabric / Forge / NeoForge / Quilt / OptiFine 安装；Modrinth / CurseForge 资源搜索与依赖安装；BMCLAPI 镜像与并发 / 重试 / 超时设置
- **内容管理**：Mod、资源包、光影、存档、截图；存档 ZIP 备份与还原；资源包 `pack.mcmeta` 与图标
- **整合包**：`halfpack` 导出，`.mrpack` / CurseForge manifest / ZIP 导入
- **Java**：本机检测、Adoptium Temurin 8 / 17 / 21 自动下载
- **诊断**：崩溃日志分析、模组依赖检查、一体式诊断包导出
- **服务器**：`servers.dat` 查看 / 编辑，Server List Ping 显示人数、版本、MOTD、延迟
- **账户与外观**：微软 / 离线账户、皮肤与披风预览、截图墙、主题色与底色自定义
- **模组辅助**：Modrinth 更新检查、机器翻译生成中文资源包、MC百科中文名查询
- **启动入口**：快捷方式 / 批处理导出、拖拽导入、`--instance` 参数启动
- **工作区**：多栏布局、标签页、预设、全局搜索与命令面板

## 运行要求

- Windows 10 1803+ / Windows 11
- .NET Framework 4.8（系统自带）
- WebView2 Runtime（Win11 自带；Win10 可通过 Evergreen 安装器安装）

程序必须放在可写目录。配置、日志、账户缓存、Java 运行时与下载内容都在 exe 旁的 `data/`，不写入用户目录。

## 构建

需要 Node.js 22、.NET SDK 8、Windows PowerShell 5.1：

```powershell
npm ci
npm run build
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\desktop-package.ps1
```

输出：`dist-desktop/Half-Mc-Launcher-<version>.exe`（单文件）。

可用环境变量：

| 变量 | 用途 |
| --- | --- |
| `DOTNET_EXE` | 指定 `dotnet.exe` 路径 |
| `HF_SIGN_PFX` / `HF_SIGN_PASSWORD` | 打包后使用 PFX 签名 |
| `HF_SIGN_THUMBPRINT` | 使用证书库中指定指纹的证书签名 |
| `HF_SIGN_SUBJECT` | 按证书主题选择签名证书 |

签名细节见 [docs/CODE-SIGNING.md](docs/CODE-SIGNING.md)。

## 测试

```powershell
npm test                 # 前端核心单元测试
npm run desktop:test     # 实例扫描 / 配置 / Java 检测 / 启动参数
```

## 目录

```text
desktop/          C# WebView2 外壳、启动与下载逻辑
src/              Vue 3 界面
shared/           前后端共享模型、外观与工作区定义
scripts/          构建、打包、签名与冒烟脚本
docs/             实现记录、代码签名、插件 SDK 等文档
```

## 账户与隐私

- 微软登录使用公开的公共客户端 ID（来自 Prism Launcher），授权页面显示的应用名称为 Prism Launcher；可在设置中换成自己的应用 ID。
- 访问令牌与 MSAL 缓存经 Windows DPAPI 加密保存在 `data/accounts.bin`，不随配置导出。
- 启动器不包含遥测；崩溃诊断包需要手动导出，且会脱敏账户名称、UUID、皮肤与客户端 ID。

## 历史

早期 Electron 外壳（`npm run dist`）已冻结，仅作历史实现保留；当前开发全部在 `desktop/` 目录的 C# + WebView2 外壳上。

## 许可证

[MIT](LICENSE) © 2026 Hakf

第三方依赖声明见 [build/THIRD-PARTY-NOTICES.txt](build/THIRD-PARTY-NOTICES.txt)。
