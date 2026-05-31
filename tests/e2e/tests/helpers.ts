import { Page, expect } from '@playwright/test';

/** 获取 CLI Token（每次新建，token 一次性） */
async function getCliToken(): Promise<string | null> {
  try {
    const resp = await fetch('http://127.0.0.1:5177/api/auth/cli-token', { method: 'POST' });
    if (resp.ok) {
      const data = await resp.json();
      if (data.token) return data.token;
    }
  } catch {
    // 忽略错误
  }
  return null;
}

/** 给 URL 附加 CLI Token */
async function urlWithToken(path: string): Promise<string> {
  const token = await getCliToken();
  if (!token) return path;
  const sep = path.includes('?') ? '&' : '?';
  return `${path}${sep}cli-token=${token}`;
}

/** 等待 Blazor 页面加载完成 */
export async function waitForBlazor(page: Page) {
  await page.waitForLoadState('networkidle');
  // Blazor Server 需要额外等待 SignalR 连接 + 组件渲染
  // 在慢速环境（如 snap Chromium）下需要更长时间
  await page.waitForTimeout(5000);
}

/** 导航到指定页面并等待加载（自动附加 CLI Token） */
export async function navigateTo(page: Page, path: string) {
  const url = await urlWithToken(path);
  await page.goto(url);
  await waitForBlazor(page);
}

/** 点击导航链接 */
export async function clickNav(page: Page, text: string) {
  await page.locator('nav a, nav .nav-link').filter({ hasText: text }).first().click();
  await waitForBlazor(page);
}
