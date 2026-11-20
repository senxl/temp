#!/usr/bin/env bash
# 启动 qq-bridge（Lagrange.Core V2 收发通道）。首次运行需要手机 QQ 扫码。
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$HERE")"
export DOTNET_ROOT="$ROOT/.toolchain/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_NOLOGO=1

PORT="${PORT:-8090}"

cd "$HERE"
dotnet build -c Release
exec dotnet bin/Release/net10.0/qq-bridge.dll --port="$PORT"
