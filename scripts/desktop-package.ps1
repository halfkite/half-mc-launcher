param(
  [string]$Configuration = 'Release'
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

  Write-Host "编译 C# 外壳（$Configuration）…"
  & $dotnet build 'desktop/HalfMcLauncher.csproj' -c $Configuration --nologo
  if ($LASTEXITCODE -ne 0) { throw 'C# 外壳构建失败' }

  $out = Join-Path $root "desktop/bin/$Configuration/net48"
  $stage = Join-Path $root 'dist-desktop/Half-Mc-Launcher'
  $zip = Join-Path $root 'dist-desktop/Half-Mc-Launcher-0.1.0-win-x64.zip'

  Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force $stage | Out-Null
  Copy-Item (Join-Path $out '*.exe') $stage
  Copy-Item (Join-Path $out '*.dll') $stage
  Copy-Item (Join-Path $out '*.exe.config') $stage
  Copy-Item (Join-Path $out 'www') $stage -Recurse
  $loader = Join-Path $stage 'runtimes/win-x64/native'
  New-Item -ItemType Directory -Force $loader | Out-Null
  Copy-Item (Join-Path $out 'runtimes/win-x64/native/WebView2Loader.dll') $loader

  New-Item -ItemType Directory -Force (Split-Path $zip) | Out-Null
  Remove-Item $zip -Force -ErrorAction SilentlyContinue
  Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

  $folderSize = (Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum
  $zipSize = (Get-Item $zip).Length
  Write-Host ''
  Write-Host ("解压后：{0:N2} MB    zip：{1:N2} MB" -f ($folderSize / 1MB), ($zipSize / 1MB))
  Write-Host ("输出：{0}" -f $zip)
}
finally {
  Pop-Location
}
