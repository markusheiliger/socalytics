#!/usr/bin/env bash
# Builds the universal libSocAlyticsMacBridge.dylib; macOS only. Usage: ./build.sh [output-directory]
set -euo pipefail
cd "$(dirname "$0")"
out="$(cd "${1:-.}" && pwd)"
swift build -c release --arch arm64 --arch x86_64
cp .build/apple/Products/Release/libSocAlyticsMacBridge.dylib "$out/"
