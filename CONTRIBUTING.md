# 参与开发

感谢愿意一起改进 Half Mc Launcher。提交前请先确认构建与测试能在本机通过。

## 环境

- Windows 10 1803+ / Windows 11
- Node.js 22
- .NET SDK 8（构建 C# 外壳；运行只需要 .NET Framework 4.8）
- WebView2 Runtime
- Windows PowerShell 5.1

## 构建与测试

```powershell
npm ci
npm run build
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\desktop-package.ps1
```

```powershell
npm test                 # 前端单元测试
npm run desktop:test     # 实例扫描 / 配置 / Java / 启动参数冒烟测试
```

真实联网安装、加载器与资源下载的脚本会修改临时目录或下载较大文件，运行前请阅读 `scripts/` 内对应脚本说明。

## 提交改动

- 一个 PR 聚焦一件事，附带复现步骤或截图。
- 界面文案使用中文，代码标识符使用英文。
- TypeScript 保持 `vue-tsc` 无错误；C# 代码遵循现有文件风格（4 空格缩进、中文注释）。
- 不要提交 `data/`、`dist-desktop/`、`node_modules/`、`*.pfx`、令牌或真实账户信息。
- 提交信息建议使用 `feat:`、`fix:`、`refactor:`、`docs:`、`build:` 前缀。

## 分支与发布

- `redesign/ui` 为当前开发分支；稳定后合并到 `main`。
- 发布由维护者打 `v*` 标签触发 GitHub Actions，构建产物在签名后上传。
- 外部 PR 的 CI 产物不带签名；正式发行版才会签名。

## 代码签名

签名流程见 [docs/CODE-SIGNING.md](docs/CODE-SIGNING.md)。请不要在 PR 中提交任何证书、私钥或签名密码。
