# 百花谷移动端 SDK / MAUI 收尾计划

## 目标
在 WebSocket 授权推送调通的基础上，补齐关键测试、清理文案细节，让 SDK 和 MAUI 程序达到「当前功能域内可维护」的状态。

## 任务清单

### 1. 为 `PushWebSocketService` 补充行为测试（高优先级）
现状：`PushWebSocketServiceTests` 只有构造/订阅/ disposal 测试，未覆盖连接、消息接收、`Authorized` 事件触发、重连逻辑。

方案：
- 引入 `IWebSocketFactory`（或 `Func<ClientWebSocket>`）注入点，使测试可以替换真实 `ClientWebSocket`
- 提供一个基于 `Channel<string>` 的 `MockClientWebSocket`，模拟服务端发送 `{"type":"Authorized"}` / `{"type":"SyncRequest",...}`
- 新增测试用例：
  - 连接成功后触发 `ConnectionStateChanged(true)`
  - 收到 `Authorized` 消息后触发 `Authorized` 事件
  - 收到 `SyncRequest` 消息后调用 `OnSyncRequest`
  - 断开/关闭后触发 `ConnectionStateChanged(false)`

### 2. 为 `SyncPage` 的轮询/WebSocket 策略补充可测试封装（中优先级）
现状：轮询/WebSocket 切换逻辑写在 `.razor` 的 `@code` 里，无法单元测试；MAUI 测试只有 9 个 DI 构造测试。

方案：
- 把「注册后等待授权」逻辑抽取到 `AuthorizationWatcher`（或类似）类，注入到 SyncPage
- 该类职责：
  - 持有 `PushWebSocketService` 和 `IDeviceRegistrationService`
  - 提供 `WaitForAuthorizationAsync(serverUrl, ct)`，内部优先 WebSocket，超时/断开后回退轮询
  - 暴露 `StateChanged` 事件供 UI 显示状态
- 为 `AuthorizationWatcher` 编写单元测试，覆盖：
  - WebSocket 推送授权时立即返回
  - WebSocket 未连接时轮询兜底
  - WebSocket 断开后切到轮询
  - 已授权后停止轮询

### 3. 清理 UI 文案与细节（低优先级）
- `SettingsPage.razor`：把「百花谷 iOS SDK 试验项目」改成准确描述（Android/iOS 双端试验版）
- 检查其他页面是否还有过时/误导性文案

### 4. 标记未实现的后端接口（低优先级）
- `PairingServiceImpl` 中 `CheckPairStatusAsync`、`VerifyTokenAsync`、`GetAuthConfigAsync` 目前抛 `NotSupportedException`
- 在代码注释和测试中明确记录：这些接口需要后端先实现 `/mg/pair/status`、`/mg/verify-token`、`/mg/auth/config`

## 验收标准
- `dotnet test tests/BaihuaguSdk.Tests` 全部通过，且新增 WebSocket 行为测试
- `dotnet test tests/MobileApp.Maui.Tests` 全部通过，且新增 `AuthorizationWatcher` 行为测试
- MAUI Android Release 构建通过
- 无过时/误导性 UI 文案

## 备注
- 保持改动最小，不引入新的 NuGet 包（尽量用 .NET 内置 `System.Threading.Channels` 或 `TaskCompletionSource` 模拟）
- 如果某项任务发现需要改动后端契约，应先在 `MobileContract` 讨论，不在本计划内自行扩展
