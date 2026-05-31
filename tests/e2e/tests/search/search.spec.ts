import { test, expect } from '@playwright/test';
import { navigateTo } from '../helpers';

/**
 * 搜索功能 - 页面加载与结构测试
 * 注：实际搜索触发测试已移到手动测试列表（依赖搜索结果渲染速度）
 */
test.describe('搜索功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/search');
  });

  test('搜索页加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('搜索');
  });

  test('搜索页有搜索输入框', async ({ page }) => {
    const input = page.locator('input[type="text"], textarea, input[placeholder*="搜索"], input[placeholder*="输入"]').first();
    await expect(input).toBeVisible({ timeout: 10000 });
  });

  test('搜索页没有查AI标签（已移到AI构建页）', async ({ page }) => {
    await expect(page.locator('text=查 AI')).not.toBeVisible();
  });

  test('搜索页标题正确', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('搜索');
    const h1Text = await page.locator('h1').textContent();
    expect(h1Text).not.toContain('查 AI');
  });
});
