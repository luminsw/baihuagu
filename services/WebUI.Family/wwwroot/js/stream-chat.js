/**
 * 流式 AI 对话：通过 fetch + ReadableStream 接收 SSE，实时回调 Blazor
 */
window.streamChat = async function(backendUrl, message, providerId, model, dotNetRef) {
    try {
        const payload = { message: message };
        if (providerId) payload.providerId = providerId;
        if (model) payload.model = model;

        const response = await fetch(backendUrl + '/api/ai/chat/stream', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });

        if (!response.ok) {
            const errorText = await response.text();
            dotNetRef.invokeMethodAsync('OnStreamError', `HTTP ${response.status}: ${errorText}`);
            return;
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;

            buffer += decoder.decode(value, { stream: true });
            const lines = buffer.split('\n');
            buffer = lines.pop(); // 保留未完成的行

            let currentEvent = '';
            for (const line of lines) {
                if (line.startsWith('event: ')) {
                    currentEvent = line.substring(7).trim();
                } else if (line.startsWith('data: ')) {
                    const data = line.substring(6);

                    if (currentEvent === 'delta') {
                        try {
                            const parsed = JSON.parse(data);
                            if (parsed.content) {
                                dotNetRef.invokeMethodAsync('OnStreamDelta', parsed.content);
                            }
                        } catch { }
                    } else if (currentEvent === 'error') {
                        dotNetRef.invokeMethodAsync('OnStreamError', data);
                    } else if (currentEvent === 'done') {
                        dotNetRef.invokeMethodAsync('OnStreamDone');
                    }
                }
            }
        }

        // 处理 buffer 中剩余内容
        if (buffer.trim()) {
            const lines = buffer.split('\n');
            let currentEvent = '';
            for (const line of lines) {
                if (line.startsWith('event: ')) {
                    currentEvent = line.substring(7).trim();
                } else if (line.startsWith('data: ')) {
                    const data = line.substring(6);
                    if (currentEvent === 'delta') {
                        try {
                            const parsed = JSON.parse(data);
                            if (parsed.content) {
                                dotNetRef.invokeMethodAsync('OnStreamDelta', parsed.content);
                            }
                        } catch { }
                    } else if (currentEvent === 'error') {
                        dotNetRef.invokeMethodAsync('OnStreamError', data);
                    } else if (currentEvent === 'done') {
                        dotNetRef.invokeMethodAsync('OnStreamDone');
                    }
                }
            }
        }

        // 如果流结束但没有收到 done 事件，也通知完成
        dotNetRef.invokeMethodAsync('OnStreamDone');
    } catch (err) {
        dotNetRef.invokeMethodAsync('OnStreamError', err.message || '未知错误');
    }
};
