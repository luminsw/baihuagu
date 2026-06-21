import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('家长看板功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/dashboard');
  });

  test('仪表盘页面加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('家长看板', { timeout: 15000 });
  });

  test('显示本周学习动态区域', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('text=本周学习动态')).toBeVisible();
  });

  test('显示本周学习趋势区域', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('text=本周学习趋势')).toBeVisible();
  });

  test('页面有 section 区域', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('.section').first()).toBeVisible();
  });
});
