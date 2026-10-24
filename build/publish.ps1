#Requires -Version 7.0
<#
.SYNOPSIS
    出发布包：便携版 zip +（可选）Velopack 安装包与更新包。

.DESCRIPTION
    产物都落在 artifacts\releases：
      ML-<ver>-portable-win-x64.zip     解压就能跑，数据目录在 exe 同级的 data\（便携版）
      ML-win-Setup.exe                  安装包（Velopack），装到 %LocalAppData%\ML.Launcher
      ML-win-<ver>-full.nupkg           更新包（vpk 用于差分的基准）
      releases.win.json                 更新源清单（放到服务器就是这个文件被拉取）

    更新源怎么用：把 releases 目录整个丢到任意静态服务器，
    然后让启动器带上 ML_UPDATE_URL=https://你的域名/ 运行即可。

.PARAMETER Version
    包版本，必须与 csproj 里的 <Version> 一致（Velopack 会以它判「有没有新版」）。

.PARAMETER SkipInstaller
    只出便携版，跳过 vpk（vpk 没装时也会自动跳过）。
#>
[CmdletBinding()]
param(
    [string] $Version = '0.1.0',
    [string] $Runtime = 'win-x64',
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ML.Host\ML.Host.csproj'
$staging = Join-Path $repoRoot 'artifacts\publish'
$releases = Join-Path $repoRoot 'artifacts\releases'

Write-Host "[1/4] 清理" -ForegroundColor Cyan
foreach ($dir in $staging, $releases) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

Write-Host "[2/4] 发布单文件（self-contained, $Runtime）" -ForegroundColor Cyan
dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:AllowedReferenceRelatedFileExtensions=none `
    -o $staging --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }

$exe = Join-Path $staging 'Enderman Launcher.exe'
if (-not (Test-Path $exe)) { throw "发布目录里没有 Enderman Launcher.exe：$staging" }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "      Enderman Launcher.exe $sizeMb MB" -ForegroundColor DarkGray

Write-Host "[3/4] 便携版 zip" -ForegroundColor Cyan
$zip = Join-Path $releases "ML-$Version-portable-$Runtime.zip"
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
$zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "      $([IO.Path]::GetFileName($zip)) $zipMb MB" -ForegroundColor DarkGray

Write-Host "[4/4] 安装包与更新包" -ForegroundColor Cyan
$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'

if ($SkipInstaller) {
    Write-Host "      跳过（-SkipInstaller）" -ForegroundColor DarkGray
}
elseif (-not (Test-Path $vpk)) {
    # 不静默跳过：安装包是用户明确要的交付物，缺工具必须让他看见。
    Write-Warning "找不到 vpk（$vpk）。装一次：dotnet tool install -g vpk"
    Write-Warning "这次只出了便携版 zip。"
}
else {
    & $vpk pack `
        --packId ML.Launcher `
        --packVersion $Version `
        --packDir $staging `
        --mainExe 'Enderman Launcher.exe' `
        --packTitle 'Enderman Launcher' `
        --packAuthors 'ML' `
        --outputDir $releases `
        --channel win `
        --yes
    if ($LASTEXITCODE -ne 0) { throw "vpk pack 失败（exit $LASTEXITCODE）" }
}

Write-Host ''
Write-Host "产物：$releases" -ForegroundColor Green
Get-ChildItem $releases | Sort-Object Name |
    Format-Table @{ n = '文件'; e = { $_.Name } }, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
