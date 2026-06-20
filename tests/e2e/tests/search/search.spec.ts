import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

test.describe('搜索功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/search');
  });

  test('搜索页加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('搜索');
  });

  test('搜索页有搜索输入框', async ({ page }) => {
    const input = page.locator('input[type="text"], input[placeholder*="搜索"]').first();
    await expect(input).toBeVisible({ timeout: 10000 });
  });

  test('搜索页有搜索按钮', async ({ page }) => {
    await expect(page.locator('button').filter({ hasText: /搜索/ })).toBeVisible();
  });

  test('搜索页有行业筛选下拉框', async ({ page }) => {
    await waitForBlazor(page);
    const industrySelect = page.locator('select.form-select').first();
    await expect(industrySelect).toBeVisible();
  });

  test('搜索页有知识库筛选下拉框', async ({ page }) => {
    await waitForBlazor(page);
    const vaultSelect = page.locator('select.form-select').nth(1);
    await expect(vaultSelect).toBeVisible();
  });

  test('输入搜索词后搜索按钮可用', async ({ page }) => {
    const input = page.locator('input[type="text"]').first();
    await input.fill('测试搜索');
    await waitForBlazor(page);

    const searchBtn = page.locator('button').filter({ hasText: /搜索/ });
    await expect(searchBtn).toBeEnabled();
  });

  test('搜索页显示搜索提示', async ({ page }) => {
    await waitForBlazor(page);
    await expect(page.locator('text=搜索提示')).toBeVisible();
  });
});
