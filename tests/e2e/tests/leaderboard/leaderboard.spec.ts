import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('赛舟榜功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/leaderboard');
  });

  test('赛舟榜页面加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('赛舟榜', { timeout: 15000 });
  });

  test('显示 Tab 切换工具栏', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('.toolbar')).toBeVisible();
  });

  test('Tab 切换功能正常', async ({ page }) => {
    await waitForBlazor(page);
    const weekTab = page.locator('.tab').filter({ hasText: '周' });
    await expect(weekTab).toBeVisible();
    await weekTab.click();
    await expect(weekTab).toHaveClass(/active/);
  });

  test('有榜单区域或暂无数据', async ({ page }) => {
    await waitForBlazor(page);
    await page.waitForTimeout(2000); // 等待数据加载
    const leaderboard = page.locator('.leaderboard-table');
    const empty = page.locator('.empty');
    const loading = page.locator('.loading');
    await expect(leaderboard.or(empty).or(loading)).toBeVisible({ timeout: 15000 });
  });
});
