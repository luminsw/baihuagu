import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

/**
 * AI 构建功能 E2E 测试
 * 注：部分元素查找测试已移到手动测试列表（在慢速 headless 环境不稳定）
 */
test.describe('AI 构建功能', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/build');
  });

  test('AI构建页加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('AI');
  });

  test('知识库选择器可见', async ({ page }) => {
    await expect(page.getByText('目标知识库')).toBeVisible();
  });

  test('从问题生成Tab有输入区域', async ({ page }) => {
    const textarea = page.locator('textarea').first();
    await expect(textarea).toBeVisible({ timeout: 10000 });
  });

  test('选择"新建"知识库后显示提示文案', async ({ page }) => {
    const select = page.getByRole('combobox');
    await expect(select).toBeVisible();

    // 选择 "新建"
    await select.selectOption('__new__');
    await waitForBlazor(page);

    // 应出现新建知识库提示
    await expect(page.getByText('✨ 新建知识库模式')).toBeVisible();
    await expect(page.getByText('新知识库将在此路径下创建子目录')).toBeVisible();
  });

  test('空查询时生成按钮禁用', async ({ page }) => {
    const textarea = page.locator('textarea').first();
    await textarea.fill('');
    await waitForBlazor(page);

    const genBtn = page.getByRole('button').filter({ hasText: /生成|创建/ }).first();
    await expect(genBtn).toBeDisabled();
  });
});
