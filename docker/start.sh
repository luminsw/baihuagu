#!/usr/bin/env bash
set -euo pipefail

# ============================================
# Family Docker 一键启动脚本
# 用法：./start.sh [--build]
# ============================================

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "${SCRIPT_DIR}"

# 检查 .env 文件
if [[ ! -f .env ]]; then
    echo "警告：未找到 .env 文件，使用默认配置"
    echo "提示：cp .env.example .env 并按需修改"
fi

# 确保宿主机目录存在
mkdir -p /opt/yj-family/data /opt/yj-family/logs \
         /opt/yj-family/config/taskrunner /opt/yj-family/config/webui /opt/yj-family/config/nginx \
         /opt/yj-family/data/openobserve

# 如果 nginx 配置不存在，从项目复制默认配置
if [[ ! -f /opt/yj-family/config/nginx/nginx.conf ]]; then
    if [[ -f "${SCRIPT_DIR}/nginx/nginx.conf" ]]; then
        cp "${SCRIPT_DIR}/nginx/nginx.conf" /opt/yj-family/config/nginx/nginx.conf
        echo "已复制默认 Nginx 配置到 /opt/yj-family/config/nginx/nginx.conf"
    fi
fi

# 构建参数
BUILD_FLAG=""
if [[ "${1:-}" == "--build" ]]; then
    BUILD_FLAG="--build"
fi

echo "启动 Family Docker 服务..."
docker compose up -d ${BUILD_FLAG} --remove-orphans

echo ""
echo "等待服务就绪..."
sleep 5

# 显示服务状态
docker compose ps

echo ""
echo "========================================"
echo "Family Docker 服务已启动"
echo "  TaskRunner:  http://127.0.0.1:8788"
echo "  WebUI:       http://127.0.0.1:5177"
echo "  Nginx (HTTP): http://127.0.0.1:80"
echo "  OpenObserve: http://127.0.0.1:5080"
echo ""
echo "数据目录: /opt/yj-family/data"
echo "日志目录: /opt/yj-family/logs"
echo "配置目录: /opt/yj-family/config"
echo "========================================"
