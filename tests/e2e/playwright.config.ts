import { defineConfig } from '@playwright/test';

export default defineConfig({
  globalSetup: './global-setup.ts',
  testDir: './tests',
  timeout: 60000,
  expect: { timeout: 15000 },
  fullyParallel: false,
  retries: 1,
  reporter: [['list'], ['json', { outputFile: 'test-results.json' }]],
  use: {
    baseURL: 'http://127.0.0.1:5177',
    headless: true,
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
    storageState: './storage-state.json',
    // Ubuntu 26.04 下 Playwright 无法自动下载浏览器，使用系统 Chromium
    launchOptions: {
      executablePath: process.env.PW_CHROMIUM_PATH || '/snap/chromium/current/usr/lib/chromium-browser/chrome',
      args: ['--no-sandbox', '--disable-setuid-sandbox'],
    },
  },
  projects: [
    // 导航系统：页面路由、导航栏、页面间跳转
    { name: 'navigation', testDir: './tests/navigation', testMatch: /.*\.spec\.ts/ },
    // 搜索功能：搜索页UI、搜索输入、查AI已移除
    { name: 'search', testDir: './tests/search', testMatch: /.*\.spec\.ts/ },
    // 知识库管理：知识库配置、根路径、Tab切换
    { name: 'vaults', testDir: './tests/vaults', testMatch: /.*\.spec\.ts/ },
    // AI构建：从问题生成、从笔记拆分、知识库选择
    { name: 'ai-build', testDir: './tests/ai-build', testMatch: /.*\.spec\.ts/ },
    // 备份恢复：创建备份、恢复备份、跨平台选项
    { name: 'backup', testDir: './tests/backup', testMatch: /.*\.spec\.ts/ },
    // 冒烟测试：所有页面能打开、不白屏、不卡spinner
    { name: 'smoke', testDir: './tests/smoke', testMatch: /.*\.spec\.ts/ },
    // Family 模式：用户类型选择、菜单过滤
    { name: 'family-mode', testDir: './tests/family-mode', testMatch: /.*\.spec\.ts/ },
    // 知识库浏览：卡片式知识库列表、目录浏览、笔记预览
    { name: 'browse', testDir: './tests/browse', testMatch: /.*\.spec\.ts/ },
    // 任务管理：任务列表、状态显示、重试、清空
    { name: 'tasks', testDir: './tests/tasks', testMatch: /.*\.spec\.ts/ },
    // 设置页：AI 提供商、模型切换、根路径
    { name: 'settings', testDir: './tests/settings', testMatch: /.*\.spec\.ts/ },
    // 记忆卡片：Anki 卡片生成任务
    { name: 'anki', testDir: './tests/anki', testMatch: /.*\.spec\.ts/ },
  ],
});
