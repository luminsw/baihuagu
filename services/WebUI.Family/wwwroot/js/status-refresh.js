// 全局状态刷新辅助脚本
// 用于在 Blazor Server 跨组件/跨页面触发状态刷新

(function() {
    'use strict';

    // 存储 .NET 引用，用于回调
    let dotNetRef = null;

    // 监听来自其他页面的刷新请求
    window.addEventListener('storage', function(e) {
        if (e.key === 'vault-status-refresh' && e.newValue) {
            console.log('[StatusRefresh] 收到存储事件，触发状态刷新');
            triggerRefresh();
        }
    });

    // 触发状态刷新
    function triggerRefresh() {
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('OnGlobalStatusRefresh')
                .catch(err => console.error('[StatusRefresh] 调用 .NET 方法失败:', err));
        }
    }

    // 公开给 Blazor 调用的方法
    window.StatusRefresh = {
        // 注册 .NET 引用
        register: function(ref) {
            dotNetRef = ref;
            console.log('[StatusRefresh] 已注册 .NET 引用');
        },

        // 注销 .NET 引用
        unregister: function() {
            dotNetRef = null;
            console.log('[StatusRefresh] 已注销 .NET 引用');
        },

        // 请求刷新状态（会通知所有页面）
        requestRefresh: function() {
            // 使用 localStorage 触发跨页面/跨组件通信
            const timestamp = Date.now().toString();
            localStorage.setItem('vault-status-refresh', timestamp);
            console.log('[StatusRefresh] 已发送刷新请求:', timestamp);
            
            // 立即触发当前页面的刷新
            triggerRefresh();
            
            // 清理（避免存储空间无限增长）
            setTimeout(() => {
                localStorage.removeItem('vault-status-refresh');
            }, 1000);
        }
    };
})();
