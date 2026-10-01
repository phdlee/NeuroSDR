#!/usr/bin/env bash
# Build ens_lte.dll with MinGW (MSYS2) against samples/srsRAN_4G
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build-mingw"
export PATH="/mingw64/bin:$PATH"

mkdir -p "$BUILD"
cd "$BUILD"

cmake -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_C_COMPILER=gcc \
  -DCMAKE_CXX_COMPILER=g++ \
  -DSRSRAN_ROOT="$ROOT/../../samples/srsRAN_4G" \
  -DENABLE_SRSUE=OFF \
  -DENABLE_SRSENB=OFF \
  -DENABLE_SRSEPC=OFF \
  -DENABLE_GUI=OFF \
  -DENABLE_RF_PLUGINS=OFF \
  -DENABLE_UHD=OFF \
  -DENABLE_BLADERF=OFF \
  -DENABLE_SOAPYSDR=OFF \
  -DENABLE_ZEROMQ=OFF \
  -DENABLE_HARDSIM=OFF \
  -DENABLE_WERROR=OFF \
  -DENABLE_ALL_TEST=OFF \
  -DBUILD_TESTING=OFF \
  -DENABLE_TIMEPROF=OFF \
  -DAUTO_DETECT_ISA=OFF \
  -DHAVE_SSE=ON \
  -DHAVE_AVX=ON \
  -DHAVE_AVX2=OFF \
  -DHAVE_FMA=OFF \
  "$ROOT"

ninja ens_lte -j"$(nproc)"
echo "Built: $BUILD/ens_lte.dll"
ls -la "$BUILD"/ens_lte.dll "$ROOT"/../ENSdr/native/win-x64/ens_lte.dll 2>/dev/null || true
