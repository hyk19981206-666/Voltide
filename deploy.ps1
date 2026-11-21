<#
.SYNOPSIS
    自动用户部署：把发布包（evidence\09\package）部署到 %LOCALAPPDATA%\Voltide\app 并从那里启动。

.DESCRIPTION
    为什么必须这么做：D:\workspace 目录带 Mandatory Label\Low Mandatory Level:(OI)(CI)(NW)，
    其下编译出的 Voltide.exe 会以 Low 完整性（S-1-16-4096）运行；explorer 是 Medium，
    UIPI 禁止 Low 向 Medium 注册托盘图标（Shell_NotifyIcon NIM_ADD 被拒，lastError=5）。
    把同一份文件复制到 %LOCALAPPDATA% 下，新文件继承的是 Medium 标签，托盘才可能注册成功。

    本脚本**不修改任何 SACL / 完整性标签 / UAC 设置**，只做：宿主完整性检查 → 复制 → 校验哈希 →
    校验目标完整性标签 → 校验最终进程完整性 → 写回执。

.PARAMETER PackageDir
    发布包目录（含 Voltide.exe 与清单所列文件）。不传则依次尝试：
      <本脚本目录>\package、<本脚本目录>\dist、<本脚本目录>（含 Voltide.exe 时）、<本脚本目录>\..\..\evidence\09\package

.PARAMETER TargetDir
    部署目标，默认 %LOCALAPPDATA%\Voltide\app。必须在 %LOCALAPPDATA% 之下。

.PARAMETER ManifestPath
    package.sha256.json 路径。不传则尝试 <PackageDir>\..\package.sha256.json 与 <PackageDir>\package.sha256.json。

.PARAMETER SkipProcessCheck
    跳过「启动一次 --test 并读取子进程令牌完整性」的最后一步校验（默认不跳过）。

.OUTPUTS
    退出码：
      0   成功
      10  当前宿主完整性过低（Low 或探测失败）：拒绝部署，绝不报告成功
      11  找不到发布包目录 / 目标目录不在 %LOCALAPPDATA% 下
      12  找不到或无法解析 package.sha256.json
      13  发布包内文件哈希与清单不符
      14  部署后目标文件哈希与清单不符
      15  完整性不达标：目标文件带 Low 标签，或最终进程为 Low
      16  复制 / 写文件失败
      17  其它异常
#>
param(
    [string]$PackageDir,
    [string]$TargetDir,
    [string]$ManifestPath,
    [switch]$SkipProcessCheck
)

$ErrorActionPreference = 'Stop'

$LowRid = 4096
$MediumRid = 8192

function Fail {
    param([int]$Code, [string]$Message)
    [Console]::Error.WriteLine("[deploy] 错误: " + $Message)
    [Console]::Error.WriteLine("[deploy] 退出码: $Code")
    exit $Code
}

function Note {
    param([string]$Text)
    Write-Host ("[deploy] " + $Text)
}

function Get-LevelName {
    param([int]$Rid)
    if ($Rid -lt 0) { return 'Unknown' }
    if ($Rid -lt $LowRid) { return 'Untrusted' }
    if ($Rid -lt $MediumRid) { return 'Low' }
    if ($Rid -lt 12288) { return 'Medium' }
    if ($Rid -lt 16384) { return 'High' }
    return 'System'
}

# ------------------------------------------------------------------ 0. 宿主完整性

$integritySource = @'
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

public static class HostIntegrity
{
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
                                           IntPtr tokenInformation, int tokenInformationLength,
                                           out int returnLength);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    public static int Rid = -1;
    public static string Sid = "";
    public static string Error = "";

    public static bool ProbeCurrent()
    {
        return Probe(GetCurrentProcess());
    }

    public static bool ProbeProcess(int processId)
    {
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (handle == IntPtr.Zero)
        {
            Error = "OpenProcess(" + processId + ") error=" + Marshal.GetLastWin32Error();
            return false;
        }
        try { return Probe(handle); }
        finally { CloseHandle(handle); }
    }

    static bool Probe(IntPtr process)
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        Rid = -1;
        Sid = "";
        try
        {
            if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out token))
            {
                Error = "OpenProcessToken error=" + Marshal.GetLastWin32Error();
                return false;
            }

            int size = 0;
            GetTokenInformation(token, 25 /* TokenIntegrityLevel */, IntPtr.Zero, 0, out size);
            if (size <= 0) size = 64;

            buffer = Marshal.AllocHGlobal(size);
            int needed;
            if (!GetTokenInformation(token, 25, buffer, size, out needed))
            {
                Error = "GetTokenInformation error=" + Marshal.GetLastWin32Error();
                return false;
            }

            IntPtr sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero) { Error = "SID 指针为空"; return false; }

            byte count = Marshal.ReadByte(sid, 1);
            int total = 8 + 4 * count;
            byte[] bytes = new byte[total];
            Marshal.Copy(sid, bytes, 0, total);

            Sid = new SecurityIdentifier(bytes, 0).Value;
            Rid = Marshal.ReadInt32(sid, 8 + 4 * (count - 1));
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
}
'@

