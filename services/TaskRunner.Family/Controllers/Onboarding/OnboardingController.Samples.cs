using TaskRunner.Core.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskRunner.Contracts.Onboarding;
using TaskRunner.Data;
using TaskRunner.Data.Entities;
using TaskRunner.Services;

namespace TaskRunner.Controllers;

public partial class OnboardingController
{
    /// <summary>
    /// 创建示例知识库和笔记
    /// </summary>
    [HttpPost("create-sample-vault")]
    public async Task<ActionResult<CreateSampleVaultResponse>> CreateSampleVault([FromBody] CreateSampleVaultRequest request)
    {
        try
        {
            string vaultName = request.VaultName;
            string vaultType = request.VaultType;

            if (string.IsNullOrWhiteSpace(vaultName))
            {
                vaultName = vaultType == "tcm" ? "中医" : "计算机";
            }

            // 使用 VaultRootPathPreference 作为父目录，如果没有则使用默认路径
            var rootPath = _vaultSettings.VaultRootPathPreference;
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                rootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "vaults");
            }

            var industry = vaultType == "tcm" ? "中医" : "计算机";
            var vaultPath = Path.Combine(rootPath, "local", industry, vaultName);
            Directory.CreateDirectory(vaultPath);

            var vault = _vaultSettings.AddVault(vaultName, vaultPath, industry);

            var createdNotes = new List<string>();

            if (vaultType == "computer")
            {
                var notePath = "notes/AI知识入门.md";
                var noteContent = GetComputerSampleNote();
                await WriteVaultNoteAsync(vaultPath, notePath, noteContent);
                createdNotes.Add(notePath);
            }
            else if (vaultType == "tcm")
            {
                var notePath = "notes/脾胃病知识.md";
                var noteContent = GetTcmSampleNote();
                await WriteVaultNoteAsync(vaultPath, notePath, noteContent);
                createdNotes.Add(notePath);
            }

            _logger.LogInformation("示例知识库已创建: {VaultName} at {VaultPath}", vaultName, vaultPath);

            return Ok(new CreateSampleVaultResponse
            {
                Success = true,
                VaultId = vault.Id,
                Message = $"知识库 '{vaultName}' 创建成功",
                CreatedNotes = createdNotes
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建示例知识库失败");
            return StatusCode(500, new CreateSampleVaultResponse
            {
                Success = false,
                Message = $"创建失败: {ex.Message}"
            });
        }
    }

    private static async Task WriteVaultNoteAsync(string vaultPath, string notePath, string content)
    {
        var fullPath = Path.Combine(vaultPath, notePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await System.IO.File.WriteAllTextAsync(fullPath, content);
    }

    private static string GetComputerSampleNote()
    {
        return """
# AI 知识入门

## 什么是人工智能

人工智能（Artificial Intelligence，AI）是计算机科学的一个分支，致力于创建能够执行通常需要人类智能的任务的系统。

## 大语言模型（LLM）

大语言模型是当前 AI 领域最热门的技术之一：

- **GPT 系列**：OpenAI 开发，广泛应用于对话、写作、编程辅助
- **Claude**：Anthropic 开发，以长上下文和安全性著称
- **DeepSeek**：国产大模型，推理能力强，性价比高
- **通义千问（Qwen）**：阿里云开发，中文理解能力优秀

## 如何使用 AI 辅助学习

1. **提问式学习**：把不懂的概念用自然语言描述给 AI，让它用通俗语言解释
2. **笔记整理**：让 AI 帮助将零散知识点整理成结构化的学习笔记
3. **知识检验**：让 AI 出题考你，检验学习效果
4. **类比理解**：请 AI 用生活中的类比来解释抽象概念

## 提示词技巧（Prompt Engineering）

- 明确角色："你是一位经验丰富的中医老师"
- 给出背景："我对编程完全不懂，请用简单语言解释"
- 指定格式："请用 Markdown 列表形式输出"
- 分步引导：复杂问题拆解成多个小问题逐一询问

---

> 💡 **家庭学习小贴士**：家长可以把计算机知识库当作全家的技术资料中心，遇到电脑问题、想学习新软件、或者了解 AI 新闻，都可以在这里记录和查阅。
""";
    }

    private static string GetTcmSampleNote()
    {
        return """
# 脾胃病知识笔记

## 对脾胃的认识

中医认为「脾胃为后天之本，气血生化之源」。脾胃功能正常，人体才能消化吸收食物中的营养，转化为气血津液。

## 常见脾胃病证型

### 1. 脾胃气虚
**表现**：食欲减退、腹胀、大便溏薄、乏力、面色萎黄
**治法**：健脾益气
**常用方药**：四君子汤、补中益气汤

### 2. 脾胃虚寒
**表现**：胃脘冷痛、喜温喜按、畏寒肢冷、大便稀溏
**治法**：温中健脾
**常用方药**：理中丸、附子理中丸

### 3. 脾胃湿热
**表现**：脘腹胀满、口苦口臭、大便黏滞、舌苔黄腻
**治法**：清热利湿
**常用方药**：三仁汤、连朴饮

### 4. 肝郁脾虚
**表现**：胸胁胀满、情志不畅、腹痛即泻、泻后痛减
**治法**：疏肝健脾
**常用方药**：痛泻要方、逍遥散

## 日常养胃要点

| 宜 | 忌 |
|---|---|
| 定时定量进餐 | 暴饮暴食 |
| 细嚼慢咽 | 狼吞虎咽 |
| 温热饮食 | 过冷过热 |
| 心情舒畅 | 忧思恼怒 |
| 适度运动 | 久坐不动 |

## 经典方剂速记

- **四君子汤**：人参、白术、茯苓、甘草 → 补气健脾基础方
- **理中丸**：人参、干姜、白术、甘草 → 温中祛寒
- **香砂六君子汤**：四君子 + 木香、砂仁、陈皮、半夏 → 健脾和胃、行气化痰

---

> 🌿 **家庭养生小贴士**：脾胃养护重在日常。早饭要吃好，午饭要吃饱，晚饭要吃少。饭后百步走，活到九十九。保持心情愉快，因为「思伤脾」，过度思虑会损伤脾胃功能。
""";
    }
}
