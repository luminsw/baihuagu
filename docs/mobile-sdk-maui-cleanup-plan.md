# 百花移动端 SDK / MAUI 收尾计划

## 目标

在 WebSocket 授权推送调通的基础上，补齐关键测试、清理文案细节，让 SDK 和 MAUI 程序达到「当前功能域内可维护」的状态。

## 当前状态（2026-06-26）

| 检查项 | 状态 | 备注 |
|--------|------|------|
| `PushWebSocketService` 行为测试 | ✅ 完成 | 新增连接、消息接收、`Authorized` / `SyncRequest` 事件、断开、重连覆盖；通过 `CreateAndConnectWebSocketAsync` 虚方法注入测试替身 |
| `AuthorizationWatcher` 抽取与测试 | ✅ 完成 | `SyncPage` 已迁移；新增 6 个单元测试覆盖立即授权、WebSocket 推送、轮询兜底、断线切换、取消、状态事件 |
| UI 文案清理 | ✅ 完成 | `HomePage`、`SettingsPage` 已移除「SDK 试验版」等过时描述 |
| 未实现后端接口标记 | ✅ 完成 | `IPairingService` 与 `PairingServiceImpl` 已用 `[后端未实现 /mg/xxx]` 标记三个未实现端点；对应单元测试已验证 `NotSupportedException` |
| MAUI Android Release 构建 | ✅ 通过 | `dotnet build clients/MobileApp.Maui/MobileApp.Maui.csproj -c Release -f net9.0-android`（已降级到 .NET 9 解决 Honor 设备兼容性问题） |
| SDK 单元测试 | ✅ 153 通过 | `dotnet test tests/BaihuaSdk.Tests` |
| MAUI 单元测试 | ✅ 9 通过 | `dotnet test tests/MobileApp.Maui.Tests` |
| 服务端单元测试 | ✅ 536 通过 | `dotnet test tests/TaskRunner.Family.Tests` |
| 轮询策略精确化 | ✅ 完成 | WebSocket 连接成功时不启动轮询，断开后才回退轮询；已更新 `AuthorizationWatcher` |
| UI 按钮合并 | ✅ 完成 | `SyncPage` 已合并为单个「🔍 检查授权状态」按钮 |
| `async void` 规范化 | ✅ 完成 | `PushWebSocketService.ScheduleReconnect` 改为 `ScheduleReconnectAsync`；`AuthorizationWatcher.OnPushAuthorized` 提取为命名事件处理器并加注释说明 |
| 真机端到端验证 | ✅ 已解决 | Honor 设备 MAUI 10 崩溃已通过降级到 .NET 9 解决；真机测试成功启动并进入主界面 |
| 相关提交 | ✅ 已推送 | `a340606` `feat(SDK+MAUI): 抽取 AuthorizationWatcher 简化授权等待流程` |

## 任务清单

### 1. 为 `PushWebSocketService` 补充行为测试（✅ 已完成）

现状：`PushWebSocketServiceTests` 之前只有构造/订阅/disposal 测试，未覆盖连接、消息接收、`Authorized` 事件触发、重连逻辑。

实施方案：
- 未引入新的 `IWebSocketFactory` 接口，而是在 `PushWebSocketService` 中抽出 `CreateAndConnectWebSocketAsync` 虚方法，测试子类重写该方法返回 `MockClientWebSocket`。
- 新增测试覆盖：
  - 连接成功后触发 `ConnectionStateChanged(true)`
  - 收到 `Authorized` 消息后触发 `Authorized` 事件
  - 收到 `SyncRequest` 消息后调用 `OnSyncRequest`
  - 断开/关闭后触发 `ConnectionStateChanged(false)`
  - 重连逻辑

### 2. 为 `SyncPage` 的轮询/WebSocket 策略补充可测试封装（✅ 已完成）

现状：轮询/WebSocket 切换逻辑原本写在 `.razor` 的 `@code` 里，无法单元测试。

实施方案：
- 新增 `AuthorizationWatcher`（`libs/BaihuaSdk/src/Services/AuthorizationWatcher.cs`），职责：
  - 持有 `IDeviceRegistrationService` 和 `PushWebSocketService`
  - 提供 `WaitForAuthorizationAsync(serverUrl, deviceName, ct)`，内部优先 WebSocket，WebSocket 不可用时回退轮询
  - 暴露 `WebSocketConnectionStateChanged` 事件供 UI 显示连接状态
