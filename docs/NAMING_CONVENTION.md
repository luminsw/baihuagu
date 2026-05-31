# 项目多端命名规范

> 目的：统一沟通口径，避免"taskrunner""webui""后台""前端"等词在不同端之间产生歧义。

---

## 一、命名原则

1. **服务端按「版本 + 层」命名**：版本（家庭/官网）+ 层（后台/前端）
2. **移动端按「平台」命名**：直接叫平台名，不加"端"
3. **简称优先用 3~4 字母代号**：口头和书面都方便
4. **禁止混用的旧叫法**：
   - ❌ "后台"——不知道指家庭后台还是官网后台
   - ❌ "前端"——不知道指 WebUI 还是移动端
   - ❌ "服务端"——两个版本各有一个服务端
   - ❌ "taskrunner"——默认指家庭版，官网版也叫 TaskRunner.Cloud
   - ❌ "webui"——默认指家庭版，官网版也叫 WebUI.Cloud

---

## 二、各端正式名称与代号

### 服务端（Server）

| 完整名称 | 中文简称 | 英文代号 | 项目目录 |
|---------|---------|---------|---------|
| 家庭版后台服务 | **家庭后台** | `TRF` (TaskRunner Family) | `services/TaskRunner.Family/` |
| 家庭版Web界面 | **家庭前端** | `WUF` (WebUI Family) | `services/WebUI.Family/` |
| 官网版后台服务 | **官网后台** | `TRC` (TaskRunner Cloud) | `services/TaskRunner.Cloud/` |
| 官网版Web界面 | **官网前端** | `WUC` (WebUI Cloud) | `services/WebUI.Cloud/` |

### 移动端（Mobile）

| 完整名称 | 中文简称 | 英文代号 | 项目目录 |
|---------|---------|---------|---------|
| 鸿蒙ArkTS移动端 | **鸿蒙版** | `HMOS` | `arkts/` |
| Android移动端 | **安卓版** | `AND` | `kotlin/` |

### 其他

| 完整名称 | 中文简称 | 英文代号 | 说明 |
|---------|---------|---------|------|
| 官网静态站 | **官网站** | `SITE` | `website/`，纯 HTML 官网 |
| 共享契约库 | **契约库** | `CONTRACTS` | `TaskRunner.Contracts/`，前后端共享 DTO |

---

## 三、使用示例

### 口头沟通
> "今天改的是 **TRC** 的 Browse API，**WUC** 的面包屑也要同步。鸿蒙版不用动。"

### 书面/文档
> 【问题】**家庭后台** (TRF) 的 `GetConfigDirectory()` 存在栈溢出。  
> 【修复】已在 TRF 和 **官网后台** (TRC) 同步修复。

### Commit Message
```
fix(TRF): GetConfigDirectory 栈溢出

- 递归调用自身导致 StackOverflowException
- fallback 改为 AppDomain.CurrentDomain.BaseDirectory
```

### Issue / Bug 标签建议
| 标签 | 含义 |
|------|------|
| `TRF` | 家庭后台 |
| `WUF` | 家庭前端 |
| `TRC` | 官网后台 |
| `WUC` | 官网前端 |
| `HMOS` | 鸿蒙版 |
| `AND` | 安卓版 |

---

## 四、目录命名对照

```
services/
├── TaskRunner.Family/      → 家庭后台 (TRF)
├── WebUI.Family/           → 家庭前端 (WUF)
├── TaskRunner.Cloud/       → 官网后台 (TRC)
└── WebUI.Cloud/            → 官网前端 (WUC)

apps/
├── arkts/                  → 鸿蒙版 (HMOS)
└── kotlin/                 → 安卓版 (AND)
```

---

## 五、常见问题

**Q：为什么不用"服务端/前端"这种通用叫法？**  
A：本项目同时维护两套服务端（家庭/官网），说"服务端"无法区分版本，容易产生"改了官网但家庭版也需要"的遗漏。

**Q：TaskRunner.Cloud 和 WebUI.Cloud 为什么不合并叫"官网服务端"？**  
A：两者是独立进程（8788 / 5177），独立部署，出问题时需要分别排查，分开命名更精确。

**Q：移动端为什么不叫"移动端"而要叫"鸿蒙版/安卓版"？**  
A：两个移动端代码完全不同（ArkTS vs Kotlin），功能进度也不同，需要区分。
