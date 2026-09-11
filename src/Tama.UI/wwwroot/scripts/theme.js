// Tama 主题助手（三态 dark/light/system，对齐原版 settingsStore.theme）：
// 切换 <html data-theme>（驱动 tama.css 的 html[data-theme] 覆盖）+ localStorage 持久化。
// 启动恢复由 index.html 内联脚本完成（早于 Blazor 首帧）；此处只暴露运行时 API 给 UI 调用。
//
// 切换过渡：**只做颜色渐变，不做整屏快照动画**（2026-09 决定）。
//   曾经的 View Transitions 圆形揭示已删除，原因：VT 把整页拍成静态位图，
//   而本项目主题有两个源——JS 写 data-theme、Blazor 写 Mud 调色板变量（--mud-palette-*）。
//   两者不可能同帧落地，快照必然抓到"半新半旧"的画面 → 揭幕揭出错色，动画结束再"啪"地跳正。
//   改成对活 DOM 直接做 CSS 过渡后，两边各晚一帧只是"晚一点开始渐变"，看不出来。
window.tamaTheme = {
    _animTimer: null,

    // 改名前的 localStorage 键（keyguard-*）兼容读取：读到就前移到 tama-* 并删掉旧键，
    // 之后只认新键。产品改名不该把用户的主题/动效/自动解锁偏好重置。
    // 对页面公开成 readPref(key)：Unlock 页读 auto-bio 也走这里，省得各写一份兼容分支。
    // （宿主的 index.html 里有一份等价的内联实现——那份必须在首帧前跑，用不到这个文件。）
    readPref: function (key) {
        try {
            var v = localStorage.getItem('tama-' + key);
            if (v !== null) return v;
            var legacy = localStorage.getItem('keyguard-' + key);
            if (legacy !== null) {
                localStorage.setItem('tama-' + key, legacy);
                localStorage.removeItem('keyguard-' + key);
            }
            return legacy;
        } catch (e) { return null; }
    },

    _reduced: function () {
        try { return window.matchMedia('(prefers-reduced-motion: reduce)').matches; } catch (e) { return false; }
    },
    get: function () {
        try { return this.readPref('theme') || 'dark'; } catch (e) { return 'dark'; }
    },
    systemMode: function () {
        try {
            return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches
                ? 'dark' : 'light';
        } catch (e) { return 'dark'; }
    },

    // 应用模式：system 用 matchMedia 解析；返回实际生效的 resolved（dark/light）。
    // animate=true 才挂 .tm-theme-anim 做一次 220ms 颜色过渡——启动恢复**必须**传 false，
    // 否则每次打开 app 都会整屏淡一下（而且是在首帧、Blazor 还没渲染完的时候）。
    apply: function (mode, animate) {
        var resolved = mode === 'system' ? this.systemMode() : (mode === 'light' ? 'light' : 'dark');
        var root = document.documentElement;

        var doApply = function () {
            root.setAttribute('data-theme', resolved);
            try { localStorage.setItem('tama-theme', mode); } catch (e) { }
        };

        if (animate === true && !this._reduced()) {
            root.classList.add('tm-theme-anim');
            // 先落实"带过渡的样式"再改主题：否则浏览器只在同一次样式计算里看到最终值，过渡不触发
            void root.offsetHeight;
            doApply();
            // 连点：复用同一个计时器，不会叠加出多次过渡
            if (this._animTimer) clearTimeout(this._animTimer);
            this._animTimer = setTimeout(function () {
                root.classList.remove('tm-theme-anim');
                window.tamaTheme._animTimer = null;
            }, 220);
        } else {
            // 瞬时切换：顺手摘掉可能残留的过渡类，避免上一次的过渡状态干扰
            if (this._animTimer) { clearTimeout(this._animTimer); this._animTimer = null; }
            root.classList.remove('tm-theme-anim');
            doApply();
        }
        return resolved;
    },

    // system 模式下监听系统外观变化；resolved 变化回报 .NET（ThemeState → 宿主标题栏）
    // 注意：这里直接设 data-theme（不带过渡）——系统外观切换不该有"用户操作"的动效
    _watcher: null,
    watchSystem: function (dotNetRef) {
        if (this._watcher) return;
        try {
            var mq = window.matchMedia('(prefers-color-scheme: dark)');
            var handler = function (e) {
                var r = e.matches ? 'dark' : 'light';
                document.documentElement.setAttribute('data-theme', r);
                dotNetRef.invokeMethodAsync('OnSystemThemeChanged', r);
            };
            mq.addEventListener('change', handler);
            this._watcher = { mq: mq, handler: handler };
        } catch (e) { }
    }
};