try {
    if (-not ('HostIntegrity' -as [type])) {
        Add-Type -TypeDefinition $integritySource -Language CSharp
    }
} catch {
    Fail 10 ("无法在宿主内编译完整性探针：" + $_.Exception.Message +
             "。既然不能证明宿主不低于 Medium，就拒绝部署。")
}

if (-not [HostIntegrity]::ProbeCurrent()) {
    Fail 10 ("无法读取当前宿主的完整性级别（" + [HostIntegrity]::Error +
             "），无法证明宿主不低于 Medium，拒绝部署。")
}

$hostRid = [HostIntegrity]::Rid
$hostSid = [HostIntegrity]::Sid
$hostLevel = Get-LevelName $hostRid

Note ("宿主完整性 = {0} (RID={1}, {2})" -f $hostLevel, $hostRid, $hostSid)

if ($hostRid -lt $MediumRid) {
    $detail = "当前宿主完整性为 " + $hostLevel + " (RID=" + $hostRid + ", " + $hostSid + ")，低于 Medium。" +
              "低完整性宿主部署出来的进程同样注册不了托盘图标，脚本拒绝继续并**不**报告成功。" +
              "请从普通（Medium）命令行或 work\Voltide\Voltide.cmd 运行。"
    Fail 10 $detail
}

# ------------------------------------------------------------------ 1. 解析发布包

$candidates = New-Object System.Collections.Generic.List[string]
if ($PackageDir) { $candidates.Add([System.IO.Path]::GetFullPath($PackageDir)) }
$candidates.Add([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'package')))
$candidates.Add([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist')))
if (Test-Path (Join-Path $PSScriptRoot 'Voltide.exe')) { $candidates.Add([System.IO.Path]::GetFullPath($PSScriptRoot)) }
$candidates.Add([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\evidence\09\package')))

$resolvedPackage = $null
foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Path (Join-Path $candidate 'Voltide.exe'))) { $resolvedPackage = $candidate; break }
}
if (-not $resolvedPackage) {
    Fail 11 ("找不到发布包目录（必须包含 Voltide.exe）。已尝试: " + ($candidates -join '; '))
}
Note ("发布包 = " + $resolvedPackage)

# ------------------------------------------------------------------ 2. 目标目录

if (-not $TargetDir) { $TargetDir = Join-Path $env:LOCALAPPDATA 'Voltide\app' }
$TargetDir = [System.IO.Path]::GetFullPath($TargetDir)

$localAppData = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA)
if (-not $TargetDir.StartsWith($localAppData, [System.StringComparison]::OrdinalIgnoreCase)) {
    Fail 11 ("目标目录不在 %LOCALAPPDATA% 之下：{0}。本方案只支持自动用户部署（复制到用户目录以获得 Medium 标签），" +
             "不支持部署到带 Low 标签的工作区。" -f $TargetDir)
}
Note ("目标目录 = " + $TargetDir)

# ------------------------------------------------------------------ 3. 清单

$manifestCandidates = New-Object System.Collections.Generic.List[string]
if ($ManifestPath) { $manifestCandidates.Add([System.IO.Path]::GetFullPath($ManifestPath)) }
$manifestCandidates.Add([System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $resolvedPackage) 'package.sha256.json')))
$manifestCandidates.Add([System.IO.Path]::GetFullPath((Join-Path $resolvedPackage 'package.sha256.json')))

$manifest = $null
$manifestFile = $null
foreach ($candidate in $manifestCandidates) {
    if (-not $candidate) { continue }
    if (-not (Test-Path $candidate)) { continue }

    try {
        $manifest = Get-Content $candidate -Raw -Encoding UTF8 | ConvertFrom-Json
        $manifestFile = $candidate
    } catch {
        Fail 12 ("无法解析清单 {0}: {1}" -f $candidate, $_.Exception.Message)
    }
    break
}

