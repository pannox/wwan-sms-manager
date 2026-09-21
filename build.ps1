# Build WWAN SMS Manager (portable single-file EXE)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src"
$outDir = Join-Path $root "release"
$out = Join-Path $outDir "WwanSmsManager.exe"
$ico = Join-Path $root "assets\app.ico"
$manifest = Join-Path $src "app.manifest"

New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wm = "C:\Windows\System32\WinMetadata"
$dll = Join-Path $fx "System.Runtime.WindowsRuntime.dll"

if (-not (Test-Path $csc)) { throw "csc.exe not found. Need .NET Framework 4.x developer tools / Windows." }

& $csc @(
  "/nologo"
  "/target:winexe"
  "/platform:anycpu"
  "/optimize+"
  "/out:$out"
  "/win32manifest:$manifest"
  "/win32icon:$ico"
  "/resource:$dll,System.Runtime.WindowsRuntime.dll"
  "/reference:$fx\System.dll"
  "/reference:$fx\System.Core.dll"
  "/reference:$fx\System.Drawing.dll"
  "/reference:$fx\System.Windows.Forms.dll"
  "/reference:$fx\System.Runtime.dll"
  "/reference:$dll"
  "/reference:$wm\Windows.Foundation.winmd"
  "/reference:$wm\Windows.Devices.winmd"
  (Join-Path $src "Program.cs")
)

if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "OK -> $out ($((Get-Item $out).Length) bytes)"
