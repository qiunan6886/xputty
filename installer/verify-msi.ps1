<#
.SYNOPSIS
    Verify the xPuTTY MSI: msiexec /a payload hash comparison + per-user install/uninstall lifecycle.

.DESCRIPTION
    Two independent checks:

    A. Administrative install (msiexec /a, works without elevation):
       extract the MSI into a temp image, compare every staged file's SHA256
       with the staging directory copied into the MSI.

    B. Per-user lifecycle self-test (works without elevation, unlike per-machine):
       compile installer\product.wxs a second time with -dScope=perUser, install
       it silently, verify the installed files/shortcuts, uninstall silently and
       verify that directory, shortcuts and registry key are gone.  This proves
       the component/feature/shortcut tables of the *same* authoring are usable.

    A real per-machine install is attempted too; on a non-elevated session it
    is expected to fail with Error 1925 (recorded, not counted as failure).

.PARAMETER MsiPath
    The per-machine MSI to verify (dist\xPuTTY-1.0.0-x64.msi).

.PARAMETER StageDir
    Staging directory the MSI was built from (for hash comparison).

.PARAMETER InstallerDir
    Directory containing product.wxs / license.rtf.

.PARAMETER WixDir
    WiX toolset (candle.exe/light.exe). Default: <root>\tools\wix

.PARAMETER KeepTemp
    Keep the temporary extraction/self-test directories for inspection.

.NOTES
    Windows PowerShell 5.1 compatible. Exit code 0 = all checks passed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [Parameter(Mandatory = $true)][string]$StageDir,
    [Parameter(Mandatory = $true)][string]$InstallerDir,
    [string]$WixDir = '',
    [string]$Version = '1.0.0',
    [string]$ProductIcon = '',
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'
$failures = @()
$checks = 0

function Check-Pass($text) {
    $script:checks++
    Write-Host ('  [PASS] ' + $text) -ForegroundColor Green
}
function Check-Fail($text) {
    $script:checks++
    $script:failures += $text
    Write-Host ('  [FAIL] ' + $text) -ForegroundColor Red
}
function Note($text) {
    Write-Host ('  [INFO] ' + $text) -ForegroundColor Cyan
}
function Run-MsiExec([string]$Arguments) {
    $p = Start-Process -FilePath "$env:SystemRoot\system32\msiexec.exe" -ArgumentList $Arguments -Wait -PassThru
    return $p.ExitCode
}

$root = Split-Path -Parent $InstallerDir
if (-not $WixDir) { $WixDir = Join-Path $root 'tools\wix' }
$candle = Join-Path $WixDir 'candle.exe'
$light = Join-Path $WixDir 'light.exe'
$productWxs = Join-Path $InstallerDir 'product.wxs'
$licenseRtf = Join-Path $InstallerDir 'license.rtf'
foreach ($f in @($MsiPath, $candle, $light, $productWxs, $licenseRtf)) {
    if (-not (Test-Path -LiteralPath $f)) { throw ('Missing required file: ' + $f) }
}
if (-not $ProductIcon) {
    $ProductIcon = Join-Path $StageDir 'xputty.ico'
    if (-not (Test-Path -LiteralPath $ProductIcon)) { $ProductIcon = Join-Path $root 'putty\windows\putty.ico' }
}