if (-not $manifest) {
    Fail 12 ("找不到 package.sha256.json。已尝试: " + ($manifestCandidates -join '; '))
}
if (-not $manifest.files -or $manifest.files.Count -lt 1) {
    Fail 12 ("清单内容为空: " + $manifestFile)
}
Note ("清单 = {0}（{1} 个文件）" -f $manifestFile, $manifest.files.Count)

# ------------------------------------------------------------------ 4. 校验发布包

$packageProblems = New-Object System.Collections.Generic.List[string]
foreach ($entry in $manifest.files) {
    $source = Join-Path $resolvedPackage $entry.path
    if (-not (Test-Path $source)) {
        $packageProblems.Add(($entry.path + " 缺失"))
        continue
    }

    $item = Get-Item $source
    $hash = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()

    if ([int64]$item.Length -ne [int64]$entry.size) {
        $packageProblems.Add(("{0} 大小 {1} != {2}" -f $entry.path, $item.Length, $entry.size))
    } elseif ($hash -ne ([string]$entry.sha256).ToLowerInvariant()) {
        $packageProblems.Add(("{0} sha256 {1} != {2}" -f $entry.path, $hash, $entry.sha256))
    }
}
if ($packageProblems.Count -gt 0) {
    Fail 13 ("发布包文件与清单不符: " + ($packageProblems -join '; '))
}
Note "发布包哈希与清单一致"

# ------------------------------------------------------------------ 5. 复制

try {
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
} catch {
    Fail 16 ("无法创建目标目录 {0}: {1}" -f $TargetDir, $_.Exception.Message)
}

$copied = New-Object System.Collections.Generic.List[string]
$kept = New-Object System.Collections.Generic.List[string]

foreach ($entry in $manifest.files) {
    $source = Join-Path $resolvedPackage $entry.path
    $destination = Join-Path $TargetDir $entry.path

    # 用户配置：devices.json 是设备显示名覆盖，已存在就不覆盖（重复安装不得丢弃用户配置）。
    if ($entry.path -ieq 'devices.json' -and (Test-Path $destination)) {
        $kept.Add($entry.path)
        Note ("保留已有用户配置: " + $entry.path)
        continue
    }

    try {
        Copy-Item $source $destination -Force
        $copied.Add($entry.path)
    } catch {
        Fail 16 ("复制失败 {0}: {1}" -f $entry.path, $_.Exception.Message)
    }
}
Note ("已复制 {0} 个文件" -f $copied.Count)

# ------------------------------------------------------------------ 6. 校验目标哈希

