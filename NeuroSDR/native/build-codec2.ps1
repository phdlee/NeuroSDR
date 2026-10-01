# Rebuild libcodec2.dll for shipping. MinGW is a *build* machine tool only.
# The DLL is statically linked (-static-libgcc -static) so users do not install MinGW.
# For x86 ENSdr later: use mingw-w64 i686 gcc, a separate build dir, copy to native\win-x86\libcodec2.dll.
# The x64 DLL cannot be loaded by a 32-bit process.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "codec2-src"
$build = Join-Path $root "codec2-mingw"
$out = Join-Path $root "win-x64"
$gcc = "C:\msys64\mingw64\bin\gcc.exe"
$make = "C:\msys64\mingw64\bin\mingw32-make.exe"
if (-not (Test-Path $gcc)) { throw "MinGW gcc not found at $gcc (build machine only)" }
$env:PATH = "C:\msys64\mingw64\bin;C:\Program Files\CMake\bin;" + $env:PATH
if (-not (Test-Path "$src\CMakeLists.txt")) {
  & "C:\Program Files\Git\cmd\git.exe" clone --depth 1 https://github.com/drowe67/codec2.git $src
}
New-Item -ItemType Directory -Force -Path $build, $out | Out-Null
& cmake -S $src -B $build -G "MinGW Makefiles" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DUNITTEST=OFF `
  -DCMAKE_C_COMPILER=$gcc -DCMAKE_MAKE_PROGRAM=$make "-DCMAKE_SHARED_LINKER_FLAGS=-static-libgcc -static"
& cmake --build $build --target codec2 -j 8
Copy-Item (Join-Path $build "src\libcodec2.dll") (Join-Path $out "libcodec2.dll") -Force
