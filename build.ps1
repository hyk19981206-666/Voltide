param(
    [string]$OutDir,
    [string]$LogPath
)

# Builds the two Voltide images with the in-box .NET Framework compiler.
#
# 默认仍产出到本脚本所在目录（work\Voltide\Voltide.exe / Voltide-Test.exe），
# 但可以用 -OutDir 指定别处：卡 2 要求「产物先输出到 evidence\09\package，
# 不要覆盖正在运行的映像」，于是编译输出与「运行中的复制」解耦。
#
# -LogPath 会把两次编译的退出码、产物大小一并写进日志文件（evidence\09\build.log）。

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not $OutDir) { $OutDir = $root }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

if (-not (Test-Path $csc)) {
    throw "Cannot find csc.exe at $csc"
}

$script:log = New-Object System.Collections.Generic.List[string]

function Say([string]$text) {
    Write-Host $text
    [void]$script:log.Add($text)
}

function Write-LogFile() {
    if (-not $LogPath) { return }

    $dir = Split-Path -Parent $LogPath
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllLines($LogPath, $script:log, $encoding)
}

$files = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter '*.cs' |
         ForEach-Object { $_.FullName }

if ($files.Count -eq 0) { throw "No .cs files found" }

$opts = @(
    '/codepage:65001',
    '/optimize+',
    '/nologo',
    '/r:System.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll'
)

$gui  = Join-Path $OutDir 'Voltide.exe'
$test = Join-Path $OutDir 'Voltide-Test.exe'
$ico  = Join-Path $root 'app.ico'

Say ("[build] {0:yyyy-MM-dd HH:mm:ss}" -f (Get-Date))
Say ("[build] root   = {0}" -f $root)
Say ("[build] outdir = {0}" -f $OutDir)
Say ("[build] csc    = {0}" -f $csc)
Say ("[build] sources= {0}" -f $files.Count)

# The icon is generated from the logo geometry by tools\makeicon.ps1; if it is
# missing the build still succeeds, just without a shell icon.
$iconOpt = @()
if (Test-Path $ico) { $iconOpt = @("/win32icon:$ico") }

# Typefaces travel inside the exe, so a single file is enough to distribute.
# Files are staged under ASCII names first: csc's /resource switch mangles
# non-ASCII paths, and one of these is 汉仪旗黑.
$resOpt = @()
$fontDir = Join-Path $root 'Fonts'
if (Test-Path $fontDir) {
    $staging = Join-Path $root 'obj'
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    $faces = @(
        @{ Source = 'Montserrat-Medium.ttf';   Embed = 'Montserrat-Medium.ttf' },
        @{ Source = 'Montserrat-SemiBold.ttf'; Embed = 'Montserrat-SemiBold.ttf' }
    )

    # The CJK face has a Chinese filename. Spelling it here would break: this script
    # is UTF-8 without BOM and PowerShell 5.1 reads it as ANSI, turning the name into
    # mojibake so Test-Path silently misses the file. Find it by elimination instead.
    $cjk = Get-ChildItem $fontDir -Filter '*.ttf' |
           Where-Object { $_.Name -notlike 'Montserrat*' } |
           Sort-Object Length -Descending |
           Select-Object -First 1

    if ($cjk) { $faces += @{ Source = $cjk.Name; Embed = 'AlibabaPuHuiTi-3-75-SemiBold.ttf' } }

    foreach ($face in $faces) {
        $src = Join-Path $fontDir $face.Source
        if (-not (Test-Path $src)) { continue }

        $staged = Join-Path $staging $face.Embed
        Copy-Item $src $staged -Force
        $resOpt += "/resource:$staged,$($face.Embed)"
    }
}

Say "Compiling GUI ($($files.Count) files)..."

$guiOutput = & $csc /target:winexe /out:$gui @opts @iconOpt @resOpt $files 2>&1
$guiExit = $LASTEXITCODE
foreach ($line in @($guiOutput)) {
    if ($line) { Say ("[gui] " + $line) }
}
if (Test-Path $gui) {
    Say ("[gui] exit={0} out={1} size={2}" -f $guiExit, $gui, (Get-Item $gui).Length)
} else {
    Say ("[gui] exit={0} out={1} <未产出>" -f $guiExit, $gui)
}
if ($guiExit -ne 0) {
    Write-LogFile
    throw "GUI compile failed (exit=$guiExit)"
}

Say "Compiling console test build..."

$testOutput = & $csc /target:exe /out:$test @opts @iconOpt @resOpt $files 2>&1
$testExit = $LASTEXITCODE
foreach ($line in @($testOutput)) {
    if ($line) { Say ("[test] " + $line) }
}
if (Test-Path $test) {
    Say ("[test] exit={0} out={1} size={2}" -f $testExit, $test, (Get-Item $test).Length)
} else {
    Say ("[test] exit={0} out={1} <未产出>" -f $testExit, $test)
}
if ($testExit -ne 0) {
    Write-LogFile
    throw "Test compile failed (exit=$testExit)"
}

foreach ($produced in @($gui, $test)) {
    try {
        [void][System.IO.File]::ReadAllBytes($produced)
        Say ("[check] 可读 {0}" -f $produced)
    } catch {
        Say ("[check] 不可读 {0}: {1}" -f $produced, $_.Exception.Message)
        Write-LogFile
        throw "产出物不可读: $produced"
    }
}

Write-Host ""
Write-Host ("GUI  -> {0}  ({1:N0} bytes)" -f $gui,  (Get-Item $gui).Length)
Write-Host ("Test -> {0}  ({1:N0} bytes)" -f $test, (Get-Item $test).Length)

Write-LogFile
