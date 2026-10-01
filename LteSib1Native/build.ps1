# Build ens_lte.dll via MSYS2 MinGW64
$ErrorActionPreference = "Stop"
$bash = "C:\msys64\usr\bin\bash.exe"
if (-not (Test-Path $bash)) { throw "MSYS2 not found at C:\msys64" }
& $bash -lc "cd /j/codex/sdr/ENSdr/LteSib1Native && sed -i 's/\r$//' build-mingw.sh && ./build-mingw.sh"
if ($LASTEXITCODE -ne 0) { throw "ens_lte build failed: $LASTEXITCODE" }
Write-Host "OK — ens_lte.dll ready"