- `SyncPage.razor` 移除 `EnsurePushConnectionAsync`、`StartPolling`、`StopPolling`、`AutoCheckRegistrationAsync`、`DoRegisterAsync` 等冗长逻辑，统一调用 `AuthorizationWatcher.WaitForAuthorizationAsync`
- 新增 `AuthorizationWatcherTests` 覆盖：
  - 注册时已授权立即返回
  - WebSocket 推送授权时返回
  - WebSocket 未连接时轮询兜底
  - WebSocket 断开后切到轮询
  - 取消时抛出 `OperationCanceledException`
  - 连接状态变化事件

> 注：`AuthorizationWatcher` 已采用精确轮询策略：WebSocket 连接成功期间不启动轮询，仅在连接失败或断开后才回退到轮询，以减少无效请求。

### 3. 清理 UI 文案与细节（✅ 已完成）

- `SettingsPage.razor`：已移除「百花 iOS SDK 试验项目」等过时文案
- `HomePage.razor`：已同步清理「SDK 试验版」等描述

### 4. 标记未实现的后端接口（✅ 已完成）

- `IPairingService`（`libs/MobileContract/Services/IPairingService.cs`）中三个未实现方法已补充 `<remarks>` 注释，明确标注：
  - `CheckPairStatusAsync` → `[后端未实现 /mg/pair/status]`
  - `VerifyTokenAsync` → `[后端未实现 /mg/verify-token]`
  - `GetAuthConfigAsync` → `[后端未实现 /mg/auth/config]`
- `PairingServiceImpl`（`libs/BaihuaSdk/src/Services/PairingServiceImpl.cs`）中对应实现已同步调整异常消息与注释，统一使用 `[后端未实现 /mg/xxx]` 前缀，并说明待后端实现后可移除。
- 这些接口需要后端先实现 `/mg/pair/status`、`/mg/verify-token`、`/mg/auth/config` 后方可移除 `NotSupportedException`。
- 未新增后端契约扩展，因为当前 MobileContract 中的 DTO（`AuthConfigRequest`、`AuthConfigResponse`、`VerifyTokenRequest`）与接口定义已足够；后续后端实现时直接按上述端点路径添加控制器方法即可。

## 后续可选优化（已完成 3/4）

1. **轮询策略精确化**（✅ 已完成）：`AuthorizationWatcher` 已改为仅在 WebSocket 未连接或断开后启动轮询；WebSocket 连接成功期间不再并行轮询，减少无效请求。
2. **UI 按钮合并**（✅ 已完成）：`SyncPage` 中「注册设备」和「刷新状态」已合并为单个「🔍 检查授权状态」按钮，统一调用 `CheckAuthorizationStatus`。
3. **`async void` 规范化**（✅ 已完成）：
   - `PushWebSocketService.ScheduleReconnect` 已改为 `ScheduleReconnectAsync()` 并返回 `Task`，调用处显式丢弃（`_ = ScheduleReconnectAsync(ct)`）。
   - `AuthorizationWatcher.OnPushAuthorized` 提取为命名事件处理器，并在注释中明确说明事件处理器签名为 `void`，所有异常已通过 `TrySetException`/`TrySetCanceled` 捕获。
4. **真机端到端验证**（✅ 已解决）：
   - **问题背景**：Honor 真机（`ADNQUT5813009383`）安装 MAUI 10 版本 APK 后启动崩溃，错误为 `java.lang.IllegalArgumentException: No view found for id 0x7f0800ff for fragment NavigationRootManager_ElementBasedFragment`。这是 MAUI 10 在部分 Android 设备上的已知框架问题（[dotnet/maui#32029](https://github.com/dotnet/maui/issues/32029)）。
   - **解决方案**：将 MAUI 项目 Android TargetFramework 从 `net10.0-android` 降级到 `net9.0-android`（LTS 版本），同时将 `ZXing.Net.Maui.Controls` 从 `0.10.1` 降级到 `0.6.0`（兼容 .NET 9）。
   - **验证结果**：
     - 模拟器（AVD `baihua_test` / Pixel 6 / Android 14）：应用启动正常，UI 渲染无异常。
     - Honor 真机（`ADNQUT5813009383`）：安装 .NET 9 版本 APK 后成功启动并进入主界面，首页、配对页、同步页导航正常。

## 验收标准

- [x] `dotnet test tests/BaihuaSdk.Tests` 全部通过，且新增 WebSocket 行为测试
- [x] `dotnet test tests/MobileApp.Maui.Tests` 全部通过
- [x] MAUI Android Release 构建通过
- [x] 无过时/误导性 UI 文案
- [x] 未实现后端接口已明确记录（等待后端实现）

## 备注

- 保持改动最小，未引入新的 NuGet 包（使用 .NET 内置 `TaskCompletionSource` 和 `Channel` 模拟 WebSocket）。
- 如果某项任务发现需要改动后端契约，应先在 `MobileContract` 讨论，不在本计划内自行扩展。
