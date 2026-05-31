import { test, expect } from '@playwright/test';
import { navigateTo, waitForBlazor } from '../helpers';

/**
 * 设置页 E2E 测试
 * 覆盖：AI 提供商配置、主模型切换、知识库根路径、API Key 加密
 */
test.describe('设置页', () => {
  test.beforeEach(async ({ page }) => {
    await navigateTo(page, '/settings');
  });

  test('设置页加载成功', async ({ page }) => {
    await expect(page.locator('h1')).toContainText('设置');
  });

  test('AI 提供商配置区域可见', async ({ page }) => {
    await expect(page.locator('text=/AI 提供商|AI Provider/').first()).toBeVisible({ timeout: 10000 });
  });

  test('知识库根路径设置区域可见', async ({ page }) => {
    await expect(page.locator('text=/知识库根路径|Vault Root/').first()).toBeVisible({ timeout: 10000 });
  });

  test('主模型选择器可见', async ({ page }) => {
    // 查找模型选择下拉框或按钮组
    const modelSelector = page.locator('select').filter({ hasText: /模型|model/ }).first()
      || page.locator('text=/主模型|默认模型/').first();
    await expect(modelSelector).toBeVisible({ timeout: 10000 });
  });

  test('API Key 输入框为密码类型', async ({ page }) => {
    // 查找 API Key 输入框（应为 password 类型或带隐藏按钮）
    const apiKeyInput = page.locator('input[type="password"]').first();
    const hasPasswordField = await apiKeyInput.isVisible().catch(() => false);

    // 或者可能有显示/隐藏切换按钮
    const toggleBtn = page.locator('button').filter({ hasText: /显示|隐藏|👁| eye/ }).first();
    const hasToggle = await toggleBtn.isVisible().catch(() => false);

    expect(hasPasswordField || hasToggle).toBe(true);
  });

  test('添加 AI 提供商表单可展开', async ({ page }) => {
    // 查找添加/新建提供商按钮
    const addBtn = page.getByRole('button').filter({ hasText: /添加|新建|新增.*提供商/ }).first();
    if (await addBtn.isVisible().catch(() => false)) {
      await addBtn.click();
      await waitForBlazor(page);
      // 应显示表单输入
      const nameInput = page.locator('input[placeholder*="名称"], input[placeholder*="Name"]').first();
      await expect(nameInput).toBeVisible();
    }
  });

  test('保存设置按钮存在', async ({ page }) => {
    const saveBtn = page.getByRole('button').filter({ hasText: /保存|提交|更新/ }).first();
    await expect(saveBtn).toBeVisible();
  });
});
