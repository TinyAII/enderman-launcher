// Walking Skeleton 页面侧：只做两件事——探测桥是否可用、测往返延迟。
// 刻意用最朴素的 setTimeout + 序号匹配，不引入任何框架，先确认边界存在。

const bridge = window.chrome?.webview;

const el = {
  b2: document.getElementById('b2'),
  b3: document.getElementById('b3'),
  b4: document.getElementById('b4'),
  d2: document.getElementById('d2'),
  d3: document.getElementById('d3'),
  d4: document.getElementById('d4'),
  verdict: document.getElementById('verdict')
};

function settle(row, detail, ok, text) {
  row.classList.toggle('ok', ok);
  row.classList.toggle('fail', !ok);
  row.querySelector('.mark').textContent = ok ? '✓' : '✗';
  detail.textContent = text;
}

let seq = 0;
const pending = new Map();
/** 收集到的实测指标，通关后一次性上抛给宿主落盘 */
const metrics = { userAgent: navigator.userAgent };

function call(cmd) {
  return new Promise((resolve, reject) => {
    const id = ++seq;
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`timeout: ${cmd}`));
    }, 5000);
    pending.set(id, { resolve, reject, timer });
    bridge.postMessage({ cmd, seq: id, t: performance.now() });
  });
}

bridge.addEventListener('message', (e) => {
  const { seq: id, payload } = e.data ?? {};
  const slot = pending.get(id);
  if (!slot) return;
  pending.delete(id);
  clearTimeout(slot.timer);
  slot.resolve(payload);
});

function finish() {
  const allOk = el.b2.classList.contains('ok')
    && el.b3.classList.contains('ok')
    && el.b4.classList.contains('ok');
  el.verdict.textContent = allOk ? '四项全通' : '有边界未通过';
  el.verdict.className = 'verdict ' + (allOk ? 'pass' : 'fail');
  metrics.passed = allOk;
  // 让宿主侧脚本能判定结果
  document.title = allOk ? 'SKELETON_PASS' : 'SKELETON_FAIL';
  // 上抛实测数字，宿主写进 artifacts/skeleton-metrics.json
  bridge.postMessage({ cmd: 'metrics', seq: -1, data: metrics });
}

async function run() {
  if (!bridge) {
    settle(el.b2, el.d2, false, 'chrome.webview 不存在——不在 WebView2 中运行');
    el.verdict.textContent = '有边界未通过';
    el.verdict.className = 'verdict fail';
    return;
  }

  settle(el.b2, el.d2, true, `${navigator.userAgent.match(/Edg\/[\d.]+/)?.[0] ?? 'WebView2'}`);

  // ③ 桥往返：连打 20 次取中位数，避免首次 JIT/路由开销污染结论
  try {
    const samples = [];
    for (let i = 0; i < 20; i++) {
      const t0 = performance.now();
      await call('ping');
      samples.push(performance.now() - t0);
    }
    samples.sort((a, b) => a - b);
    const median = samples[samples.length >> 1];
    const p95 = samples[Math.floor(samples.length * 0.95)];
    metrics.bridge = {
      medianMs: +median.toFixed(3),
      p95Ms: +p95.toFixed(3),
      maxMs: +samples[samples.length - 1].toFixed(3),
      samples: samples.length
    };
    settle(el.b3, el.d3, true,
      `往返中位 ${median.toFixed(2)} ms · p95 ${p95.toFixed(2)} ms · n=20`);
  } catch (err) {
    settle(el.b3, el.d3, false, err.message);
  }

  // ④ 进程外插件
  try {
    const p = await call('plugin');
    if (p.ok) {
      metrics.plugin = {
        pid: p.pid,
        node: p.node,
        handshakeMs: +(p.handshakeMicros / 1000).toFixed(3)
      };
      settle(el.b4, el.d4, true,
        `node ${p.node} · pid ${p.pid} · 握手 ${(p.handshakeMicros / 1000).toFixed(2)} ms`);
    } else {
      settle(el.b4, el.d4, false, p.error ?? '未知错误');
    }
  } catch (err) {
    settle(el.b4, el.d4, false, err.message);
  }

  finish();
}

run();
