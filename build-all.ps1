<#
.SYNOPSIS
    xPuTTY one-command build: PuTTY fork -> WinForms shell -> staging -> MSI -> SHA256 manifest.

.DESCRIPTION
    Pipeline:
        [1/6] build the PuTTY fork   (D:\workspace\xputty\putty)
              targets: putty, plink, pscp, psftp, puttygen, pageant
        [2/6] build the WinForms shell (shell\build-shell.ps1 -> build\shell\xputty.exe)
        [3/6] assemble staging dir   (build\stage)
        [4/6] candle + light         -> dist\xPuTTY-<Version>-x64.msi
        [5/6] write SHA256 manifest  (dist\SHA256SUMS.txt) and print it
        [6/6] optional -Verify       (installer\verify-msi.ps1: msiexec /a + per-user lifecycle)

    The MSVC /showIncludes prefix repair (Chinese Windows + Ninja) is reused
    from build\common\BuildCommon.psm1 - the same helper used by build-putty.ps1.

.PARAMETER SkipPutty
    Do not rebuild the PuTTY fork; reuse the existing binaries in -PuttyBuildDir.

.PARAMETER SkipShell
    Do not run shell\build-shell.ps1; reuse the existing build\shell\xputty.exe
    (or the file given with -ShellExe).

.PARAMETER ShellExe
    Explicit path to the shell xputty.exe (skips the shell build). Useful to
    validate the pipeline with a placeholder while the shell is still in
    development, e.g.:
        -SkipShell -ShellExe D:\workspace\xputty\build\shell\xputty.exe

.PARAMETER Fresh
    Delete the PuTTY fork build dir and the WiX intermediate dir before building.

.PARAMETER Verify
    After the MSI is built, run installer\verify-msi.ps1 (administrative
    install hash comparison + per-user install/uninstall lifecycle self-test).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File D:\workspace\xputty\build-all.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File D:\workspace\xputty\build-all.ps1 -Fresh -Verify

.NOTES
    Exit code is non-zero on any failure. Windows PowerShell 5.1 compatible.
