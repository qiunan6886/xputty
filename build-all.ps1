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
    [switch]$Verify,
    [switch]$AllowPlaceholder
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

# Newest modification time across the shell sources (cs files, manifest, icon).
function Get-ShellSourceNewestTime {
    $newest = [datetime]::MinValue
    $srcDir = Join-Path $Root 'shell\src'
    if (Test-Path -LiteralPath $srcDir) {
        Get-ChildItem -LiteralPath $srcDir -Recurse -File | ForEach-Object {
            if ($_.LastWriteTime -gt $newest) { $newest = $_.LastWriteTime }
        }
    }
    foreach ($extra in @((Join-Path $Root 'shell\app.manifest'), (Join-Path $Root 'shell\assets\xputty.ico'))) {
        if (Test-Path -LiteralPath $extra) {
            $m = (Get-Item -LiteralPath $extra).LastWriteTime
            if ($m -gt $newest) { $newest = $m }
        }
    }
    return $newest
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
$sumsPath     = Join-Path $DistDir 'SHA256SUMS.txt'
$buildInfoPath = Join-Path $DistDir 'BUILD-INFO.txt'
$placeholderWarningPath = Join-Path $DistDir 'PLACEHOLDER-BUILD-WARNING.txt'

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
    # Safety net: warn (loudly) if the fork sources changed after the last build.
    $forkNewest = Get-ChildItem -LiteralPath $PuttySourceDir -Recurse -File |
                  Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $forkExe = Join-Path $PuttyBuildDir 'putty.exe'
    if ((Test-Path -LiteralPath $forkExe) -and $null -ne $forkNewest) {
        $forkExeTime = (Get-Item -LiteralPath $forkExe).LastWriteTime
        if ($forkNewest.LastWriteTime -gt $forkExeTime) {
            Write-Warn ('-SkipPutty: fork sources are NEWER than ' + $forkExe + ' (source: ' + $forkNewest.LastWriteTime +
                        ', binary: ' + $forkExeTime + '). You are reusing stale binaries - drop -SkipPutty to rebuild.')
        }
    }
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
$shellDefault = Join-Path $Root 'build\shell\xputty.exe'
$shellPath = ''
$shellIsPlaceholder = $false

if ($ShellExe) {
    $shellPath = $ShellExe
    Write-Warn ('using externally supplied shell binary: ' + $shellPath)
} else {
    if ($SkipShell) {
        Write-Host ('  -SkipShell: reusing ' + $shellDefault)
    } else {
        if (-not (Test-Path -LiteralPath $ShellScript)) {
            Fail ('Shell build script not found: ' + $ShellScript +
                  '. The WinForms shell is still in development; use -AllowPlaceholder to validate the pipeline.')
        }
        Write-Host ('  running: ' + $ShellScript)
        $t = Get-Date
        $shellOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $ShellScript 2>&1
        $shellExit = $LASTEXITCODE
        $shellOutput | ForEach-Object { Write-Host ('  | ' + $_) }
        if ($shellExit -eq 0) {
            Write-Ok ('shell built in ' + (Format-Elapsed $t) + ': ' + $shellDefault)
        } else {
            # A running xputty.exe locks the output file (csc error CS2012). If the
            # existing binary is at least as new as the sources, reuse it instead
            # of failing: the binary is a genuine build, just currently in use by
            # the developer's test session.
            $fallbackOk = $false
            if (Test-Path -LiteralPath $shellDefault) {
                $fallbackOk = ((Get-Item -LiteralPath $shellDefault).LastWriteTime -ge (Get-ShellSourceNewestTime))
            }
            if ($fallbackOk) {
                Write-Warn ('shell build failed (exit ' + $shellExit + ') but the existing build\shell\xputty.exe is newer than ' +
                            'the shell sources (likely because a running xputty.exe locks the file); reusing it')
            } elseif (-not $AllowPlaceholder) {
                Fail ('shell build failed (exit ' + $shellExit + '). The WinForms shell is not ready (or its output is ' +
                      'locked by a running xputty.exe). Re-run when dev produces build\shell\xputty.exe, or pass ' +
                      '-AllowPlaceholder to validate the pipeline with a putty.exe placeholder (such an MSI is clearly ' +
                      'marked and must not be shipped).')
            } else {
                Write-Warn ('shell build failed (exit ' + $shellExit + '); continuing because -AllowPlaceholder was given')
            }
        }
    }
    $shellPath = $shellDefault
}

$placeholderSource = Join-Path $Root 'build\_placeholder\xputty.exe'

if ($shellPath -and (Test-Path -LiteralPath $shellPath)) {
    $shellHash = (Get-FileHash -LiteralPath $shellPath -Algorithm SHA256).Hash
    $puttyHash = (Get-FileHash -LiteralPath $puttyBins['putty'] -Algorithm SHA256).Hash
    if ($shellHash -eq $puttyHash) { $shellIsPlaceholder = $true }

    # Freshness check: the binary must not be older than the shell sources.
    # Skipped for explicit -ShellExe overrides (deliberate external binary).
    if (-not $ShellExe -and -not $shellIsPlaceholder) {
        $newestSource = Get-ShellSourceNewestTime
        $shellMtime = (Get-Item -LiteralPath $shellPath).LastWriteTime
        if ($newestSource -gt $shellMtime) {
            if (-not $AllowPlaceholder) {
                Fail ('build\shell\xputty.exe is OLDER than the shell sources (exe: ' + $shellMtime +
                      ', newest source: ' + $newestSource + '). Run shell\build-shell.ps1, or pass ' +
                      '-AllowPlaceholder to accept this binary for pipeline validation only.')
            }
            Write-Warn ('shell binary is older than the shell sources (exe: ' + $shellMtime + ', newest source: ' + $newestSource + ')')
        }
    }
} else {
    if (-not $AllowPlaceholder) {
        Fail ('Shell executable missing: ' + $shellPath + '. The WinForms shell is still in development. ' +
              'Run shell\build-shell.ps1 once it is ready, or pass -AllowPlaceholder to validate the pipeline ' +
              'with a putty.exe placeholder (never ship that MSI).')
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $placeholderSource) -Force | Out-Null
    Copy-Item -LiteralPath $puttyBins['putty'] -Destination $placeholderSource -Force
    $shellPath = $placeholderSource
    $shellIsPlaceholder = $true
    Write-Warn 'Shell missing: using putty.exe as a PLACEHOLDER xputty.exe (pipeline validation only)'
}

if ($shellIsPlaceholder -and -not $AllowPlaceholder) {
    Fail ('build\shell\xputty.exe is byte-identical to putty.exe (placeholder). Refusing to build a release MSI - ' +
          'wait for the real shell, or pass -AllowPlaceholder for a clearly marked pipeline-validation MSI.')
}

$msiSuffix = ''
if ($shellIsPlaceholder) { $msiSuffix = '-PLACEHOLDER' }
$msiPath = Join-Path $DistDir ('xPuTTY-' + $Version + '-x64' + $msiSuffix + '.msi')

$shellLabel = ''
if ($shellIsPlaceholder) { $shellLabel = '  [PLACEHOLDER - NOT FOR RELEASE]' }
Write-Ok ('shell: ' + $shellPath + '  (' + (Get-Item -LiteralPath $shellPath).Length + ' bytes)' + $shellLabel)

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

# Optional themes\default.ini (installer/assets is the repo default; shell dirs win if present)
$themeCandidates = @(
    (Join-Path $Root 'shell\assets\themes\default.ini'),
    (Join-Path $Root 'shell\themes\default.ini'),
    (Join-Path $Root 'installer\assets\themes\default.ini'),
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

# ------------------------------------------- 5b. build info / placeholder markers
# The real build removes any stale placeholder artifacts; a placeholder build
# leaves an explicit warning file + a *-PLACEHOLDER.msi name so it can never be
# mistaken for a shippable release.
Get-ChildItem -LiteralPath $DistDir -Filter 'xPuTTY-*-PLACEHOLDER.msi' -File -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.FullName -ne $msiPath) { Remove-Item -LiteralPath $_.FullName -Force }
}
if ($shellIsPlaceholder) {
    $warnText = @(
        'xPuTTY PLACEHOLDER BUILD - NOT FOR RELEASE',
        '==========================================',
        '',
        'This MSI was built with -AllowPlaceholder because build\shell\xputty.exe',
        '(the WinForms shell) does not exist yet or is not newer than the shell sources.',
        'xputty.exe inside this MSI is a copy of putty.exe and only proves that the',
        'build/install pipeline works.',
        '',
        'Do not distribute this MSI. Once the shell is built by dev',
        '(shell\build-shell.ps1 -> build\shell\xputty.exe), run:',
        '    powershell -NoProfile -ExecutionPolicy Bypass -File D:\workspace\xputty\build-all.ps1 -Verify',
        'and the real dist\xPuTTY-<version>-x64.msi will be produced instead.',
        '',
        ('Generated: ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')),
        ('MSI: ' + $msiPath)
    )
    [System.IO.File]::WriteAllLines($placeholderWarningPath, $warnText, (New-Object System.Text.UTF8Encoding($false)))
    Write-Warn ('PLACEHOLDER build: wrote ' + $placeholderWarningPath)
} else {
    if (Test-Path -LiteralPath $placeholderWarningPath) { Remove-Item -LiteralPath $placeholderWarningPath -Force }
}

