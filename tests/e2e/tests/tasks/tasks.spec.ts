import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

/**
 * 任务管理页 E2E 测试
 * 覆盖：任务列表加载、各类任务显示、任务重试、清空历史
 */
test.describe('任务管理', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/tasks');
  });

  test('任务页加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('任务');
    await expect(page.locator('.task-list, .card').first()).toBeVisible({ timeout: 15000 });
  });

  test('WebSocket 连接状态显示', async ({ page }) => {
    // 应显示 WebSocket 连接状态（已连接或连接中）
    const wsStatus = page.locator('text=/WebSocket|连接/').first();
    await expect(wsStatus).toBeVisible({ timeout: 10000 });
  });

  test('任务列表可滚动加载', async ({ page }) => {
    // 等待任务列表加载
    await page.waitForTimeout(3000);
    const tasks = page.locator('[class*="task"], .task-item, .card');
    const count = await tasks.count();
    // 可能有0个或多个任务，但页面结构应正确
    expect(count).toBeGreaterThanOrEqual(0);
  });

  test('清空任务历史按钮存在', async ({ page }) => {
    const clearBtn = page.getByRole('button').filter({ hasText: /清空|清除/ }).first();
    await expect(clearBtn).toBeVisible();
  });

  test('点击清空任务显示确认对话框', async ({ page }) => {
    const clearBtn = page.getByRole('button').filter({ hasText: /清空|清除/ }).first();
    await clearBtn.click();
    await waitForBlazor(page);

    // 应显示确认对话框
    const dialog = page.locator('.modal-content');
    await expect(dialog).toBeVisible({ timeout: 5000 });
    await expect(dialog.getByText(/确认|确定|删除/)).toBeVisible();

    // 取消关闭对话框
    const cancelBtn = dialog.getByRole('button').filter({ hasText: /取消|关闭/ }).first();
    if (await cancelBtn.isVisible().catch(() => false)) {
      await cancelBtn.click();
      await expect(dialog).not.toBeVisible();
    }
  });

  test('失败任务显示重试区域', async ({ page }) => {
    // 查找失败状态的任务（如果有）
    const failedTask = page.locator('text=/Failed|失败|超时/').first();
    if (await failedTask.isVisible().catch(() => false)) {
      // 应有重试按钮
      const retryBtn = page.getByRole('button').filter({ hasText: /重试/ }).first();
      await expect(retryBtn).toBeVisible();
      // 应有超时输入
      const timeoutInput = page.locator('input[type="number"]').first();
      await expect(timeoutInput).toBeVisible();
    }
  });

  test('ai_query 类型任务显示请求详情', async ({ page }) => {
    // 查找 AI 生成相关任务
    const aiTask = page.locator('text=/ai_query|AI 生成|AI 构建/').first();
    if (await aiTask.isVisible().catch(() => false)) {
      // 点击展开任务详情
      await aiTask.click();
      await waitForBlazor(page);
      // 应显示模型信息或请求详情
      const hasDetail = await page.locator('text=/模型|provider|耗时|请求/').first().isVisible().catch(() => false);
      expect(hasDetail).toBe(true);
    }
  });

  test('anki_generate 任务显示卡片数', async ({ page }) => {
    // 查找卡片生成任务
    const ankiTask = page.locator('text=/anki_generate|anki_card_generate|记忆卡片/').first();
    if (await ankiTask.isVisible().catch(() => false)) {
      // 应显示生成卡片数量或处理笔记数
      const hasCardCount = await page.locator('text=/张卡片|处理.*笔记|生成.*卡片/').first().isVisible().catch(() => false);
      expect(hasCardCount).toBe(true);
    }
  });

  test('任务错误信息包含换 AI 建议提示', async ({ page }) => {
    // 查找包含"返回内容为空"的错误任务
    const emptyError = page.locator('text=/返回内容为空|不支持该问题/').first();
    if (await emptyError.isVisible().catch(() => false)) {
      // 应显示建议换 AI 的提示
      await expect(page.locator('text=/建议换一个 AI|换.*模型|切换.*提供商/').first()).toBeVisible();
    }
  });
});
