# mDNS 服务发现配置指南

## 概述

本项目已将原有的UDP广播发现机制升级为标准的mDNS（Multicast DNS）服务发现。mDNS使用标准协议（端口5353，地址224.0.0.251），提供更好的跨平台兼容性和网络效率。

## 技术对比

| 特性 | 旧版UDP广播 | 新版mDNS |
|------|------------|----------|
| **协议** | UDP广播 | UDP多播 |
| **地址** | 255.255.255.255（广播） | 224.0.0.251（多播） |
| **端口** | 12345（自定义） | 5353（标准） |
| **数据格式** | 自定义JSON | 标准DNS报文 |
| **网络效率** | 低（影响所有设备） | 高（只影响感兴趣设备） |
| **跨平台** | 有限 | 广泛支持（Windows/macOS/Linux） |
| **标准性** | 私有协议 | IETF标准（RFC 6762） |

## 后端配置（TaskRunner）

### 1. 新增的mDNS服务

**文件：** `services/TaskRunner.Family/Services/MDnsService.cs`

**功能：**
- 使用标准mDNS协议（端口5353）注册服务
- 服务类型：`_http._tcp.local`
- 服务名称：`doctor-notes-sync`
- 自动广告服务信息（每60秒）
- 响应mDNS查询请求

**配置：**
- 自动启动，无需额外配置
- 使用环境变量 `ASPNETCORE_URLS` 或 `appsettings.json` 中的端口配置
- 默认HTTP端口：8788

### 2. 服务注册

在 `Program.cs` 中添加了mDNS服务注册：

```csharp
// 注册mDNS服务
builder.Services.AddSingleton<MDnsService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MDnsService>());
```

### 3. 服务信息

mDNS服务广告以下信息：
- **服务名称**：`doctor-notes-sync._http._tcp.local`
- **IP地址**：本机局域网IP
- **端口**：从Kestrel配置获取（默认8788）
- **API地址**：`http://{ip}:{port}`
- **服务ID**：`com.doctornotes.sync`

## 移动端配置（HarmonyOS）

### 1. 新增的mDNS客户端

**文件：** `arkts/entry/src/main/ets/MDnsClient.ets`

**功能：**
- 监听mDNS多播组（224.0.0.251:5353）
- 解析标准DNS报文格式
- 自动发现 `_http._tcp.local` 服务
- 支持服务发现回调

### 2. 增强配对服务更新

**文件：** `arkts/entry/src/main/ets/EnhancedPairingService.ets`

**更新内容：**
- 添加mDNS客户端支持
- 优先使用mDNS，失败时回退到UDP广播
- 处理mDNS服务发现事件
- 保持向后兼容性

### 3. 发现流程

1. **启动发现**：调用 `startDiscovery()` 方法
2. **mDNS优先**：尝试启动mDNS监听
3. **回退机制**：如果mDNS失败，回退到UDP广播
4. **服务处理**：将发现的mDNS服务转换为 `ServerConnectionInfo`

## 网络要求

### 防火墙配置

**Windows：**
```powershell
# 允许mDNS入站
New-NetFirewallRule -DisplayName "mDNS (UDP 5353)" -Direction Inbound -Protocol UDP -LocalPort 5353 -Action Allow

# 允许mDNS出站
New-NetFirewallRule -DisplayName "mDNS (UDP 5353)" -Direction Outbound -Protocol UDP -LocalPort 5353 -Action Allow
```

**Linux/macOS：**
```bash
# 通常不需要额外配置，系统已支持mDNS
```

### 路由器配置

大多数家用路由器默认允许mDNS流量。如果遇到问题，请确保：
1. 多播（Multicast）功能已启用
2. UDP端口5353未被阻止
3. IGMP snooping已启用（可选）

## 故障排除

### 1. mDNS服务未启动

**症状：** 移动端无法发现服务端

**检查：**
```bash
# 检查服务端mDNS服务是否运行
netstat -an | grep 5353

# 检查防火墙规则
sudo ufw status verbose
```

**解决：**
- 确保防火墙允许UDP 5353端口
- 检查网络是否支持多播

### 2. 服务发现失败

**症状：** 移动端显示"mDNS listening failed"

**检查：**
```bash
# 使用工具测试mDNS
# Windows: Bonjour Browser 或 mDNSResponder
# macOS: dns-sd
# Linux: avahi-browse

dns-sd -B _http._tcp local
```

**解决：**
- 确保网络接口支持多播
- 检查路由器多播设置
- 尝试重启网络服务

### 3. 兼容性问题

**症状：** 某些设备无法发现服务

**解决：**
- 系统自动回退到UDP广播
- 检查UDP广播端口12345是否开放
- 确保服务端UDP广播仍在运行

## 开发说明

### 1. 测试mDNS功能

**服务端测试：**
```bash
# 使用dig查询mDNS服务
dig @224.0.0.251 -p 5353 _http._tcp.local PTR

# 使用nmap扫描
nmap -sU -p 5353 224.0.0.251
```

**客户端测试：**
```typescript
// 手动测试mDNS客户端
const mdnsClient = getMDnsClient();
await mdnsClient.startListening();
const services = mdnsClient.getDiscoveredServices();
console.log('Discovered services:', services);
```

### 2. 调试日志

**服务端日志：**
- 查看 `MDnsService` 日志输出
- 检查服务注册和广告状态

**客户端日志：**
- 查看 `EnhancedPairingService` 日志
- 检查mDNS监听状态
- 查看发现的服务信息

### 3. 性能考虑

- mDNS广告间隔：60秒（可配置）
- 查询响应：立即响应
- 网络流量：仅多播组设备接收
- 内存使用：轻量级UDP通信

## 向后兼容性

### 1. UDP广播支持

系统保持对旧版UDP广播的兼容：
- mDNS失败时自动回退到UDP广播
- UDP广播端口仍为12345
- 广播格式保持不变

### 2. 配置迁移

无需迁移配置，系统自动检测并选择最佳发现方式。

### 3. API兼容性

所有现有API保持不变：
- `EnhancedPairingService` 接口不变
- `ServerConnectionInfo` 结构不变
- 发现回调机制不变

## 高级配置

### 1. 自定义服务名称

**服务端：**
```csharp
// 在MDnsService构造函数中修改
private readonly string _serviceName = "custom-service-name";
private readonly string _serviceType = "_http._tcp.local";
```

**客户端：**
```typescript
// 在MDnsClient构造函数中修改
private readonly serviceType: string = '_http._tcp.local';
private readonly serviceNamePrefix: string = 'custom-service-name';
```

### 2. 广告间隔调整

**服务端：**
```csharp
// 在MDnsService.StartAsync中修改
_advertisementTimer = new Timer(AdvertiseService, null, TimeSpan.Zero, TimeSpan.FromSeconds(30)); // 改为30秒
```

### 3. 多播地址配置

**默认：** `224.0.0.251`（IPv4 mDNS地址）
**IPv6：** `FF02::FB`（IPv6 mDNS地址）

如需支持IPv6，需要修改：
- 服务端：`_multicastAddress` 配置
- 客户端：`multicastAddress` 配置
- 网络栈：双栈支持

## 参考链接

1. [RFC 6762 - mDNS](https://tools.ietf.org/html/rfc6762)
2. [RFC 6763 - DNS-Based Service Discovery](https://tools.ietf.org/html/rfc6763)
3. [Apple Bonjour](https://developer.apple.com/bonjour/)
4. [Avahi (Linux mDNS)](https://avahi.org/)
5. [Windows mDNS](https://docs.microsoft.com/windows/win32/dnssd/mdns-and-dns-sd)