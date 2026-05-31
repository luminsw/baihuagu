import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

/**
 * Family 模式用户类型选择测试
 * 验证：首次访问弹窗、模式切换、菜单过滤
 */
test.describe('Family 模式 - 用户类型选择', () => {

  test.beforeEach(async ({ page }) => {
    // 清除 localStorage 模拟首次访问
    await page.goto('/');
    await page.evaluate(() => { localStorage.clear(); });
    await page.reload();
    await waitForBlazor(page);
  });

  test('首次访问显示用户类型选择对话框', async ({ page }) => {
    // 对话框应包含两个选项
    const dialog = page.locator('.modal-content');
    await expect(dialog).toBeVisible({ timeout: 10000 });
    const buttons = dialog.locator('button');
    await expect(buttons).toHaveCount(2);
    await expect(dialog.getByText('非专业人员').first()).toBeVisible();
    await expect(dialog.getByText('专业人员').nth(1)).toBeVisible();
    await expect(dialog.getByText('请选择适合您的使用模式')).toBeVisible();
  });

  test('选择"非专业人员"后对话框关闭', async ({ page }) => {
    const dialog = page.locator('.modal-content');
    await dialog.locator('button').nth(0).click();
    await page.waitForTimeout(800);
    await expect(dialog).not.toBeVisible();
  });

  test('选择"专业人员"后对话框关闭', async ({ page }) => {
    const dialog = page.locator('.modal-content');
    await dialog.locator('button').nth(1).click();
    await page.waitForTimeout(800);
    await expect(dialog).not.toBeVisible();
  });

  test('导航栏底部显示模式切换开关', async ({ page }) => {
    await page.locator('text=非专业人员').click();
    await page.waitForTimeout(800);
    // 应有"专业"和"简易"标签
    await expect(page.locator('text=专业').first()).toBeVisible();
    await expect(page.locator('text=简易').first()).toBeVisible();
    // 应有开关滑块
    await expect(page.locator('.switch-track')).toBeVisible();
  });

  test('专业模式显示高级菜单（基础设施场景）', async ({ page }) => {
    const dialog = page.locator('.modal-content');
    await dialog.locator('button').nth(1).click();
    await page.waitForTimeout(800);
    // 切换到基础设施场景
    await page.locator('text=基础设施').click();
    await page.waitForTimeout(800);
    // 专业模式应显示硬件评测等
    await expect(page.locator('text=硬件评测')).toBeVisible();
    await expect(page.locator('text=AI 性能监控')).toBeVisible();
    await expect(page.locator('text=日志配置')).toBeVisible();
  });

  test('简易模式隐藏高级菜单（基础设施场景）', async ({ page }) => {
    const dialog = page.locator('.modal-content');
    await dialog.locator('button').nth(0).click();
    await page.waitForTimeout(800);
    // 切换到基础设施场景
    await page.locator('text=基础设施').click();
    await page.waitForTimeout(800);
    // 简易模式不应显示硬件评测等
    await expect(page.locator('text=硬件评测')).not.toBeVisible();
    await expect(page.locator('text=AI 性能监控')).not.toBeVisible();
    await expect(page.locator('text=日志配置')).not.toBeVisible();
  });

  test('点击开关可切换模式', async ({ page }) => {
    // 先选择非专业人员
    const dialog = page.locator('.modal-content');
    await dialog.locator('button').nth(0).click();
    await page.waitForTimeout(800);
    // 点击开关切换到专业模式
    await page.locator('.mode-switch').click();
    await page.waitForTimeout(800);
    // 切换到基础设施场景验证
    await page.locator('text=基础设施').click();
    await page.waitForTimeout(800);
    await expect(page.locator('text=硬件评测')).toBeVisible();
  });

  test('刷新页面后记住上次选择的模式', async ({ page }) => {
    const dialog = page.locator('.modal-content');
    await dialog.locator('button').nth(1).click();
    await page.waitForTimeout(800);
    // 刷新页面
    await page.reload();
    await waitForBlazor(page);
    // 不应再显示对话框
    await expect(page.locator('.modal-content')).not.toBeVisible();
    // 切换到基础设施验证专业模式仍然生效
    await page.locator('text=基础设施').click();
    await page.waitForTimeout(800);
    await expect(page.locator('text=硬件评测')).toBeVisible();
  });
});
