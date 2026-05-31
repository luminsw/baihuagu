using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TaskRunner.Contracts.Devices;
using TaskRunner.Data;
using TaskRunner.Data.Entities;

namespace TaskRunner.Services
{
    /// <summary>
    /// 设备状态
    /// </summary>
    public enum DeviceStatus
    {
        Pending,
        Authorized,
        Revoked,
        Rejected
    }

    /// <summary>
    /// 设备信息
    /// </summary>
    public class DeviceInfo
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public DeviceStatus Status { get; set; }
        public DateTime FirstRequestTime { get; set; }
        public DateTime? AuthorizedTime { get; set; }
        public DateTime? LastSyncTime { get; set; }
        public string? AccessToken { get; set; }
        public string? IpAddress { get; set; }
        public int SyncCount { get; set; }
        public DateTime? FirstSyncTime { get; set; }
        /// <summary>最近30天内同步过的知识库ID列表</summary>
        public List<string> SyncedVaultIds { get; set; } = new();
    }

    /// <summary>
    /// 配对请求信息
    /// </summary>
    public class PairRequestInfo
    {
        public string RequestId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string PairCode { get; set; } = string.Empty;
        public DateTime RequestTime { get; set; }
        public string? IpAddress { get; set; }
    }

    /// <summary>
    /// 设备授权服务 - 管理设备配对和授权（使用 SQLite + EF Core 存储）
    /// </summary>
    public class DeviceService
    {
        private readonly ILogger<DeviceService> _logger;
        private string _pairCode;
        private readonly IHubContext<Hubs.DeviceHub>? _deviceHub;
        private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
        private readonly IConfiguration _configuration;
        
        // 待授权设备队列（内存中，不持久化）
        private readonly ConcurrentDictionary<string, PairRequestInfo> _pendingRequests = new();
        
        // 请求处理结果记录（内存中，短期缓存）
        private readonly ConcurrentDictionary<string, string> _requestResults = new();

        // 推送同步请求队列（按设备ID分组，内存中）
        private readonly ConcurrentDictionary<string, ConcurrentQueue<PushSyncRequest>> _pushRequests = new();
        
        // 长轮询等待者（按查询键分组，用于实时唤醒）
        private readonly ConcurrentDictionary<string, TaskCompletionSource<List<PushSyncRequest>>> _pendingWaiters = new();
        
        // WebSocket 推送连接（按 deviceName 分组）
        private readonly ConcurrentDictionary<string, WebSocket> _pushSockets = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pushSocketCts = new();
        
        // 局域网发现请求锁，防止同名设备创建多个 pending 请求
        private readonly object _lanDiscoveryLock = new();

        public DeviceService(
            IConfiguration configuration, 
            ILogger<DeviceService> logger, 
            IDbContextFactory<AppDbContext> dbContextFactory,
            IHubContext<Hubs.DeviceHub>? deviceHub = null)
        {
            _logger = logger;
            _configuration = configuration;
            _dbContextFactory = dbContextFactory;
            _deviceHub = deviceHub;
            
            // 优先使用配置中的配对码，如果没有则生成随机配对码
            _pairCode = configuration["PairCode"] ?? GenerateRandomPairCode();
            

        }

        /// <summary>
        /// 生成随机配对码（8位字母数字混合）
        /// </summary>
        public static string GenerateRandomPairCode()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 排除易混淆字符 I,1,O,0
            return new string(Enumerable.Range(0, 8)
                .Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
        }

        /// <summary>
        /// 获取当前配对码
        /// </summary>
        public string GetPairCode() => _pairCode;

        /// <summary>
        /// 刷新配对码（生成新的随机配对码）
        /// </summary>
        public string RefreshPairCode()
        {
            _pairCode = GenerateRandomPairCode();
            _logger.LogInformation("配对码已刷新");
            return _pairCode;
        }


        public bool ValidatePairCode(string? pairCode)
        {
            if (string.IsNullOrWhiteSpace(pairCode)) return false;
            return pairCode.Trim() == _pairCode;
        }

        public PairRequestInfo SubmitPairRequest(string deviceName, string pairCode, string? ipAddress = null, string? requestId = null)
        {
            // 如果指定了 requestId（如 challenge），检查是否已存在
            if (!string.IsNullOrEmpty(requestId) && _pendingRequests.ContainsKey(requestId))
            {
                _logger.LogInformation("设备配对请求已存在: {DeviceName}, 请求ID: {RequestId}", 
                    deviceName, requestId);
                return _pendingRequests[requestId];
            }

            var existingRequest = _pendingRequests.Values
                .FirstOrDefault(r => r.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            
            if (existingRequest != null)
            {
                _logger.LogInformation("设备配对请求已存在: {DeviceName}, 请求ID: {RequestId}", 
                    deviceName, existingRequest.RequestId);
                return existingRequest;
            }

            var request = new PairRequestInfo
            {
                RequestId = requestId ?? Guid.NewGuid().ToString("N"),
                DeviceName = deviceName,
                PairCode = pairCode,
                RequestTime = DateTime.UtcNow,
                IpAddress = ipAddress
            };

            _pendingRequests[request.RequestId] = request;
            _logger.LogInformation("设备请求配对: {DeviceName}, 请求ID: {RequestId}", deviceName, request.RequestId);
            
            _ = NotifyDeviceStatusChangedAsync("pair_request", deviceName, request.RequestId)
                .ContinueWith(t => 
                {
                    if (t.IsFaulted)
                    {
                        _logger.LogError(t.Exception, "发送配对通知失败");
                    }
                });
            
            return request;
        }

        /// <summary>
        /// 局域网自动发现请求：无需配对码，直接创建待授权请求
        /// </summary>
        public PairRequestInfo SubmitLanDiscoveryRequest(string deviceName, string? ipAddress = null)
        {
            lock (_lanDiscoveryLock)
            {
                var existingRequest = _pendingRequests.Values
                    .FirstOrDefault(r => r.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
                
                if (existingRequest != null)
                {
                    _logger.LogInformation("局域网发现请求已存在: {DeviceName}, 请求ID: {RequestId}", 
                        deviceName, existingRequest.RequestId);
                    return existingRequest;
                }

                var request = new PairRequestInfo
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    DeviceName = deviceName,
                    PairCode = "LAN", // 标记为局域网发现，不参与配对码验证
                    RequestTime = DateTime.UtcNow,
                    IpAddress = ipAddress
                };

                _pendingRequests[request.RequestId] = request;
                _logger.LogInformation("局域网发现设备: {DeviceName}, 请求ID: {RequestId}, IP: {Ip}", 
                    deviceName, request.RequestId, ipAddress);
                
                _ = NotifyDeviceStatusChangedAsync("lan_discovered", deviceName, request.RequestId)
                    .ContinueWith(t => 
                    {
                        if (t.IsFaulted)
                        {
                            _logger.LogError(t.Exception, "发送局域网发现通知失败");
                        }
                    });
                
                return request;
            }
        }

        public IReadOnlyList<PairRequestInfo> GetPendingRequests()
        {
            return _pendingRequests.Values.ToList();
        }

        public string? GetRequestResult(string requestId)
        {
            if (_pendingRequests.ContainsKey(requestId))
            {
                return "pending";
            }
            
            _requestResults.TryGetValue(requestId, out var result);
            return result;
        }

        public (bool success, string? accessToken, string? error) AuthorizeDevice(string requestId)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            if (!_pendingRequests.TryRemove(requestId, out var request))
            {
                return (false, null, "请求不存在或已处理");
            }

            // 清除同名设备的其他待授权请求，防止授权后仍然显示在待授权列表中
            var keysToRemove = _pendingRequests
                .Where(kv => kv.Value.DeviceName.Equals(request.DeviceName, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in keysToRemove)
            {
                _pendingRequests.TryRemove(key, out _);
            }

            // 去重：同名设备已授权则直接返回现有令牌
            var existingAuthorized = dbContext.AuthorizedDevices
                .FirstOrDefault(d => d.DeviceName == request.DeviceName && d.Status == "Authorized");
            if (existingAuthorized != null)
            {
                _requestResults[requestId] = "authorized";
                _logger.LogInformation("设备 {DeviceName} 已授权，复用现有令牌", request.DeviceName);
                _ = NotifyDeviceStatusChangedAsync("authorized", request.DeviceName, requestId);
                return (true, existingAuthorized.AccessToken, null);
            }

            var deviceId = Guid.NewGuid().ToString("N");
            var accessToken = Guid.NewGuid().ToString("N");

            dbContext.AuthorizedDevices.Add(new AuthorizedDevice
            {
                DeviceId = deviceId,
                DeviceName = request.DeviceName,
                AccessToken = accessToken,
                Status = "Authorized",
                IpAddress = request.IpAddress,
                AuthorizedTime = DateTime.UtcNow
            });
            dbContext.SaveChanges();
            
            _requestResults[requestId] = "authorized";
            
            _logger.LogInformation("设备已授权: {DeviceName}, DeviceId: {DeviceId}", request.DeviceName, deviceId);
            
            _ = NotifyDeviceStatusChangedAsync("authorized", request.DeviceName, requestId);
            
            return (true, accessToken, null);
        }

        public bool RejectRequest(string requestId)
        {
            if (_pendingRequests.TryRemove(requestId, out var request))
            {
                _requestResults[requestId] = "rejected";
                
                _logger.LogInformation("设备配对请求已拒绝: {DeviceName}, RequestId: {RequestId}", 
                    request.DeviceName, requestId);
                
                _ = NotifyDeviceStatusChangedAsync("rejected", request.DeviceName, requestId);
                
                return true;
            }
            return false;
        }

        public bool RevokeDevice(string deviceId)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            var device = dbContext.AuthorizedDevices
                .FirstOrDefault(d => d.DeviceId == deviceId);
                
            if (device != null)
            {
                device.Status = "Revoked";
                dbContext.SaveChanges();
                
                _logger.LogInformation("设备授权已撤销: {DeviceName}, DeviceId: {DeviceId}", 
                    device.DeviceName, deviceId);
                
                _ = NotifyDeviceStatusChangedAsync("revoked", device.DeviceName, null);
                
                return true;
            }
            return false;
        }

        public bool ValidateAccessToken(string? accessToken)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            if (string.IsNullOrWhiteSpace(accessToken)) return false;
            
            var device = dbContext.AuthorizedDevices
                .FirstOrDefault(d => d.AccessToken == accessToken && d.Status == "Authorized");
                
            return device != null;
        }

        public DeviceInfo? GetDeviceByToken(string accessToken)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            var device = dbContext.AuthorizedDevices
                .FirstOrDefault(d => d.AccessToken == accessToken);
                
            if (device == null) return null;
            
            return MapToDeviceInfo(device);
        }

        public void UpdateLastSyncTime(string accessToken)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            var device = dbContext.AuthorizedDevices
                .FirstOrDefault(d => d.AccessToken == accessToken);
                
            if (device != null)
            {
                device.LastSyncTime = DateTime.UtcNow;
                device.SyncCount++;
                if (device.FirstSyncTime == null)
                {
                    device.FirstSyncTime = DateTime.UtcNow;
                }
                dbContext.SaveChanges();
                
                _ = NotifyDeviceStatusChangedAsync("sync_updated", device.DeviceName, null);
            }
        }

        public IReadOnlyList<DeviceInfo> GetAuthorizedDevices()
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            var devices = dbContext.AuthorizedDevices
                .Where(d => d.Status == "Authorized")
                .OrderBy(d => d.AuthorizedTime)
                .AsEnumerable()
                .GroupBy(d => d.DeviceName)
                .Select(g => g.First())
                .ToList();

            // 查询每个设备最近30天同步过的知识库
            var since = DateTime.UtcNow.AddDays(-30);
            var syncLogs = dbContext.DeviceSyncLogs
                .Where(l => l.SyncTime >= since && !string.IsNullOrEmpty(l.VaultId))
                .ToList();

            var result = new List<DeviceInfo>();
            foreach (var device in devices)
            {
                var info = MapToDeviceInfo(device);
                info.SyncedVaultIds = syncLogs
                    .Where(l => l.DeviceId == device.DeviceId)
                    .Select(l => l.VaultId!)
                    .Distinct()
                    .ToList();
                result.Add(info);
            }
            return result;
        }

        /// <summary>
        /// 自动授权设备（无需等待 WebUI 审批）
        /// </summary>
        public (bool success, string? accessToken, string? error) AutoAuthorizeDevice(string deviceName, string? ipAddress = null)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();

            // 检查是否已存在同名授权设备
            var existing = dbContext.AuthorizedDevices
                .FirstOrDefault(d => d.DeviceName == deviceName && d.Status == "Authorized");
            if (existing != null)
            {
                return (true, existing.AccessToken, null);
            }

            var deviceId = Guid.NewGuid().ToString("N");
            var accessToken = Guid.NewGuid().ToString("N");

            dbContext.AuthorizedDevices.Add(new AuthorizedDevice
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                AccessToken = accessToken,
                Status = "Authorized",
                IpAddress = ipAddress,
                AuthorizedTime = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SyncCount = 0
            });
            dbContext.SaveChanges();

            _logger.LogInformation("设备已自动授权: {DeviceName}, DeviceId: {DeviceId}", deviceName, deviceId);
            return (true, accessToken, null);
        }

        /// <summary>
        /// 记录设备同步活动
        /// </summary>
        public void RecordSyncActivity(string deviceId, string deviceName, string? vaultId, int fileCount, string syncType, string? ipAddress = null)
        {
            try
            {
                using var dbContext = _dbContextFactory.CreateDbContext();

                // 更新设备同步计数
                var device = dbContext.AuthorizedDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (device != null)
                {
                    device.SyncCount++;
                    device.LastSyncTime = DateTime.UtcNow;
                    if (device.FirstSyncTime == null)
                    {
                        device.FirstSyncTime = DateTime.UtcNow;
                    }
                    device.UpdatedAt = DateTime.UtcNow;
                }

                // 记录同步日志
                dbContext.DeviceSyncLogs.Add(new DeviceSyncLog
                {
                    DeviceId = deviceId,
                    DeviceName = deviceName,
                    IpAddress = ipAddress,
                    VaultId = vaultId,
                    FileCount = fileCount,
                    SyncType = syncType,
                    SyncTime = DateTime.UtcNow
                });

                dbContext.SaveChanges();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "记录同步活动失败: {DeviceId}", deviceId);
            }
        }

        /// <summary>
        /// 获取移动端统计信息
        /// </summary>
        public MobileStats GetMobileStats()
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            var devices = dbContext.AuthorizedDevices.Where(d => d.Status == "Authorized").ToList();
            var syncLogs = dbContext.DeviceSyncLogs.ToList();

            return new MobileStats
            {
                TotalDevices = devices.Count,
                TotalSyncs = syncLogs.Count,
                TotalSyncFiles = syncLogs.Sum(l => l.FileCount),
                ActiveDevices7Days = devices.Count(d => d.LastSyncTime.HasValue && d.LastSyncTime.Value > DateTime.UtcNow.AddDays(-7)),
                ActiveDevices30Days = devices.Count(d => d.LastSyncTime.HasValue && d.LastSyncTime.Value > DateTime.UtcNow.AddDays(-30)),
                Devices = devices.Select(d => new DeviceStat
                {
                    DeviceId = d.DeviceId,
                    DeviceName = d.DeviceName,
                    IpAddress = d.IpAddress,
                    SyncCount = d.SyncCount,
                    FirstSyncTime = d.FirstSyncTime,
                    LastSyncTime = d.LastSyncTime,
                    AuthorizedTime = d.AuthorizedTime
                }).OrderByDescending(d => d.LastSyncTime).ToList()
            };
        }

        public IReadOnlyList<DeviceInfo> GetAllDevices()
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            // 获取所有设备（包括已撤销的）
            return dbContext.AuthorizedDevices
                .OrderBy(d => d.AuthorizedTime)
                .Select(MapToDeviceInfo)
                .ToList();
        }

        public bool IsDeviceNameAuthorized(string deviceName)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            return dbContext.AuthorizedDevices
                .Any(d => d.DeviceName == deviceName && d.Status == "Authorized");
        }

        public DeviceInfo? GetAuthorizedDeviceByName(string deviceName)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();
            var device = dbContext.AuthorizedDevices
                .FirstOrDefault(d => d.DeviceName == deviceName && d.Status == "Authorized");
                
            return device == null ? null : MapToDeviceInfo(device);
        }

        private static DeviceInfo MapToDeviceInfo(AuthorizedDevice device)
        {
            return new DeviceInfo
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                Status = device.Status == "Authorized" ? DeviceStatus.Authorized : DeviceStatus.Revoked,
                AuthorizedTime = device.AuthorizedTime,
                LastSyncTime = device.LastSyncTime,
                AccessToken = device.AccessToken,
                IpAddress = device.IpAddress,
                SyncCount = device.SyncCount,
                FirstSyncTime = device.FirstSyncTime,
                SyncedVaultIds = new List<string>()
            };
        }

        private async Task NotifyDeviceStatusChangedAsync(string action, string deviceName, string? requestId)
        {
            if (_deviceHub == null)
            {
                _logger.LogWarning("无法通知：_deviceHub 为 null");
                return;
            }

            try
            {
                await _deviceHub.Clients.All.SendAsync("DeviceStatusChanged", new
                {
                    action = action,
                    deviceName = deviceName,
                    requestId = requestId,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送 WebSocket 通知失败: {Action} - {DeviceName}", action, deviceName);
            }
        }

        /// <summary>
        /// 添加推送同步请求到队列
        /// </summary>
        public PushSyncRequest AddPushRequest(string deviceId, string? deviceName, string? vaultId, string? vaultName, string? action)
        {
            var request = new PushSyncRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                DeviceId = deviceId,
                VaultId = vaultId ?? "",
                VaultName = vaultName ?? "",
                Action = action ?? "sync",
                CreatedAt = DateTime.UtcNow
            };

            // 按 deviceId 存储
            var queue = _pushRequests.GetOrAdd(deviceId, _ => new ConcurrentQueue<PushSyncRequest>());
            queue.Enqueue(request);

            // 同时按 deviceName 存储，方便移动端通过设备名称轮询
            if (!string.IsNullOrEmpty(deviceName))
            {
                var nameQueue = _pushRequests.GetOrAdd($"name:{deviceName}", _ => new ConcurrentQueue<PushSyncRequest>());
                nameQueue.Enqueue(request);
            }

            _logger.LogInformation("推送同步请求已入队: DeviceId={DeviceId}, DeviceName={DeviceName}, VaultId={VaultId}, RequestId={RequestId}", 
                deviceId, deviceName, request.VaultId, request.RequestId);

            // 唤醒长轮询等待者
            TryWakeWaiters(deviceId, deviceName);

            // 通过 WebSocket 实时推送（不阻塞）
            if (!string.IsNullOrEmpty(deviceName))
            {
                _ = NotifyPushViaWebSocketAsync(deviceName, request);
            }

            return request;
        }

        private void TryWakeWaiters(string deviceId, string? deviceName)
        {
            // 按 deviceId 唤醒
            if (_pendingWaiters.TryRemove(deviceId, out var waiter))
            {
                var requests = DequeueValidRequests(_pushRequests.GetOrAdd(deviceId, _ => new ConcurrentQueue<PushSyncRequest>()));
                waiter.TrySetResult(requests);
            }

            // 按 deviceName 唤醒（尝试多种键格式，兼容移动端直接用 deviceName 查询）
            if (!string.IsNullOrEmpty(deviceName))
            {
                var nameKey = $"name:{deviceName}";
                
                // 尝试 name: 前缀键
                if (_pendingWaiters.TryRemove(nameKey, out var nameWaiter))
                {
                    var nameRequests = DequeueValidRequests(_pushRequests.GetOrAdd(nameKey, _ => new ConcurrentQueue<PushSyncRequest>()));
                    nameWaiter.TrySetResult(nameRequests);
                }
                
                // 也尝试不带前缀的 deviceName（GetPendingPushRequestsAsync 可能直接用 query 作为键注册）
                if (_pendingWaiters.TryRemove(deviceName, out var plainWaiter))
                {
                    var plainRequests = DequeueValidRequests(_pushRequests.GetOrAdd(nameKey, _ => new ConcurrentQueue<PushSyncRequest>()));
                    plainWaiter.TrySetResult(plainRequests);
                }
            }
        }

        /// <summary>
        /// 获取设备的待处理推送请求（移动端轮询调用）
        /// 支持通过 deviceId 或 deviceName 查询
        /// </summary>
        public List<PushSyncRequest> GetPendingPushRequests(string query)
        {
            return GetPendingPushRequestsAsync(query, wait: false, timeoutMs: 0).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 获取设备的待处理推送请求（支持长轮询）
        /// </summary>
        public async Task<List<PushSyncRequest>> GetPendingPushRequestsAsync(string query, bool wait, int timeoutMs)
        {
            // 先尝试立即返回已有的请求
            var immediateResult = TryGetPendingRequests(query);
            if (immediateResult.Count > 0 || !wait || timeoutMs <= 0)
            {
                return immediateResult;
            }

            // 没有请求且客户端愿意等待：注册等待者
            var tcs = new TaskCompletionSource<List<PushSyncRequest>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingWaiters[query] = tcs;

            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var reg = cts.Token.Register(() => tcs.TrySetResult(new List<PushSyncRequest>()));
                
                return await tcs.Task;
            }
            finally
            {
                _pendingWaiters.TryRemove(query, out _);
            }
        }

        private List<PushSyncRequest> TryGetPendingRequests(string query)
        {
            // 先尝试按 deviceId 查找
            if (_pushRequests.TryGetValue(query, out var queue))
            {
                return DequeueValidRequests(queue);
            }

            // 再尝试按 deviceName 查找
            if (_pushRequests.TryGetValue($"name:{query}", out var nameQueue))
            {
                return DequeueValidRequests(nameQueue);
            }

            return new List<PushSyncRequest>();
        }

        private List<PushSyncRequest> DequeueValidRequests(ConcurrentQueue<PushSyncRequest> queue)
        {
            var requests = new List<PushSyncRequest>();
            while (queue.TryDequeue(out var request))
            {
                // 只返回5分钟内的请求，过期丢弃
                if (DateTime.UtcNow - request.CreatedAt < TimeSpan.FromMinutes(5))
                {
                    requests.Add(request);
                }
            }
            return requests;
        }

        // ========== WebSocket 推送 ==========

        public void RegisterPushSocket(string deviceName, WebSocket socket)
        {
            // 关闭旧连接
            if (_pushSockets.TryRemove(deviceName, out var oldSocket))
            {
                try { oldSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Replaced", CancellationToken.None).Wait(2000); } catch { /* ignore */ }
                try { oldSocket.Dispose(); } catch { /* ignore */ }
            }
            if (_pushSocketCts.TryRemove(deviceName, out var oldCts))
            {
                try { oldCts.Cancel(); } catch { /* ignore */ }
                try { oldCts.Dispose(); } catch { /* ignore */ }
            }

            var cts = new CancellationTokenSource();
            _pushSockets[deviceName] = socket;
            _pushSocketCts[deviceName] = cts;
            _logger.LogInformation("[WebSocket] 设备 {DeviceName} 已连接推送通道", deviceName);

            // 启动接收循环（保持连接活跃，处理关闭）
            _ = RunPushSocketReceiveLoop(deviceName, socket, cts.Token);
        }

        private async Task RunPushSocketReceiveLoop(string deviceName, WebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[1024 * 4];
            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[WebSocket] 设备 {DeviceName} 接收循环异常", deviceName);
            }
            finally
            {
                _pushSockets.TryRemove(deviceName, out _);
                _pushSocketCts.TryRemove(deviceName, out _);
                try { socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None).Wait(1000); } catch { }
                try { socket.Dispose(); } catch { }
                _logger.LogInformation("[WebSocket] 设备 {DeviceName} 推送连接已断开", deviceName);
            }
        }

        public async Task NotifyPushViaWebSocketAsync(string deviceName, PushSyncRequest request)
        {
            if (!_pushSockets.TryGetValue(deviceName, out var socket) || socket.State != WebSocketState.Open)
                return;

            var message = JsonSerializer.Serialize(new
            {
                type = "SyncRequest",
                vaultId = request.VaultId ?? "",
                vaultName = request.VaultName ?? "",
                action = request.Action ?? "sync",
                timestamp = DateTime.UtcNow
            });

            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None);
                _logger.LogInformation("[WebSocket] 已向 {DeviceName} 推送同步通知: Vault={VaultId}", deviceName, request.VaultId);
                
                // WebSocket 推送成功，从队列中移除该请求（避免 HTTP 轮询重复触发）
                DequeueSpecificRequest(request.DeviceId, request.RequestId);
                if (!string.IsNullOrEmpty(deviceName))
                {
                    DequeueSpecificRequest($"name:{deviceName}", request.RequestId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WebSocket] 向 {DeviceName} 推送失败", deviceName);
                _pushSockets.TryRemove(deviceName, out _);
                try { socket.Dispose(); } catch { }
            }
        }

        private void DequeueSpecificRequest(string queueKey, string requestId)
        {
            if (!_pushRequests.TryGetValue(queueKey, out var queue))
                return;
            
            // 临时保存不需要移除的请求
            var temp = new List<PushSyncRequest>();
            while (queue.TryDequeue(out var item))
            {
                if (item.RequestId != requestId)
                {
                    temp.Add(item);
                }
            }
            // 把不需要移除的请求重新入队
            foreach (var item in temp)
            {
                queue.Enqueue(item);
            }
        }
    }

    /// <summary>
    /// 推送同步请求
    /// </summary>
    public class PushSyncRequest
    {
        public string RequestId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string VaultId { get; set; } = "";
        public string VaultName { get; set; } = "";
        public string Action { get; set; } = "sync";
        public DateTime CreatedAt { get; set; }
    }

}
