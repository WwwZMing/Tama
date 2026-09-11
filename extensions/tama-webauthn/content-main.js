// MAIN world shim：拦截 navigator.credentials.create/get，把 WebAuthn 请求转发给 Tama。
// 由 content-isolated.js 动态注入到页面（MAIN world）执行；
// 与 content-isolated.js 通过 window.postMessage（带一次性 request id）通信。

(() => {
  const tag = '[Tama]';
  if (window.__kgWeAuthnShimmed || !window.navigator || !window.navigator.credentials) {
    console.log(tag, 'shim skipped (already shimmed or no credentials API)');
    return;
  }
  window.__kgWeAuthnShimmed = true;
  console.log(tag, 'shim installed, intercepting navigator.credentials');

  const credentials = navigator.credentials;
  const origCreate = credentials.create.bind(credentials);
  const origGet = credentials.get.bind(credentials);

  function sendToBackground(type, payload) {
    return new Promise((resolve, reject) => {
      const id = Math.random().toString(36).slice(2) + Date.now().toString(36);
      const handler = (e) => {
        if (e.source !== window) return;
        const d = e.data;
        if (!d || d.__kg_resp !== id) return;
        window.removeEventListener('message', handler);
        if (d.__kg_error) reject(new Error(d.__kg_error));
        else resolve(d.__kg_data);
      };
      window.addEventListener('message', handler);
      window.postMessage({ __kg_req: id, __kg_type: type, __kg_payload: payload }, '*');
    });
  }

  // === 序列化（ArrayBuffer/TypedArray → base64url；保持其余字段 JSON 安全） ===

  function bufToB64(buf) {
    const bytes = buf instanceof Uint8Array ? buf : new Uint8Array(buf);
    let bin = '';
    for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
    return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }

  function normalizeId(v) {
    if (!v) return null;
    return typeof v === 'string' ? v : bufToB64(v);
  }

  function serializePublicKeyOptions(opts) {
    const out = {
      rpId: opts.rp?.id || window.location.hostname,
      rpName: opts.rp?.name,
      challenge: normalizeId(opts.challenge),
      origin: window.location.origin,
      userName: opts.user?.name,
      userDisplayName: opts.user?.displayName,
      userHandle: normalizeId(opts.user?.id),
      timeout: opts.timeout,
      attestation: opts.attestation,
      userVerification: opts.authenticatorSelection?.userVerification,
      pubKeyCredParams: (opts.pubKeyCredParams || []).map(p => ({ type: p.type, alg: p.alg })),
      allowCredentials: (opts.allowCredentials || []).map(c => ({ type: c.type, id: normalizeId(c.id), transports: c.transports })),
      excludeCredentials: (opts.excludeCredentials || []).map(c => ({ type: c.type, id: normalizeId(c.id) })),
    };
    return out;
  }

  function b64ToBuf(b64) {
    const s = b64.replace(/-/g, '+').replace(/_/g, '/');
    const padded = s.padEnd(s.length + ((4 - (s.length % 4)) % 4), '=');
    const bin = atob(padded);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return bytes.buffer;
  }

  function deserializeCredential(cred) {
    if (!cred || cred.error) throw new Error(cred?.detail || cred?.error || 'Tama passkey failed');

    // 标准 AuthenticatorAttestationResponse / AuthenticatorAssertionResponse 是带方法的，
    // 页面 JS 会调用 getPublicKey()/getPublicKeyAlgorithm()/getAuthenticatorData()/getTransports()，
    // 普通对象缺这些方法会导致网站 "添加失败"。这里按 WebAuthn 规范补上。
    const response = {};
    if (cred.response?.clientDataJSON) response.clientDataJSON = b64ToBuf(cred.response.clientDataJSON);
    if (cred.response?.attestationObject) response.attestationObject = b64ToBuf(cred.response.attestationObject);
    if (cred.response?.authenticatorData) response.authenticatorData = b64ToBuf(cred.response.authenticatorData);
    if (cred.response?.signature) response.signature = b64ToBuf(cred.response.signature);
    if (cred.response?.userHandle) response.userHandle = b64ToBuf(cred.response.userHandle);

    response.getPublicKey = () => (cred.publicKey ? b64ToBuf(cred.publicKey) : undefined); // SPKI（DER）
    response.getPublicKeyAlgorithm = () => (cred.publicKeyAlgorithm || -7);
    response.getAuthenticatorData = () => response.authenticatorData;
    response.getTransports = () => (cred.transports || ['internal']);

    return {
      id: cred.id,
      rawId: b64ToBuf(cred.rawId),
      type: 'public-key',
      response,
    };
  }

  credentials.create = async (options) => {
    if (!options || !options.publicKey) return origCreate(options);
    console.log(tag, 'intercepting create for rpId:', options.publicKey.rp?.id || window.location.hostname);
    const payload = serializePublicKeyOptions(options.publicKey);
    const result = await sendToBackground('webauthn/create', payload);
    return deserializeCredential(result);
  };

  credentials.get = async (options) => {
    if (!options || !options.publicKey) return origGet(options);
    // conditional UI（autocomplete="webauthn"）是 passive 监听，劫持会破坏原生选择器 → 放行
    if (options.publicKey.mediation === 'conditional') return origGet(options);

    // 诊断：微软显式设置的 rpId vs 页面 hostname（判断凭据该绑定哪个域）
    console.log(tag, 'get options: rpId=', options.publicKey.rpId, '| hostname=', window.location.hostname,
      '| allowCredentials=', (options.publicKey.allowCredentials || []).length);
    // 诊断：原始 challenge（页面字节 → b64url）——与 Tama 日志 challengeReceived 对比
    console.log(tag, 'get challenge (page):', options.publicKey.challenge ? bufToB64(options.publicKey.challenge) : '(missing)');
    const payload = serializePublicKeyOptions(options.publicKey);

    // 只有 Tama 有匹配凭据才拦截；否则放行原生认证器（Windows Hello / 其他设备）。
    // 避免把"用户想用 Windows Hello 原生密钥"的请求劫持成 Tama 报错。
    try {
      const probe = await sendToBackground('webauthn/probe', {
        rpId: payload.rpId,
        allowCredentialIds: payload.allowCredentials?.map(c => c.id),
      });
      console.log(tag, 'probe result for', payload.rpId, ':', JSON.stringify(probe));
      if (!probe || probe.hasMatch !== true) {
        console.log(tag, 'no Tama credential, falling back to native');
        return origGet(options);
      }
    } catch (err) {
      // 探测失败（Tama 未运行等）→ 放行原生，不阻塞网站登录
      console.warn(tag, 'probe failed, falling back to native:', err);
      return origGet(options);
    }

    console.log(tag, 'intercepting get for rpId:', payload.rpId);
    // allowCredentials 传字符串数组（后端 WebAuthnGetRequest.AllowCredentialIds 是 List<string>，
    // 对象数组会反序列化失败变 null）
    const result = await sendToBackground('webauthn/get', {
      rpId: payload.rpId,
      challenge: payload.challenge,
      origin: payload.origin,
      credentialId: payload.allowCredentials?.[0]?.id || null,
      allowCredentialIds: (payload.allowCredentials || []).map(c => c.id),
    });
    return deserializeCredential(result);
  };
})();
