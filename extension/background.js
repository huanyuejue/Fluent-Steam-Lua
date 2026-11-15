// 扩展的后台脚本：所有发往本地桥接服务的请求都由这里发起。
// 放在后台是为了借用扩展自身的权限域，绕开商店页面自身的 CSP 限制。

const BRIDGE = 'http://127.0.0.1:27361';

// 请求桥接服务。业务错误（4xx/5xx 且带 JSON error 字段）解析出 code 返回，
// 网络层异常抛出去交给调用方区分，这样前端才能显示具体原因而不是一句"失败"
async function call(path, options) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), path === '/api/health' ? 1500 : 5000);
  let resp;
  let payload = null;
  try {
    resp = await fetch(BRIDGE + path, Object.assign({
      headers: { 'Content-Type': 'application/json' }, signal: controller.signal
    }, options));
    try {
      payload = await resp.json();
    } catch {
      payload = null;
    }
  } finally {
    clearTimeout(timer);
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

const messageCache = new Map();

function normalizeLanguage(language) {
  return /^zh(?:[-_]|$)/i.test(language || '') ? 'zh' : 'en';
}

async function readLocale(health) {
  const stored = await chrome.storage.local.get('fluentLanguage');
  const language = health && (health.language === 'en' || health.language === 'zh')
    ? health.language : stored.fluentLanguage || normalizeLanguage(chrome.i18n.getUILanguage());
  const normalized = normalizeLanguage(language);
  if (stored.fluentLanguage !== normalized && health && health.language)
    await chrome.storage.local.set({ fluentLanguage: normalized });
  if (!messageCache.has(normalized)) {
    const locale = normalized === 'zh' ? 'zh_CN' : 'en';
    const response = await fetch(chrome.runtime.getURL('_locales/' + locale + '/messages.json'));
    const entries = await response.json();
    messageCache.set(normalized, Object.fromEntries(Object.entries(entries).map(([key, value]) => [key, value.message])));
  }
  return { language: normalized, messages: messageCache.get(normalized) };
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg || (msg.type !== 'fluent-import' && msg.type !== 'fluent-locale')) return false;

  (async () => {
    let locale = {};
    try {
      // 先探活：桥接没起来时和其它错误区分开，前端要给出不同的指引
      let health;
      let unavailableDetail;
      try {
        health = await call('/api/health', { method: 'GET' });
      } catch (e) {
        unavailableDetail = String(e.message || e);
      }

      locale = await readLocale(health);
      if (msg.type === 'fluent-locale') {
        sendResponse(Object.assign({ ok: true }, locale));
        return;
      }

      if (!health || !health.ok) {
        sendResponse(Object.assign({ ok: false, error: 'bridge_unavailable', detail: unavailableDetail }, locale));
        return;
      }

      const result = await call('/api/import', {
        method: 'POST',
        body: JSON.stringify({ appId: msg.appId, name: msg.name || '', nameLanguage: msg.nameLanguage || '' })
      });

      sendResponse(Object.assign({ ok: true, jobId: result.jobId, duplicate: !!result.duplicate }, locale));
    } catch (e) {
      // 优先用桥接返回的业务错误码，没有就退回异常文本
      sendResponse(Object.assign({
        ok: false,
        error: e && e.code ? e.code : String(e && e.message ? e.message : e)
      }, locale));
    }
  })();

  return true; // 保持消息通道打开以便异步回复
});
