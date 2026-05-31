#!/usr/bin/env bash
# macOS/Linux 开发启动脚本
# 在两个独立终端中启动 Task Runner 和 WebUI

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

echo "========================================"
echo "百花谷 - 开发环境启动 (macOS/Linux)"
echo "========================================"
echo ""

# 检查 dotnet
if ! command -v dotnet &> /dev/null; then
    echo "错误：未安装 .NET SDK"
    echo "请访问 https://dotnet.microsoft.com/download 安装"
    exit 1
fi

echo "正在启动服务..."
echo ""

# 清理编译服务器缓存，避免 VBCSCompiler 缓存导致 stale binary
echo "清理编译服务器缓存..."
dotnet build-server shutdown 2>/dev/null || true
echo ""

# 启动 Task Runner
echo "[1/2] 启动 Task Runner (端口 8788)..."
osascript -e "tell application \"Terminal\" to do script \"cd '$ROOT/services/TaskRunner.Family' && dotnet watch run --non-interactive\"" 2>/dev/null || \
echo "请手动在终端运行: cd \"$ROOT/services/TaskRunner.Family\" && dotnet watch run --non-interactive"

# 启动 WebUI
echo "[2/2] 启动 WebUI (端口 5177)..."
osascript -e "tell application \"Terminal\" to do script \"cd '$ROOT/services/WebUI' && dotnet watch run --non-interactive\"" 2>/dev/null || \
echo "请手动在终端运行: cd \"$ROOT/services/WebUI\" && dotnet watch run --non-interactive"

echo ""
echo "========================================"
echo "服务启动中..."
echo "========================================"
echo ""
echo "访问地址:"
echo "  - Task Runner: http://localhost:8788"
echo "  - WebUI:       http://localhost:5177"
echo ""
echo "提示: 使用 Ctrl+C 停止服务"
