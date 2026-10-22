param(
  [string]$Configuration = 'Release',
  [switch]$Launch
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = $env:DOTNET_EXE
if (-not $dotnet) { $dotnet = 'dotnet' }

Push-Location $root
try {
  if (-not (Test-Path 'dist/index.html')) {
    Write-Host '前端产物缺失，先执行 npm run build …'
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw '前端构建失败' }
  }

  Write-Host "编译 C# 外壳（$Configuration，单文件）…"
  # 从干净的输出目录构建，确保 Costura 的嵌入与清理行为一致。
  Remove-Item (Join-Path $root "desktop/bin/$Configuration") -Recurse -Force -ErrorAction SilentlyContinue
  & $dotnet build 'desktop/HalfMcLauncher.csproj' -c $Configuration --nologo
  if ($LASTEXITCODE -ne 0) { throw 'C# 外壳构建失败' }

  $out = Join-Path $root "desktop/bin/$Configuration/net48"
  $exe = Join-Path $out 'Half Mc Launcher.exe'
  if (-not (Test-Path $exe)) { throw "没有找到构建产物：$exe" }

  # 单文件检查：除 exe/config/pdb 外不应再有 dll，否则说明 Costura 没有嵌入。
  $leftovers = Get-ChildItem $out -File -Filter *.dll -ErrorAction SilentlyContinue
  if ($leftovers) {
    Write-Warning ("仍有未嵌入的 DLL：" + (($leftovers | ForEach-Object Name) -join ', '))
  }

  # 版本号取自 csproj 的 <Version>，产物形如 Half-Mc-Launcher-0.1.0-r6.exe
  [xml]$project = Get-Content (Join-Path $root 'desktop/HalfMcLauncher.csproj')
  $version = @($project.Project.PropertyGroup.Version)[0]
  if (-not $version) { $version = '0.1.0' }

  $target = Join-Path $root ("dist-desktop/Half-Mc-Launcher-$version.exe")
  New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
  Remove-Item $target -Force -ErrorAction SilentlyContinue
  Copy-Item $exe $target
  # 去掉网络来源标记，避免双击时弹出“无法验证发布者”的安全警告
  try { Unblock-File -LiteralPath $target -ErrorAction Stop } catch { }
  Remove-Item -LiteralPath ($target + ':Zone.Identifier') -ErrorAction SilentlyContinue
  $notices = Join-Path $root 'build/THIRD-PARTY-NOTICES.txt'
  if (Test-Path $notices) { Copy-Item $notices (Join-Path (Split-Path $target) 'THIRD-PARTY-NOTICES.txt') -Force }

  # 未签名的 exe 从资源管理器直接双击时，Windows 可能弹出“未知发布者”提示。
  # 附带一个批处理入口，通过 CreateProcess 启动，可绕开该提示。
  $launcher = Join-Path (Split-Path $target) 'Start Launcher.cmd'
  $exeName = Split-Path $target -Leaf
  $launchLine = 'start "" "%~dp0{0}"' -f $exeName
  Set-Content -LiteralPath $launcher -Encoding ASCII -Value @('@echo off', $launchLine)
  try { Unblock-File -LiteralPath $launcher -ErrorAction Stop } catch { }

  Write-Host ''
  Write-Host ("单文件 EXE：{0:N2} MB（{1}）" -f ((Get-Item $target).Length / 1MB), $version)
  Write-Host ("输出：{0}" -f $target)

  if ($Launch) {
    Write-Host '启动新版本…'
    Set-Location (Split-Path $target)
    & $target
  }
}
finally {
  Pop-Location
}
