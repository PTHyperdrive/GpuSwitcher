# Builds the release package into out\release\:
#   GpuSwitcher-<version>-win-x64.zip   the app (single-file exes + native\ hook + docs) in its own folder
#   GpuSwitcher.exe, gpusw.exe, GpuSwitcherSvc.exe   the same executables for direct download
#   SHA256SUMS.txt
# Version comes from Directory.Build.props.
param([switch]$SkipNative)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
if (-not $SkipNative) { & (Join-Path $root 'src\Hook\build.ps1') | Out-Null }

$name = "GpuSwitcher-$version-win-x64"
$out = Join-Path $root 'out\release'
$pkg = Join-Path $out $name
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $pkg | Out-Null

foreach ($proj in 'src\App\GpuSwitcher.csproj', 'src\Cli\gpusw.csproj', 'src\Service\GpuSwitcherSvc.csproj') {
    dotnet publish (Join-Path $root $proj) -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=none -o $pkg -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md') $pkg

# Zip the folder itself: extracting gives a dedicated GpuSwitcher-* folder, which is what the service installer expects.
Compress-Archive -Path $pkg -DestinationPath (Join-Path $out "$name.zip")
Copy-Item (Join-Path $pkg 'GpuSwitcher.exe'), (Join-Path $pkg 'gpusw.exe'), (Join-Path $pkg 'GpuSwitcherSvc.exe') $out

Get-ChildItem $out -File | Where-Object { $_.Extension -in '.zip', '.exe' } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii

Get-ChildItem $out -File | Select-Object Name, Length