#>
[CmdletBinding()]
param(
    [string]$Root = 'D:\workspace\xputty',

    # PuTTY fork source/build (NOT putty-src: that is the pristine baseline)
    [string]$PuttySourceDir = '',
    [string]$PuttyBuildDir = '',

    # Shell
    [string]$ShellScript = '',
    [string]$ShellExe = '',

    # Staging / output
    [string]$StageDir = '',
    [string]$DistDir = '',
    [string]$WixDir = '',

    [string]$Version = '1.0.0',
    [ValidateSet('Release', 'Debug', 'RelWithDebInfo', 'MinSizeRel')]
    [string]$Config = 'Release',
    [string]$VSRoot = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools',

    [switch]$SkipPutty,
    [switch]$SkipShell,
    [switch]$Fresh,
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
$totalStart = Get-Date
$script:Failures = @()

# ---------------------------------------------------------------- helpers
function Write-Step($text) {
    Write-Host ''
    Write-Host ('=== ' + $text + ' ===') -ForegroundColor Cyan
}

function Write-Ok($text) {
    Write-Host ('  ' + $text) -ForegroundColor Green
}

function Write-Warn($text) {
    Write-Host ('  WARNING: ' + $text) -ForegroundColor Yellow
}

function Fail($message) {
    Write-Host ''
    Write-Host ('ERROR: ' + $message) -ForegroundColor Red
    exit 1
}

function Format-Elapsed($ts) {
    return ([math]::Round(((Get-Date) - $ts).TotalSeconds, 1).ToString() + ' s')
}

# ------------------------------------------------------------- defaults
if (-not $PuttySourceDir) { $PuttySourceDir = Join-Path $Root 'putty' }
if (-not $PuttyBuildDir)  { $PuttyBuildDir  = Join-Path $Root 'build\putty-fork' }
if (-not $ShellScript)    { $ShellScript    = Join-Path $Root 'shell\build-shell.ps1' }
if (-not $StageDir)       { $StageDir       = Join-Path $Root 'build\stage' }
if (-not $DistDir)        { $DistDir        = Join-Path $Root 'dist' }
if (-not $WixDir)         { $WixDir         = Join-Path $Root 'tools\wix' }

$installerDir = Join-Path $Root 'installer'
$productWxs   = Join-Path $installerDir 'product.wxs'
$licenseRtf   = Join-Path $installerDir 'license.rtf'
$wixOutDir    = Join-Path $Root 'build\wix-out'
$candleExe    = Join-Path $WixDir 'candle.exe'
$lightExe     = Join-Path $WixDir 'light.exe'
$msiPath      = Join-Path $DistDir ('xPuTTY-' + $Version + '-x64.msi')
$sumsPath     = Join-Path $DistDir 'SHA256SUMS.txt'

$puttyTargets = @('putty', 'plink', 'pscp', 'psftp', 'puttygen', 'pageant')

Import-Module (Join-Path $Root 'build\common\BuildCommon.psm1') -Force -DisableNameChecking

# ------------------------------------------------------------- 0. checks
Write-Step '0/6 Pre-flight checks'
Write-Host ('Root          : ' + $Root)
Write-Host ('PuttySourceDir: ' + $PuttySourceDir)
Write-Host ('PuttyBuildDir : ' + $PuttyBuildDir)
Write-Host ('StageDir      : ' + $StageDir)
Write-Host ('DistDir       : ' + $DistDir)
Write-Host ('WixDir        : ' + $WixDir)
Write-Host ('Version       : ' + $Version)

foreach ($f in @($candleExe, $lightExe, $productWxs, $licenseRtf)) {
    if (-not (Test-Path -LiteralPath $f)) { Fail ('Missing required file: ' + $f) }
}
if (-not (Test-Path -LiteralPath (Join-Path $PuttySourceDir 'CMakeLists.txt'))) {
    Fail ('PuTTY fork source tree not found (no CMakeLists.txt): ' + $PuttySourceDir)
}
if (-not (Test-Path -LiteralPath $DistDir)) { New-Item -ItemType Directory -Path $DistDir -Force | Out-Null }
Write-Ok 'toolchain + sources present'

# -------------------------------------------------- 1. PuTTY fork build
$puttyTimings = @()
if (-not $SkipPutty) {
    Write-Step ('1/6 Building PuTTY fork (' + ($puttyTargets -join ', ') + ')')
    $t = Get-Date
    try {
        $puttyResult = Invoke-PuttyBuild -SourceDir $PuttySourceDir -BuildDir $PuttyBuildDir `
                                         -Config $Config -Targets $puttyTargets `
                                         -VSRoot $VSRoot -Fresh:$Fresh
    } catch {
        Fail ('PuTTY fork build failed: ' + $_.Exception.Message)
    }
    foreach ($k in $puttyResult.Timings.Keys) { $puttyTimings += ($k + '=' + $puttyResult.Timings[$k] + 's') }
    Write-Ok ('PuTTY fork build finished in ' + (Format-Elapsed $t) + '  (' + ($puttyTimings -join ', ') + ')')
} else {
    Write-Step '1/6 Building PuTTY fork: SKIPPED (-SkipPutty)'
}

$puttyBins = @{}
$missing = @()
foreach ($target in $puttyTargets) {
    $exe = Join-Path $PuttyBuildDir ($target + '.exe')
    if (Test-Path -LiteralPath $exe) {
        $puttyBins[$target] = $exe
    } else {
        $missing += ($target + '.exe')
    }
}
if ($missing.Count -gt 0) {
    Fail ('PuTTY binaries missing in ' + $PuttyBuildDir + ': ' + ($missing -join ', ') +
          '  -- run without -SkipPutty (or with -Fresh) first')
}
Write-Ok ('all ' + $puttyTargets.Count + ' PuTTY binaries present (putty.exe, plink.exe, pscp.exe, psftp.exe, puttygen.exe, pageant.exe)')

# --------------------------------------------------------- 2. shell build
Write-Step '2/6 Building WinForms shell (xputty.exe)'
$shellPath = ''
if ($ShellExe) {
    $shellPath = $ShellExe
    Write-Warn ('using externally supplied shell binary: ' + $shellPath)
} elseif ($SkipShell) {
    $shellPath = Join-Path $Root 'build\shell\xputty.exe'
    Write-Host ('  -SkipShell: reusing ' + $shellPath)
} else {
    if (-not (Test-Path -LiteralPath $ShellScript)) {
        Fail ('Shell build script not found: ' + $ShellScript +
              '. The shell may still be in development - use -SkipShell -ShellExe <path> to test the pipeline with a placeholder.')
    }
    Write-Host ('  running: ' + $ShellScript)
    $t = Get-Date
    $shellOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $ShellScript 2>&1
    $shellExit = $LASTEXITCODE
    $shellOutput | ForEach-Object { Write-Host ('  | ' + $_) }
    if ($shellExit -ne 0) {
        Fail ('shell build failed (exit ' + $shellExit + '). If the shell is still in development, ' +
              'use -SkipShell -ShellExe <existing xputty.exe> (a placeholder is acceptable for pipeline validation).')
    }
    $shellPath = Join-Path $Root 'build\shell\xputty.exe'
    Write-Ok ('shell built in ' + (Format-Elapsed $t) + ': ' + $shellPath)
}
if (-not (Test-Path -LiteralPath $shellPath)) {
    Fail ('Shell executable not found: ' + $shellPath +
          '. Build it with shell\build-shell.ps1, or pass -SkipShell -ShellExe <path>.')
}
$shellHash = (Get-FileHash -LiteralPath $shellPath -Algorithm SHA256).Hash
$puttyHash = (Get-FileHash -LiteralPath $puttyBins['putty'] -Algorithm SHA256).Hash
if ($shellHash -eq $puttyHash) {
    Write-Warn 'xputty.exe is byte-identical to putty.exe (placeholder shell): MSI is only a pipeline/install test, rebuild when the real shell exists'
}
Write-Ok ('shell: ' + $shellPath + '  (' + (Get-Item -LiteralPath $shellPath).Length + ' bytes)')

# ------------------------------------------------------- 3. stage assembly
Write-Step '3/6 Assembling staging directory'
if (Test-Path -LiteralPath $StageDir) { Remove-Item -LiteralPath $StageDir -Recurse -Force }
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

Copy-Item -LiteralPath $shellPath -Destination (Join-Path $StageDir 'xputty.exe') -Force
$staged = @('xputty.exe')
foreach ($target in $puttyTargets) {
    $name = $target + '.exe'
    Copy-Item -LiteralPath $puttyBins[$target] -Destination (Join-Path $StageDir $name) -Force
    $staged += $name
}

# Documentation: PuTTY LICENCE + PuTTY MSI readme (end-user oriented)
$licenseSrc = Join-Path $PuttySourceDir 'LICENCE'
if (-not (Test-Path -LiteralPath $licenseSrc)) { Fail ('PuTTY LICENCE not found: ' + $licenseSrc) }
Copy-Item -LiteralPath $licenseSrc -Destination (Join-Path $StageDir 'LICENCE.txt') -Force
$staged += 'LICENCE.txt'

$readmeCandidates = @(
    (Join-Path $PuttySourceDir 'windows\README-msi.txt'),
    (Join-Path $PuttySourceDir 'README')
)
$readmeSrc = $readmeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $readmeSrc) { Fail 'Neither windows\README-msi.txt nor README found in the PuTTY tree' }
Copy-Item -LiteralPath $readmeSrc -Destination (Join-Path $StageDir 'README-PuTTY.txt') -Force
$staged += 'README-PuTTY.txt'

