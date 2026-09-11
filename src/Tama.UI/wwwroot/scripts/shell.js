// Tama 小屏外壳：底部胶囊导航的抽屉开关 + 左右滑动切页。
//
// 为什么要有这一层：宽屏的侧栏是 MudDrawer 的 Persistent 变体（无条件 Open），
// 小屏上把它变成"默认收起、按钮唤出"的浮层需要改 Variant —— 那是 C# 参数，
// 而"屏幕宽度"只有浏览器知道。走 C# 状态就得 JS 通知 + 首帧闪一下（先按宽屏渲染、
// 收到宽度再改成浮层）。这里改成**纯 class 驱动**：CSS 负责小屏布局，本脚本只往
// <html> 上挂/摘 tm-drawer-open，首帧就是对的，两个宿主（MAUI WebView2 / Blazor Server）
// 行为也完全一致。
//
// 滑动手势用 Pointer Events + CSS 的 touch-action: pan-y（见 tama.css 小屏段），
// 不用 document 上的非 passive touchmove —— 那会让浏览器在每次滚动前都等 JS，
// 手机上一眼可见地掉帧。pan-y 把纵向滚动留给内核，横向手势才送到这里。
window.tamaShell = (function () {
    'use strict';

    var BREAKPOINT = 640;   // 必须与 tama.css 小屏段的 @media 宽度一致
    var SWIPE_MIN = 56;     // 触发切页的横向位移阈值（px）
    var AXIS_LOCK = 12;     // 判定"这是横向手势"的起始位移
    var doc = document.documentElement;

    var dotNet = null;
    var active = null;      // { id, x, y, axis }：axis 0=未定 1=横 2=竖

    function compact() {
        try { return window.matchMedia('(max-width: ' + BREAKPOINT + 'px)').matches; }
        catch (e) { return window.innerWidth <= BREAKPOINT; }
    }

    function isOpen() { return doc.classList.contains('tm-drawer-open'); }

    function openDrawer() {
        if (!compact()) return false;
        doc.classList.add('tm-drawer-open');
        return true;
    }
    function closeDrawer() {
        doc.classList.remove('tm-drawer-open');
        return true;
    }
    function toggleDrawer() { return isOpen() ? closeDrawer() : openDrawer(); }

    // 这些起点上不做切页手势：
    //  · 表单控件（输入、range 滑杆、下拉）本身要吃横向拖拽
    //  · 弹层/抽屉/胶囊自己
    //  · 标了 data-tm-noswipe 的容器（给将来需要横滑的内容留的口子）
    function blockedStart(target) {
        if (!target || !target.closest) return true;
        return !!target.closest(
            'input, textarea, select, [contenteditable="true"], [data-tm-noswipe],' +
            '.tm-capsule, .tm-mobile-btn, .mud-dialog, .mud-popover, .mud-drawer, .tm-scrollbar-layer');
    }

    function onPointerDown(e) {
        active = null;
        if (!compact()) return;
        if (e.pointerType === 'mouse') return;      // 桌面鼠标划拉不切页
        if (isOpen()) return;
        if (blockedStart(e.target)) return;
        // 列表行展开时不切页：展开区里可能有输入/选中，滑走会把用户的东西丢掉
        if (document.querySelector('.tm-row-detail')) return;
        active = { id: e.pointerId, x: e.clientX, y: e.clientY, axis: 0 };
    }

    function onPointerMove(e) {
        if (!active || e.pointerId !== active.id) return;
        var dx = e.clientX - active.x;
        var dy = e.clientY - active.y;
        if (active.axis === 0) {
            if (Math.abs(dx) < AXIS_LOCK && Math.abs(dy) < AXIS_LOCK) return;
            active.axis = Math.abs(dx) > Math.abs(dy) ? 1 : 2;
        }
    }

    function onPointerUp(e) {
        if (!active || e.pointerId !== active.id) return;
        var a = active;
        active = null;
        if (a.axis !== 1) return;                   // 纵向手势：本来就是在滚页面
        var dx = e.clientX - a.x;
        if (Math.abs(dx) < SWIPE_MIN) return;
        if (!dotNet) return;
        // 手指左滑（dx < 0）→ 底栏里的下一项；右滑 → 上一项
        var dir = dx < 0 ? 1 : -1;
        try { dotNet.invokeMethodAsync('OnSwipeAsync', dir); }
        catch (err) { /* 电路已断开（Blazor Server 断线重连中）：这次手势丢掉即可 */ }
    }

    function onPointerCancel(e) {
        if (active && e.pointerId === active.id) active = null;
    }

    // 点了抽屉里的导航项 / 遮罩 → 收起抽屉。
    // 捕获阶段监听：Blazor 的路由拦截在冒泡阶段，先收抽屉再导航，避免看到抽屉停在那
    document.addEventListener('click', function (e) {
        var t = e.target;
        if (!t || !t.closest) return;
        if (t.closest('[data-tm-close-drawer]') || t.closest('.tm-drawer .mud-nav-link')) closeDrawer();
    }, true);

    window.addEventListener('resize', function () { if (!compact()) closeDrawer(); });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeDrawer(); });

    document.addEventListener('pointerdown', onPointerDown, { passive: true });
    document.addEventListener('pointermove', onPointerMove, { passive: true });
    document.addEventListener('pointerup', onPointerUp, { passive: true });
    document.addEventListener('pointercancel', onPointerCancel, { passive: true });

    return {
        // —— 供 Blazor (IJSRuntime) 调用 ——
        /** 绑定布局组件，开启滑动切页（不绑则只保留抽屉开关） */
        initSwipe: function (ref) { dotNet = ref || null; },
        /** 释放：电路/组件销毁时调用，避免向已死的 DotNetObjectReference 发消息 */
        disposeSwipe: function () { dotNet = null; active = null; },
        openDrawer: openDrawer,
        closeDrawer: closeDrawer,
        toggleDrawer: toggleDrawer,
        /** 排查用：当前是否处于小屏外壳 */
        isCompact: function () { return compact(); }
    };
})();
