// Isolated world content script：
// 1) document_start 时动态注入 content-main.js 到页面（MAIN world）——不依赖 manifest 的
//    "world": "MAIN"（Chrome 111 前的版本/部分 Edge 不支持，静默失败导致 shim 不生效）。
// 2) 桥接 MAIN world shim 与 background：MAIN 发 postMessage(__kg_req) → 本脚本收到 →
//    chrome.runtime.sendMessage → 结果回传。

(() => {
  const tag = '[Tama]';
  console.log(tag, 'content-isolated loaded');

  // 注入 MAIN world shim（保证在页面脚本运行前注入）
  const script = document.createElement('script');
  script.src = chrome.runtime.getURL('content-main.js');
  script.onload = () => console.log(tag, 'content-main injected into page');
  script.onerror = (e) => console.error(tag, 'content-main injection FAILED', e);
  (document.head || document.documentElement).appendChild(script);

  window.addEventListener('message', async (e) => {
    if (e.source !== window) return;
    const d = e.data;
    if (!d || !d.__kg_req || !d.__kg_type) return;

    const requestId = d.__kg_req;
    console.log(tag, 'bridge request:', d.__kg_type, d.__kg_payload ? Object.keys(d.__kg_payload) : null);
    try {
      const response = await chrome.runtime.sendMessage({
        type: d.__kg_type,
        payload: d.__kg_payload,
      });
      console.log(tag, 'bridge response:', d.__kg_type, response && typeof response === 'object' ? (response.error ? 'ERROR ' + response.error : 'ok') : response);
      window.postMessage({ __kg_resp: requestId, __kg_data: response }, '*');
    } catch (err) {
      console.error(tag, 'bridge error:', err);
      window.postMessage({
        __kg_resp: requestId,
        __kg_error: String(err?.message || err),
      }, '*');
    }
  });
})();