# BUILD-INFO.txt: one place to see versions, hashes and what went into the MSI
$commitId = 'unknown'
$commitFile = Join-Path $PuttyBuildDir 'CMakeFiles\cmake_commit.c'
if (Test-Path -LiteralPath $commitFile) {
    $cm = [regex]::Match([System.IO.File]::ReadAllText($commitFile), 'commitid\[\] = "([0-9a-f]+)"')
    if ($cm.Success) { $commitId = $cm.Groups[1].Value }
}
$shellSha = (Get-FileHash -LiteralPath $shellPath -Algorithm SHA256).Hash
$info = @()
$info += 'xPuTTY build info'
$info += '================'
$info += ('Built at      : ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
$info += ('Version       : ' + $Version)
$info += ('Config        : ' + $Config)
$info += ('MSI           : ' + (Split-Path -Leaf $msiPath))
$info += ('MSI SHA256    : ' + $msiHash)
$info += ('MSI size      : ' + $msiSize + ' bytes')
$info += ('Placeholder   : ' + $shellIsPlaceholder)
$info += ('Shell         : ' + $shellPath)
$info += ('Shell SHA256  : ' + $shellSha)
$info += ('PuTTY fork    : ' + $PuttySourceDir)
$info += ('PuTTY build   : ' + $PuttyBuildDir)
$info += ('PuTTY commit  : ' + $commitId)
# Fingerprint of the xPuTTY-specific fork files, so a frozen source state can be
# reproduced/verified later (the pristine PuTTY part is pinned by $commitId).
$info += 'Fork sources  :'
foreach ($rel in @('windows\window.c', 'windows\xputty.c', 'windows\xputty.h', 'windows\putty.c', 'windows\CMakeLists.txt')) {
    $p = Join-Path $PuttySourceDir $rel
    if (Test-Path -LiteralPath $p) {
        $info += ('  ' + (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash + '  ' + $rel)
    }
}
if (Test-Path -LiteralPath (Join-Path $Root 'shell\src')) {
    $shellNewest = Get-ChildItem -LiteralPath (Join-Path $Root 'shell\src') -Recurse -File |
                   Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $info += ('Shell newest  : ' + $shellNewest.LastWriteTime + '  ' + $shellNewest.Name)
}
if ($puttyTimings.Count -gt 0) { $info += ('PuTTY timings : ' + ($puttyTimings -join ', ')) }
$info += ('Staged files  : ' + $staged.Count)
foreach ($m in $manifest) { $info += ('  ' + $m.Sha256 + '  ' + $m.File) }
$info += ('  ' + $msiHash + '  ' + (Split-Path -Leaf $msiPath))
[System.IO.File]::WriteAllLines($buildInfoPath, $info, (New-Object System.Text.UTF8Encoding($false)))
Write-Ok ('build info written: ' + $buildInfoPath)

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
Write-Host ('  Shell      : ' + $shellPath)
Write-Host ('  Build info : ' + $buildInfoPath)
if ($puttyTimings.Count -gt 0) { Write-Host ('  PuTTY      : ' + ($puttyTimings -join ', ')) }
$totalSeconds = ((Get-Date) - $totalStart).TotalSeconds
Write-Host ('  Total time : ' + (Format-Seconds $totalSeconds))
if ($shellIsPlaceholder) {
    Write-Warn 'THIS IS A PLACEHOLDER BUILD (xputty.exe = putty.exe). Not for release.'
}
exit 0
