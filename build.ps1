# Builds "TCIA Downloader.exe" with the C# compiler that ships with Windows (.NET Framework 4.8).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$src = Join-Path $here 'src\*.cs'
$manifest = Join-Path $here 'src\app.manifest'
$ico = Join-Path $here 'src\app.ico'
$dist = Join-Path $here 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$common = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', '/warn:4',
    "/win32manifest:$manifest",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.Net.Http.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
    '/r:System.Web.Extensions.dll')

# Pass 1: build once without an icon and let the app draw its own icon file.
$tmp = Join-Path $dist '_icon-builder.exe'
& $csc @common "/out:$tmp" $src
if ($LASTEXITCODE -ne 0) { throw "Compile failed" }
$p = Start-Process -FilePath $tmp -ArgumentList @('--write-icon', "`"$ico`"") -Wait -PassThru
if ($p.ExitCode -ne 0 -or -not (Test-Path $ico)) { throw "Icon generation failed" }
Remove-Item $tmp -Force

# Pass 2: the real build, with the icon embedded.
$out = Join-Path $dist 'TCIA Downloader.exe'
& $csc @common "/win32icon:$ico" "/out:$out" $src
if ($LASTEXITCODE -ne 0) { throw "Compile failed" }
Write-Host "Built $out"
