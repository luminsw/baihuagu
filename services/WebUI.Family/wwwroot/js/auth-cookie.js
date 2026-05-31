// 认证 Cookie 管理函数

/**
 * 设置认证 Cookie
 * @param {string} name - Cookie 名称
 * @param {string} value - Cookie 值
 * @param {number} days - 过期天数
 */
function setAuthCookie(name, value, days) {
    const maxAge = days * 24 * 60 * 60;
    document.cookie = `${name}=${value}; path=/; max-age=${maxAge}; SameSite=Strict`;
}

/**
 * 删除认证 Cookie
 * @param {string} name - Cookie 名称
 */
function deleteAuthCookie(name) {
    document.cookie = `${name}=; path=/; max-age=0; SameSite=Strict`;
}

/**
 * 获取 Cookie 值
 * @param {string} name - Cookie 名称
 * @returns {string|null} - Cookie 值或 null
 */
function getCookie(name) {
    const value = `; ${document.cookie}`;
    const parts = value.split(`; ${name}=`);
    if (parts.length === 2) return parts.pop().split(';').shift();
    return null;
}
