// Service worker：接收 content script 的请求，通过 native messaging 转发给 Tama.NativeHost。
// 每个请求建立一次连接（host 短生命周期，简单可靠）。

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg || !msg.type) return; // 不响应非本扩展消息
  if (!['webauthn/create', 'webauthn/get', 'webauthn/probe'].includes(msg.type)) {
    sendResponse({ error: `unsupported type: ${msg.type}` });
    return;
  }

  (async () => {
    let settled = false;
    console.log('[Tama] connecting native host:', msg.type);
    const port = chrome.runtime.connectNative('com.tama.webauthn');

    port.onMessage.addListener((resp) => {
      if (settled) return;
      settled = true;
      console.log('[Tama] native host response:', resp?.error ? 'ERROR ' + resp.error : 'ok');
      sendResponse(resp);
      try { port.disconnect(); } catch { /* already closed */ }
    });

    port.onDisconnect.addListener(() => {
      if (settled) return;
      settled = true;
      const lastError = chrome.runtime.lastError?.message || 'disconnected';
      console.error('[Tama] native host disconnected:', lastError);
      sendResponse({ error: `native host error: ${lastError}` });
    });

    try {
      // host 转发给 Tama bridge，params 为 JSON 字符串。
      // 契约：method ∈ {webauthn/create, webauthn/get, webauthn/probe}（与上面 allowlist 一致），
      // 失败返回 {error: string}。服务端实现见 src/Tama.Services/Bridge/ExtensionBridge.cs
      port.postMessage({
        method: msg.type,
        params: msg.payload ? JSON.stringify(msg.payload) : null,
      });
    } catch (err) {
      if (!settled) {
        settled = true;
        sendResponse({ error: String(err?.message || err) });
      }
    }
  })();

  return true; // 异步 sendResponse
});