# Product icon: designer-provided shell\assets\xputty.ico, else PuTTY's own icon
$iconCandidates = @(
    (Join-Path $Root 'shell\assets\xputty.ico'),
    (Join-Path $PuttySourceDir 'windows\putty.ico')
)
$iconSrc = $iconCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iconSrc) { Fail 'No product icon found (expected shell\assets\xputty.ico or putty\windows\putty.ico)' }
Copy-Item -LiteralPath $iconSrc -Destination (Join-Path $StageDir 'xputty.ico') -Force
if ($iconSrc -like '*shell\assets\xputty.ico') {
    Write-Ok ('product icon: ' + $iconSrc)
} else {
    Write-Warn ('shell\assets\xputty.ico not found, falling back to PuTTY icon: ' + $iconSrc)
}
$staged += 'xputty.ico'

# Optional themes\default.ini
$themeCandidates = @(
    (Join-Path $Root 'shell\assets\themes\default.ini'),
    (Join-Path $Root 'shell\themes\default.ini'),
    (Join-Path $Root 'themes\default.ini')
)
$themeSrc = $themeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$includeThemes = '0'
if ($themeSrc) {
    $themeDir = Join-Path $StageDir 'themes'
    New-Item -ItemType Directory -Path $themeDir -Force | Out-Null
    Copy-Item -LiteralPath $themeSrc -Destination (Join-Path $themeDir 'default.ini') -Force
    $staged += 'themes\default.ini'
    $includeThemes = '1'
    Write-Ok ('theme: ' + $themeSrc)
} else {
    Write-Host '  (no themes\default.ini found - optional, skipped)'
}
Write-Ok ('staged ' + $staged.Count + ' files into ' + $StageDir)

