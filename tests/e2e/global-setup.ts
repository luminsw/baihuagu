import { request } from '@playwright/test';

/**
 * 全局 Setup：获取 CLI Token 并写入 storageState，供所有测试使用
 * 家庭版需要认证，测试前先自动获取 token
 */
export default async function globalSetup() {
  const baseURL = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5177';

  try {
    const ctx = await request.newContext();
    const resp = await ctx.post(`${baseURL}/api/auth/cli-token`);
    if (resp.status() === 200) {
      const data = await resp.json();
      if (data.token) {
        // 写入 storageState（供有状态测试使用）
        const fs = require('fs');
        const storageState = {
          cookies: [
            {
              name: 'webui_auth',
              value: data.token,
              domain: '127.0.0.1',
              path: '/',
              expires: Math.floor(Date.now() / 1000) + 3600,
              httpOnly: true,
              secure: false,
              sameSite: 'Lax' as const,
            }
          ],
          origins: []
        };
        fs.writeFileSync('./storage-state.json', JSON.stringify(storageState, null, 2));
        console.log('[globalSetup] CLI token acquired and saved to storage-state.json');
      }
    } else {
      console.warn(`[globalSetup] Failed to get CLI token: ${resp.status()}`);
    }
    await ctx.dispose();
  } catch (err) {
    console.warn('[globalSetup] Error getting CLI token:', (err as Error).message);
  }
}
