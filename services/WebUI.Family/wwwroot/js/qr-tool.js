/**
 * 二维码工具 - JS 辅助函数
 */
window.generateQRCode = function (container, text) {
    if (!container) return;
    container.innerHTML = '';
    try {
        new QRCode(container, {
            text: text,
            width: 256,
            height: 256,
            colorDark: '#000000',
            colorLight: '#ffffff',
            correctLevel: QRCode.CorrectLevel.M
        });
    } catch (e) {
        console.error('QRCode generation failed:', e);
        container.innerHTML = '<div style="color:red;padding:1rem;">生成二维码失败</div>';
    }
};