$targetProblems = New-Object System.Collections.Generic.List[string]
foreach ($entry in $manifest.files) {
    if ($kept -contains $entry.path) { continue }

    $destination = Join-Path $TargetDir $entry.path
    if (-not (Test-Path $destination)) {
        $targetProblems.Add(($entry.path + " 未落地"))
        continue
    }

    $hash = (Get-FileHash $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    $length = (Get-Item $destination).Length
    if ([int64]$length -ne [int64]$entry.size -or $hash -ne ([string]$entry.sha256).ToLowerInvariant()) {
        $targetProblems.Add(("{0} 目标哈希不符" -f $entry.path))
    }
}
if ($targetProblems.Count -gt 0) {
    Fail 14 ("部署后哈希校验失败: " + ($targetProblems -join '; '))
}
Note "部署后目标哈希与清单一致"

# ------------------------------------------------------------------ 7. 完整性标签

$lowLabelFiles = New-Object System.Collections.Generic.List[string]

foreach ($path in @($TargetDir) + @($manifest.files | ForEach-Object { Join-Path $TargetDir $_.path })) {
    if (-not (Test-Path $path)) { continue }

    $acl = (& icacls $path 2>&1 | Out-String)
    if ($acl -match 'Low Mandatory Level' -or $acl -match 'S-1-16-4096' -or $acl -match '低强制级别') {
        $lowLabelFiles.Add($path)
    }
}

if ($lowLabelFiles.Count -gt 0) {
    Fail 15 ("以下对象仍带 Low 完整性标签，托盘在此路径下不可能注册成功: " + ($lowLabelFiles -join '; '))
}
Note "目标文件与目标目录均无 Low 完整性标签"

# ------------------------------------------------------------------ 8. 最终进程完整性

$processLevel = 'skipped'
$processRid = -1
$processSid = ''
$deployedExe = Join-Path $TargetDir 'Voltide.exe'

if (-not $SkipProcessCheck) {
    if (-not (Test-Path $deployedExe)) {
        Fail 15 ("目标目录缺少 Voltide.exe: " + $deployedExe)
    }

    $probeOut = Join-Path $env:TEMP 'Voltide-deploy-probe.json'
    $probeErr = Join-Path $env:TEMP 'Voltide-deploy-probe.err.txt'
    if (Test-Path $probeOut) { Remove-Item $probeOut -Force -ErrorAction SilentlyContinue }

    $child = Start-Process -FilePath $deployedExe -ArgumentList '--test', '--json' `
                           -PassThru -RedirectStandardOutput $probeOut -RedirectStandardError $probeErr `
                           -WindowStyle Hidden

    for ($i = 0; $i -lt 300; $i++) {
        if ($child.HasExited) { break }
        if ([HostIntegrity]::ProbeProcess($child.Id)) {
            $processRid = [HostIntegrity]::Rid
            $processSid = [HostIntegrity]::Sid
            break
        }
        Start-Sleep -Milliseconds 25
    }

    if ($processRid -ge 0) {
        $processLevel = Get-LevelName $processRid
        Note ("最终进程完整性 = {0} (RID={1}, {2})" -f $processLevel, $processRid, $processSid)
    } else {
        $processLevel = 'unknown'
        Note ("警告: 未能在子进程存活期内读到令牌完整性（{0}）——不据此声称成功，仅如实记录。" -f [HostIntegrity]::Error)
    }

    try { if (-not $child.HasExited) { $child.WaitForExit(10000) | Out-Null } } catch { }

    if ($processRid -ge 0 -and $processRid -lt $MediumRid) {
        Fail 15 ("部署后的最终进程仍是 {0} 完整性 (RID={1})，托盘不可能注册成功。" -f $processLevel, $processRid)
    }
}

# ------------------------------------------------------------------ 9. 回执

$receiptPath = Join-Path $TargetDir 'install-receipt.json'
$receipt = [pscustomobject]@{
    installedAt      = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    packageDir       = $resolvedPackage
    manifestPath     = $manifestFile
    targetDir        = $TargetDir
    hostIntegrity    = ('{0} (RID={1}, {2})' -f $hostLevel, $hostRid, $hostSid)
    processIntegrity = ('{0} (RID={1}, {2})' -f $processLevel, $processRid, $processSid)
    copiedFiles      = @($copied)
    keptUserConfig   = @($kept)
    files            = @($manifest.files)
}

try {
    $receiptJson = $receipt | ConvertTo-Json -Depth 6
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($receiptPath, $receiptJson, $utf8)

    # 回读验证：写坏的 JSON 等于没有回执。
    $back = Get-Content $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $back.files -or $back.files.Count -ne $manifest.files.Count) {
        Fail 16 ("回执回读失败: " + $receiptPath)
    }
} catch {
    Fail 16 ("写回执失败: " + $_.Exception.Message)
}

# ------------------------------------------------------------------ 9.5 运行中实例

$running = @(Get-Process -Name Voltide -ErrorAction SilentlyContinue)
foreach ($proc in $running) {
    $runningPath = '<无法读取>'
    try { $runningPath = $proc.Path } catch { }
    Note ("注意: 已有 Voltide 实例在运行 (PID=" + $proc.Id + ", " + $runningPath + ")。" +
          "单实例互斥不绑定路径，新实例会立刻退出并把面板交给这个实例；" +
          "如果它正是旧的 dist 副本（Low 完整性、没有托盘图标），请先用任务管理器结束它。" +
          "本脚本不结束任何进程。")
}

# ------------------------------------------------------------------ 10. 完成

Note "----------------------------------------------------------------"
Note ("部署成功: " + $TargetDir)
Note ("用户入口: " + (Join-Path $TargetDir 'Voltide.cmd') + "  （或直接 " + $deployedExe + " --show）")
Note ("回执: " + $receiptPath)
Note "本脚本未修改任何 SACL/完整性标签，未启用开机自启（自启开关在托盘菜单里）。"
exit 0
