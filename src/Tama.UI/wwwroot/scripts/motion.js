// Tama 动效编排层（设计取舍见 css/motion.css 头部注释）。
//
// 分工：能纯 CSS 表达的一律在 motion.css（进场、弹簧、按压、数字/圆环插值），
// 这里只做 CSS 做不到的四件事：
//   1. **重放**——CSS 动画在同一个类名上只会跑一次，路由切换/解锁收尾必须
//      "摘类 → 强制回流 → 加类"才能重播；
//   2. **列表 FLIP**——Blazor 整块换 DOM，增删/重排的行会跳；挂 vendor 库接手；
//   3. **数字滚动**——文本节点没法用 CSS 插值；
//   4. **侧栏滑块**——要量目标行的位置。
//
// 唯一的外部依赖（vendor/auto-animate.min.js，8 KB）出处，换版本时逐项核对：
//   包      @formkit/auto-animate          npm
//   版本    0.10.0（该包只发 ESM，没有 UMD/IIFE 构建）
//   来源    https://cdn.jsdelivr.net/npm/@formkit/auto-animate@0.10.0/index.min.js
//   SHA256  5CA9D8E8F3DA139285D954D0F796908683723D5892B418ADA84DBAEE89366794
//   许可    MIT（仓库根 LICENSE 之外，随包另附 /LICENSE）
// 文件未做任何修改。
//
// 为什么是经典脚本而不是 ES module：与 clipboard.js / theme.js / scrollbar.js 一致，
// 而且 Blazor 启动早于 module 脚本的求值时机（module 是 defer 语义），
// 经典脚本 + 动态 import() 能保证 window.tamaMotion 在 Blazor 首次 interop 前就位。
// auto-animate 只有 ESM，所以只能动态 import——这也顺带把"加载失败"变成可降级：
// 列表不动画，其余照常（可用 tamaMotion.aaState() 查是 'ready' 还是 'failed'）。
window.tamaMotion = (function () {
    'use strict';

    var PREF_KEY = 'tama-motion';   // 'full' | 'off'
    var LEGACY_PREF_KEY = 'keyguard-motion';   // 改名前的键，只读一次用于前移
    var ENTER_MS = 700;                 // 与 motion.css 的级联窗口对齐（240 延迟 + 340 时长 + 余量）
    var SCAN_MS = 120;                  // DOM 变动后的重扫节流

    var autoAnimate = null;             // vendor 库（异步到位；null = 降级为不动画）
    var aaState = 'pending';            // pending | ready | failed
    var animated = new WeakSet();       // 已挂 auto-animate 的容器
    var counted = new WeakSet();        // 已跑过数字滚动的元素
    var scanTimer = 0;

    // ---------- 动效闸门 ----------
    function systemReduced() {
        try { return window.matchMedia('(prefers-reduced-motion: reduce)').matches; } catch (e) { return false; }
    }
    function pref() {
        try {
            var v = localStorage.getItem(PREF_KEY);
            if (v === null) {
                // 改名前的键（keyguard-motion）兼容读取：读到就前移到新键。
                // 宿主 index.html 里有一份等价的内联实现（那份要赶在首帧前跑）。
                v = localStorage.getItem(LEGACY_PREF_KEY);
                if (v !== null) {
                    localStorage.setItem(PREF_KEY, v);
                    localStorage.removeItem(LEGACY_PREF_KEY);
                }
            }
            return v === 'off' ? 'off' : 'full';
        } catch (e) { return 'full'; }
    }
    // 用户选"完整"但系统要求减少动效时，以系统为准（无障碍优先，用户改不了）
    function enabled() { return pref() === 'full' && !systemReduced(); }

    function setPref(mode) {
        var m = mode === 'off' ? 'off' : 'full';
        try { localStorage.setItem(PREF_KEY, m); } catch (e) { /* 隐私模式下写不进，按本次会话生效 */ }
        document.documentElement.classList.toggle('tm-motion-off', m === 'off');
        return m;
    }

    // ---------- 元素解析：接受选择器 / 元素 / null ----------
    function resolve(target) {
        if (!target) return null;
        if (typeof target === 'string') return document.querySelector(target);
        return target.nodeType === 1 ? target : null;
    }

    // ---------- 1. 类名重放 ----------
    function replay(target, cls, ms) {
        var el = resolve(target);
        if (!el) return false;
        el.classList.remove(cls);
        void el.offsetWidth;            // 强制回流：不这么做浏览器认为"类没变过"，动画不重播
        el.classList.add(cls);
        var key = '__kgT_' + cls;
        if (el[key]) clearTimeout(el[key]);
        el[key] = setTimeout(function () {
            el.classList.remove(cls);
            el[key] = 0;
        }, ms || 700);
        return true;
    }

    var BLOCK_CLASS = 'tm-enter-block';   // 见 replayBlocks：标记"这一批已经在 DOM 里"的页面块

    /**
     * 页面块级重放：**只让此刻已经在 DOM 里的直接子块进场**。
     *
     * 为什么不能直接用 `.tm-content.tm-enter > *`：慢页面的内容是分几批进 DOM 的
     * （安全卫士要等 HIBP、密码库要等查询），而 tm-enter 得挂 ENTER_MS 才摘下来——
     * 期间后到的每一批都会各自再淡入一次，实测密码库一次切页能连闪三下
     * （骨架 / 筛选栏 / 列表各一下），用户看到的就是"一闪一闪"。
     * 打上标记之后，后插入的块不再跟着动，一次导航就只有一次进场。
     */
    function replayBlocks(rootSel, cls, ms) {
        var root = resolve(rootSel || '.tm-content');
        if (!root) return false;
        var kids = Array.prototype.slice.call(root.children);
        for (var i = 0; i < kids.length; i++) kids[i].classList.add(BLOCK_CLASS);
        var ok = replay(root, cls, ms);
        setTimeout(function () {
            for (var j = 0; j < kids.length; j++) kids[j].classList.remove(BLOCK_CLASS);
        }, ms || ENTER_MS);
        return ok;
    }

    // ---------- 2. 列表 FLIP ----------
    function attachLists(root) {
        if (!autoAnimate || aaState !== 'ready') return;
        var scope = root && root.querySelectorAll ? root : document;
        var list = scope.querySelectorAll('.tm-anim');
        for (var i = 0; i < list.length; i++) {
            var el = list[i];
            if (animated.has(el)) continue;
            animated.add(el);
            try {
                autoAnimate(el, {
                    duration: 240,
                    // 与 motion.css 的 --tm-spring-soft 兜底同一条曲线：
                    // WAAPI 的 easing 不接受 linear() 弹簧（各内核支持不一），用 cubic-bezier 保稳
                    easing: 'cubic-bezier(.22, 1, .36, 1)'
                });
            } catch (e) {
                animated.delete(el);    // 失败就退出标记，下次重扫再试
            }
        }
    }

    // ---------- 3. 数字滚动 ----------
    function runCount(el) {
        if (counted.has(el)) return;
        var to = parseFloat(el.getAttribute('data-tm-count'));
        if (!isFinite(to)) return;
        counted.add(el);
        if (!enabled()) { el.textContent = String(to); return; }

        var dur = 700;
        var t0 = 0;
        function step(now) {
            if (!t0) t0 = now;
            var p = Math.min(1, (now - t0) / dur);
            var e = 1 - Math.pow(1 - p, 3);     // easeOutCubic：数字"冲"到终点再停，比线性有精神
            if (p < 1) {
                el.textContent = String(Math.round(to * e));
                requestAnimationFrame(step);
            } else {
                el.textContent = String(to);    // 收尾写准，别让四舍五入停在 99
            }
        }
        requestAnimationFrame(step);
    }

    function countUp(root) {
        var scope = root && root.querySelectorAll ? root : document;
        var list = scope.querySelectorAll('[data-tm-count]');
        for (var i = 0; i < list.length; i++) runCount(list[i]);
    }

    // ---------- 4. 侧栏滑块 ----------
    function navPill() {
        var menu = document.querySelector('.tm-drawer .mud-navmenu');
        if (!menu) return;
        var pill = menu.querySelector('.tm-nav-pill');
        if (!pill) {
            pill = document.createElement('div');
            pill.className = 'tm-nav-pill';
            menu.insertBefore(pill, menu.firstChild);
        }
        var active = menu.querySelector('.mud-nav-link.active');
        if (!active) {
            pill.classList.remove('tm-pill-on');
            document.documentElement.classList.remove('tm-navpill');
            return;
        }
        // 首次出现不加过渡：否则胶囊会从菜单顶端"滑"下来，像是点错了什么
        var first = !document.documentElement.classList.contains('tm-navpill');
        var menuTop = menu.getBoundingClientRect().top;
        var linkTop = active.getBoundingClientRect().top;
        if (first) pill.style.transition = 'none';
        pill.style.height = active.getBoundingClientRect().height + 'px';
        pill.style.transform = 'translateY(' + (linkTop - menuTop) + 'px)';
        if (first) {
            void pill.offsetWidth;
            pill.style.transition = '';
            document.documentElement.classList.add('tm-navpill');
        }
        pill.classList.add('tm-pill-on');
    }

    // ---------- 重扫（DOM 增删后兜底） ----------
    function refresh() {
        attachLists(document);
        countUp(document);
        navPill();
    }

    function scheduleRefresh() {
        if (scanTimer) clearTimeout(scanTimer);
        scanTimer = setTimeout(function () { scanTimer = 0; refresh(); }, SCAN_MS);
    }

    // ---------- vendor 库：相对本脚本自身的 URL 解析 ----------
    // 不能用 './vendor/...'：宿主页有 <base href="/">，相对说明符会按页面解析成 /vendor/...
    function scriptUrl() {
        if (document.currentScript && document.currentScript.src) return document.currentScript.src;
        var all = document.getElementsByTagName('script');   // async/defer 场景兜底
        for (var i = all.length - 1; i >= 0; i--) {
            if (all[i].src && all[i].src.indexOf('motion.js') >= 0) return all[i].src;
        }
        return '';
    }

    function loadAutoAnimate() {
        var base = scriptUrl();
        if (!base) { aaState = 'failed'; return; }
        var url;
        try { url = new URL('vendor/auto-animate.min.js', base).href; }
        catch (e) { aaState = 'failed'; return; }
        try {
            import(/* webpackIgnore: true */ url).then(function (mod) {
                autoAnimate = mod && (mod.default || mod.autoAnimate);
                aaState = autoAnimate ? 'ready' : 'failed';
                if (aaState === 'ready') attachLists(document);
            }, function () { aaState = 'failed'; });
        } catch (e) {
            aaState = 'failed';
        }
    }

    // ---------- 启动 ----------
    function start() {
        loadAutoAnimate();
        refresh();
        try {
            // 只关心增删：Blazor 大量切 class / 改属性，跟属性就会一直重扫
            new MutationObserver(scheduleRefresh)
                .observe(document.body, { childList: true, subtree: true });
        } catch (e) { /* 老 WebView 无 MutationObserver：退回显式 refresh() 驱动 */ }
        // 系统外观/动效偏好运行时变化：从"减少"切回"完整"时把已经静态化的数字补正
        try {
            window.matchMedia('(prefers-reduced-motion: reduce)')
                .addEventListener('change', function () { refresh(); });
        } catch (e) { /* 老内核无 addEventListener 版 matchMedia */ }
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();

    return {
        // —— 供 Blazor (IJSRuntime) 调用 ——
        /** 路由切换 / 解锁遮罩收起后重放内容级联进场（只对此刻已在 DOM 里的页面块） */
        pageEnter: function (sel) {
            if (!enabled()) return;
            replayBlocks(sel || '.tm-content', 'tm-enter', ENTER_MS);
            // 页面内的一次性进场元素跟着一起播（数字、滑块位置都变了）
            countUp(document);
            navPill();
        },
        /** 失败反馈：摇头 */
        shake: function (sel) { return replay(sel || '.tm-unlock-card', 'tm-shake', 700); },
        /** 左右滑动切页：dir = 'next' | 'prev'（内容按方向从侧边进场，取代 pageEnter） */
        slide: function (dir, sel) {
            if (!enabled()) return false;
            return replayBlocks(sel || '.tm-content', dir === 'prev' ? 'tm-slide-prev' : 'tm-slide-next', 420);
        },
        /** 命中反馈：弹一下 */
        bump: function (sel) { return replay(sel, 'tm-bump', 420); },
        /** 重扫列表/数字（程序性改完 DOM 后可手动调） */
        refresh: function () { refresh(); },
        // —— 动效开关（设置页） ——
        getPref: function () { return pref(); },
        setPref: function (mode) { return setPref(mode); },
        /** 当前是否真的会动（用户开关 ∩ 系统偏好） */
        isEnabled: function () { return enabled(); },
        /** 排查用：'pending' | 'ready' | 'failed'（false = 列表 FLIP 降级） */
        aaState: function () { return aaState; }
    };
})();
