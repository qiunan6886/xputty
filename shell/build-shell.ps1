# Build the xPuTTY WinForms shell (xputty.exe) with the Roslyn compiler
# that ships with Visual Studio BuildTools.  No .NET SDK required: the
# shell targets .NET Framework 4.8, which is part of Windows 10/11.
param(
    [string]$SrcDir = "$PSScriptRoot\src",
    [string]$OutDir = "D:\workspace\xputty\build\shell",
    [switch]$Debug
)

$ErrorActionPreference = 'Stop'

$csc = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
if (-not (Test-Path $csc)) {
    throw "Roslyn csc.exe not found at $csc"
}
$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

New-Item -ItemType Directory -Force $OutDir | Out-Null

$sources = Get-ChildItem -Path $SrcDir -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { throw "No .cs sources found under $SrcDir" }

$refs = @(
    "$fw\System.dll",
    "$fw\System.Core.dll",
    "$fw\System.Drawing.dll",
    "$fw\System.Windows.Forms.dll",
    "$fw\System.Runtime.Serialization.dll",
    "$fw\System.Xml.dll"
) | ForEach-Object { "/r:`"$_`"" }

$args = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/platform:anycpu',
    '/langversion:7.3',
    "/out:`"$OutDir\xputty.exe`"",
    "/win32manifest:`"$PSScriptRoot\app.manifest`""
)

$icon = Join-Path $PSScriptRoot 'assets\xputty.ico'
if (Test-Path $icon) { $args += "/win32icon:`"$icon`"" }

if ($Debug) { $args += '/debug+'; $args += '/define:DEBUG' } else { $args += '/debug-' }

$args += $refs
$args += $sources

Write-Host "Compiling $($sources.Count) source files ..."
& $csc @args
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "BUILD OK -> $OutDir\xputty.exe" -ForegroundColor Green
