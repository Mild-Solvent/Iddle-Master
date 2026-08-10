# Builds dist\IdleMaster.exe using the in-box .NET Framework compiler.
# No SDK, no NuGet, no internet needed.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$dist = Join-Path $root 'dist'
$out  = Join-Path $dist 'IdleMaster.exe'

if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

$refs = @(
  'System.dll'
  'System.Core.dll'
  'System.Drawing.dll'
  'System.Windows.Forms.dll'
  'System.ServiceProcess.dll'
) | ForEach-Object { "/r:$_" }

& $csc /nologo /target:winexe /platform:x64 /optimize+ `
  /win32manifest:"$root\src\app.manifest" `
  /out:"$out" $refs "$root\src\IdleMaster.cs"

if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

Write-Host ""
Write-Host "built: $out" -ForegroundColor Green
Write-Host ("size : {0:N0} bytes" -f (Get-Item $out).Length)
