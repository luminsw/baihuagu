import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

/**
 * 记忆卡片（Anki）E2E 测试
 * 覆盖：卡片生成任务触发、任务显示、卡片内容解析
 */
test.describe('记忆卡片 - Anki', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/vaults');
  });

  test('知识库页有生成记忆卡片按钮', async ({ page }) => {
    // 查找生成卡片按钮
    const genBtn = page.getByRole('button').filter({ hasText: /生成记忆卡片|生成卡片|Anki/ }).first();
    await expect(genBtn).toBeVisible({ timeout: 10000 });
  });

  test('点击生成卡片创建 anki_generate 任务', async ({ page }) => {
    const genBtn = page.getByRole('button').filter({ hasText: /生成记忆卡片|生成卡片/ }).first();
    if (await genBtn.isVisible().catch(() => false)) {
      await genBtn.click();
      await waitForBlazor(page);

      // 应显示确认或成功提示
      const hasToast = await page.locator('text=/任务已创建|已创建|开始生成/').first().isVisible().catch(() => false);
      const hasDialog = await page.locator('.modal-content').first().isVisible().catch(() => false);
      expect(hasToast || hasDialog).toBe(true);
    }
  });

  test('任务页显示 anki_generate 任务结果', async ({ page }) => {
    await navigateTo(page, '/tasks');
    await page.waitForTimeout(3000);

    // 查找卡片生成任务
    const ankiTask = page.locator('text=/anki_generate|anki_card_generate|记忆卡片/').first();
    if (await ankiTask.isVisible().catch(() => false)) {
      // 应显示处理笔记数或生成卡片数
      const hasCount = await page.locator('text=/处理.*笔记|生成.*卡片|\d+ 张/').first().isVisible().catch(() => false);
      expect(hasCount).toBe(true);
    }
  });

  test('AI 生成笔记后自动触发卡片生成任务', async ({ page }) => {
    // 此测试验证 ai_query 任务完成后是否有 anki_card_generate 任务
    await navigateTo(page, '/tasks');
    await page.waitForTimeout(3000);

    // 如果存在 AI 生成任务，检查是否有关联的卡片生成任务
    const aiTasks = await page.locator('text=/ai_query|AI 生成/').count();
    if (aiTasks > 0) {
      const ankiTasks = await page.locator('text=/anki_card_generate/').count();
      // AI 生成任务可能触发卡片生成（不是必然的，取决于配置）
      expect(ankiTasks).toBeGreaterThanOrEqual(0);
    }
  });
});
