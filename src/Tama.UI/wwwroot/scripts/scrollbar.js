// Tama 自绘 overlay 滚动条（材质口径见 css/tama.css「滚动条」小节）。
//
// 为什么自绘：Chromium 只要标准属性 scrollbar-width/scrollbar-color 生效就会忽略
//   ::-webkit-scrollbar（本项目实测），而标准属性给不了「悬停显形 / 拖拽 / 渐隐 /
//   圆角胶囊」——要做 macOS Tahoe 那种 overlay 滚动条只能自己画。
// 做法：JS 找出所有真实滚动容器 → 加 .tm-sb-none 关掉原生条 → 在 fixed 覆盖层里按
//   容器几何画 thumb。滚动本身（键盘 / 滚轮 / 触控板惯性 / scrollIntoView）零接管，
//   这里只负责"画"和"抓"，所以不会引入滚动逻辑上的偏差。
// 对外入口：window.tamaScrollbar.refresh()（程序性改完 DOM 后可手动重扫；
//   平时靠 MutationObserver 自动兜底）。
window.tamaScrollbar = (function () {
    'use strict';

    // 先打标记再谈隐藏原生条：CSS 里 html.tm-sb-js 才生效 —— 脚本要是没加载/报错，
    // 原生条照旧在，不会出现「条被藏了、又没人画」的空窗。
    document.documentElement.classList.add('tm-sb-js');

    var IDLE_MS = 1100;      // 停止滚动/移出后多久淡出（macOS overlay 手感）
    var MIN_THUMB = 32;      // thumb 最短长度，保证抓得住
    var MIN_OVERFLOW = 1;    // 超出多少像素才算可滚动
    var MIN_EXTENT = 56;     // 容器太矮/太窄就不接管（chip 行那种碎溢出画出来更闹）
    var TRACK_INSET = 3;     // 轨道两端留缝，避开容器圆角
    var GUTTER = 3;          // 条身与容器边缘的留缝
    var SCAN_DELAY = 160;    // DOM 增删后的重扫节流

    var layer = null;        // 常驻覆盖层（position:fixed，不吃事件）
    var bars = [];
    var pending = [];
    var rafId = 0;
    var scanTimer = 0;

    function scrollingEl() {
        return document.scrollingElement || document.documentElement;
    }

    function isRoot(el) {
        return el === scrollingEl() || el === document.documentElement || el === document.body;
    }

    function viewportBox(el) {
        if (isRoot(el)) {
            return { top: 0, left: 0, right: window.innerWidth, bottom: window.innerHeight };
        }
        var r = el.getBoundingClientRect();
        return { top: r.top, left: r.left, right: r.right, bottom: r.bottom };
    }

    // 弹窗打开时根滚动条不画：背后页面此时被遮罩盖住且不可交互，画出来就是浮在遮罩上的一块异物。
    // 用 rect 判活（关闭态的容器多为 display:none / 零尺寸），避免 MudDialogProvider 常驻节点误判。
    function modalOpen() {
        var list = document.querySelectorAll('.mud-dialog-container, .mud-overlay');
        for (var i = 0; i < list.length; i++) {
            var r = list[i].getBoundingClientRect();
            if (r.width > 0 && r.height > 0) return true;
        }
        return false;
    }

    // ---------- 几何（只读，不改宿主布局） ----------
    function measure(bar) {
        var el = bar.host;
        if (!el.isConnected) return null;
        if (bar.root && modalOpen()) return null;

        var y = bar.axis === 'y';
        var box = viewportBox(el);
        // 与视口求交：滚出屏幕外的部分不画（覆盖层用的就是视口坐标）
        var start = Math.max(y ? box.top : box.left, 0) + TRACK_INSET;
        var end = Math.min(y ? box.bottom : box.right,
                           y ? window.innerHeight : window.innerWidth) - TRACK_INSET;
        var track = end - start;
        if (track < MIN_THUMB + 2) return null;

        var client = y ? el.clientHeight : el.clientWidth;
        var total = y ? el.scrollHeight : el.scrollWidth;
        var pos = y ? el.scrollTop : el.scrollLeft;
        var max = total - client;
        if (max <= MIN_OVERFLOW) return null;

        // 比例换算：thumb 长度 = 可视/内容；位置 = 滚动进度 × 可移动余量
        var thumbLen = Math.min(track, Math.max(MIN_THUMB, Math.round(client / total * track)));
        var thumbPos = start + Math.round(pos / max * (track - thumbLen));
        // cross = 容器在横轴上的可视边缘（视口坐标）。条必须贴着自己容器的边，不是贴视口边，
        // 否则卡片里的嵌套滚动区会把条画到窗口最右侧，跟容器脱节（也会和根条叠在一起）。
        var cross = y ? Math.min(box.right, window.innerWidth) : Math.min(box.bottom, window.innerHeight);
        return { start: start, track: track, thumbPos: thumbPos, thumbLen: thumbLen, cross: cross };
    }

    // ---------- 单条滚动条 ----------
    function Bar(host, axis) {
        var self = this;
        this.host = host;
        this.axis = axis;
        this.root = isRoot(host);
        this.geom = null;
        this.cross = 0;       // 容器横轴可视边缘（视口坐标），update() 里刷新
        this.drag = null;
        this.near = false;
        this.hideTimer = 0;
        this.ro = null;

        this.el = document.createElement('div');
        this.el.className = 'tm-scrollbar tm-scrollbar-' + axis;
        this.thumb = document.createElement('div');
        this.thumb.className = 'tm-scrollbar-thumb';
        this.el.appendChild(this.thumb);
        layer.appendChild(this.el);

        if (host.classList) host.classList.add('tm-sb-none');

        // 根滚动容器的 scroll 事件目标是 Document 而非 <html>（挂在 html 上永远收不到，
        // 实证：thumb 钉在顶部不动）。root 挂 document，普通容器挂自身。
        var listenEl = this.root ? document : host;
        this.onScroll = function () { self.tick(); };
        this.onWheel = function () { self.reveal(); };
        listenEl.addEventListener('scroll', this.onScroll, { passive: true });
        listenEl.addEventListener('wheel', this.onWheel, { passive: true });
        this.el.addEventListener('pointerdown', function (e) { self.onDown(e); });
        this.el.addEventListener('contextmenu', function (e) { e.preventDefault(); });
        this.thumb.addEventListener('pointermove', function (e) { self.onMove(e); });
        this.thumb.addEventListener('pointerup', function (e) { self.endDrag(e); });
        this.thumb.addEventListener('pointercancel', function (e) { self.endDrag(e); });

        // 纯布局变化（窗口缩放、字体落地、抽屉开合）不产生 DOM 增删，得靠 RO 兜
        if (typeof ResizeObserver === 'function') {
            this.ro = new ResizeObserver(function () { queue(self); });
            this.ro.observe(host);
        }
    }

    Bar.prototype.update = function () {
        var m = measure(this);
        this.geom = m;
        if (!m) {
            this.el.style.display = 'none';
            return;
        }
        if (this.el.style.display === 'none') {
            this.el.style.display = '';
            this.el.classList.remove('tm-sb-on', 'tm-sb-grow');
        }
        var y = this.axis === 'y';
        var s = this.el.style;
        this.cross = m.cross;
        // 纵轴条：top/height 由这里给，宽度交给 CSS 的 --tm-sb-w（悬停放大靠它过渡）。
        // 横轴锚点一律用 right/bottom 而不是 left/top → 放大时朝内容侧生长，不会挤出视口。
        if (y) {
            s.top = m.start + 'px'; s.height = m.track + 'px';
            s.right = (window.innerWidth - m.cross + GUTTER) + 'px';
            s.left = ''; s.width = '';
        } else {
            s.left = m.start + 'px'; s.width = m.track + 'px';
            s.bottom = (window.innerHeight - m.cross + GUTTER) + 'px';
            s.top = ''; s.height = '';
        }
        var t = this.thumb.style;
        if (y) { t.top = (m.thumbPos - m.start) + 'px'; t.height = m.thumbLen + 'px'; }
        else { t.left = (m.thumbPos - m.start) + 'px'; t.width = m.thumbLen + 'px'; }
    };

    Bar.prototype.tick = function () {
        queue(this);
        this.reveal();
    };

    Bar.prototype.reveal = function () {
        if (!this.geom) return;   // 不可滚动 / 不在视口内就别显形
        this.el.classList.add('tm-sb-on');
        if (!this.near && !this.drag) this.scheduleHide();
    };

    Bar.prototype.scheduleHide = function () {
        var self = this;
        if (this.hideTimer) clearTimeout(this.hideTimer);
        this.hideTimer = setTimeout(function () {
            self.hideTimer = 0;
            if (!self.near && !self.drag) self.el.classList.remove('tm-sb-on', 'tm-sb-grow');
        }, IDLE_MS);
    };

    // 指针贴近条身（容器边缘 ~24px 带）才显形+放大 —— 对齐 overlay 滚动的「不打扰」手感：
    // 指针停在内容里不会常驻一条胶囊。位置全用解析式算（锚点见 update()），
    // 不调 getBoundingClientRect：这个函数每次 pointermove 都要跑。
    var NEAR_BAND = 24;
    Bar.prototype.proximity = function (x, y) {
        if (!this.geom) { this.near = false; return; }
        var y_ = this.axis === 'y';
        var edge = this.cross - NEAR_BAND;
        var along = y_ ? y : x;
        var cross = y_ ? x : y;
        var near = !!this.drag || (cross >= edge
            && along >= this.geom.start - NEAR_BAND
            && along <= this.geom.start + this.geom.track + NEAR_BAND);
        if (near === this.near) return;
        this.near = near;
        if (near) {
            if (this.hideTimer) { clearTimeout(this.hideTimer); this.hideTimer = 0; }
            this.el.classList.add('tm-sb-on', 'tm-sb-grow');
        } else {
            if (!this.drag) this.el.classList.remove('tm-sb-grow');
            this.scheduleHide();
        }
    };

    Bar.prototype.onDown = function (e) {
        var y = this.axis === 'y';
        e.preventDefault();
        e.stopPropagation();

        if (e.target === this.thumb) {
            // 拖拽：按「轨道可移动量 : 可滚动量」的比例反算滚动位置
            var client = y ? this.host.clientHeight : this.host.clientWidth;
            var total = y ? this.host.scrollHeight : this.host.scrollWidth;
            var travel = this.geom ? Math.max(1, this.geom.track - this.geom.thumbLen) : 1;
            this.drag = {
                origin: y ? e.clientY : e.clientX,
                scroll: y ? this.host.scrollTop : this.host.scrollLeft,
                ratio: Math.max(1, total - client) / travel
            };
            if (this.hideTimer) { clearTimeout(this.hideTimer); this.hideTimer = 0; }
            this.el.classList.add('tm-sb-on', 'tm-sb-grow');
            try { this.thumb.setPointerCapture(e.pointerId); } catch (err) { /* 老 WebView 无 capture */ }
            return;
        }

        // 点轨道：整屏翻页（与系统 overlay 条一致）
        var box = this.thumb.getBoundingClientRect();
        var before = y ? (e.clientY < box.top) : (e.clientX < box.left);
        var page = (y ? this.host.clientHeight : this.host.clientWidth) * (before ? -1 : 1);
        if (y) this.host.scrollTop += page; else this.host.scrollLeft += page;
    };

    Bar.prototype.onMove = function (e) {
        if (!this.drag) return;
        var y = this.axis === 'y';
        var delta = (y ? e.clientY : e.clientX) - this.drag.origin;
        var next = this.drag.scroll + delta * this.drag.ratio;
        if (y) this.host.scrollTop = next; else this.host.scrollLeft = next;
    };

    Bar.prototype.endDrag = function (e) {
        if (!this.drag) return;
        this.drag = null;
        try { this.thumb.releasePointerCapture(e.pointerId); } catch (err) { /* ignore */ }
        if (!this.near) this.el.classList.remove('tm-sb-grow');
        this.scheduleHide();
    };

    Bar.prototype.dispose = function () {
        if (this.hideTimer) clearTimeout(this.hideTimer);
        if (this.ro) { this.ro.disconnect(); this.ro = null; }
        if (this.el.parentNode) this.el.parentNode.removeChild(this.el);
        // 与构造时同侧解绑（root 挂的是 document）
        var listenEl = this.root ? document : this.host;
        listenEl.removeEventListener('scroll', this.onScroll);
        listenEl.removeEventListener('wheel', this.onWheel);
        // 解管时把原生条还回去；未接管的零散滚动区仍由 CSS 的 * 兜底保持细条中性色
        if (this.host.classList) this.host.classList.remove('tm-sb-none');
    };

    // ---------- 调度 ----------
    function queue(bar) {
        if (pending.indexOf(bar) < 0) pending.push(bar);
        if (!rafId) rafId = requestAnimationFrame(flush);
    }

    function flush() {
        rafId = 0;
        var list = pending;
        pending = [];
        for (var i = 0; i < list.length; i++) list[i].update();
    }

    function updateAll() {
        for (var i = 0; i < bars.length; i++) queue(bars[i]);
    }

    // 指针位置：rAF 节流后分发给各条，判断「贴边显形」
    var proximityQueued = false;
    var pointer = { x: -1, y: -1 };

    function onPointerMove(e) {
        pointer.x = e.clientX;
        pointer.y = e.clientY;
        if (proximityQueued) return;
        proximityQueued = true;
        requestAnimationFrame(function () {
            proximityQueued = false;
            for (var i = 0; i < bars.length; i++) bars[i].proximity(pointer.x, pointer.y);
        });
    }

    // 指针离开窗口时 pointermove 不再来，得主动把「贴边」状态清掉，否则那条会一直亮着
    function clearProximity() {
        for (var i = 0; i < bars.length; i++) bars[i].proximity(-1, -1);
    }

    // ---------- 扫描 ----------
    function wants(el, axis) {
        var y = axis === 'y';
        var root = scrollingEl();

        if (isRoot(el)) {
            // 根滚动条：html 的 computed overflow 是 visible，但视口照样滚 —— 只能实测
            var rcl = y ? root.clientHeight : root.clientWidth;
            var rsc = y ? root.scrollHeight : root.scrollWidth;
            return rcl > 0 && rsc - rcl > MIN_OVERFLOW;
        }

        // 便宜的检查放前面：绝大多数元素在 clientHeight/scrollHeight 这步就退出了
        var cl = y ? el.clientHeight : el.clientWidth;
        if (cl < MIN_EXTENT) return false;
        var sc = y ? el.scrollHeight : el.scrollWidth;
        if (sc - cl <= MIN_OVERFLOW) return false;

        var cs = getComputedStyle(el);
        if (cs.display === 'none' || cs.visibility === 'hidden') return false;
        var ov = y ? cs.overflowY : cs.overflowX;
        return ov === 'auto' || ov === 'scroll' || ov === 'overlay';
    }

    function syncAxis(host, axis) {
        var found = null;
        for (var i = 0; i < bars.length; i++) {
            if (bars[i].host === host && bars[i].axis === axis) { found = bars[i]; break; }
        }
        if (wants(host, axis)) {
            if (!found) {
                var b = new Bar(host, axis);
                bars.push(b);
                queue(b);
            }
        } else if (found) {
            found.dispose();
            bars.splice(bars.indexOf(found), 1);
        }
    }

    function sync(host) {
        syncAxis(host, 'y');
        syncAxis(host, 'x');   // 横向也要管：原生条一隐藏，横向溢出就成了"看不见却能滚"
    }

    function scan() {
        if (!document.body || !ensureLayer()) return;
        // 只有真正的视口滚动元素配一条根条：html/body 在 isRoot 判定下会报同一份指标，
        // 若放任下面那圈循环再去 sync 它们，就会叠出两条完全重合的根条。
        sync(scrollingEl());

        var all = document.querySelectorAll('*');
        for (var i = 0; i < all.length; i++) {
            var el = all[i];
            if (isRoot(el) || layer.contains(el)) continue;
            sync(el);
        }
    }

    function scheduleScan() {
        if (scanTimer) clearTimeout(scanTimer);
        scanTimer = setTimeout(function () {
            scanTimer = 0;
            scan();
            updateAll();
        }, SCAN_DELAY);
    }

    // ---------- 覆盖层 ----------
    // 层级跟着 Mud 自己的 dialog 令牌走：高于弹窗内容，低于启动遮罩（.tm-boot-veil = 3000）。
    // Mud 的主题令牌是运行时才写进 :root 的，所以每次 scan 都重取一遍，不能只取一次。
    function syncLayerZ() {
        if (!layer) return;
        var z = parseInt(getComputedStyle(document.documentElement).getPropertyValue('--mud-zindex-dialog'), 10);
        layer.style.zIndex = String((isFinite(z) ? z : 1300) + 10);
    }

    function ensureLayer() {
        if (layer && layer.isConnected) { syncLayerZ(); return layer; }
        if (!document.body) return null;
        layer = document.createElement('div');
        layer.className = 'tm-scrollbar-layer';
        syncLayerZ();
        document.body.appendChild(layer);
        return layer;
    }

    // ---------- 启动 ----------
    function start() {
        if (!ensureLayer()) return;
        scan();
        window.addEventListener('resize', function () { scan(); updateAll(); }, { passive: true });
        document.addEventListener('pointermove', onPointerMove, { passive: true });
        document.addEventListener('mouseleave', clearProximity);
        // 只关心增删：Mud 大量切 class 会刷属性事件，跟属性就会一直重扫
        try {
            new MutationObserver(scheduleScan)
                .observe(document.body, { childList: true, subtree: true });
        } catch (e) { /* 老 WebView 无 MutationObserver：退回 resize/scroll 驱动 */ }
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();

    return {
        refresh: function () { scan(); updateAll(); },
        count: function () { return bars.length; }   // 排查用：当前接管的（容器×轴）条数
    };
})();
