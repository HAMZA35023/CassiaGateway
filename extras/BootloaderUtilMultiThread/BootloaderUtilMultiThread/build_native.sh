#!/usr/bin/env bash
set -euo pipefail

# Native build (run this on the ARM device itself)
mkdir -p build
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j

echo
echo "Built: build/libBootloaderUtilMultiThread.so"
file build/libBootloaderUtilMultiThread.so || true
