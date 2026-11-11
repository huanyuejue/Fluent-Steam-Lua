// 在 Steam 商店主游戏页注入「一键入库」按钮。
// 只认 /app/<数字>/ 形式的页面，捆绑包、DLC、创意工坊等一律跳过。

(function () {
  'use strict';

  const BUTTON_ID = 'fluent-lua-import-btn';
  const PURCHASE_SELECTOR = '#game_area_purchase';
  const INJECTED_FLAG = 'fluentLuaInjected';

  // 主商店页判定：路径必须是 /app/<数字> 或 /app/<数字>/<名字>/
  function resolveAppId() {
    const m = location.pathname.match(/^\/app\/(\d+)(?:\/[^/]*)?\/?$/);
    if (!m) return null;
    const appId = parseInt(m[1], 10);
    if (!Number.isFinite(appId) || appId <= 0) return null;
    return appId;
  }

  // 这些特征说明当前页不是可独立入库的主游戏页
  function isExcludedPage() {
    // 从 DLC 或捆绑包跳转过来会带 snr 参数
    if (/[?&]snr=/.test(location.search)) return true;
    // 捆绑包/合集页没有独立的购买区块结构
    if (document.querySelector('.game_area_purchase_game_dropdown_selection')) return true;
    // 免费游戏或软件类没有购买区
    return false;
  }

  function readGameName() {
    const el = document.querySelector('#appHubAppName') ||
               document.querySelector('.apphub_AppName');
    return el ? el.textContent.trim() : '';
  }

  function buildButton() {
    const btn = document.createElement('button');
    btn.id = BUTTON_ID;
    btn.type = 'button';
    btn.className = 'fluent-lua-btn';
    btn.textContent = '一键入库';
    btn.title = '调用 Fluent Steam Lua 生成入库文件';
    return btn;
  }

  function setState(btn, state, text) {
    btn.dataset.state = state;
    btn.textContent = text;
  }

  // 把后台返回的错误码翻译成用户能照着做的提示。
  // 按钮上空间有限只放短句，完整原因放 title 供悬停查看
  function describeError(code) {
    const map = {
      bridge_unavailable: {
        text: '桥接服务未启动',
        detail: '请打开 Fluent Steam Lua，在设置中开启「商店页一键入库」'
      },
      method_not_allowed: { text: '请求被拒绝', detail: '桥接服务不接受当前请求方式' },
      invalid_appid: { text: 'AppID 无效', detail: '无法从当前页面识别游戏，请确认是游戏主页' },
      not_found: { text: '接口不存在', detail: '桥接版本过旧，请更新后再试' },
      job_not_found: { text: '任务已失效', detail: '入库任务已被清理，请重新点击' },
      no_ui_thread: { text: '桥接窗口异常', detail: '桥接进程未能创建窗口，请重启该功能' }
    };

    if (!code) return { text: '入库失败', detail: '未知原因' };
    if (map[code]) return map[code];

    // 网络类错误：fetch 失败通常是桥接没起来或端口被占
    if (/Failed to fetch|NetworkError|ECONNREFUSED/i.test(code)) {
      return {
        text: '无法连接桥接服务',
        detail: '桥接进程可能已退出，请在设置中关闭再重新开启该功能'
      };
    }

    // 其余情况保留原始信息，方便用户反馈问题
    return { text: '入库失败', detail: String(code) };
  }

  async function doImport(btn, appId) {
    if (btn.disabled) return;
    btn.disabled = true;
    setState(btn, 'working', '入库中…');

    let holdMs = 3200;
    try {
      const resp = await chrome.runtime.sendMessage({
        type: 'fluent-import',
        appId: appId,
        name: readGameName()
      });

      if (resp && resp.ok) {
        setState(btn, 'done', resp.duplicate ? '已在入库' : '已提交');
      } else {
        // 把后台带回的具体原因显示出来，否则用户只知道失败却不知道怎么办
        const reason = describeError(resp && resp.error);
        setState(btn, 'error', reason.text);
        btn.title = reason.detail;
        holdMs = 6000;   // 出错了多停留一会，让用户看清

        // 桥接没起来是最高频的失败原因，额外弹一次明显提示，
        // 光靠按钮上一行小字用户很容易错过
        if (resp && resp.error === 'bridge_unavailable') {
          showBridgeHint();
        }
      }
    } catch (e) {
      // service worker 未响应：多半是扩展刚被重载，提示重试而不是笼统失败
      setState(btn, 'error', '扩展未就绪，请重试');
      btn.title = String(e && e.message ? e.message : e);
      holdMs = 6000;
    }

    // 结果提示留一会儿再复原，避免按钮永久卡在结果态
    setTimeout(() => {
      btn.disabled = false;
      btn.title = '调用 Fluent Steam Lua 生成入库文件';
      setState(btn, '', '一键入库');
    }, holdMs);
  }

  /// 桥接未启动时在按钮下方浮出一个提示条，比按钮上的短字更容易被注意到
  function showBridgeHint() {
    const existing = document.getElementById('fluent-lua-hint');
    if (existing) existing.remove();

    const box = document.createElement('div');
    box.id = 'fluent-lua-hint';
    box.className = 'fluent-lua-hint';
    box.innerHTML =
      '<strong>桥接服务未运行</strong>' +
      '<span>打开 Fluent Steam Lua，在「设置」里开启「商店页一键入库」后重试。</span>';

    const btn = document.getElementById(BUTTON_ID);
    if (btn && btn.parentNode) {
      btn.parentNode.insertBefore(box, btn.nextSibling);
    } else {
      document.body.appendChild(box);
    }

    // 留足阅读时间，也允许点击关闭
    const timer = setTimeout(() => box.remove(), 12000);
    box.addEventListener('click', () => {
      clearTimeout(timer);
      box.remove();
    });
  }

  function tryInject() {
    if (document.getElementById(BUTTON_ID)) return;
    if (document.body && document.body.dataset[INJECTED_FLAG]) return;

    const appId = resolveAppId();
    if (!appId) return;
    if (isExcludedPage()) return;

    const purchase = document.querySelector(PURCHASE_SELECTOR);
    if (!purchase) return;

    const btn = buildButton();
    btn.addEventListener('click', () => doImport(btn, appId));

    // 插在购买区块最前面，贴近用户视线又不会挡住原生购买按钮
    purchase.parentNode.insertBefore(btn, purchase);

    if (document.body) document.body.dataset[INJECTED_FLAG] = '1';
  }

  // Steam 商店是整页渲染，但部分区域会异步补全，稍作重试即可
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => setTimeout(tryInject, 400));
  } else {
    setTimeout(tryInject, 400);
  }

  const observer = new MutationObserver(() => {
    if (!document.getElementById(BUTTON_ID)) tryInject();
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });

  // 单页内跳转时 URL 会变，重新判定一次
  let lastPath = location.pathname;
  setInterval(() => {
    if (location.pathname !== lastPath) {
      lastPath = location.pathname;
      const old = document.getElementById(BUTTON_ID);
      if (old) old.remove();
      tryInject();
    }
  }, 1200);
})();
