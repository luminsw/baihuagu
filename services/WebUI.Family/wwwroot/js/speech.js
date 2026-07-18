let speechSynthesis = window.speechSynthesis;
let currentUtterance = null;

window.speakText = function(text) {
    stopSpeaking();
    
    if (!speechSynthesis) {
        console.warn('浏览器不支持语音合成');
        return;
    }
    
    const utterance = new SpeechSynthesisUtterance(text);
    utterance.lang = 'zh-CN';
    utterance.rate = 1.0;
    utterance.pitch = 1.0;
    
    utterance.onend = function() {
        currentUtterance = null;
    };
    
    utterance.onerror = function() {
        currentUtterance = null;
    };
    
    currentUtterance = utterance;
    speechSynthesis.speak(utterance);
};

window.stopSpeaking = function() {
    if (speechSynthesis) {
        speechSynthesis.cancel();
        currentUtterance = null;
    }
};