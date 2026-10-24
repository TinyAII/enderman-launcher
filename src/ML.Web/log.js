// 日志窗口的界面层。它只收事件、不主动发命令——
// 「启动/停止」这类决定都在主窗口，日志窗口是被动显示器。

const $ = (id) => document.getElementById(id);

// 一次启动的日志可能有上万行。DOM 节点无限涨会越拖越慢，
// 超过这个数就从最旧的开始扔。
const MAX_LINES = 5000;

let lineCount = 0;

function appendLine(level, text) {
  const log = $('log');

  // 自动滚动只在用户本来就贴着底部时才做。
  // 否则他往上翻着看崩溃现场，新日志一来就被拉回底部，等于不让人看。
  const atBottom = log.scrollHeight - log.scrollTop - log.clientHeight < 30;

  const span = document.createElement('span');
  span.className = level || 'game';
  span.textContent = `${text}\n`;
  log.appendChild(span);
  lineCount++;

  while (lineCount > MAX_LINES && log.firstChild) {
    log.removeChild(log.firstChild);
    lineCount--;
  }

  if (atBottom) {
    log.scrollTop = log.scrollHeight;
  }
}

function clearLines() {
  $('log').textContent = '';
  lineCount = 0;
}

function setState(text, isError = false) {
  const el = $('state');
  el.textContent = text;
  el.classList.toggle('error', isError);
}
window.chrome?.webview?.addEventListener('message', (e) => {
  const message = e.data;
  if (!message?.event) {
    return;
  }

  switch (message.event) {
    case 'log.reset':
      clearLines();
      $('instance').textContent = '准备中…';
      setState('');
      break;

    case 'log':
      if (message.data?.instance) {
        $('instance').textContent = message.data.instance;
      }
      appendLine(message.data?.level, message.data?.text ?? '');
      break;

    case 'launch.done':
      if (message.data?.ok === false) {
        setState(`启动失败：${message.data.error}`, true);
      } else {
        setState(`已退出 · 退出码 ${message.data.exitCode} · ${message.data.diagnosis} · ${message.data.seconds}s`);
      }
      break;

    default:
      // 其它事件（安装进度、设置变更、更新进度）不归这个窗口管，静默忽略。
      break;
  }
});

$('clear').addEventListener('click', clearLines);

// 主窗口切主题时这边跟着变，不用关掉重开。首帧由 log.html 里的内联脚本套好。
if (typeof BroadcastChannel === 'function') {
  const themeChannel = new BroadcastChannel('ml-theme');
  themeChannel.addEventListener('message', (event) => {
    if (event.data === 'light') {
      document.documentElement.dataset.theme = 'light';
    } else {
      document.documentElement.removeAttribute('data-theme');
    }
  });
}

// 宿主可能在本页加载完之前就开始推事件了，那些消息会先囤在宿主那边、
// 加载完成后一次补发过来（见 LogWindow.Post 的缓冲）。这里不需要额外做什么。
