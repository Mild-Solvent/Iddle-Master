# Builds dist\IdleMasterRebuild.exe, dist\IdleMaster.exe and dist\IdleMasterSetup.exe
# using the in-box .NET Framework compiler. No SDK, no NuGet, no internet needed.
#
# Three exes, nested like dolls:
#   IdleMasterRebuild.exe   goes inside every backup kit the app writes
#   IdleMaster.exe          the app; carries the rebuild exe and the icon inside it
#   IdleMasterSetup.exe     the installer; carries the app inside it
# so each one is built from the one that just came out, in that order.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$dist = Join-Path $root 'dist'
$out  = Join-Path $dist 'IdleMaster.exe'
$setup = Join-Path $dist 'IdleMasterSetup.exe'
$rebuild = Join-Path $dist 'IdleMasterRebuild.exe'
$icon = Join-Path $root 'src\idlemaster.ico'

if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
if (-not (Test-Path $icon)) { throw "icon missing - run make-icon.ps1 first" }
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

$refs = @(
  'System.dll'
  'System.Core.dll'
  'System.Drawing.dll'
  'System.Windows.Forms.dll'
  'System.ServiceProcess.dll'
  'System.IO.Compression.dll'
) | ForEach-Object { "/r:$_" }

# A running copy holds a lock on the exe. Windows allows renaming a running
# binary, so park it instead of failing the build.
foreach ($target in @($out, $setup, $rebuild)) {
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

# One argument each, no embedded quotes - PowerShell quotes the whole thing
# because the path has a space in it, and csc unwraps that correctly.
$iconArg = "/win32icon:$icon"

& $csc /nologo /target:winexe /platform:x64 /optimize+ `
  /win32manifest:"$root\src\app.manifest" $iconArg `
  /out:"$rebuild" $refs "$root\src\Rebuild.cs"
if ($LASTEXITCODE -ne 0) { throw "rebuild build failed ($LASTEXITCODE)" }

$resRebuild = "/resource:$rebuild,IdleMasterRebuild.exe"
$resIcon = "/resource:$icon,idlemaster.ico"

& $csc /nologo /target:winexe /platform:x64 /optimize+ `
  /win32manifest:"$root\src\app.manifest" $iconArg $resRebuild $resIcon `
  /out:"$out" $refs "$root\src\IdleMaster.cs" "$root\src\Theme.cs" "$root\src\Ui.cs" `
  "$root\src\Cleanup.cs" "$root\src\DiskScan.cs" "$root\src\TreeMap.cs" "$root\src\WizTree.cs" "$root\src\Debloat.cs" "$root\src\Backup.cs" `
  "$root\src\NetGuard.cs" "$root\src\Vpn.cs" "$root\src\Procs.cs" `
  "$root\src\SoloInstance.cs" "$root\src\Feedback.cs"
if ($LASTEXITCODE -ne 0) { throw "app build failed ($LASTEXITCODE)" }

$resource = "/resource:$out,IdleMaster.exe"

& $csc /nologo /target:winexe /platform:x64 /optimize+ $iconArg `
  $resource `
  /out:"$setup" $refs "$root\src\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "setup build failed ($LASTEXITCODE)" }

Write-Host ""
foreach ($f in @($rebuild, $out, $setup)) {
  Write-Host "built: $f" -ForegroundColor Green
  Write-Host ("size : {0:N0} bytes" -f (Get-Item $f).Length)
}
