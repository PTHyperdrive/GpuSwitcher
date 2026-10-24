# Builds gshook32/64.dll and gsinject32/64.exe with MSVC into ..\..\native\
param([string]$OutDir = (Join-Path $PSScriptRoot '..\..\native'))

$ErrorActionPreference = 'Stop'
$vcvars = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvarsall.bat',
                        'C:\Program Files (x86)\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvarsall.bat' -ErrorAction SilentlyContinue |
          Sort-Object FullName -Descending | Select-Object -First 1
if (-not $vcvars) { throw 'MSVC (vcvarsall.bat) not found' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$obj = Join-Path $env:TEMP 'gsw-native-obj'
New-Item -ItemType Directory -Force $obj | Out-Null

foreach ($arch in 'x64', 'x86') {
    $bits = if ($arch -eq 'x64') { '64' } else { '32' }
    $cet = if ($arch -eq 'x64') { '/CETCOMPAT' } else { '' }
    $cmd = "call `"$($vcvars.FullName)`" $arch >nul && cd /d `"$PSScriptRoot`" && " +
           "cl /nologo /O2 /W4 /WX /MT /guard:cf /LD /Fo`"$obj\\`" gshook.c /Fe:`"$OutDir\gshook$bits.dll`" /link /DEF:gshook.def /guard:cf $cet dxgi.lib && " +
           "cl /nologo /O2 /W4 /WX /MT /guard:cf /Fo`"$obj\\`" gsinject.c /Fe:`"$OutDir\gsinject$bits.exe`" /link /guard:cf $cet"
    # vcvarsall complains on stderr when vswhere is not on PATH; keep it out of PowerShell's error stream
    $ErrorActionPreference = 'Continue'
    $out = cmd /c "($cmd) 2>&1"
    $ErrorActionPreference = 'Stop'
    $out | Where-Object { $_ -notmatch 'vswhere' } | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "native build failed for $arch" }
}
Get-ChildItem $OutDir -Include *.dll, *.exe -Recurse | Select-Object Name, Length
