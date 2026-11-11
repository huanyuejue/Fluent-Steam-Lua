// 扩展的后台脚本：所有发往本地桥接服务的请求都由这里发起。
// 放在后台是为了借用扩展自身的权限域，绕开商店页面自身的 CSP 限制。

const BRIDGE = 'http://127.0.0.1:27361';

// 请求桥接服务。业务错误（4xx/5xx 且带 JSON error 字段）解析出 code 返回，
// 网络层异常抛出去交给调用方区分，这样前端才能显示具体原因而不是一句"失败"
async function call(path, options) {
  const resp = await fetch(BRIDGE + path, Object.assign({
    headers: { 'Content-Type': 'application/json' }
  }, options));

  let payload = null;
  try {
    payload = await resp.json();
  } catch {
    payload = null;
  }

  if (payload && payload.ok === false) {
    const err = new Error(payload.error || 'unknown');
    err.code = payload.error || 'unknown';
    throw err;
  }

  if (!resp.ok) {
    const err = new Error('HTTP ' + resp.status);
    err.code = 'http_' + resp.status;
    throw err;
  }

  return payload;
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg || msg.type !== 'fluent-import') return false;

  (async () => {
    try {
      // 先探活：桥接没起来时和其它错误区分开，前端要给出不同的指引
      let health;
      try {
        health = await call('/api/health', { method: 'GET' });
      } catch (e) {
        sendResponse({ ok: false, error: 'bridge_unavailable', detail: String(e.message || e) });
        return;
      }

      if (!health || !health.ok) {
        sendResponse({ ok: false, error: 'bridge_unavailable' });
        return;
      }

      const result = await call('/api/import', {
        method: 'POST',
        body: JSON.stringify({ appId: msg.appId, name: msg.name || '' })
      });

      sendResponse({ ok: true, jobId: result.jobId, duplicate: !!result.duplicate });
    } catch (e) {
      // 优先用桥接返回的业务错误码，没有就退回异常文本
      sendResponse({
        ok: false,
        error: e && e.code ? e.code : String(e && e.message ? e.message : e)
      });
    }
  })();

  return true; // 保持消息通道打开以便异步回复
});
