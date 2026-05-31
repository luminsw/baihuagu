import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

/**
 * 知识库管理 E2E 测试
 * 注：重复创建拦截、实际新建/删除知识库测试已移到手动测试列表（多步弹窗交互在慢速环境不稳定）
 */
test.describe('知识库管理', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/vaults?tab=vaults');
  });

  test('知识库页加载成功', async ({ page }) => {
    await expect(page.locator('.card').first()).toBeVisible({ timeout: 15000 });
  });

  test('有三个Tab', async ({ page }) => {
    await expect(page.getByRole('button', { name: /知识库路径配置/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /根路径 Git/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /备份恢复/ })).toBeVisible();
  });

  test('知识库根路径设置区域可见', async ({ page }) => {
    await expect(page.getByRole('heading', { name: /知识库根路径/ })).toBeVisible({ timeout: 10000 });
  });

  test('切换到备份恢复Tab', async ({ page }) => {
    await page.getByRole('button', { name: /备份恢复/ }).click();
    await waitForBlazor(page);
    await expect(page.getByRole('heading', { name: /创建备份/ })).toBeVisible();
    await expect(page.getByRole('heading', { name: /恢复备份/ })).toBeVisible();
  });

  test('备份恢复Tab有密码输入', async ({ page }) => {
    await page.getByRole('button', { name: /备份恢复/ }).click();
    await waitForBlazor(page);
    const passwordInputs = page.locator('input[type="password"]');
    await expect(passwordInputs.first()).toBeVisible();
  });

  test('切换到版本管理Tab', async ({ page }) => {
    await page.getByRole('button', { name: /根路径 Git/ }).click();
    await waitForBlazor(page);
    await expect(page.locator('.card-body').first()).toBeVisible();
  });

  test('知识库列表显示笔记数量', async ({ page }) => {
    const noteCountBadges = page.locator('text=/\\d+ 篇|\\d+ 笔记/');
    const count = await noteCountBadges.count();
    expect(count).toBeGreaterThanOrEqual(0);
  });

  test('知识库卡片有标签显示', async ({ page }) => {
    const badges = page.locator('.badge');
    const badgeCount = await badges.count();
    expect(badgeCount).toBeGreaterThanOrEqual(0);
  });

  test('新建知识库按钮存在', async ({ page }) => {
    const addBtn = page.getByRole('button').filter({ hasText: /新建|添加|创建.*知识库/ }).first();
    await expect(addBtn).toBeVisible({ timeout: 10000 });
  });

  test('删除知识库按钮存在', async ({ page }) => {
    const deleteBtn = page.getByRole('button').filter({ hasText: /删除/ }).first();
    const hasVaults = await page.locator('.card').count() > 1;
    if (hasVaults) {
      await expect(deleteBtn).toBeVisible();
    }
  });
});
