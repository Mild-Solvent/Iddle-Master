# Builds dist\IdleMaster.exe and dist\IdleMasterSetup.exe using the in-box
# .NET Framework compiler. No SDK, no NuGet, no internet needed.
#
# The setup carries the app inside it as a resource, so the app is always built
# first and the setup is always built from the exe that just came out.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$dist = Join-Path $root 'dist'
$out  = Join-Path $dist 'IdleMaster.exe'
$setup = Join-Path $dist 'IdleMasterSetup.exe'

if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

$refs = @(
  'System.dll'
  'System.Core.dll'
  'System.Drawing.dll'
  'System.Windows.Forms.dll'
  'System.ServiceProcess.dll'
) | ForEach-Object { "/r:$_" }

# A running copy holds a lock on the exe. Windows allows renaming a running
# binary, so park it instead of failing the build.
foreach ($target in @($out, $setup)) {
  if (Test-Path $target) {
    try { Remove-Item $target -Force }
    catch {
      $parked = "$target.old-" + (Get-Date -Format 'yyyyMMddHHmmss')
      Rename-Item $target (Split-Path $parked -Leaf)
      Write-Host "in use - parked as $(Split-Path $parked -Leaf)" -ForegroundColor Yellow
    }
  }
}
Get-ChildItem $dist -Filter '*.old-*' -ErrorAction SilentlyContinue | ForEach-Object {
  try { Remove-Item $_.FullName -Force } catch { }
}

& $csc /nologo /target:winexe /platform:x64 /optimize+ `
  /win32manifest:"$root\src\app.manifest" `
  /out:"$out" $refs "$root\src\IdleMaster.cs"
if ($LASTEXITCODE -ne 0) { throw "app build failed ($LASTEXITCODE)" }

# One argument, no embedded quotes - PowerShell quotes it as a whole because the
# path has a space in it, and csc unwraps that correctly.
$resource = "/resource:$out,IdleMaster.exe"

& $csc /nologo /target:winexe /platform:x64 /optimize+ `
  $resource `
  /out:"$setup" $refs "$root\src\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "setup build failed ($LASTEXITCODE)" }

Write-Host ""
Write-Host "built: $out" -ForegroundColor Green
Write-Host ("size : {0:N0} bytes" -f (Get-Item $out).Length)
Write-Host "built: $setup" -ForegroundColor Green
Write-Host ("size : {0:N0} bytes" -f (Get-Item $setup).Length)
