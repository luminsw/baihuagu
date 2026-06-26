# 百花谷

家庭版后端服务 + yj CLI 工具，面向本地/局域网使用。

## 项目结构

```
services/
  TaskRunner.Family/    # 家庭版 Task Runner（API 服务）
  TaskRunner.AI/        # AI 模型、聊天、配置管理
  TaskRunner.Vault/     # 知识库、同步、搜索、索引
  WebUI.Family/         # 家庭版 Web 界面（Blazor Server）
  TaskRunner.Contracts/ # 共享 DTO 与接口契约
  Core.Shared/          # 共享服务层
  TaskRunner.Data/      # 共享 EF Core 数据层
libs/
  BaihuaguSdk/          # 跨平台移动端 SDK（net9.0;net10.0，零 MAUI 依赖）
  MobileContract/       # 移动端契约（DTO、接口定义）
clients/
  MobileApp.Maui/       # MAUI Blazor Hybrid 移动客户端
scripts/                # 开发、发布、部署脚本
docs/                   # 文档
docker/                 # Docker 配置
bhg                     # 极简 CLI 工具（Linux/Mac）
tests/
  BaihuaguSdk.Tests/    # SDK 单元测试与集成测试
  MobileApp.Maui.Tests/ # MAUI DI 回归测试
  e2e/                  # Playwright E2E 测试
```

## 快速启动

```bash
# 一键打开管理面板（自动启动服务）
./bhg dashboard

# 手动启动
cd services/TaskRunner.Family && dotnet run
cd services/WebUI.Family && dotnet run
```

## 访问授权

无需密码，无需 IP 白名单。使用本机 CLI Token 授权：

```bash
./bhg dashboard   # 本机一键访问
```

## 端口

| 服务 | 端口 | 说明 |
|------|------|------|
| TaskRunner.Family | 8788 | 家庭/亲子功能（任务、成就、OpenClaw、设备配对） |
| TaskRunner.AI | 8791 | AI 模型、聊天、配置管理 |
| TaskRunner.Vault | 8790 | 知识库、同步、搜索、索引 |
| WebUI.Family | 5177 | Blazor Server 管理后台 |
