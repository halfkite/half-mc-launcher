param(
  [Parameter(Mandatory = $true)][string]$File,
  [string]$Pfx,
  [string]$Password,
  [string]$Thumbprint,
  [string]$Subject = 'Half Mc Launcher',
  [string]$Timestamp = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $File)) { throw "找不到要签名的文件：$File" }

function Find-Certificate {
  if ($Pfx) {
    if (-not (Test-Path -LiteralPath $Pfx)) { throw "找不到证书文件：$Pfx" }
    $secure = ConvertTo-SecureString -String $Password -AsPlainText -Force
    $imported = Import-PfxCertificate -FilePath $Pfx -CertStoreLocation 'Cert:\CurrentUser\My' -Password $secure
    return $imported
  }
  if ($Thumbprint) {
    $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "当前用户证书库中没有指纹为 $Thumbprint 的证书" }
    return $cert
  }
  $cert = Get-ChildItem 'Cert:\CurrentUser\My' -CodeSigningCert |
    Where-Object { $_.Subject -like "*$Subject*" } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
  if (-not $cert) { throw "没有找到主题包含「$Subject」的代码签名证书。请用 -Pfx 或 -Thumbprint 指定。" }
  return $cert
}

$certificate = Find-Certificate
if (-not $certificate.HasPrivateKey) { throw "证书不包含私钥，无法签名" }

Write-Host "使用证书：$($certificate.Subject) ($($certificate.Thumbprint))"
$signature = Set-AuthenticodeSignature -LiteralPath $File -Certificate $certificate -TimestampServer $Timestamp -HashAlgorithm SHA256
$check = Get-AuthenticodeSignature -LiteralPath $File
Write-Host "签名状态：$($check.Status) / $($check.StatusMessage)"

if ($check.Status -ne 'Valid') {
  Write-Warning "签名已完成，但当前机器未信任该证书链，Windows 仍会显示未知发布者。要消除提示，需要 CA 颁发的代码签名证书，或把自签名证书安装到「受信任的根证书颁发机构」。"
}
