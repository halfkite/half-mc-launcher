# 代码签名与可信发布

Windows 只有信任到受信任根证书的签名才会显示发布者名称。**自签名证书不会让其他电脑信任**，因此公开发布需要 CA 证书或可信签名服务。

## 推荐：SignPath Foundation（开源项目免费）

SignPath 为公开的开源项目提供免费代码签名，签出的 exe 使用受信任的证书，适合 GitHub 发布。

申请条件（以 SignPath 审核为准）：

- 仓库公开，包含 OSI 认可的开源许可证（本仓库为 MIT）
- 通过 GitHub Actions 等可信构建生成产物
- 项目维护者完成身份验证

申请入口：<https://signpath.org/>（Foundation 计划），文档：<https://docs.signpath.io/>

### 申请通过后的配置

1. 在 SignPath 控制台创建 Project，记下 **organization id** 与 **project slug**。
2. 创建 **artifact configuration**，指向单个 exe：
   - 路径示例：`Half-Mc-Launcher-*.exe`
   - 如上传的是 zip，需要在配置中声明压缩包内的 exe
   - 记下它的 slug，填入 `SIGNPATH_ARTIFACT_CONFIG_SLUG`
3. 创建 **signing policy**（通常先 `test-signing`，发布用 `release-signing`），记下 slug。
4. 在 GitHub 仓库 **Settings → Secrets and variables → Actions** 添加：

| 类型 | 名称 | 值 |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | SignPath 生成的 API Token |
| Variable | `SIGNPATH_ORG_ID` | 组织 ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | 项目 slug |
| Variable | `SIGNPATH_POLICY_SLUG` | 签名策略 slug，例如 `release-signing` |
| Variable | `SIGNPATH_ARTIFACT_CONFIG_SLUG` | 产物配置 slug |

5. 推送 `v*` 标签或手动运行 `.github/workflows/desktop-release.yml`。未配置 Token 时工作流只构建未签名 exe；配置后会自动提交 SignPath 签名、替换为签名版本并上传 `desktop-signed`。

## 备选：Certum 开源代码签名

Certum 对通过审核的开源项目提供免费代码签名证书，通常需要项目主页、许可证与维护者身份证明。申请后可用其云签名工具或导出的证书按下面的本地流程签名。

## 本地签名（已有 PFX 或证书指纹）

打包脚本已内置签名步骤，设置环境变量后重新打包即可：

```powershell
$env:HF_SIGN_PFX = 'D:\keys\half-codesign.pfx'
$env:HF_SIGN_PASSWORD = '...'
# 或者使用当前用户证书库中的指纹
# $env:HF_SIGN_THUMBPRINT = 'ABCDEF...'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\desktop-package.ps1
```

脚本会调用 `scripts/sign-desktop.ps1`，使用 SHA-256 与 DigiCert 时间戳服务，并输出 `Get-AuthenticodeSignature` 的校验结果。

注意：

- 私钥、PFX 与密码不要提交到仓库，只放本机或 CI Secrets。
- 2023 年起 CA 通常要求私钥保存在硬件令牌或云端 HSM，PFX 可能无法直接导出。
- 未配置证书时构建不会失败，产物保持未签名。

## 自签名证书

可以生成自签名证书做本机测试：

```powershell
New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Half Mc Launcher (Development)' `
  -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature
```

签名后仅当证书被安装到目标机器的「受信任的根证书颁发机构」时才会显示为可信。**不要把它当作公开发布签名**：其他电脑仍会提示未知发布者。

## SmartScreen 说明

即使使用 CA 颁发的 OV 证书，新程序仍可能需要下载量来建立 SmartScreen 信誉；EV 证书通常能更快建立信任，但不保证完全无提示。签名解决的是「发布者身份可信」，SmartScreen 还叠加了信誉判断。