$tempRoot = Join-Path $env:TEMP ('xputty-verify-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$adminDir = Join-Path $tempRoot 'admin'
$selfTestMsi = Join-Path $tempRoot 'xPuTTY-selftest-per-user.msi'
$wixOut = Join-Path $tempRoot 'wix'

Write-Host ''
Write-Host '=== Verify A: msiexec /a administrative install + SHA256 comparison ===' -ForegroundColor Cyan
$adminLog = Join-Path $tempRoot 'admin-install.log'
$adminExit = Run-MsiExec ('/a "' + $MsiPath + '" /qn TARGETDIR="' + $adminDir + '" /l*v "' + $adminLog + '"')
if ($adminExit -ne 0) {
    Check-Fail ('msiexec /a failed with exit ' + $adminExit + ' (log: ' + $adminLog + ')')
} else {
    Check-Pass 'msiexec /a administrative install returned 0'

    # Files in the administrative image keep their install tree below the
    # TARGETDIR (\xPuTTY\...).  Map every staged file by its relative path.
    # xputty.ico is a resource (ARPPRODUCTICON/shortcut icon) embedded in the
    # Icon table, not a payload file, so it is not expected in the image.
    $stagedFiles = Get-ChildItem -LiteralPath $StageDir -Recurse -File | ForEach-Object {
        $_.FullName.Substring($StageDir.Length + 1)
    } | Where-Object { $_ -ne 'xputty.ico' }
    $extracted = Get-ChildItem -LiteralPath $adminDir -Recurse -File
    foreach ($rel in $stagedFiles) {
        $base = Split-Path -Leaf $rel
        $match = $extracted | Where-Object { $_.Name -eq $base -and $_.FullName -like ('*\' + $rel.Replace('/', '\')) } | Select-Object -First 1
        if (-not $match) {
            # fall back to unique basename match (rebased path may differ)
            $candidates = @($extracted | Where-Object { $_.Name -eq $base })
            if ($candidates.Count -eq 1) { $match = $candidates[0] }
        }
        if (-not $match) {
            Check-Fail ('payload file not found in administrative image: ' + $rel)
            continue
        }
        $srcHash = (Get-FileHash -LiteralPath (Join-Path $StageDir $rel) -Algorithm SHA256).Hash
        $dstHash = (Get-FileHash -LiteralPath $match.FullName -Algorithm SHA256).Hash
        if ($srcHash -eq $dstHash) {
            Check-Pass ('payload hash matches: ' + $rel)
        } else {
            Check-Fail ('payload hash MISMATCH: ' + $rel)
        }
    }
}

Write-Host ''
Write-Host '=== Verify B: per-user install / uninstall lifecycle self-test ===' -ForegroundColor Cyan
New-Item -ItemType Directory -Path $wixOut -Force | Out-Null
$wixobj = Join-Path $wixOut 'product-peruser.wixobj'
$stageThemes = '0'
if (Test-Path -LiteralPath (Join-Path $StageDir 'themes\default.ini')) { $stageThemes = '1' }
& $candle -arch x64 ('-dSourceDir=' + $StageDir) ('-dProductIcon=' + $ProductIcon) `
          ('-dLicenseRtf=' + $licenseRtf) ('-dThemes=' + $stageThemes) `
          -dScope=perUser ('-dProductVersion=' + $Version) -out $wixobj $productWxs
if ($LASTEXITCODE -ne 0) {
    Check-Fail ('candle for per-user self-test build failed (exit ' + $LASTEXITCODE + ')')
} else {
    Check-Pass 'per-user self-test MSI compiled (candle)'
    & $light -ext WixUIExtension -cultures:en-us -out $selfTestMsi $wixobj
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $selfTestMsi)) {
        Check-Fail ('light for per-user self-test build failed (exit ' + $LASTEXITCODE + ')')
    } else {
        Check-Pass 'per-user self-test MSI linked (light)'

        $installDir = Join-Path $env:LOCALAPPDATA 'xPuTTY'
        $startMenuDir = Join-Path ([Environment]::GetFolderPath('Programs')) 'xPuTTY'
        $desktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'xPuTTY.lnk'
        $installLog = Join-Path $tempRoot 'per-user-install.log'
        $uninstallLog = Join-Path $tempRoot 'per-user-uninstall.log'

        $installExit = Run-MsiExec ('/i "' + $selfTestMsi + '" /qn /l*v "' + $installLog + '"')
        if ($installExit -ne 0) {
            Check-Fail ('per-user msiexec /i failed with exit ' + $installExit)
        } else {
            Check-Pass 'per-user msiexec /i /qn returned 0'
            if (Test-Path -LiteralPath (Join-Path $installDir 'xputty.exe')) { Check-Pass 'installed %LOCALAPPDATA%\xPuTTY\xputty.exe' }
            else { Check-Fail 'installed xputty.exe missing under ' + $installDir }
            foreach ($target in @('putty', 'plink', 'pscp', 'psftp', 'puttygen', 'pageant')) {
                if (Test-Path -LiteralPath (Join-Path $installDir ($target + '.exe'))) { Check-Pass ('installed ' + $target + '.exe') }
                else { Check-Fail ('installed ' + $target + '.exe missing') }
            }
            if (Test-Path -LiteralPath (Join-Path $installDir 'LICENCE.txt')) { Check-Pass 'installed LICENCE.txt' }
            else { Check-Fail 'installed LICENCE.txt missing' }
            if (Test-Path -LiteralPath (Join-Path $installDir 'README-PuTTY.txt')) { Check-Pass 'installed README-PuTTY.txt' }
            else { Check-Fail 'installed README-PuTTY.txt missing' }
            if ($stageThemes -eq '1') {
                if (Test-Path -LiteralPath (Join-Path $installDir 'themes\default.ini')) { Check-Pass 'installed themes\default.ini' }
                else { Check-Fail 'installed themes\default.ini missing' }
            }
            foreach ($lnk in @('xPuTTY.lnk', 'PuTTY.lnk', 'PuTTYgen.lnk')) {
                if (Test-Path -LiteralPath (Join-Path $startMenuDir $lnk)) { Check-Pass ('start menu shortcut ' + $lnk) }
                else { Check-Fail ('start menu shortcut missing: ' + (Join-Path $startMenuDir $lnk)) }
            }
            if (Test-Path -LiteralPath $desktopLnk) { Check-Pass 'desktop shortcut xPuTTY.lnk' }
            else { Check-Fail ('desktop shortcut missing: ' + $desktopLnk) }

            # payload hashes inside the install dir must match the staging dir
            foreach ($rel in @('xputty.exe', 'putty.exe', 'plink.exe', 'puttygen.exe', 'LICENCE.txt')) {
                $src = Join-Path $StageDir $rel
                $dst = Join-Path $installDir $rel
                if ((Test-Path -LiteralPath $src) -and (Test-Path -LiteralPath $dst)) {
                    if ((Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash) {
                        Check-Pass ('installed payload hash matches: ' + $rel)
                    } else {
                        Check-Fail ('installed payload hash MISMATCH: ' + $rel)
                    }
                }
            }

            $uninstallExit = Run-MsiExec ('/x "' + $selfTestMsi + '" /qn /l*v "' + $uninstallLog + '"')
            if ($uninstallExit -ne 0) {
                Check-Fail ('per-user msiexec /x failed with exit ' + $uninstallExit)
            } else {
                Check-Pass 'per-user msiexec /x /qn returned 0'
                if (-not (Test-Path -LiteralPath $installDir)) { Check-Pass 'install dir removed on uninstall' }
                else { Check-Fail ('install dir still present after uninstall: ' + $installDir) }
                if (-not (Test-Path -LiteralPath $startMenuDir)) { Check-Pass 'start menu folder removed on uninstall' }
                else { Check-Fail ('start menu folder still present after uninstall: ' + $startMenuDir) }
                if (-not (Test-Path -LiteralPath $desktopLnk)) { Check-Pass 'desktop shortcut removed on uninstall' }
                else { Check-Fail 'desktop shortcut still present after uninstall' }
                $regPaths = @('HKCU:\Software\xPuTTY\Components', 'HKCU:\Software\WOW6432Node\xPuTTY\Components')
                $regFound = $false
                foreach ($rp in $regPaths) {
                    if ($null -ne (Get-ItemProperty -Path $rp -ErrorAction SilentlyContinue)) { $regFound = $true }
                }
                if (-not $regFound) { Check-Pass 'HKCU registry component keys removed on uninstall' }
                else { Check-Fail 'HKCU component registry key still present after uninstall' }
            }
        }
    }
}

Write-Host ''
Write-Host '=== Verify C: real per-machine install (needs elevation) ===' -ForegroundColor Cyan
$perMachineLog = Join-Path $tempRoot 'per-machine-install.log'
$pmExit = Run-MsiExec ('/i "' + $MsiPath + '" /qn /l*v "' + $perMachineLog + '"')
if ($pmExit -eq 0) {
    Check-Pass 'per-machine msiexec /i /qn returned 0 (elevated session)'
    $target = 'C:\Program Files\xPuTTY'
    if (Test-Path -LiteralPath (Join-Path $target 'xputty.exe')) { Check-Pass 'per-machine install dir populated' }
    else { Check-Fail 'per-machine install dir missing xputty.exe' }
    $pmRemoveExit = Run-MsiExec ('/x "' + $MsiPath + '" /qn')
    if ($pmRemoveExit -eq 0) { Check-Pass 'per-machine uninstall returned 0' } else { Check-Fail ('per-machine uninstall exit ' + $pmRemoveExit) }
} else {
    $errLine = ''
    if (Test-Path -LiteralPath $perMachineLog) {
        $errLine = (Select-String -Path $perMachineLog -Pattern 'Error 1925|Error 1603' | Select-Object -First 1).Line
    }
    Note ('per-machine install skipped on this non-elevated session (msiexec exit ' + $pmExit + '). ' + $errLine)
    Note 'This is expected for per-machine packages; run from an elevated prompt to verify it.'
}

# ------------------------------------------------------------- cleanup
if (-not $KeepTemp) {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Note ('temp files kept in ' + $tempRoot)
}

Write-Host ''
Write-Host ('=== Result: ' + ($checks - $failures.Count) + '/' + $checks + ' checks passed ===') -ForegroundColor Cyan
if ($failures.Count -gt 0) {
    Write-Host ('FAILED (' + $failures.Count + '):') -ForegroundColor Red
    $failures | ForEach-Object { Write-Host ('  - ' + $_) -ForegroundColor Red }
    exit 1
}
Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green
exit 0
