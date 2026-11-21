param(
    [string]$Target,
    [switch]$NoKill,
    [string]$ManifestPath,
    [string]$BuildLogPath
)

# Builds Voltide and assembles a clean, runnable folder:
#   <Target>\Voltide.exe
#   <Target>\Voltide-Test.exe
#   <Target>\Voltide.cmd          用户入口（已部署则直接启动，否则先自动部署）
#   <Target>\Install-Voltide.ps1  部署脚本（deploy.ps1 的原样副本）
#   <Target>\devices.json         设备显示名覆盖（用户配置，已存在则不覆盖）
#
# 默认目标仍是 dist\；卡 2 用 -Target ..\..\evidence\09\package 产出发布包，
# 并用 -NoKill 避免结束用户正在运行的实例。
#
# 历史行为：脚本原先无条件 Stop-Process 掉所有 Voltide 进程。现在必须显式
# 不传 -NoKill 才会那么做——因为产物可以直接写到 -Target，不再占用运行中的映像。

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot   # this script lives next to build.ps1

if (-not $Target) { $Target = Join-Path $root 'dist' }
$Target = [System.IO.Path]::GetFullPath($Target)
if (-not $ManifestPath) { $ManifestPath = Join-Path $Target 'package.sha256.json' }

if ($NoKill) {
    Write-Host "NoKill: 不结束任何 Voltide 进程（产物直接写进 $Target）" -ForegroundColor DarkGray
} else {
    # A running copy locks the exe, so close it before compiling — not after.
    $running = Get-Process Voltide -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Closing running copy..." -ForegroundColor Yellow
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }
}

Write-Host "Building into $Target ..." -ForegroundColor Cyan

& (Join-Path $root 'build.ps1') -OutDir $Target -LogPath $BuildLogPath
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$exe = Join-Path $Target 'Voltide.exe'
if (-not (Test-Path $exe)) { throw "Voltide.exe not found in $Target" }

# ---------------------------------------------------------------- extra files

# 设备显示名覆盖是**用户配置**：目标已存在时保留用户那份，不覆盖。
$devices = Join-Path $root 'devices.json'
if (Test-Path $devices) {
    $dstDevices = Join-Path $Target 'devices.json'
    if (Test-Path $dstDevices) {
        Write-Host "devices.json 已存在：保留现有用户配置，不覆盖" -ForegroundColor Yellow
    } else {
        Copy-Item $devices $dstDevices -Force
        Write-Host "已复制 devices.json" -ForegroundColor DarkGray
    }
}

# 用户入口与部署脚本：随包发布，用户不需要手工复制任何文件。
$cmd = Join-Path $root 'Voltide.cmd'
if (Test-Path $cmd) {
    Copy-Item $cmd (Join-Path $Target 'Voltide.cmd') -Force
    Write-Host "已复制 Voltide.cmd" -ForegroundColor DarkGray
}

$deploy = Join-Path $root 'deploy.ps1'
if (Test-Path $deploy) {
    Copy-Item $deploy (Join-Path $Target 'Install-Voltide.ps1') -Force
    Write-Host "已复制 deploy.ps1 -> Install-Voltide.ps1" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- manifest

if ($ManifestPath) {
    $ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
    $manifestDir = Split-Path -Parent $ManifestPath
    if ($manifestDir -and -not (Test-Path $manifestDir)) {
        New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
    }

    $entries = @()
    foreach ($file in (Get-ChildItem $Target -Recurse -File | Sort-Object FullName)) {
        if ($file.FullName -ieq $ManifestPath -or $file.Name -ieq 'package.sha256.json') { continue }
        $relative = $file.FullName.Substring($Target.Length).TrimStart('\', '/')
        $entries += [pscustomobject]@{
            path   = $relative
            size   = [int64]$file.Length
            sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    $manifest = [pscustomobject]@{
        generatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
        packageDir  = $Target
        files       = $entries
    }

    $json = $manifest | ConvertTo-Json -Depth 6
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($ManifestPath, $json, $encoding)

    # 回读验证：写坏 JSON 等于没有清单。
    $back = Get-Content $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $back.files -or $back.files.Count -ne $entries.Count) {
        throw "package manifest 回读失败: $ManifestPath"
    }

    Write-Host ""
    Write-Host "--- package manifest ---" -ForegroundColor Green
    foreach ($e in $back.files) {
        Write-Host ("{0,-24} {1,10:N0}  {2}" -f $e.path, $e.size, $e.sha256)
    }
    Write-Host ("manifest -> {0}  ({1} files, 回读校验通过)" -f $ManifestPath, $back.files.Count) -ForegroundColor Gray
}

Write-Host ""
Write-Host "--- packed ---" -ForegroundColor Green
Get-ChildItem $Target -Recurse -File |
    Select-Object @{n='Path';e={$_.FullName.Substring($Target.Length + 1)}}, @{n='KB';e={[math]::Round($_.Length / 1KB, 1)}} |
    Format-Table -AutoSize

$size = (Get-ChildItem $Target -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("total: {0:N1} MB" -f ($size / 1MB)) -ForegroundColor Gray
Write-Host ""
Write-Host "Run it with: $Target\Voltide.cmd" -ForegroundColor Gray
