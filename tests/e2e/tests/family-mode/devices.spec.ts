import { test, expect } from '@playwright/test';

/**
 * 移动端管理页 E2E 测试
 * 注：设备注册后页面异步刷新测试已移到手动测试列表
 */

const apiPort = process.env.API_PORT || '8788';
const oneHopPort = 8789;

test.describe('移动端管理', () => {

  test('mDNS 发现端点应返回正确服务信息', async ({ request }) => {
    const res = await request.get(`http://localhost:${apiPort}/mg/discovery`);
    expect(res.status()).toBe(200);
    const json = await res.json();
    expect(json.serviceId).toBe('com.doctornotes.sync');
    expect(json.oneHopEnabled).toBe(true);
    expect(json.oneHopPort).toBe(oneHopPort);
  });
});