# ------------------------------------------------------------ 4. MSI build
Write-Step '4/6 Compiling MSI (candle + light)'
if ($Fresh -and (Test-Path -LiteralPath $wixOutDir)) { Remove-Item -LiteralPath $wixOutDir -Recurse -Force }
if (-not (Test-Path -LiteralPath $wixOutDir)) { New-Item -ItemType Directory -Path $wixOutDir -Force | Out-Null }
$wixobj = Join-Path $wixOutDir 'product.wixobj'

$candleArgs = @(
    '-arch', 'x64',
    ('-dSourceDir=' + $StageDir),
    ('-dProductIcon=' + (Join-Path $StageDir 'xputty.ico')),
    ('-dLicenseRtf=' + $licenseRtf),
    ('-dThemes=' + $includeThemes),
    ('-dScope=perMachine'),
    ('-dProductVersion=' + $Version),
    '-out', $wixobj,
    $productWxs
)
$t = Get-Date
& $candleExe @candleArgs
if ($LASTEXITCODE -ne 0) { Fail ('candle failed (exit ' + $LASTEXITCODE + ')') }
Write-Ok ('candle ok (' + (Format-Elapsed $t) + ')')

$t = Get-Date
& $lightExe -ext WixUIExtension -cultures:en-us -out $msiPath $wixobj
if ($LASTEXITCODE -ne 0) { Fail ('light failed (exit ' + $LASTEXITCODE + ')') }
if (-not (Test-Path -LiteralPath $msiPath)) { Fail ('light did not produce ' + $msiPath) }
if (Test-Path -LiteralPath $sumsPath) { Remove-Item -LiteralPath $sumsPath -Force }
Write-Ok ('light ok (' + (Format-Elapsed $t) + ') -> ' + $msiPath)

# ------------------------------------------------------ 5. SHA256 manifest
Write-Step '5/6 SHA256 manifest'
$sumLines = @()
$manifest = @()
foreach ($rel in $staged) {
    $full = Join-Path $StageDir $rel
    $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
    $size = (Get-Item -LiteralPath $full).Length
    $sumLines += ($hash + '  ' + $rel)
    $manifest += ([pscustomobject]@{ File = $rel; Bytes = $size; Sha256 = $hash })
}
$msiHash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash
$sumLines += ($msiHash + '  ' + (Split-Path -Leaf $msiPath))
$msiSize = (Get-Item -LiteralPath $msiPath).Length

[System.IO.File]::WriteAllLines($sumsPath, $sumLines, (New-Object System.Text.UTF8Encoding($false)))

$msiItem = [pscustomobject]@{ File = (Split-Path -Leaf $msiPath); Bytes = $msiSize; Sha256 = $msiHash }
$manifest | ForEach-Object { Write-Host ('  {0,-24} {1,12}  {2}' -f $_.File, $_.Bytes, $_.Sha256) }
Write-Host ('  ' + ('-' * 78))
Write-Host ('  {0,-24} {1,12}  {2}' -f $msiItem.File, $msiItem.Bytes, $msiItem.Sha256) -ForegroundColor Green
Write-Ok ('manifest written: ' + $sumsPath)

# ----------------------------------------------------------- 6. verify
if ($Verify) {
    Write-Step '6/6 Verifying MSI'
    $verifyScript = Join-Path $installerDir 'verify-msi.ps1'
    if (-not (Test-Path -LiteralPath $verifyScript)) {
        Fail ('-Verify requested but verify script not found: ' + $verifyScript)
    }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $verifyScript -MsiPath $msiPath -StageDir $StageDir -InstallerDir $installerDir -Version $Version -ProductIcon (Join-Path $StageDir 'xputty.ico')
    if ($LASTEXITCODE -ne 0) { Fail ('MSI verification failed (exit ' + $LASTEXITCODE + ')') }
} else {
    Write-Step '6/6 Verification: SKIPPED (pass -Verify to run installer\verify-msi.ps1)'
}

# ------------------------------------------------------------- summary
Write-Step 'Build complete'
Write-Host ('  MSI        : ' + $msiPath)
Write-Host ('  Size       : ' + $msiSize + ' bytes')
Write-Host ('  SHA256     : ' + $msiHash)
Write-Host ('  Manifest   : ' + $sumsPath)
Write-Host ('  Stage dir  : ' + $StageDir)
if ($puttyTimings.Count -gt 0) { Write-Host ('  PuTTY      : ' + ($puttyTimings -join ', ')) }
$totalSeconds = ((Get-Date) - $totalStart).TotalSeconds
Write-Host ('  Total time : ' + (Format-Seconds $totalSeconds))
exit 0
