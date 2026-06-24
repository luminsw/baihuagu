# 开发说明（家庭版 Family）

> 当前架构已从单体后台拆分为 3 个独立后端服务：
> - **TaskRunner.Family** (8788) — 家庭/亲子功能（任务、成就、OpenClaw、设备配对）
> - **TaskRunner.AI** (8791) — AI 模型、聊天、配置管理
> - **TaskRunner.Vault** (8790) — 知识库、同步、搜索、索引
>
> 3 个服务共用同一个 SQLite `taskrunner.db`（通过 `Core.Shared` 共享数据层）。

## 助手 / 自动化约定

- **只要本仓库内 `dotnet build` 成功**，即应保证后台处于运行状态：
  - TaskRunner.Family **8788**、TaskRunner.AI **8791**、TaskRunner.Vault **8790**、WebUI **5177**
  - 若未监听，应在释放端口/处理文件锁后 **`dotnet watch run`** 拉起对应服务
- **WebUI 与 TaskRunner 之间的共享数据类型和 API 接口定义必须放在 `TaskRunner.Contracts`**，两边禁止各自重复定义。新增或修改 API 契约时，先更新 Contracts，再让两边引用同一版本。
- **共享业务服务（如 `VaultSettingsService`、`VaultNoteIndexer`）放在 `Core.Shared`**，`TaskRunner.Family` 与 `TaskRunner.Vault` 均通过引用 `Core.Shared` 使用，避免 HTTP 调用开销。

## 目录

- `services/TaskRunner.Family/`：家庭版主后台（亲子功能、设备管理）
- `services/TaskRunner.AI/`：AI 微服务（模型、聊天、配置）
- `services/TaskRunner.Vault/`：知识库微服务（Vault、Sync、Search）
- `services/WebUI.Family/`：家庭版 Web 界面（Blazor Server）
- `services/TaskRunner.Contracts/`：共享 DTO 与接口契约
- `services/Core.Shared/`：共享服务层（含 VaultSettingsService、DeviceService 等）
- `services/TaskRunner.Data/`：共享 EF Core 数据层
- `bhg`：极简 CLI 工具（Linux/Mac）
- `docs/`：协议与架构文档
- `scripts/`：开发、发布、部署脚本

## 访问授权

```bash
# 一键打开管理面板（自动启动服务）
./bhg dashboard
```

无需密码，无需 IP 白名单。授权基于操作系统用户权限（只有能运行 `bhg` 命令的本机用户才能访问）。

## 常用命令

```bash
# 开发模式（Linux/macOS，一键启动全部 3 个后台 + WebUI）
./bhg dashboard

# 或手动分别启动
# 终端 1
cd services/TaskRunner.AI && dotnet watch run --non-interactive --no-hot-reload --urls "http://0.0.0.0:8791"
# 终端 2
cd services/TaskRunner.Vault && dotnet watch run --non-interactive --no-hot-reload --urls "http://0.0.0.0:8790"
# 终端 3
cd services/TaskRunner.Family && dotnet watch run --non-interactive --no-hot-reload
# 终端 4
cd services/WebUI.Family && dotnet watch run --non-interactive

# 编译验证（推送前必须执行）
dotnet build services/DoctorNotes.Family.slnx -c Release
```

## 端口

| 服务 | 端口 | 说明 |
|------|------|------|
| TaskRunner.Family | 8788 | HTTP API（家庭/亲子功能） |
| TaskRunner.AI | 8791 | HTTP API（AI 模型与配置） |
| TaskRunner.Vault | 8790 | HTTP API（知识库、同步、搜索） |
| WebUI | 5177 | HTTP Blazor Server |

## 移动端兼容

移动端（鸿蒙/安卓）通过 `http://<server>:8788` 发现服务器并调用 API。
`TaskRunner.Family` 在 8788 上保留了一个**转发中间件**，将移动端调用的 Vault 域 API 路径（如 `/mg/manifest`、`/mg/file`、`/mg/cards`、`/mg/vaults` 等）透明转发到 `TaskRunner.Vault`（8790）。因此 **移动端代码无需任何改动**。

授权与认证：
- 局域网发现/配对阶段通过 HMAC 签名（共享 `sharedSecret`）校验设备身份。
- 转发到 `TaskRunner.Vault` 时，`TaskRunner.Family` 会为已授权设备自动附加 `Authorization: Bearer <accessToken>`，Vault 侧校验 Bearer Token 或本机回环请求。
