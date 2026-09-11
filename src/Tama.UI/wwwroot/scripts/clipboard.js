// Tama UI 微型 JS 工具（随 Blazor 页面按需调用）。
// 注意：函数挂在 window 上，任何 RCL 页面都可通过 IJSRuntime 调用。
window.tamaClipboard = {
    _clearTimer: null,

    // 复制文本：优先 Clipboard API，失败回退 execCommand（WebView2 兼容）。
    // autoClearMs（可选）：指定后在该毫秒数后自动清空剪贴板（敏感内容防泄漏），
    // 新的复制会取消上一次的清除计划。
    copy: function (text, autoClearMs) {
        var self = window.tamaClipboard;
        var done = function () { return true; };
        var fallback = function () {
            try {
                var ta = document.createElement('textarea');
                ta.value = text;
                ta.style.position = 'fixed';
                ta.style.opacity = '0';
                document.body.appendChild(ta);
                ta.focus();
                ta.select();
                document.execCommand('copy');
                document.body.removeChild(ta);
                return true;
            } catch (e) {
                return false;
            }
        };
        var ok = false;
        if (navigator.clipboard && navigator.clipboard.writeText) {
            try {
                navigator.clipboard.writeText(text).then(done, function () { fallback(); });
                ok = true;
            } catch (e) {
                ok = fallback();
            }
        } else {
            ok = fallback();
        }

        // 敏感内容定时清空：覆盖式写空串，并取消旧计划（只保留最近一次）
        if (ok && typeof autoClearMs === 'number' && autoClearMs > 0) {
            if (self._clearTimer) { clearTimeout(self._clearTimer); self._clearTimer = null; }
            self._clearTimer = setTimeout(function () {
                try {
                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText('').then(function () {}, function () {});
                    }
                } catch (e) { /* 清空失败不阻塞 */ }
                self._clearTimer = null;
            }, autoClearMs);
        }
        return ok;
    },

    // 立即清空剪贴板（供页面显式调用）
    clear: function () {
        var self = window.tamaClipboard;
        if (self._clearTimer) { clearTimeout(self._clearTimer); self._clearTimer = null; }
        try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText('').then(function () {}, function () {});
                return true;
            }
        } catch (e) { /* ignore */ }
        return false;
    }
};
