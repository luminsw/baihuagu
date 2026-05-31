import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

/**
 * 冒烟测试：验证所有页面能正常打开，不会白屏或卡在加载状态
 * 
 * 核心原则：不论配置是否有问题，都不能使用户陷入困惑
 * - 不能白屏
 * - 不能永久 spinner
 * - 配置缺失时显示可理解的提示
 */
test.describe('冒烟测试 - 所有页面', () => {

  test('首页 (/) 能打开且不白屏', async ({ page }) => {
    await navigateTo(page, '/');
    // 页面应有可见内容（导航栏或任何元素）
    await expect(page.locator('nav')).toBeVisible({ timeout: 15000 });
    // 不应有未处理的错误
    await expect(page.locator('.error-boundary')).not.toBeVisible();
  });

  test('搜索页 (/search) 能打开且不白屏', async ({ page }) => {
    await navigateTo(page, '/search');
    await expect(page.locator('h1')).toBeVisible({ timeout: 15000 });
    await expect(page.locator('h1')).toContainText('搜索');
  });

  test('AI 构建页 (/build) 能打开且不白屏', async ({ page }) => {
    await navigateTo(page, '/build');
    // 应显示标题，不应白屏
    await expect(page.locator('h1')).toBeVisible({ timeout: 15000 });
    // 不应永久显示加载中（5秒内应消失）
    await page.waitForTimeout(5000);
    await expect(page.getByText('正在加载配置...')).not.toBeVisible();
  });

  test('AI 对话页 (/messages) 能打开且不白屏', async ({ page }) => {
    await navigateTo(page, '/messages');
    await expect(page.locator('h1, .chat-container, .messages-page, .card').first()).toBeVisible({ timeout: 15000 });
  });

  test('任务页 (/tasks) 能打开且不白屏', async ({ page }) => {
    await navigateTo(page, '/tasks');
    // 应有可见内容，不应白屏
    await expect(page.locator('h1, .card, .task-list').first()).toBeVisible({ timeout: 15000 });
  });

  test('知识库管理页 (/vaults) 能打开且不白屏', async ({ page }) => {
    await navigateTo(page, '/vaults');
    await expect(page.locator('.card').first()).toBeVisible({ timeout: 15000 });
  });

  test('设置页 (/settings) 能打开且不白屏', async ({ page }) => {
    await navigateTo(page, '/settings');
    await expect(page.locator('h1, .card, .settings-page').first()).toBeVisible({ timeout: 15000 });
  });
});

test.describe('冒烟测试 - 知识库页 Tab 体验', () => {

  test('知识库路径配置 Tab 不卡在加载中', async ({ page }) => {
    await navigateTo(page, '/vaults?tab=vaults');
    // 5秒内应停止加载
    await page.waitForTimeout(5000);
    await expect(page.getByText('正在加载...')).not.toBeVisible();
    // 应有可见内容
    await expect(page.locator('.card-body').first()).toBeVisible();
  });

  test('根路径 Git Tab 不卡在加载中', async ({ page }) => {
    await navigateTo(page, '/vaults?tab=git');
    // 5秒内应停止加载
    await page.waitForTimeout(5000);
    await expect(page.getByText('正在加载...')).not.toBeVisible();
    // 应显示有意义的内容（不是空白）
    const hasContent = await page.locator('.alert, .card-body, h5').first().isVisible();
    expect(hasContent).toBeTruthy();
  });

  test('备份恢复 Tab 不卡在加载中', async ({ page }) => {
    await navigateTo(page, '/vaults?tab=backup');
    // 5秒内应停止加载
    await page.waitForTimeout(5000);
    await expect(page.getByText('正在加载...')).not.toBeVisible();
    // 应显示创建/恢复区域
    await expect(page.getByRole('heading', { name: /创建备份/ })).toBeVisible();
  });
});

test.describe('冒烟测试 - 无永久 Spinner', () => {

  const pages = [
    { name: '首页', path: '/' },
    { name: '搜索', path: '/search' },
    { name: 'AI构建', path: '/build' },
    { name: 'AI对话', path: '/messages' },
    { name: '任务', path: '/tasks' },
    { name: '知识库', path: '/vaults' },
    { name: '设置', path: '/settings' },
  ];

  for (const p of pages) {
    test(`${p.name} (${p.path}) 无永久 spinner`, async ({ page }) => {
      await navigateTo(page, p.path);
      // 等待足够时间让所有加载完成
      await page.waitForTimeout(8000);
      // 不应有 spinner 还在转（排除按钮内的小 spinner）
      const spinners = page.locator('.spinner-border:not(.spinner-border-sm)');
      const spinnerCount = await spinners.count();
      expect(spinnerCount).toBe(0);
    });
  }
});
