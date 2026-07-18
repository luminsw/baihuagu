let speechSynthesis = window.speechSynthesis;
let currentUtterance = null;
let dotNetRef = null;

window.speakText = function(text, dotNetObjRef, seq) {
    // 停止之前的播放
    if (speechSynthesis) {
        speechSynthesis.cancel();
    }
    currentUtterance = null;
    
    if (!speechSynthesis) {
        console.warn('浏览器不支持语音合成');
        return;
    }

    if (dotNetObjRef) {
        dotNetRef = dotNetObjRef;
    }
    
    const utterance = new SpeechSynthesisUtterance(text);
    utterance.lang = 'zh-CN';
    utterance.rate = 1.0;
    utterance.pitch = 1.0;
    
    // 捕获当前 seq 值，用于回调时传递
    const mySeq = seq;
    
    utterance.onend = function() {
        currentUtterance = null;
        if (dotNetRef) {
            // 延迟 100ms 通知 Blazor，避免在 headless/无音频环境下过快回调导致无限循环
            setTimeout(function() {
                try {
                    dotNetRef.invokeMethodAsync('OnSpeechEnded', mySeq);
                } catch (e) {
                    console.warn('通知播放结束失败:', e);
                }
            }, 100);
        }
    };
    
    utterance.onerror = function(e) {
        currentUtterance = null;
        // canceled 是用户主动停止，不需要通知
        if (e && e.error === 'canceled') return;
        if (dotNetRef) {
            // 延迟 100ms 通知 Blazor
            setTimeout(function() {
                try {
                    dotNetRef.invokeMethodAsync('OnSpeechEnded', mySeq);
                } catch (ex) {
                    console.warn('通知播放错误失败:', ex);
                }
            }, 100);
        }
    };
    
    currentUtterance = utterance;
    speechSynthesis.speak(utterance);
};

window.stopSpeaking = function() {
    if (speechSynthesis) {
        speechSynthesis.cancel();
    }
    currentUtterance = null;
};

window.isSpeaking = function() {
    return speechSynthesis && speechSynthesis.speaking;
};
