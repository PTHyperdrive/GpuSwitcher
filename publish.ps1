# Builds everything and installs GpuSwitcher (tray app + gpusw CLI + boot-guard service + native hook) into one folder.
#   .\publish.ps1                       -> wherever the boot guard is installed, else %ProgramFiles%\GpuSwitcher
#   .\publish.ps1 -InstallDir D:\x      -> somewhere else
#   .\publish.ps1 -SkipNative           -> reuse the prebuilt native\ binaries
param(
    [string]$InstallDir,
    [switch]$SkipNative
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $InstallDir) {
    $svcPath = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\GpuSwitcherBootGuard' -ErrorAction SilentlyContinue).ImagePath
    $InstallDir = if ($svcPath) { Split-Path $svcPath.Trim('"') } else { Join-Path $env:ProgramFiles 'GpuSwitcher' }
}

if (-not $SkipNative) { & (Join-Path $root 'src\Hook\build.ps1') | Out-Null }

# Stage inside the repo: %TEMP% can be an 8.3 short path, which breaks the relative-path math below.
$stage = Join-Path $root 'out\stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
$stage = (Resolve-Path $stage).Path
foreach ($proj in 'src\App\GpuSwitcher.csproj', 'src\Cli\gpusw.csproj', 'src\Service\GpuSwitcherSvc.csproj') {
    dotnet publish (Join-Path $root $proj) -c Release -o $stage -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}

# The boot-guard service runs from the install folder: stop it for the copy, start it again afterwards.
# (Once it is installed the folder is admin-only, so run this script elevated.)
$svc = Get-Service GpuSwitcherBootGuard -ErrorAction SilentlyContinue
$restartService = $svc -and $svc.Status -eq 'Running'
if ($restartService) { Write-Host 'Stopping the boot-guard service…'; Stop-Service GpuSwitcherBootGuard }

$running = Get-Process GpuSwitcher -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallDir\*" }
if ($running) {
    Write-Host 'Stopping the running GpuSwitcher…'
    # Closing the window only hides it to the tray; config is saved on every change, so ending it is safe.
    $running | ForEach-Object { $_.Kill(); $_.WaitForExit(3000) | Out-Null }
}

New-Item -ItemType Directory -Force $InstallDir, (Join-Path $InstallDir 'native') | Out-Null
foreach ($file in Get-ChildItem $stage -Recurse -File) {
    $rel = $file.FullName.Substring($stage.Length + 1)
    $dest = Join-Path $InstallDir $rel
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    try { Copy-Item $file.FullName $dest -Force }
    catch {
        # gshook*.dll stays loaded (pinned) inside every hooked app until it exits.
        # A loaded DLL can still be renamed, so move it aside and drop the new one in.
        $aside = "$dest.old-$(Get-Date -Format yyyyMMddHHmmss)"
        Move-Item $dest $aside -Force
        Copy-Item $file.FullName $dest -Force
        Write-Host "  $rel was in use; old copy kept as $(Split-Path $aside -Leaf)"
    }
}
# Sandboxed (Chromium GPU process: restricted token) and packaged (AppContainer) apps can only load the hook
# if the folder grants them read/execute: Users, ALL APPLICATION PACKAGES, ALL RESTRICTED APPLICATION PACKAGES.
icacls (Join-Path $InstallDir 'native') /grant '*S-1-5-32-545:(OI)(CI)RX' '*S-1-15-2-1:(OI)(CI)RX' '*S-1-15-2-2:(OI)(CI)RX' /T /Q | Out-Null
Get-ChildItem (Join-Path $InstallDir 'native') -Filter '*.old-*' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }   # succeeds once no app holds them
Remove-Item $stage -Recurse -Force
if ($restartService) { Start-Service GpuSwitcherBootGuard; Write-Host 'Boot-guard service restarted (it reopens the tray app).' }
Write-Host "Installed to $InstallDir"
Get-ChildItem $InstallDir -Include GpuSwitcher.exe, GpuSwitcherSvc.exe, gpusw.exe, gshook*.dll, gsinject*.exe -Recurse | Select-Object FullName, Length
