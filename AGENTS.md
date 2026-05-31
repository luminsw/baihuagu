# 开发说明（家庭版 Family）

## 助手 / 自动化约定

- **只要本仓库内 `dotnet build` 成功**，即应保证后台处于运行状态：Task Runner **8788** (HTTP)、WebUI **5177** (HTTP) 在监听；若未监听，应在释放端口/处理文件锁后 **`dotnet watch run`** 拉起。
- **WebUI 与 TaskRunner 之间的共享数据类型和 API 接口定义必须放在 `TaskRunner.Contracts`**，两边禁止各自重复定义。新增或修改 API 契约时，先更新 Contracts，再让两边引用同一版本。

## 目录

- `services/TaskRunner.Family/`：家庭版后台服务（C# / ASP.NET Core）
- `services/WebUI.Family/`：家庭版 Web 界面（Blazor Server）
- `services/TaskRunner.Contracts/`：共享 DTO 与接口契约
- `yj`：极简 CLI 工具（Linux/Mac）
- `docs/`：协议与架构文档
- `scripts/`：开发、发布、部署脚本

## 访问授权

```bash
# 一键打开管理面板（自动启动服务）
./yj dashboard
```

无需密码，无需 IP 白名单。授权基于操作系统用户权限（只有能运行 `yj` 命令的本机用户才能访问）。

## 常用命令

```bash
# 开发模式（PowerShell）
.\scripts\dev-backend.ps1

# 开发模式（Linux/macOS）
./scripts/dev-backend.sh

# 手动启动
cd services/TaskRunner.Family && dotnet watch run --non-interactive --no-hot-reload
cd services/WebUI.Family && dotnet watch run --non-interactive
```

## 端口

| 服务 | 端口 | 说明 |
|------|------|------|
| Task Runner | 8788 | HTTP API |
| WebUI | 5177 | HTTP Blazor Server |
