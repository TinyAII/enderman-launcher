// 界面层。只做三件事：把命令发出去、把回执贴上界面、把事件写进日志。
// 所有业务判断都在 ML.Host 里，这里不解析版本号、不拼路径、不算哈希。

const $ = (id) => document.getElementById(id);

// ── 桥 ────────────────────────────────────────────────
// WebView2 的 postMessage 没有内建请求/响应配对，自己发 seq 自己收。

let nextSeq = 1;
const pending = new Map();

function send(cmd, data = {}) {
  const seq = nextSeq++;
  return new Promise((resolve, reject) => {
    pending.set(seq, { resolve, reject });
    window.chrome.webview.postMessage({ seq, cmd, data });

    // 桥本身出问题时会永远等下去，给个上限别让界面卡死。
    setTimeout(() => {
      if (pending.delete(seq)) {
        reject(new Error(`命令 ${cmd} 超时`));
      }
    }, 120_000);
  });
}

window.chrome?.webview?.addEventListener('message', (e) => {
  const message = e.data;

  if (message && message.event) {
    handleEvent(message.event, message.data || {});
    return;
  }

  const entry = pending.get(message?.seq);
  if (!entry) {
    return;
  }

  pending.delete(message.seq);

  if (message.payload?.ok === false) {
    // 把整个 payload 挂到 error 上：有些「失败」是业务状态而不是故障
    // （比如 needsGameRoot），调用方需要读到那个字段才知道该弹什么。
    const error = new Error(message.payload.error || '未知错误');
    error.payload = message.payload;
    entry.reject(error);
  } else {
    entry.resolve(message.payload);
  }
});

// ── 状态 ──────────────────────────────────────────────
//
// 主界面不再有日志页：游戏日志在启动时开的独立窗口里（见 log.html）。
// 这里只维护「按钮该不该禁用」这类界面状态。

let state = {
  versions: [],
  instances: [],
  plugins: [],
  pluginPages: [],
  memory: { totalMb: 0, usedMb: 0, autoMb: 0 },
  pluginUiItems: [],
  detailPlugin: null,
  currentVersion: null,
  dataRoot: null,
  gameRoot: null,
  settings: null,
  candidates: [],
  // 正在跑的游戏属于哪个实例。宿主同时只允许一个，所以这里用一个名字就够。
  running: [],
  editing: null,
  confirmYes: null,
  noticeAction: null,
  // 账号簿（正版 + 离线）
  accounts: [],
  lastAccountId: null,
  accountClientConfigured: false,
  // 'msa' | 'offline'。默认看正版；用户点过筛选之后就听他的。
  accountFilter: 'msa',
  pluginSource: 'official',
  accountFilterTouched: false,
  // client_id 从哪来：'settings' | 'env' | 'builtin' | 'none'。
  // 有 env/builtin 时界面不再要用户填，除非他主动要换自己的应用。
  accountClientSource: 'none',
  currentCode: false,
  picking: null
};

function setStatus(id, text, isError = false) {
  const el = $(id);
  el.textContent = text;
  el.classList.toggle('error', isError);
}

// ── 展示用的小工具 ────────────────────────────────────
//
// 只做「怎么显示」，不做业务判断：类型的含义由宿主的 version.json 决定，
// 这里只是把 release / snapshot 翻成中文并配个颜色。

const VERSION_TYPES = {
  release: ['release', '正式版'],
  snapshot: ['snapshot', '快照'],
  old_beta: ['', '旧测试版'],
  old_alpha: ['', '远古版'],
  broken: ['broken', '元数据损坏']
};

/// 版本条目的颜色按「类型」定，不按 id 哈希：一屏六条各自随机色相看着像噪声，
/// 而 release / snapshot / broken 本来就是有意义的状态。
const TYPE_HUES = {
  release: 214,
  snapshot: 32,
  old_beta: 286,
  old_alpha: 286,
  broken: 0
};

/// 实例没有 type 字段，用一组收敛过的色相（蓝→青→绿→琥珀→紫）按名字取一个，
/// 保证两个实例不会总是撞成同一个颜色。
const INSTANCE_HUES = [214, 190, 158, 32, 262, 286];

/// 名字 → 固定色相。同一个实例每次打开都是同一个颜色，不用为它准备封面图。
function hueOf(text, palette = INSTANCE_HUES) {
  let sum = 0;
  for (let i = 0; i < text.length; i++) {
    sum = (sum * 31 + text.charCodeAt(i)) % 100000;
  }
  return palette[sum % palette.length];
}


/// 封面上那一行短字：`1.20.1` → `1.20`；取不到就留空（只显示色块）。
function shortVersion(text) {
  // 版本号完整显示到第二段：1.21.8 比 1.21 有用得多（同一主版本下有 8 个补丁版）
  const version = /^(\d+(?:\.\d+){0,2})/.exec(text);
  if (version && version[1].length <= 6) {
    return version[1];
  }

  if (version) {
    return version[1].split('.').slice(0, 2).join('.');
  }

  // 插件名不是版本号（"示例插件" / "ml.demo"）：以前这里返回空串，
  // 结果色块上什么都没有，看着像没加载完。取第一个有意义的字符当标识。
  const word = text.trim();
  if (!word) {
    return '';
  }

  const first = /[a-zA-Z0-9一-龥]/.exec(word);
  return first ? first[0].toUpperCase() : '';
}


/// 账号头像。暂时统一用一张像素头像——正版拉真实皮肤要联网，先不做。
function makeAvatar() {
  const img = document.createElement('img');
  img.className = 'avatar';
  img.src = './assets/steve-head.png';
  img.alt = '';
  img.draggable = false;

  return img;
}

/// 插件头像：清单里给了 icon 就用它，这样"插件长什么样"由插件自己说了算。
function makePluginIcon(src, alt) {
  const img = document.createElement('img');
  img.className = 'tile plugin-icon';
  img.src = src;
  img.alt = alt ?? '';
  img.title = alt ?? '';
  img.draggable = false;

  // 图挂了退回首字方块，别在列表上留一个破图
  img.addEventListener('error', () => {
    img.replaceWith(makeTile(alt ?? '?', null));
  });

  return img;
}

function makeTile(text, h = null, type = null) {
  const tile = document.createElement('div');
  tile.className = 'tile';
  tile.style.setProperty('--h', String(h ?? hueOf(text)));

  if (type) {
    tile.dataset.type = type;
  }

  const label = shortVersion(text);
  if (label) {
    const span = document.createElement('span');
    span.textContent = label;
    // 字多的（1.21.8）缩一号，别让 44px 的方块里挤成两行或溢出
    if (label.length >= 5) {
      span.className = 'tile-tight';
    }
    tile.appendChild(span);
  }

  return tile;
}

function makeEmpty(title, hint, icon) {
  const box = document.createElement('div');
  box.className = 'empty';
  box.innerHTML = `${icon}<strong></strong><span></span>`;
  box.querySelector('strong').textContent = title;
  box.querySelector('span').textContent = hint;
  return box;
}

const ICON_CUBE = '<svg viewBox="0 0 24 24" width="34" height="34" aria-hidden="true">'
  + '<path d="M12 2 3 7v10l9 5 9-5V7z" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/>'
  + '<path d="M3 7l9 5 9-5M12 12v10" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/></svg>';

const ICON_GRID = '<svg viewBox="0 0 24 24" width="34" height="34" aria-hidden="true">'
  + '<rect x="3.5" y="3.5" width="7" height="7" rx="2" fill="none" stroke="currentColor" stroke-width="1.4"/>'
  + '<rect x="13.5" y="3.5" width="7" height="7" rx="2" fill="none" stroke="currentColor" stroke-width="1.4"/>'
  + '<rect x="3.5" y="13.5" width="7" height="7" rx="2" fill="none" stroke="currentColor" stroke-width="1.4"/>'
  + '<rect x="13.5" y="13.5" width="7" height="7" rx="2" fill="none" stroke="currentColor" stroke-width="1.4"/></svg>';


// ── 本地版本（只用来填「新建实例」的下拉框） ───────────

async function loadVersions() {
  try {
    const payload = await send('version.list');
    state.versions = payload.versions ?? [];

    // 版本页已经去掉了：这份列表只用来填「新建实例」的下拉框。
    const select = $('new-version');
    select.innerHTML = '';
    for (const v of state.versions) {
      const option = document.createElement('option');
      option.value = v.id;
      option.textContent = v.id;
      select.appendChild(option);
    }
  } catch (error) {
    state.versions = [];

    // 没指定游戏目录不是故障，是还没配置。此时唯一有用的动作是让用户选目录。
    // 也正因为不是故障，不能写 __mlError——那是给冒烟测试判定「首屏炸了」用的。
    if (error.payload?.needsGameRoot) {
      showFirstRun();
      return;
    }

    window.__mlError = error.message;
  }
}



// ── 实例页 ────────────────────────────────────────────

async function loadInstances(quiet = false) {
  try {
    const payload = await send('instance.list');
    state.instances = payload.instances;
    renderInstances();

    // 首页的实例下拉和启动按钮都依赖这份列表
    if ($('home-instance')) {
      renderHome();
    }

    if (quiet) {
      return;
    }

    if (payload.warnings?.length) {
      setStatus('instances-status', `有 ${payload.warnings.length} 个实例读不出来：${payload.warnings[0]}`, true);
    } else {
      setStatus('instances-status', payload.instances.length ? '' : '');
    }
  } catch (error) {
    setStatus('instances-status', `读取实例失败：${error.message}`, true);
  }
}

function renderInstances() {
  const host = $('instances');
  host.innerHTML = '';

  if (state.instances.length === 0) {
    host.appendChild(makeEmpty(
      '还没有实例',
      '点右上角新建一个实例，选个版本、起个名字就行。一个实例就是一套独立的存档、模组与设置。',
      ICON_GRID));
    return;
  }

  for (const instance of state.instances) {
    const row = document.createElement('div');
    row.className = 'row';

    const grow = document.createElement('div');
    grow.className = 'grow';

    const line = document.createElement('div');
    line.className = 'line';

    const name = document.createElement('div');
    name.className = 'id';
    name.textContent = instance.name;
    name.title = instance.name;

    const tag = document.createElement('span');
    tag.className = instance.installed ? 'tag ok' : 'tag bad';
    tag.textContent = instance.installed ? '已安装' : '缺文件';

    line.append(name, tag);

    // 六个字段拆成小方块：原来挤在一行 meta 里，扫的时候要逐字读。
    const chips = document.createElement('div');
    chips.className = 'chips';
    const played = instance.lastPlayed
      ? `最后玩 ${instance.lastPlayed.slice(0, 16).replace('T', ' ')}`
      : '还没玩过';

    for (const text of [
      instance.versionId,
      `${instance.memory} MB`,
      `${instance.width}×${instance.height}`,
      instance.player,
      played
    ]) {
      const chip = document.createElement('span');
      chip.textContent = text;
      chips.appendChild(chip);
    }

    grow.append(line, chips);

    const actions = document.createElement('div');
    actions.className = 'row-actions';
    const running = state.running.includes(instance.name);

    if (running) {
      // 运行中只留一个「停止」：宿主同时只允许一个游戏进程，
      // 这时候再摆启动/编辑/删除，点哪个都只会得到一句拒绝。
      const stop = document.createElement('button');
      stop.className = 'act danger-solid';
      stop.textContent = '停止';
      stop.addEventListener('click', () => stopInstance(instance.name));
      actions.appendChild(stop);
    } else {
      // 账号按钮：显示这个实例当前绑的是哪个账号，点开可以换。
      const bound = state.accounts.find((account) => account.id === instance.accountId);
      const account = document.createElement('button');
      account.className = 'act account';
      account.textContent = bound ? `账号 · ${bound.name}` : '账号 · 未指定';
      account.title = bound
        ? `启动账号：${bound.name}（${bound.kind === 'msa' ? '正版' : '离线'}）`
        : '没绑定账号：按最近使用的账号启动，账号簿为空就用实例玩家名';
      account.addEventListener('click', () => openAccountPicker(instance.name));

      const launch = document.createElement('button');
      launch.className = 'act primary';
      launch.textContent = '启动';
      launch.disabled = !instance.installed || state.running.includes(instance.name);
      launch.title = instance.installed ? `启动 ${instance.name}` : '这个版本的 version.json 或 client.jar 不在';
      launch.addEventListener('click', () => launchInstance(instance.name));

      // 编辑 / 删除 收进「⋯」：一行摆四个控件的时候，删除和启动一样醒目，
      // 而它其实很少用、且最危险。收起来之后行尾只剩 启动 + ⋯。
      const more = document.createElement('button');
      more.className = 'act icon-only';
      more.textContent = '⋯';
      more.title = '更多操作';
      more.disabled = state.running.includes(instance.name);

      const menu = document.createElement('div');
      menu.className = 'menu';
      menu.hidden = true;

      for (const [label, handler, tone] of [
        ['编辑', () => openInstanceEditor(instance.name), ''],
        ['校验文件', () => verifyInstance(instance.name), ''],
        ['删除', () => deleteInstance(instance.name), 'danger']
      ]) {
        const item = document.createElement('button');
        item.type = 'button';
        item.textContent = label;

        if (tone) {
          item.className = tone;
        }

        item.addEventListener('click', () => {
          menu.hidden = true;
          handler();
        });
        menu.appendChild(item);
      }

      more.addEventListener('click', (event) => {
        event.stopPropagation();
        const open = menu.hidden;
        closeAllMenus();
        menu.hidden = !open;
      });

      const moreBox = document.createElement('div');
      moreBox.className = 'row-more';
      moreBox.append(more, menu);

      actions.append(account, launch, moreBox);
    }

    const version = state.versions.find((v) => v.id === instance.versionId);
    row.append(makeTile(instance.versionId, null, version?.type), grow, actions);
    host.appendChild(row);
  }
}

/// 新建实例的入口收在列表右上角：平时不占地方，点开才要名字和版本。
function openNewInstance() {
  $('new-instance').hidden = false;
  $('new-name').focus();
}

function closeNewInstance() {
  $('new-instance').hidden = true;
  setStatus('new-instance-status', '', false);
}

async function createInstance() {
  const name = $('new-name').value.trim();
  const versionId = $('new-version').value;

  if (!name) {
    setStatus('new-instance-status', '先给实例起个名字。', true);
    return;
  }

  try {
    await send('instance.create', { name, versionId });
    $('new-name').value = '';
    closeNewInstance();
    await loadInstances();
  } catch (error) {
    setStatus('new-instance-status', `新建失败：${error.message}`, true);
  }
}

/// 删除先进确认框：这一步会把 game/ 里的存档一起挪走（进 instances/.trash，可恢复，
/// 但恢复要手动去翻目录，所以不能一点就删）。
function deleteInstance(name) {
  askConfirm(
    '删除实例',
    `「${name}」以及它 game/ 里的存档会被移到 instances/.trash。要找回来的话得手动从那里拷出来，确定删？`,
    '删除',
    async () => {
      closeConfirm();

      try {
        await send('instance.delete', { name });
        await loadInstances();
        setStatus('instances-status', `已删除「${name}」，存档在 instances/.trash 里。`);
      } catch (error) {
        setStatus('instances-status', `删除失败：${error.message}`, true);
      }
    });
}

/// 启动。宿主立刻回执、真正的输出走事件，所以这里只能先按「点了就算在跑」上状态，
/// 失败会在 launch.done 里回来（那时再把状态清掉）。
async function launchInstance(name) {
  // 多开：只给这个实例打标记，别的行不受影响
  if (!state.running.includes(name)) {
    state.running.push(name);
  }

  renderInstances();
  setStatus('instances-status', `正在启动 ${name}…日志在单独窗口里。`);

  try {
    await send('launch.start', { name });
  } catch (error) {
    state.running = state.running.filter((item) => item !== name);
    renderInstances();
    setStatus('instances-status', `启动失败：${error.message}`, true);
  }
}

async function stopInstance(name) {
  setStatus('instances-status', `正在停止 ${name}…`);

  try {
    await send('launch.kill', { name });
  } catch (error) {
    setStatus('instances-status', `停止失败：${error.message}`, true);
  }
}

// ── 实例编辑 ──────────────────────────────────────────
//
// 后端 instance.update 只认 memory / width / height / player，版本号不能改——
// 换版本等于换一整套库，那是新建实例的事。

function openInstanceEditor(name) {
  const instance = state.instances.find((item) => item.name === name);
  if (!instance) {
    return;
  }

  state.editing = name;
  $('edit-subtitle').textContent = `${name} · ${instance.versionId}　改完保存，下次启动生效，存档不动。`;
  $('edit-memory').value = instance.memory;
  $('edit-width').value = instance.width;
  $('edit-height').value = instance.height;
  $('edit-player').value = instance.player;
  setStatus('edit-status', '');
  $('instance-edit').hidden = false;
  $('edit-memory').focus();
}

function closeInstanceEditor() {
  $('instance-edit').hidden = true;
  state.editing = null;
}

async function saveInstanceEditor() {
  const name = state.editing;
  if (!name) {
    return;
  }

  const memory = Number($('edit-memory').value);
  const width = Number($('edit-width').value);
  const height = Number($('edit-height').value);
  const player = $('edit-player').value.trim();

  // 只检查「填没填」，不在这里夹取数值：夹取规则住在 Core 的 Normalize 里。
  if (!Number.isFinite(memory) || !Number.isFinite(width) || !Number.isFinite(height) || !player) {
    setStatus('edit-status', '四项都要填，数值不能为空。', true);
    return;
  }

  try {
    await send('instance.update', { name, memory, width, height, player });
    closeInstanceEditor();
    await loadInstances();
    setStatus('instances-status', '已保存，下次启动生效。');
  } catch (error) {
    setStatus('edit-status', `保存失败：${error.message}`, true);
  }
}

// ── 确认框 ────────────────────────────────────────────

/// tone：'danger-solid'（默认，真会丢东西的操作）或 'primary'（允许这类不放红色的）
function askConfirm(title, text, actionLabel, onYes, tone = 'danger-solid') {
  $('confirm-title').textContent = title;
  $('confirm-text').textContent = text;
  $('confirm-yes').textContent = actionLabel;
  $('confirm-yes').className = `act ${tone}`;
  state.confirmYes = onYes;
  $('confirm').hidden = false;
  $('confirm-no').focus();
}

function closeConfirm() {
  $('confirm').hidden = true;
  state.confirmYes = null;
}

// ── 结果弹窗 ──────────────────────────────────────────
//
// 登录成功/失败必须弹出来。状态行在弹窗底部，用户根本不会去看，
// 出个 HTTP 400 就只剩「什么也没发生」。

function showNotice(title, text, tone = 'info', raw = null, action = null) {
  $('notice-title').textContent = title;
  $('notice-title').className = tone === 'ok' ? 'ok' : tone === 'bad' ? 'bad' : '';
  $('notice-text').textContent = text;

  // 原文单独一块：中文解释给普通人看，原文给愿意去搜 AADSTS 码的人。
  const rawBox = $('notice-raw');
  rawBox.hidden = !raw;
  rawBox.textContent = raw ?? '';

  // 附加动作（例如「购买 Minecraft」）：没有就整个收起，不留空按钮。
  const actionButton = $('notice-action');
  actionButton.hidden = !action;
  state.noticeAction = action?.url ?? null;
  if (action) {
    actionButton.textContent = action.label;
  }

  $('notice').hidden = false;
  $('notice-ok').focus();
}

function closeNotice() {
  $('notice').hidden = true;
}

/// 宿主的错误串常以「登录失败：」开头，标题里已经写了，正文里去掉更干净。
function stripFailurePrefix(text) {
  return String(text ?? '').replace(/^登录失败[：:]\s*/, '');
}

/// 把微软的英文错误翻成人话。
/// 命中就返回中文解释（原文仍旧会显示在下面），没命中返回 null——不硬编，也不假装看懂了。
function explainLoginError(text) {
  const value = String(text ?? '');

  // 没买 Java 版 / 没建角色名：这类要给出路（购买链接），不能只报错
  if (/没有 Java 版|没有购买 Java 版|Minecraft 登录失败（HTTP 40[13]）/.test(value)) {
    return {
      zh: `你尚未购买正版 Minecraft，或者 Xbox Game Pass 已到期。

如果已经买过，可能是还没创建角色名——到 minecraft.net 登录后，在个人资料里建一个，再回来点一次「登录正版账号」。`,
      // 宿主给的这句本来就是中文，再当"原文"列一遍只是重复
      hideRaw: true,
      action: {
        label: '购买 Minecraft',
        url: 'https://www.minecraft.net/zh-hans/store/minecraft-java-bedrock-edition'
      }
    };
  }

  // 应用自己没配好（跟玩家无关，最容易吓到人的一类）
  if (/AADSTS7000218|AADSTS70002\b|must be marked as 'mobile'/i.test(value)) {
    return '这个启动器应用没开通「公共客户端」权限，属于应用配置问题，不是你的账号有问题。'
      + '（开发者：到 Azure 应用 → 清单，把 allowPublicClient 设为 true，或在「身份验证」里把「允许公共客户端流」设为「是」。）';
  }

  if (/AADSTS700016|AADSTS90002|AADSTS900144/i.test(value)) {
    return '找不到这个应用身份：应用 ID 不对，或者应用建在另一个账号的目录里。请更新启动器版本。';
  }

  if (/AADSTS50020|AADSTS50059|AADSTS50034/i.test(value)) {
    return '这个微软账号不在应用允许的范围内。开发者需要把「受支持的账户类型」改成'
      + '「任何 Entra ID 租户 + 个人 Microsoft 账户」。';
  }

  if (/AADSTS70008|AADSTS700082|invalid_grant/i.test(value)) {
    return '授权已经过期或被撤销了（改过密码、或在微软账号里移除过授权）。重新登录一次即可。';
  }

  if (/AADSTS7000215|invalid_client/i.test(value)) {
    return '应用身份没被微软接受：多半是应用配置缺失。如果刚改过配置，等几秒再试。';
  }

  // Xbox / Minecraft 侧（这类宿主已经给了中文，这里只作兜底）
  if (/2148916233/.test(value)) {
    return '这个微软账号还没有 Xbox 档案。去 xbox.com 用同一个账号登录一次（免费），再回来重试。';
  }

  if (/2148916235/.test(value)) {
    return '这个账号所在的国家/地区不支持 Xbox Live。';
  }

  if (/2148916238/.test(value)) {
    return '这是未成年账号，需要先加入微软家庭组才能登录。';
  }

  // 网络：HttpClient 超时/解析失败的原文是英文，玩家看不懂
  if (/timed out|timeout|No such host|Name or service not known|actively refused/i.test(value)) {
    return '连不上微软的服务器（超时或域名解析失败）。检查网络，必要时挂代理再试。';
  }

  return null;
}

/// 失败弹窗的统一入口：中文解释在上，微软原文在下面一块。
function showLoginFailure(text) {
  const raw = stripFailurePrefix(text);
  const hint = explainLoginError(raw);

  closeLoginBox();

  if (typeof hint === 'string') {
    showNotice('登录失败', hint, 'bad', raw);
  } else if (hint) {
    showNotice('登录失败', hint.zh, 'bad', hint.hideRaw ? null : raw, hint.action);
  } else {
    // 没见过就原样给，不硬编，也不额外重复一遍
    showNotice('登录失败', raw, 'bad', null);
  }
}

// ── 设置页 ────────────────────────────────────────────
//
// 这一页的所有字段都由宿主校验后落盘（config.json，原子写）。
// 前端不夹取数值、不猜默认值：夹取规则住在 Core 的 LauncherSettings 里，
// 这里多写一份就会跟那边悄悄漂移。

// ── 内存分配 ──────────────────────────────────────────
//
// 一条内存条：左边深色是系统已用，紧跟一段浅色是"给这个游戏留的"。
// 自动配置的数字来自宿主（memory.info），自定义来自滑块。

function formatGb(mb) {
  return `${(mb / 1024).toFixed(1)} GB`;
}

/// 自定义能选到的上限：物理内存减去 1 GB（总得给系统留一口）。
/// 读不到物理内存时才退回 32 GB 的兜底值。
function memoryCeilingMb() {
  const total = state.memory.totalMb;
  return total > 0 ? Math.max(MIN_MEMORY_MB, total - SYSTEM_RESERVE_MB) : 32768;
}

function renderMemory() {
  const total = state.memory.totalMb;
  const used = state.memory.usedMb;
  const custom = $('mem-custom').checked;
  const ceiling = memoryCeilingMb();
  const range = $('mem-range');

  // 滑块上限跟着本机内存走。超过上限的旧值要压回来，
  // 否则界面上会出现"给游戏 20 GB，而机器只有 16 GB"这种事。
  range.max = String(ceiling);
  if (Number(range.value) > ceiling) {
    range.value = String(ceiling);
  }

  const assigned = custom ? Number(range.value) : state.memory.autoMb;

  $('mem-range').disabled = !custom;
  const span = Number(range.max) - Number(range.min);
  const fillPercent = span > 0 ? ((Number(range.value) - Number(range.min)) / span) * 100 : 0;
  range.style.setProperty('--fill', `${fillPercent}%`);
  $('mem-ceiling').textContent = total > 0
    ? `上限 ${formatGb(ceiling)}（本机 ${formatGb(total)}，留 ${formatGb(SYSTEM_RESERVE_MB)} 给系统）`
    : '';
  // 自动模式下这里只写「自动」：具体数字由下面的「游戏分配」承担，
  // 否则同一个数在滑块右边和条下方各出现一次，容易看混。
  $('mem-read').textContent = custom ? `${assigned} MB` : '自动';

  if (!total) {
    // 读不到物理内存时不要编数字
    $('mem-used-text').textContent = '读不到物理内存';
    $('mem-game-text').textContent = `${assigned} MB`;
    $('mem-bar-used').style.width = '0%';
    $('mem-bar-game').style.width = '0%';
    return;
  }

  const usedPercent = Math.min(100, (used / total) * 100);
  // 游戏分配从已用之后开始画，但最多画到条尾，超了就压到剩余空间
  const gamePercent = Math.min(100 - usedPercent, (assigned / total) * 100);

  // 只有自定义才谈"超配"：自动配置刻意按物理总量算（Windows 平常把内存当缓存吃满，
  // "当前可用"永远很小），拿它去和可用比会一直亮警报。
  const overCommit = custom && assigned > total - used;

  $('mem-bar-used').style.width = `${usedPercent}%`;
  $('mem-bar-game').style.width = `${Math.max(0, gamePercent)}%`;
  $('mem-bar-game').classList.toggle('over', overCommit);
  $('mem-used-text').textContent = `${formatGb(used)} / ${formatGb(total)}`;
  $('mem-game-text').textContent = overCommit
    ? `${formatGb(assigned)}（超过当前可用）`
    : formatGb(assigned);
}

// 内存条要跟着系统走：只在设置页可见时每 2 秒拉一次，切走就停。
const MEMORY_POLL_MS = 2000;
const MIN_MEMORY_MB = 512;
const SYSTEM_RESERVE_MB = 1024;
let memoryTimer = null;

function startMemoryPolling() {
  if (memoryTimer !== null) {
    return;
  }

  loadMemoryInfo();
  memoryTimer = setInterval(loadMemoryInfo, MEMORY_POLL_MS);
}

function stopMemoryPolling() {
  if (memoryTimer === null) {
    return;
  }

  clearInterval(memoryTimer);
  memoryTimer = null;
}

async function loadMemoryInfo() {
  try {
    const info = await send('memory.info');
    state.memory = {
      totalMb: info.totalMb ?? 0,
      usedMb: info.usedMb ?? 0,
      autoMb: info.autoMb ?? 0
    };
    renderMemory();
  } catch (error) {
    console.warn(`读不到内存信息：${error.message}`);
  }
}

function fillSettings(settings) {
  const customMemory = settings.useCustomMemory === true;
  $('mem-auto').checked = !customMemory;
  $('mem-custom').checked = customMemory;
  $('mem-range').value = settings.memoryMb ?? 2048;
  $('mem-range').disabled = !customMemory;
  $('mem-optimize').checked = settings.memoryOptimize === true;
  $('set-diagnostic-log').checked = settings.diagnosticLog === true;
  $('set-check-files').checked = settings.checkFilesBeforeLaunch !== false;
  $('set-auto-repair').checked = settings.autoRepairMissing !== false;
  $('set-auto-server').value = settings.autoJoinServer ?? '';
  $('set-warn-plugin-restart').checked = settings.warnRestartAfterPluginChange !== false;
  // 0 也当默认：旧配置里没这个字段，反序列化给的就是 0
  const threads = settings.downloadThreads > 0 ? settings.downloadThreads : 64;
  $('dl-threads').value = threads;
  $('dl-threads-value').textContent = String(threads);
  $('dl-speed').value = settings.downloadSpeedLimitMb > 0 ? settings.downloadSpeedLimitMb : '';
  $('dl-parallel').value = String(settings.downloadParallelTasks > 0 ? settings.downloadParallelTasks : 1);
  renderMemory();
  $('set-visibility').value = settings.launcherVisibility ?? 'keep';
  $('set-priority').value = settings.gamePriority ?? 'normal';
  $('set-game-window').value = settings.gameWindowMode ?? 'default';
  $('set-java').value = settings.javaHome ?? '';
  applyStoredJavaChoice();
  $('set-game-args').value = (settings.extraGameArguments ?? []).join(String.fromCharCode(10));
  $('set-prelaunch').value = settings.preLaunchCommand ?? '';
  $('set-gc').value = settings.gcPreset ?? 'default';
  $('set-gpu').checked = settings.preferHighPerformanceGpu === true;
  $('set-disable-wrapper').checked = settings.disableLaunchWrapper === true;
  $('set-jvm').value = (settings.extraJvmArguments ?? []).join('\n');
}

async function loadSettings() {
  try {
    const payload = await send('settings.get');
    state.settings = payload.settings;
    state.gameRoot = payload.gameRoot;
    fillSettings(payload.settings);
    renderGameRoots(payload);
    loadLogInfo();

    if (payload.broken) {
      setStatus('set-save-status', '上回的配置读不出来，已备份成 config.json.broken，现在用的是默认值。', true);
    }

    if (!payload.hasGameRoot) {
      showFirstRun();
    }
  } catch (error) {
    setStatus('set-save-status', `读取设置失败：${error.message}`, true);
  }
}

async function saveSettings() {
  // 只发这一页真正管的字段。gameRoot 留空表示「还没指定」，
  // 不能顺手写成空字符串以外的哨兵值——空就是空，宿主自己认得。
  // 目录不在这里保存：它有自己的增删改命令，混进「保存设置」只会让两边打架
  const payload = {
    memoryMb: Number($('mem-range').value),
    useCustomMemory: $('mem-custom').checked,
    memoryOptimize: $('mem-optimize').checked,
    diagnosticLog: $('set-diagnostic-log').checked,
    checkFilesBeforeLaunch: $('set-check-files').checked,
    autoRepairMissing: $('set-auto-repair').checked,
    autoJoinServer: $('set-auto-server').value.trim(),
    warnRestartAfterPluginChange: $('set-warn-plugin-restart').checked,
    downloadThreads: Number($('dl-threads').value),
    downloadSpeedLimitMb: Number($('dl-speed').value || 0),
    downloadParallelTasks: Number($('dl-parallel').value),
    launcherVisibility: $('set-visibility').value,
    gamePriority: $('set-priority').value,
    gameWindowMode: $('set-game-window').value,
    javaHome: $('set-java').value.trim(),
    extraGameArguments: $('set-game-args').value.split(String.fromCharCode(10))
      .map((line) => line.trim())
      .filter((line) => line.length > 0),
    preLaunchCommand: $('set-prelaunch').value.trim(),
    gcPreset: $('set-gc').value,
    preferHighPerformanceGpu: $('set-gpu').checked,
    disableLaunchWrapper: $('set-disable-wrapper').checked,
    // JVM 参数按空格拆：多参数写在同一行时，按行拆会变成一个带空格的参数，java 不认
    extraJvmArguments: $('set-jvm').value
      .split(' ')
      .map((line) => line.trim())
      .filter((line) => line.length > 0)
  };

  try {
    const result = await send('settings.update', payload);
    state.settings = result.settings;
    state.gameRoot = result.gameRoot;
    fillSettings(result.settings);
    setStatus('set-save-status', '已保存。');
    hideFirstRun();
    await loadVersions();
  } catch (error) {
    setStatus('set-save-status', `保存失败：${error.message}`, true);
  }
}

async function detectGameRoot() {
  setStatus('first-run-status', '正在找…');
  try {
    const payload = await send('settings.detect');
    state.candidates = payload.candidates ?? [];
    renderCandidates('first-run-candidates', state.candidates);

    if (state.candidates.length === 0) {
      setStatus('first-run-status', '没找到现成的 .minecraft。点「添加目录」手动选一个。');
    } else {
      setStatus('first-run-status',
        payload.best ? `找到 ${state.candidates.length} 个，最像的是 ${payload.best}` : `找到 ${state.candidates.length} 个`);
    }
  } catch (error) {
    setStatus('first-run-status', `检测失败：${error.message}`, true);
  }
}

// ── 一键启动首页 ───────────────────────────────────────
// 只做一件事：把"该点哪个"变成一个按钮。实例和账号各一个下拉，默认记住上次用的。

function renderHome() {
  const instances = state.instances ?? [];
  const accounts = state.accounts ?? [];

  // 实例：按最近游玩排，默认选第一个（也就是上次玩的）
  const instanceSelect = $('home-instance');
  const previousInstance = instanceSelect.value;
  instanceSelect.innerHTML = '';

  const sorted = [...instances].sort((a, b) => {
    const left = a.lastPlayed ?? '';
    const right = b.lastPlayed ?? '';
    return right.localeCompare(left);
  });

  if (!sorted.length) {
    const option = document.createElement('option');
    option.textContent = '还没有实例';
    option.value = '';
    instanceSelect.appendChild(option);
  } else {
    for (const instance of sorted) {
      const option = document.createElement('option');
      option.value = instance.name;
      option.textContent = `${instance.name} · ${instance.versionId}`;
      instanceSelect.appendChild(option);
    }

    // 保留用户这次会话里选过的，别每次刷新都跳回第一个
    if (sorted.some((instance) => instance.name === previousInstance)) {
      instanceSelect.value = previousInstance;
    }
  }

  // 账号：默认"最近使用"那个
  const accountSelect = $('home-account');
  accountSelect.innerHTML = '';

  if (!accounts.length) {
    const option = document.createElement('option');
    option.textContent = '还没有账号';
    option.value = '';
    accountSelect.appendChild(option);
  } else {
    for (const account of accounts) {
      const option = document.createElement('option');
      option.value = account.id;
      option.textContent = account.name + (account.kind === 'msa' ? '（正版）' : '（离线）');
      accountSelect.appendChild(option);
    }

    if (state.lastAccountId) {
      accountSelect.value = state.lastAccountId;
    }
  }

  updateHomeLaunch();
}

/// 启动按钮的可用状态：没实例、或者这个实例正在跑，都不让点。
function updateHomeLaunch() {
  const name = $('home-instance').value;
  const running = name && (state.running ?? []).includes(name);

  $('home-launch').disabled = !name || Boolean(running);
  $('home-launch').textContent = !name ? '先建一个实例' : running ? '正在运行' : '启动游戏';
}

async function launchFromHome() {
  const name = $('home-instance').value;

  if (!name) {
    return;
  }

  // 换账号这一步只是"记住下次用谁"：启动时宿主取的就是最近使用的那个账号。
  const accountId = $('home-account').value;

  if (accountId && accountId !== state.lastAccountId) {
    try {
      await send('account.remember', { id: accountId });
      state.lastAccountId = accountId;
    } catch (error) {
      setStatus('home-status', `切换账号失败：${error.message}`, true);
      return;
    }
  }

  setStatus('home-status', `正在启动 ${name}…`, false);
  await launchInstance(name);
}

// ── 崩溃恢复 ───────────────────────────────────────────
// 插件和宿主同进程，拦不住它把进程带崩；能保证的是"下次启动知道崩了，并且能一键恢复"。

let crashReport = null;

async function loadCrashState() {
  try {
    const info = await send('crash.info');

    if (!info.pending) {
      $('crash-dialog').hidden = true;
      return;
    }

    // 设置页那一行：目录 + 历史上崩过几次
    $('crash-dir').value = info.directory ?? '';
    $('crash-dir').title = info.directory ?? '';

    const history = info.history ?? [];
    setStatus('crash-summary', history.length
      ? `共 ${history.length} 次崩溃，最近一次 ${new Date(history[0].time).toLocaleString()}`
      : '还没有崩溃记录', false);

    crashReport = info.pending;

    // 只有"真的崩过"才值得弹框。只是被强制结束/关机没走正常退出时，
    // 弹一个"崩溃了"会吓人一跳，而且用户也没什么可做的——只在设置页标一句就够了。
    const wasRealCrash = crashReport.source !== "ProcessExit" && crashReport.exceptionType !== "AbnormalExit";

    if (!wasRealCrash) {
      $('crash-dialog').hidden = true;
      setStatus('crash-summary',
        `上次没有正常退出（${new Date(crashReport.time).toLocaleString()}），没有异常记录`, false);
      await send('crash.clear');
      return;
    }

    $('crash-time').textContent = new Date(crashReport.time).toLocaleString();
    $('crash-plugin').textContent = crashReport.activePlugin ?? '没能定位到具体插件';
    $('crash-reason').textContent = crashReport.message || crashReport.exceptionType || '—';
    $('crash-reason').title = `${crashReport.exceptionType}\n${crashReport.stackTrace ?? ''}`;

    // 定位不到插件时，"关闭崩溃插件"这个选项没有意义
    $('crash-restart-without').disabled = !crashReport.activePlugin;

    setStatus('crash-status', '', false);
    $('crash-dialog').hidden = false;
  } catch (error) {
    console.warn(`读不到崩溃状态：${error.message}`);
  }
}

async function crashExport() {
  try {
    const result = await send('crash.export');

    setStatus('crash-status', result.ok ? `已导出：${result.path}` : result.error, !result.ok);
  } catch (error) {
    setStatus('crash-status', `导出失败：${error.message}`, true);
  }
}

async function crashRestart(options) {
  try {
    await send('crash.restart', options ?? {});
  } catch (error) {
    setStatus('crash-status', `重启失败：${error.message}`, true);
  }
}

// ── 下载任务 ───────────────────────────────────────────
// 进度靠宿主推快照（250ms 节流），前端只做展示；速度由两次快照的差值算，
// 让宿主去算速度就得在核心层存时间戳，没必要。

const DOWNLOAD_STATE_TEXT = {
  Queued: '排队中',
  Running: '下载中',
  Paused: '已暂停',
  Done: '已完成',
  Failed: '失败',
  Canceled: '已取消'
};

function formatBytes(bytes) {
  if (!bytes) {
    return '0 B';
  }

  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let index = 0;

  while (value >= 1024 && index < units.length - 1) {
    value /= 1024;
    index++;
  }

  return `${value >= 100 || index === 0 ? Math.round(value) : value.toFixed(1)} ${units[index]}`;
}

function renderDownloadTasks(tasks) {
  state.downloadTasks = tasks ?? [];

  const host = $('download-list');
  host.innerHTML = '';

  if (state.downloadTasks.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'empty compact';
    empty.textContent = '还没有下载任务。点右上角「新建下载任务」，把链接粘进去。';
    host.appendChild(empty);
    return;
  }

  for (const task of state.downloadTasks) {
    host.appendChild(makeDownloadRow(task));
  }
}

function makeDownloadRow(task) {
  const row = document.createElement('div');
  row.className = `dl-row ${task.state.toLowerCase()}`;
  row.dataset.id = task.id;

  // 标题行
  const head = document.createElement('div');
  head.className = 'dl-head';

  const title = document.createElement('div');
  title.className = 'dl-title';
  title.textContent = task.title;
  title.title = task.title;

  const badge = document.createElement('span');
  badge.className = task.state === 'Failed' ? 'badge warn' : task.state === 'Done' ? 'badge ok' : 'badge';
  badge.textContent = DOWNLOAD_STATE_TEXT[task.state] ?? task.state;

  head.append(title, badge);

  // 进度条
  const bar = document.createElement('div');
  bar.className = 'dl-bar';

  const fill = document.createElement('div');
  fill.className = 'dl-fill';

  const percent = task.totalBytes > 0
    ? Math.min(100, Math.round((task.doneBytes * 100) / task.totalBytes))
    : (task.state === 'Done' ? 100 : 0);

  fill.style.width = `${percent}%`;
  bar.appendChild(fill);

  // 说明行
  const meta = document.createElement('div');
  meta.className = 'dl-meta';

  const detail = task.totalBytes > 0
    ? `${formatBytes(task.doneBytes)} / ${formatBytes(task.totalBytes)}`
    : formatBytes(task.doneBytes);

  meta.textContent = `${detail} · 文件 ${task.filesDone}/${task.filesTotal}`
    + (task.error ? ` · ${task.error}` : '');

  // 操作
  const actions = document.createElement('div');
  actions.className = 'dl-actions';

  if (task.state === 'Running' || task.state === 'Queued') {
    actions.appendChild(makeSmallButton('暂停', () => downloadControl('download.pause', task.id)));
    actions.appendChild(makeSmallButton('取消', () => downloadControl('download.cancel', task.id)));
  } else if (task.state === 'Paused') {
    actions.appendChild(makeSmallButton('继续', () => downloadControl('download.resume', task.id)));
    actions.appendChild(makeSmallButton('取消', () => downloadControl('download.cancel', task.id)));
  } else {
    actions.appendChild(makeSmallButton('移除', () => downloadControl('download.remove', task.id)));
  }

  row.append(head, bar, meta, actions);

  return row;
}

function makeSmallButton(text, handler) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'act small';
  button.textContent = text;
  button.addEventListener('click', handler);

  return button;
}

async function downloadControl(command, id) {
  try {
    const result = await send(command, { id });

    if (!result.ok) {
      setStatus('download-status', result.error ?? '操作失败。', true);
      return;
    }

    renderDownloadTasks(result.tasks);
  } catch (error) {
    setStatus('download-status', `操作失败：${error.message}`, true);
  }
}

/// 完整校验：逐个算 SHA1，慢，所以放在「⋯」里手动点。
async function verifyInstance(name) {
  showNotice('校验文件', `正在逐个校验「${name}」的文件，可能要一两分钟…`, 'info');

  try {
    const result = await send('instance.verify', { name });

    if (!result.ok) {
      showNotice('校验失败', result.error ?? '读不到这个实例的清单。', 'error');
      return;
    }

    if (result.healthy) {
      showNotice('校验通过', `共 ${result.total} 个文件，全部完好。`, 'ok');
      return;
    }

    const lines = [
      `共 ${result.total} 个文件：缺 ${result.missing} 个、大小不符 ${result.wrongSize} 个、哈希不符 ${result.wrongHash} 个。`,
      '',
      '坏文件（最多列 20 个）：',
      ...(result.broken ?? [])
    ];

    showNotice('发现问题', lines.join(String.fromCharCode(10)), 'warn');
  } catch (error) {
    showNotice('校验失败', error.message, 'error');
  }
}

async function loadDownloads() {
  try {
    const payload = await send('download.list');
    renderDownloadTasks(payload.tasks);
  } catch (error) {
    setStatus('download-status', `读不到下载任务：${error.message}`, true);
  }
}

function openDownloadDialog() {
  $('download-urls').value = '';
  $('download-new-dialog').hidden = false;
  setStatus('download-new-status', '', false);
  $('download-urls').focus();
}

function closeDownloadDialog() {
  $('download-new-dialog').hidden = true;
}

async function startDownload() {
  const urls = $('download-urls').value
    .split(String.fromCharCode(10))
    .map(line => line.trim())
    .filter(line => line.length > 0);

  if (urls.length === 0) {
    setStatus('download-new-status', '先粘一个链接进来。', true);
    return;
  }

  try {
    const result = await send('download.add', { urls });

    if (!result.ok) {
      setStatus('download-new-status', result.error, true);
      return;
    }

    renderDownloadTasks(result.tasks);

    // 有行被跳过就别急着关弹窗：用户得知道哪一条没下
    if (result.errors && result.errors.length > 0) {
      setStatus('download-new-status', result.errors.join('；'), true);
      return;
    }

    closeDownloadDialog();
    setStatus('download-status', `已新建 ${result.created} 个任务`, false);
  } catch (error) {
    setStatus('download-new-status', `新建失败：${error.message}`, true);
  }
}

// ── 启动器日志 ─────────────────────────────────────────
// 目录是固定的（数据目录下 logs），只读展示；这里只管打开、清理、切诊断开关。

async function loadLogInfo() {
  try {
    const info = await send('log.info');
    state.logDirectory = info.directory;
    state.logCount = info.count;
    $('log-dir').value = info.directory;
    $('log-dir').title = info.directory;
  } catch (error) {
    setStatus('log-status', `读不到日志目录：${error.message}`, true);
  }
}

async function openLogDirectory() {
  if (!state.logDirectory) {
    return;
  }

  try {
    await send('shell.openFolder', { path: state.logDirectory });
  } catch (error) {
    setStatus('log-status', `打不开：${error.message}`, true);
  }
}

/// 清理前必须问一次：这一步会删掉之前所有局游戏的日志，删了没法恢复。
/// 正在写的那份不会动（宿主按文件句柄判断，不是靠猜）。
function clearLogs() {
  askConfirm(
    '清理启动器日志',
    '是否确定要删除全部启动器日志。不会删除正在写入的日志',
    '清理',
    async () => {
      closeConfirm();

      try {
        const result = await send('log.clear');
        await loadLogInfo();
        setStatus(
          'log-status',
          `已删除 ${result.deleted} 个` +
            (result.skipped > 0 ? `，跳过正在写入的 ${result.skipped} 个` : '') +
            (result.failed > 0 ? `，${result.failed} 个删不掉（可能被别的程序占用）` : ''),
          result.failed > 0);
      } catch (error) {
        setStatus('log-status', `清理失败：${error.message}`, true);
      }
    },
    'danger-solid');
}

// ── Minecraft 目录列表 ─────────────────────────────────
// 目录是「一整套 versions/libraries」，切换等于换了整个游戏环境，
// 所以每次改完都从宿主拉一次完整状态，不在前端自己拼。

function renderGameRoots(payload) {
  if (!payload) {
    return;
  }

  state.gameRoots = payload.gameRoots ?? [];
  state.activeGameRootId = payload.activeGameRootId ?? null;

  if (payload.gameRoot) {
    state.gameRoot = payload.gameRoot;
  }

  const host = $('game-root-list');
  host.innerHTML = '';

  if (state.gameRoots.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'empty compact';
    empty.textContent = '还没有目录。点右上角「添加目录」，选一个 .minecraft 文件夹。';
    host.appendChild(empty);
    return;
  }

  for (const root of state.gameRoots) {
    host.appendChild(makeGameRootRow(root, root.id === state.activeGameRootId));
  }
}

const ICON_PENCIL = '<svg viewBox="0 0 24 24" width="17" height="17" aria-hidden="true"><path d="M4 20h4l10-10-4-4L4 16v4Z" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/><path d="M14 6l4 4" fill="none" stroke="currentColor" stroke-width="1.7"/></svg>';
const ICON_FOLDER = '<svg viewBox="0 0 24 24" width="17" height="17" aria-hidden="true"><path d="M3 7.5A1.5 1.5 0 0 1 4.5 6h4l2 2.5h7A1.5 1.5 0 0 1 19 10v7a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 3 17V7.5Z" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/></svg>';
const ICON_CROSS = '<svg viewBox="0 0 24 24" width="17" height="17" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>';

function makeIconButton(svg, tip, handler) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'dir-btn';
  button.title = tip;
  button.setAttribute('aria-label', tip);
  button.innerHTML = svg;
  button.addEventListener('click', (event) => {
    event.stopPropagation();
    handler();
  });

  return button;
}

function makeGameRootRow(root, active) {
  const row = document.createElement('div');
  row.className = active ? 'dir-row active' : 'dir-row';
  row.dataset.id = root.id;

  const main = document.createElement('div');
  main.className = 'dir-main';

  const line = document.createElement('div');
  line.className = 'dir-line';

  const note = document.createElement('div');
  note.className = 'dir-note';
  note.textContent = root.note || root.path;
  note.title = note.textContent;

  line.appendChild(note);

  if (!root.exists) {
    const warn = document.createElement('span');
    warn.className = 'badge warn';
    warn.textContent = '路径不存在';
    line.appendChild(warn);
  }

  const path = document.createElement('div');
  path.className = 'dir-path mono';
  path.textContent = root.path;
  path.title = root.path;

  main.append(line, path);

  const actions = document.createElement('div');
  actions.className = 'dir-actions';
  actions.append(
    makeIconButton(ICON_PENCIL, '备注', () => openGameRootNote(root)),
    makeIconButton(ICON_FOLDER, '打开文件夹', () => send('shell.openFolder', { path: root.path })),
    makeIconButton(ICON_CROSS, '从列表中移除', () => removeGameRoot(root))
  );

  row.append(main, actions);
  row.addEventListener('click', () => selectGameRoot(root.id));

  return row;
}

async function selectGameRoot(id) {
  if (id === state.activeGameRootId) {
    return;
  }

  try {
    const result = await send('gameRoot.select', { id });
    renderGameRoots(result.state);
    // 换目录 = 换了一整套版本，实例页的版本下拉必须跟着重拉
    await loadVersions();
  } catch (error) {
    setStatus('set-game-root-status', `切换目录失败：${error.message}`, true);
  }
}

async function addGameRoot() {
  try {
    const picked = await send('shell.pickFolder');

    if (picked.canceled) {
      return;
    }

    const result = await send('gameRoot.add', { path: picked.path });

    if (!result.ok) {
      setStatus('set-game-root-status', result.error, true);
      return;
    }

    renderGameRoots(result.state);
    setStatus(
      'set-game-root-status',
      result.hasVersions
        ? ''
        : '这个目录里没有 versions，新建实例时不会有版本可选。',
      !result.hasVersions);

    await loadVersions();
  } catch (error) {
    setStatus('set-game-root-status', `添加失败：${error.message}`, true);
  }
}

// 备注弹窗
let noteTargetId = null;

function openGameRootNote(root) {
  noteTargetId = root.id;
  $('game-root-note-input').value = root.note ?? '';
  $('game-root-note').hidden = false;
  setStatus('game-root-note-status', '', false);
  $('game-root-note-input').focus();
  $('game-root-note-input').select();
}

function closeGameRootNote() {
  $('game-root-note').hidden = true;
  noteTargetId = null;
}

async function saveGameRootNote() {
  if (!noteTargetId) {
    return;
  }

  try {
    const result = await send('gameRoot.note', {
      id: noteTargetId,
      note: $('game-root-note-input').value.trim()
    });

    if (!result.ok) {
      setStatus('game-root-note-status', result.error, true);
      return;
    }

    renderGameRoots(result.state);
    closeGameRootNote();
  } catch (error) {
    setStatus('game-root-note-status', `保存失败：${error.message}`, true);
  }
}

function removeGameRoot(root) {
  askConfirm(
    '从列表中移除',
    `「${root.note || root.path}」会从列表里去掉，磁盘上的文件夹不动。确定？`,
    '移除',
    async () => {
      closeConfirm();

      try {
        const result = await send('gameRoot.remove', { id: root.id });
        renderGameRoots(result.state);
        await loadVersions();
      } catch (error) {
        setStatus('set-game-root-status', `移除失败：${error.message}`, true);
      }
    },
    'danger');
}

/// 候选目录列表。每条都标出「凭什么认为它是游戏目录」——版本多的排在前面。
function renderCandidates(hostId, candidates) {
  const host = $(hostId);
  host.innerHTML = '';

  for (const candidate of candidates) {
    const row = document.createElement('div');
    row.className = 'row';
    row.dataset.candidate = candidate.path;

    const grow = document.createElement('div');
    grow.className = 'grow';

    const line = document.createElement('div');
    line.className = 'line';

    const path = document.createElement('div');
    path.className = 'id mono';
    path.textContent = candidate.path;
    path.title = candidate.path;

    const badge = document.createElement('span');
    badge.className = candidate.valid ? 'badge ok' : 'badge warn';
    badge.textContent = candidate.valid ? '可用' : '存疑';

    line.append(path, badge);

    const meta = document.createElement('div');
    meta.className = 'meta';
    meta.textContent = `${candidate.source} · ${candidate.versions} 个版本`;

    grow.append(line, meta);

    const use = document.createElement('button');
    use.className = candidate.valid ? 'act primary' : 'act';
    use.textContent = '用这个';
    use.addEventListener('click', () => applyGameRoot(candidate.path));

    row.append(grow, use);
    host.appendChild(row);
  }
}

async function applyGameRoot(path) {
  $('first-run-path').value = path;

  try {
    const result = await send('gameRoot.add', { path });
    renderGameRoots(result.state);
    setStatus('first-run-status', '');
    hideFirstRun();
    await loadVersions();
  } catch (error) {
    setStatus('first-run-status', `用不了这个目录：${error.message}`, true);
  }
}

/// 手动输入框只在选了「手动填写目录…」时露出来。
function syncJavaManualVisibility() {
  const manual = state.javaChoice === '__manual__';
  $('set-java').hidden = !manual;

  if (manual) {
    $('set-java').focus();
  }
}

async function loadJavaList() {
  // 整份扫描结果留在这里，显示、顺序、选中都在前端维护。
  try {
    const payload = await send('java.list');
    state.javaAll = payload.java ?? [];
    state.javaHidden = new Set();
    applyStoredJavaChoice();
    renderJavaList();

    setStatus(
      'java-status',
      state.javaAll.length > 0
        ? `扫到 ${state.javaAll.length} 个 Java（版本从高到低）。点左边圆点选一个，或用「自动选择」。`
        : '本机没扫到 Java，可以点「手动填写目录…」。',
      state.javaAll.length === 0);
  } catch (error) {
    setStatus('java-status', `扫描失败：${error.message}`, true);
  }
}

/// 把设置里存的 javaHome 翻译成"选中哪一项"。
function applyStoredJavaChoice() {
  const stored = ($('set-java').value || '').trim();

  if (stored.length === 0) {
    state.javaChoice = '';
    return;
  }

  const known = (state.javaAll ?? []).some((java) => java.home === stored);
  state.javaChoice = known ? stored : '__manual__';
  syncJavaManualVisibility();
}

/// 当前参与显示与选择的 Java（被移出的不算）。
function visibleJava() {
  const hidden = state.javaHidden ?? new Set();
  return (state.javaAll ?? []).filter((java) => !hidden.has(java.home));
}

/// 一行：圆点 + 文字 + 右侧操作。
function makeJavaRow({ key, title, subtitle, actions, chosen }) {
  const row = document.createElement('label');
  row.className = chosen ? 'java-row chosen' : 'java-row';

  const radio = document.createElement('input');
  radio.type = 'radio';
  radio.name = 'java-choice';
  radio.value = key;
  radio.checked = chosen;
  radio.addEventListener('change', () => {
    state.javaChoice = key;

    if (key !== '__manual__') {
      $('set-java').value = key;
    }

    syncJavaManualVisibility();
    renderJavaList();
  });

  const text = document.createElement('div');
  text.className = 'java-meta';

  const head = document.createElement('div');
  head.className = 'java-ver';
  head.textContent = title;
  text.appendChild(head);

  if (subtitle) {
    const path = document.createElement('div');
    path.className = 'java-path';
    path.textContent = subtitle;
    path.title = subtitle;
    text.appendChild(path);
  }

  row.append(radio, text);

  if (actions) {
    row.appendChild(actions);
  }

  return row;
}

/// 列表：自动选择 + 每个 Java + 手动填写。
function renderJavaList() {
  const host = $('java-list');
  host.replaceChildren();

  const list = visibleJava();

  host.appendChild(makeJavaRow({
    key: '',
    title: '自动选择',
    subtitle: '用优先度最高的那个（列表第一项，且要满足版本要求）',
    chosen: state.javaChoice === ''
  }));

  list.forEach((java, index) => {
    const actions = document.createElement('div');
    actions.className = 'java-actions';

    const up = document.createElement('button');
    up.className = 'icon-btn';
    up.type = 'button';
    up.textContent = '↑';
    up.title = '提高优先级';
    up.disabled = index === 0;
    up.addEventListener('click', (event) => {
      event.preventDefault();
      moveJava(java.home, -1);
    });

    const down = document.createElement('button');
    down.className = 'icon-btn';
    down.type = 'button';
    down.textContent = '↓';
    down.title = '降低优先级';
    down.disabled = index === list.length - 1;
    down.addEventListener('click', (event) => {
      event.preventDefault();
      moveJava(java.home, 1);
    });

    const remove = document.createElement('button');
    remove.className = 'icon-btn';
    remove.type = 'button';
    remove.textContent = '✕';
    remove.title = '从列表中移出（重新扫描会再次出现）';
    remove.addEventListener('click', (event) => {
      event.preventDefault();
      state.javaHidden.add(java.home);
      renderJavaList();
    });

    const open = document.createElement('button');
    open.className = 'icon-btn';
    open.type = 'button';
    open.textContent = '打开';
    open.title = '打开所在文件夹';
    open.addEventListener('click', (event) => {
      event.preventDefault();
      send('shell.openFolder', { path: java.home }).catch((error) => {
        setStatus('java-status', `打不开：${error.message}`, true);
      });
    });

    actions.append(up, down, remove, open);

    host.appendChild(makeJavaRow({
      key: java.home,
      title: `Java ${java.version} · ${java.arch}`,
      subtitle: java.home,
      actions,
      chosen: state.javaChoice === java.home
    }));
  });

  host.appendChild(makeJavaRow({
    key: '__manual__',
    title: '手动填写目录…',
    subtitle: state.javaChoice === '__manual__' ? '' : '本机没扫到时用这个',
    chosen: state.javaChoice === '__manual__'
  }));
}

/// 上下移动一个 Java，并把新顺序存进设置。
async function moveJava(home, delta) {
  const list = [...visibleJava()];
  const index = list.findIndex((java) => java.home === home);
  const target = index + delta;

  if (index < 0 || target < 0 || target >= list.length) {
    return;
  }

  [list[index], list[target]] = [list[target], list[index]];

  // 被移出的排在最后：不显示，但顺序信息留着
  const hidden = (state.javaAll ?? []).filter((java) => state.javaHidden.has(java.home));
  state.javaAll = [...list, ...hidden];

  renderJavaList();

  try {
    const result = await send('settings.update', { javaOrder: list.map((java) => java.home) });
    state.settings = result.settings;
  } catch (error) {
    setStatus('java-status', `顺序没能保存：${error.message}`, true);
  }
}

// ── 插件 ──────────────────────────────────────────────
//
// 插件真的会被加载：宿主启动时扫一遍目录，这里显示的是加载结果，不是"磁盘上有什么"。
// 停用会写进 plugins/state.json，下次启动仍然停用。

const PLUGIN_STATE = {
  enabled: ['已启用', 'ok'],
  disabled: ['已停用', ''],
  'needs-consent': ['待确认', 'warn'],
  error: ['加载失败', 'bad'],
  rejected: ['被拒绝', 'bad'],
  invalid: ['清单错误', 'bad'],
  pending: ['加载中', ''],
  ready: ['已发现', 'ok']
};

async function loadPlugins() {
  try {
    const payload = await send('plugin.list');
    applyPlugins(payload);
  } catch (error) {
    state.plugins = [];
    renderPlugins(null);
    setStatus('plugins-status', `读取插件失败：${error.message}`, true);
  }
}

// ── 插件界面注入 ──────────────────────────────────────
//
// 插件提供的是 HTML 片段 + 可选脚本，宿主把它们挂到预设插槽里。
// 脚本在同页面上下文执行，能碰到整个页面——所以它和 ui:page 权限、
// 以及"来源可信"这件事绑在一起。这不是沙箱，是契约。

const PLUGIN_UI_API = {
  notice: (title, text) => showNotice(title, text ?? '', 'info'),
  // 跳到某个页面：自己的整页（'plugin:<id>'）、首页（'home'）、实例页（'instances'）都行。
  // 工具栏按钮点一下切到自己的页面，靠的就是它。
  navigate: (view) => switchTo(view),
  // 启动层专用：插件验完自己的东西后调它收掉这一层
  dismissStartup: () => {
    document.querySelectorAll('.plugin-startup').forEach((layer) => layer.remove());
  },
  status: (text) => setStatus('plugins-status', text ?? '')
};

/// 被插件顶掉的宿主控件。每次重挂界面时先全部还回去，再按插件的新声明重新隐藏——
/// 这样"停用插件 → 宿主恢复原状"是结构性的，不靠插件自觉。
const replacedByPlugins = [];

async function loadPluginUi(includeStartup = false) {
  // 先还原上一轮被顶掉的控件
  for (const control of replacedByPlugins) {
    control.hidden = false;
  }

  replacedByPlugins.length = 0;

  let items = [];

  try {
    items = (await send('plugin.ui')).ui ?? [];
  } catch (error) {
    console.error('[plugin.ui]', error);
    return;
  }

  state.pluginUiItems = items;

  for (const slot of document.querySelectorAll('.plugin-slot')) {
    slot.replaceChildren();
  }

  // 整页先拆掉上一次的（插件停用/换版本时要能收回去）
  $('rail-plugin-pages').replaceChildren();

  for (const old of document.querySelectorAll('.panel[data-plugin-page]')) {
    old.remove();
  }

  state.pluginPages = [];

  // 启动层只在真启动时挂：运行中启停插件如果也挂，用户正用着会被突然盖住
  if (includeStartup) {
    const startups = items.filter((item) => item.slot === 'startup');

    if (startups.length > 0) {
      mountStartup(startups[0]);

      if (startups.length > 1) {
        console.warn(`[plugin.startup] ${startups.length} 个插件都想占用启动层，只挂了第一个（${startups[0].pluginId}）`);
      }
    }
  }

  for (const item of items) {
    if (item.slot === 'startup') {
      continue;
    }

    if (item.slot === 'page') {
      mountPluginPage(item);
      continue;
    }

    const slot = document.querySelector(`.plugin-slot[data-slot="${item.slot}"]`);
    if (!slot) {
      continue;
    }

    const panel = document.createElement('div');
    panel.className = 'plugin-panel';
    panel.dataset.plugin = item.pluginId;
    panel.innerHTML = item.html ?? '';
    slot.appendChild(panel);

    if (item.script) {
      runPluginUiScript(item, panel);
    }

    // 插件声明要顶掉哪个宿主控件（比如「新建实例」），宿主自己动手藏，
    // 并且记下来，下次重挂时还原。
    for (const id of item.replaces ?? []) {
      const control = document.getElementById(id);

      if (control) {
        control.hidden = true;
        replacedByPlugins.push(control);
      }
    }
  }
}

// 整页兜底图标：插件没给 icon 时用它，至少不是空白按钮
const PLUGIN_PAGE_ICON = '<svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">'
  + '<path d="M10 3.5h4v2.2a2 2 0 1 0 0 4v.1h2.2v4h-2.2a2 2 0 1 1-4 0H7.8v-4H10a2 2 0 1 0 0-4V3.5z"'
  + ' fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/></svg>';

/// 启动层：铺满窗口的一块，插件自己决定画什么（动画、密码页），验证完自己移除。
/// 宿主不管里面的逻辑——密码怎么存、怎么校验，都是插件的事。
function mountStartup(item) {
  document.querySelectorAll('.plugin-startup').forEach((old) => old.remove());

  const layer = document.createElement('div');
  layer.className = 'plugin-startup';
  layer.dataset.plugin = item.pluginId;
  layer.innerHTML = item.html ?? '';

  document.body.appendChild(layer);

  if (item.script) {
    runPluginUiScript(item, layer);
  }

  // 插件验完密码后调 MLPlugin.dismissStartup() 把它收掉
  return layer;
}

/// 插件整页：侧栏加一个入口，正文是一整块面板。
/// 下载器、市场这类插件需要的是"一整页"，不是工具栏上的一个按钮。
function mountPluginPage(item) {
  const index = state.pluginPages.filter((p) => p.id === item.pluginId).length;
  const view = index === 0 ? `plugin:${item.pluginId}` : `plugin:${item.pluginId}:${index}`;
  const title = item.title ?? item.pluginId;

  const section = document.createElement('section');
  section.className = 'panel';
  section.id = `panel-${view}`;
  section.hidden = true;
  section.dataset.pluginPage = item.pluginId;

  const host = document.createElement('div');
  host.className = 'plugin-page';
  host.dataset.plugin = item.pluginId;
  host.innerHTML = item.html ?? '';
  section.appendChild(host);

  document.querySelector('.scroll').appendChild(section);

  const button = document.createElement('button');
  button.className = 'rail-btn';
  button.dataset.nav = view;
  button.title = title;

  if (item.icon) {
    const img = document.createElement('img');
    img.className = 'rail-icon';
    img.src = item.icon;
    img.alt = '';
    button.appendChild(img);
  } else {
    button.innerHTML = PLUGIN_PAGE_ICON;
  }

  const label = document.createElement('span');
  label.className = 'rail-label';
  label.textContent = title;
  button.appendChild(label);

  $('rail-plugin-pages').appendChild(button);

  state.pluginPages.push({ id: item.pluginId, view, title });

  if (item.script) {
    runPluginUiScript(item, host);
  }
}

function runPluginUiScript(item, root, extra = {}) {
  const api = {
    id: item.pluginId,
    root,
    ...PLUGIN_UI_API,
    // 调插件自己的代码：插件的 C# 侧实现 IPluginCommand 才有的接
    call: async (command, payload) => {
      const response = await send('plugin.command', {
        id: item.pluginId,
        command,
        payload: payload ?? null
      });

      return response.result;
    },
    ...extra
  };

  try {
    // 只传这个对象进去；不给 window、不给 document 的引用。
    // 真想绕过去拦不住（同上下文），但至少默认路径是干净的。
    new Function('MLPlugin', item.script)(api);
  } catch (error) {
    console.error(`插件 ${item.pluginId} 的界面脚本出错`, error);
    setStatus('plugins-status', `插件 ${item.pluginId} 的界面脚本出错：${error.message}`, true);
  }
}

function applyPlugins(payload) {
  state.plugins = payload.plugins ?? [];
  renderPlugins(payload.root);

  const enabled = state.plugins.filter((p) => p.state === 'enabled').length;
  const broken = state.plugins.filter((p) => p.state === 'error' || p.state === 'rejected' || p.state === 'invalid').length;

  setStatus('plugins-status', state.plugins.length
    ? `${state.plugins.length} 个插件 · 已加载 ${enabled}${broken > 0 ? ` · ${broken} 个有问题` : ''}`
    : '', broken > 0);

  // 插件文件变过：热加载虽然生效了，但旧实例的状态不一定收干净，提醒重启一次
  const changed = payload.changed ?? [];
  const restartBox = $('plugins-restart');

  if (restartBox) {
    restartBox.hidden = !payload.restartSuggested;

    if (payload.restartSuggested) {
      $('plugins-restart-text').textContent = changed.length
        ? `插件文件变过（${changed.join('、')}）。新代码已经加载，但旧实例的状态不一定收干净，重启一次最稳。`
        : '插件文件变过。新代码已经加载，但旧实例的状态不一定收干净，重启一次最稳。';
    }
  }

  // 启停/授权都会改变"哪些插件在跑"，界面片段跟着重挂。
  loadPluginUi();
}

/// 官方插件用 ml. 前缀的 id 区分。写在清单里靠 id 判断，比再加一个字段省事，
/// 而且"官方"这件事本来就是发行方自己说了算的。
function isOfficialPlugin(plugin) {
  return String(plugin.id ?? '').toLowerCase().startsWith('ml.');
}

for (const button of document.querySelectorAll('.seg-btn[data-plugin-source]')) {
  button.addEventListener('click', () => {
    state.pluginSource = button.dataset.pluginSource;
    renderPlugins(null);
  });
}

function renderPlugins(root) {
  if (root !== null && root !== undefined) {
    state.pluginsRoot = root;
  }

  const host = $('plugins');

  for (const button of document.querySelectorAll('.seg-btn[data-plugin-source]')) {
    const active = button.dataset.pluginSource === state.pluginSource;
    button.classList.toggle('active', active);
    button.setAttribute('aria-selected', active ? 'true' : 'false');
  }

  const list = state.plugins.filter((plugin) =>
    (state.pluginSource === 'official') === isOfficialPlugin(plugin));

  host.innerHTML = '';

  if (!list.length) {
    const what = state.pluginSource === 'official' ? '官方插件' : '第三方插件';

    host.appendChild(makeEmpty(
      state.plugins.length ? `没有${what}` : '还没有插件',
      state.plugins.length
        ? '换个筛选看看，或者把插件目录放进来。'
        : `每个插件是一个目录，里面放 manifest.json。插件目录：${state.pluginsRoot ?? '数据目录下的 plugins/'}`,
      ICON_CUBE));
    return;
  }

  for (const plugin of list) {
    const row = document.createElement('div');
    row.className = 'row';
    row.dataset.plugin = plugin.id;

    const grow = document.createElement('div');
    grow.className = 'grow';

    const line = document.createElement('div');
    line.className = 'line';

    const name = document.createElement('div');
    name.className = 'id';
    name.textContent = plugin.name ?? plugin.id;
    name.title = plugin.directory ?? plugin.id;

    const [stateText, stateTone] = PLUGIN_STATE[plugin.state] ?? [plugin.state, ''];
    const badge = document.createElement('span');
    badge.className = stateTone ? `badge ${stateTone}` : 'badge';
    badge.textContent = stateText;

    line.append(name, badge);

    const meta = document.createElement('div');
    meta.className = 'meta';
    meta.textContent = [
      plugin.version,
      plugin.type === 'declarative' ? '声明式' : '程序集',
      plugin.permissions?.length ? `权限 ${plugin.permissions.length} 项` : null
    ].filter(Boolean).join(' · ');

    grow.append(line, meta);

    // 出错就把原因摊开写，不要只给一个红点让人猜
    if (plugin.error) {
      const error = document.createElement('div');
      error.className = 'plugin-error';
      error.textContent = plugin.error;
      grow.append(error);
    }

    // 启停用开关表达，不用「停用/启用」两个字的按钮：
    // 开关一眼能看出"现在是什么状态"，文字按钮得先读一遍才知道当前是开还是关。
    const toggle = document.createElement('label');
    toggle.className = 'switch';
    toggle.title = plugin.enabled ? '已启用，点一下停用' : '已停用，点一下启用';

    const toggleInput = document.createElement('input');
    toggleInput.type = 'checkbox';
    toggleInput.checked = plugin.enabled;

    // 清单不合法 / 依赖不满足时没有启停可言：先修东西，再点「重新扫描」。
    const locked = plugin.state === 'invalid' || plugin.state === 'rejected';
    toggleInput.disabled = locked;
    toggleInput.addEventListener('change', () => togglePlugin(plugin));

    toggle.appendChild(toggleInput);

    // 整行可点：点开是"这个插件自己的设置"。里面的按钮不触发（下面判断了 closest）
    row.addEventListener('click', (event) => {
      if (event.target.closest('button, input, label')) {
        return;
      }

      openPluginDetail(plugin);
    });

    // 头像优先用插件自己在清单里给的图；没给才用名字首字拼一个方块
    row.append(plugin.icon ? makePluginIcon(plugin.icon, plugin.name ?? plugin.id) : makeTile(plugin.name ?? plugin.id, null), grow);

    // 待确认：主按钮是「允许…」，副按钮仍然是停用
    if (plugin.state === 'needs-consent') {
      const allow = document.createElement('button');
      allow.className = 'act primary';
      allow.textContent = '允许…';
      allow.addEventListener('click', () => askPluginPermission(plugin));
      row.append(allow);
    }

    row.append(toggle);
    host.appendChild(row);
  }
}

// 权限的中文说法：直接给一串 launch:jvm-args 没人看得懂它到底允许什么
const PERMISSION_TEXT = {
  'launch:jvm-args': '向启动参数里加 JVM 参数（能加载 -javaagent 这类会执行代码的插件）',
  'launch:game-args': '向启动命令里加游戏参数',
  'network:https': '联网（尚未实现）',
  'fs:instance': '读写实例目录（尚未实现）',
  'account:token': '使用你的账号令牌（尚未实现）',
  'ui:page': '在启动器界面里挂面板（面板里可以带一段页面脚本）'
};

function askPluginPermission(plugin) {
  const missing = plugin.missingPermissions ?? [];
  const lines = missing.map((p) => `· ${PERMISSION_TEXT[p] ?? p}`).join('\n');

  askConfirm(
    '允许插件使用权限？',
    `插件「${plugin.name ?? plugin.id}」想使用这些权限：\n\n${lines}\n\n允许后它会立刻被加载。来源不明的插件请不要放行。`,
    '允许',
    async () => {
      // 其它确认框都是回调里自己关（删除实例/删除账号），这里保持一致
      closeConfirm();

      try {
        applyPlugins(await send('plugin.grant', { id: plugin.id, permissions: missing }));
      } catch (error) {
        setStatus('plugins-status', `允许失败：${error.message}`, true);
      }
    },
    'primary');
}

// ── 插件详情 / 设置 ────────────────────────────────────
//
// 插件页里点一行 → 打开这个插件的详情：基本信息、权限、以及插件自己画的设置界面。
// 设置界面是插件提供的 HTML 片段（slot=settings），值存在 plugin-data/<id>/settings.json，
// 界面和插件代码读写同一份。

async function openPluginDetail(plugin) {
  state.detailPlugin = plugin;

  $('detail-title').textContent = plugin.name ?? plugin.id;

  const [stateText] = PLUGIN_STATE[plugin.state] ?? [plugin.state];
  $('detail-meta').textContent = [
    plugin.id,
    plugin.version ? `v${plugin.version}` : null,
    plugin.type === 'declarative' ? '声明式（无程序集）' : '程序集',
    stateText
  ].filter(Boolean).join(' · ');

  $('detail-perms').textContent = plugin.permissions?.length
    ? '权限：\n' + plugin.permissions.map((p) => `· ${PERMISSION_TEXT[p] ?? p}`).join('\n')
    : '没有声明任何权限。';

  const box = $('detail-settings');
  box.replaceChildren();

  const fragment = (state.pluginUiItems ?? [])
    .find((item) => item.slot === 'settings' && item.pluginId === plugin.id);

  if (!fragment) {
    box.textContent = '这个插件没有提供设置界面。';
  } else {
    let values = {};

    try {
      values = (await send('plugin.settings.get', { id: plugin.id })).settings ?? {};
    } catch (error) {
      box.textContent = `读设置失败：${error.message}`;
    }

    const panel = document.createElement('div');
    panel.className = 'plugin-panel';
    panel.innerHTML = fragment.html ?? '';
    box.appendChild(panel);

    if (fragment.script) {
      runPluginUiScript(fragment, panel, {
        settings: {
          values,
          set: (key, value) => send('plugin.settings.set', { id: plugin.id, key, value })
        }
      });
    }
  }

  $('detail-toggle').textContent = plugin.enabled ? '停用' : '启用';
  $('plugin-detail').hidden = false;
}

function closePluginDetail() {
  $('plugin-detail').hidden = true;
  state.detailPlugin = null;
}

async function togglePlugin(plugin) {
  try {
    const payload = await send('plugin.toggle', { id: plugin.id, enabled: !plugin.enabled });
    applyPlugins(payload);
  } catch (error) {
    setStatus('plugins-status', `切换失败：${error.message}`, true);
  }
}

function openAddPlugin(kind) {
  $('plugins-add-menu').hidden = true;
  $('add-plugin-kind').value = kind ?? 'folder';
  $('add-plugin-source').value = '';
  setStatus('add-plugin-status', '');
  $('add-plugin').hidden = false;
  $('add-plugin-source').focus();
}

function closeAddPlugin() {
  $('add-plugin').hidden = true;
}

async function installPlugin() {
  const source = $('add-plugin-source').value.trim();
  const kind = $('add-plugin-kind').value;

  if (!source) {
    setStatus('add-plugin-status', '先填路径或地址。', true);
    return;
  }

  setStatus('add-plugin-status', kind === 'github' ? '正在从 GitHub 下载…' : '正在安装…');

  try {
    const payload = await send('plugin.install', { source, kind });
    closeAddPlugin();
    applyPlugins(payload);
    setStatus('plugins-status', `已安装插件 ${payload.id}。`, false);
  } catch (error) {
    setStatus('add-plugin-status', error.message, true);
  }
}

$('plugins-add').addEventListener('click', () => openAddPlugin('folder'));

$('plugins-add-more').addEventListener('click', (event) => {
  event.stopPropagation();
  const menu = $('plugins-add-menu');
  menu.hidden = !menu.hidden;
});

for (const item of document.querySelectorAll('#plugins-add-menu button')) {
  item.addEventListener('click', async () => {
    const kind = item.dataset.add;
    $('plugins-add-menu').hidden = true;

    if (kind === 'open') {
      try {
        await send('shell.openFolder', {});
      } catch (error) {
        setStatus('plugins-status', error.message, true);
      }
      return;
    }

    openAddPlugin(kind);
  });
}

/// 关掉所有下拉菜单。点别处、或者菜单之间互斥时都用它。
function closeAllMenus() {
  for (const menu of document.querySelectorAll('.menu')) {
    menu.hidden = true;
  }
}

document.addEventListener('click', (event) => {
  // 点在菜单自己（或触发它的按钮）上不关，其它一律收起来
  if (!event.target.closest('.menu') && !event.target.closest('.split')) {
    closeAllMenus();
  }
});

$('add-plugin-install').addEventListener('click', installPlugin);
$('add-plugin-close').addEventListener('click', closeAddPlugin);
$('add-plugin-source').addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    installPlugin();
  }
});

async function reloadPlugins() {
  setStatus('plugins-status', '正在重新扫描…');

  try {
    applyPlugins(await send('plugin.reload'));
  } catch (error) {
    setStatus('plugins-status', `重新扫描失败：${error.message}`, true);
  }
}

// ── 账号簿 ────────────────────────────────────────────
//
// 正版与离线在同一个列表里，一排排并列；启动时按实例上选中的 id 取一个。
// 登录与令牌全在宿主（内核零网络），这里只管显示、添加、选择。

function renderAccounts(payload) {
  if (!payload) {
    return;
  }

  state.accounts = payload.accounts ?? [];
  state.lastAccountId = payload.lastAccountId ?? null;

  if ($('home-account')) {
    renderHome();
  }
  state.accountClientConfigured = payload.clientIdConfigured === true;
  state.accountClientSource = payload.clientIdSource ?? 'none';

  // 第一次进来时如果正版一个都没有、离线却有，就直接落在离线那一栏，
  // 别让人对着一个空列表以为账号丢了。
  if (!state.accountFilterTouched
      && !state.accounts.some((account) => account.kind === 'msa')
      && state.accounts.some((account) => account.kind !== 'msa')) {
    state.accountFilter = 'offline';
  }

  renderAccountList();
}

/// 只画筛选出来的那一类。正版和离线共用同样的行，所以切换时不用重建数据。
function renderAccountList() {
  const kind = state.accountFilter;

  // 只动账号自己的分段。以前这里是全局 .seg-btn，插件页的分段也被一起重置，
  // 结果是点插件页的按钮时三个全亮、筛选看着像没生效。
  for (const button of document.querySelectorAll('.seg-btn[data-account-kind]')) {
    const active = button.dataset.accountKind === kind;
    button.classList.toggle('active', active);
    button.setAttribute('aria-selected', active ? 'true' : 'false');
  }

  // 每个筛选后面只跟一个「添加」动作，避免两个按钮同时摆着让人犹豫点哪个。
  $('account-login').hidden = kind !== 'msa';
  $('account-add-offline').hidden = kind !== 'offline';

  const list = state.accounts.filter((account) => (kind === 'msa') === (account.kind === 'msa'));
  const host = $('accounts');
  host.innerHTML = '';

  if (list.length === 0) {
    const box = document.createElement('div');
    box.className = 'empty compact';
    box.textContent = kind === 'msa'
      ? '还没有登录正版账号。点右上角「登录正版账号」。'
      : '还没有离线账号。点右上角「添加离线账号」。';
    host.appendChild(box);
    return;
  }

  for (const account of list) {
    host.appendChild(makeAccountRow(account));
  }
}

function switchAccountFilter(kind) {
  state.accountFilter = kind;
  state.accountFilterTouched = true;
  setStatus('account-status', '');
  renderAccountList();
}

for (const button of document.querySelectorAll('.seg-btn[data-account-kind]')) {
  button.addEventListener('click', () => switchAccountFilter(button.dataset.accountKind));
}

function makeAccountRow(account) {
  const row = document.createElement('div');
  row.className = 'row';
  row.dataset.account = account.id;

  const tile = makeAvatar();

  const main = document.createElement('div');
  main.className = 'account-main';

  const line = document.createElement('div');
  line.className = 'line';

  const name = document.createElement('div');
  name.className = 'id';
  name.textContent = account.name;
  name.title = account.name;

  const badge = document.createElement('span');
  badge.className = account.kind === 'msa' ? 'badge ok' : 'badge';
  badge.textContent = account.kind === 'msa' ? '正版' : '离线';

  line.append(name, badge);

  const meta = document.createElement('div');
  meta.className = 'meta mono';
  meta.textContent = `UUID ${account.uuid ?? '—'}`;
  meta.title = meta.textContent;

  main.append(line, meta);

  const remove = document.createElement('button');
  remove.className = 'act danger';
  remove.textContent = '删除';
  remove.addEventListener('click', () => removeAccount(account));

  row.append(tile, main, remove);
  return row;
}

async function loadAccounts() {
  try {
    renderAccounts(await send('account.list'));
  } catch (error) {
    setStatus('account-status', `读取账号失败：${error.message}`, true);
  }
}

// 登录是一个弹窗，不是页面上常驻的一条表单：
// 不登录的人永远不该看见「微软应用 ID」和设备码这两个概念。
function openLoginBox() {
  state.currentCode = false;
  showDeviceCode(null);

  setStatus('login-status', state.accountClientConfigured
    ? '点「开始登录」拿一个设备码，然后在浏览器里输。'
    : '这个构建没有配置登录应用，正版登录不可用。');
  $('account-login-box').hidden = false;
  $('login-start').focus();
}

function closeLoginBox() {
  $('account-login-box').hidden = true;
  state.currentCode = false;
  showDeviceCode(null);
}

function showDeviceCode(info) {
  $('account-device').hidden = !info;
  state.currentCode = !!info;

  // 有码之后主要动作变成「重新打开网页 / 复制代码 / 取消」，
  // 「开始登录」和「关闭」就该让位，别摆一排按钮让人挑。
  $('login-start').hidden = !!info;
  $('login-close').hidden = !!info;
  $('account-cancel').hidden = !info;
  $('login-open').hidden = !info;
  $('login-copy').hidden = !info;

  if (!info) {
    return;
  }

  $('device-uri').textContent = info.verificationUri;
  $('device-code').textContent = info.userCode;

  // 复制成功没成功要如实说：之前文案写死"已复制"，等于对着用户撒谎。
  applyCopyState(info.copied);
}

/// copied: true 成功 / false 失败 / undefined 未知（比如事件里没带）
function applyCopyState(copied) {
  const label = $('device-copy-state');

  if (copied === true) {
    label.textContent = '已复制到剪贴板，直接粘贴即可。';
    return;
  }

  if (copied === false) {
    label.textContent = '自动复制没成功，请点下面「复制代码」，或者手动抄写这个码。';
    return;
  }

  label.textContent = '在下面选中这个码，或点「复制代码」。';
}

async function startAccountLogin() {
  // 应用 ID 由程序内置，界面不提供修改入口：所有玩家都用同一个应用身份，
  // 授权页上显示的也永远是同一个名字。
  if (!state.accountClientConfigured) {
    setStatus('login-status', '这个构建没有配置登录应用，正版登录不可用。', true);
    return;
  }

  setStatus('login-status', '正在向微软申请设备码…');

  try {
    const result = await send('account.login.start');
    showDeviceCode(result);

    setStatus('login-status', result.browserOpened
      ? '等你在网页里完成授权，这里会自动登录。'
      : '没能自动打开浏览器，请手动访问上面那个地址。', !result.browserOpened);
  } catch (error) {
    showDeviceCode(null);
    setStatus('login-status', error.message, true);

    // 拿设备码就失败（网络、应用配置、微软 400），同样弹出来。
    showLoginFailure(error.message);
  }
}

async function openLoginPage() {
  try {
    const result = await send('account.login.open');
    setStatus('login-status', result.ok ? '已重新打开登录网页。' : '打不开浏览器，请手动访问上面的地址。', !result.ok);
  } catch (error) {
    setStatus('login-status', error.message, true);
  }
}

async function copyLoginCode() {
  try {
    const result = await send('account.login.copy');
    setStatus('login-status', result.ok ? '已复制到剪贴板。' : (result.error ?? '复制失败。'), !result.ok);
    applyCopyState(result.ok === true);
  } catch (error) {
    setStatus('login-status', error.message, true);
  }
}

async function cancelAccountLogin() {
  try {
    await send('account.login.cancel');
    setStatus('login-status', '已取消登录。');
  } catch (error) {
    setStatus('login-status', error.message, true);
  } finally {
    showDeviceCode(null);
  }
}

// ── 添加 / 删除离线账号 ────────────────────────────────

function openAddOffline() {
  $('account-new-name').value = '';
  setStatus('account-add-status', '');
  $('account-add').hidden = false;
  $('account-new-name').focus();
}

function closeAddOffline() {
  $('account-add').hidden = true;
}

async function submitAddOffline() {
  const name = $('account-new-name').value.trim();

  if (!name) {
    setStatus('account-add-status', '名字不能为空。', true);
    return;
  }

  try {
    const result = await send('account.add.offline', { name });
    closeAddOffline();

    // 刚加的一定是离线账号，直接切到那一栏让它出现在眼前。
    state.accountFilter = 'offline';
    state.accountFilterTouched = true;

    renderAccounts({
      accounts: result.accounts,
      lastAccountId: state.lastAccountId,
      clientIdConfigured: state.accountClientConfigured
    });
    setStatus('account-status', `已添加离线账号「${name}」。去实例页给它选一个实例。`);
  } catch (error) {
    setStatus('account-add-status', `添加失败：${error.message}`, true);
  }
}

function removeAccount(account) {
  askConfirm(
    '删除账号',
    `「${account.name}」会从账号簿里移除${account.kind === 'msa' ? '，本机的令牌一并删掉（要重新登录才能再用）' : ''}。选过它的实例会变成「不指定」。`,
    '删除',
    async () => {
      closeConfirm();

      try {
        await send('account.remove', { id: account.id });
        await loadAccounts();
        await loadInstances(true);
        setStatus('account-status', `已删除「${account.name}」。`);
      } catch (error) {
        setStatus('account-status', `删除失败：${error.message}`, true);
      }
    });
}

// ── 给实例选账号 ──────────────────────────────────────
//
// 一个实例可以绑定一个账号；不绑就走「最近使用」，账号簿空的就退回实例玩家名。

function openAccountPicker(instanceName) {
  const instance = state.instances.find((item) => item.name === instanceName);
  if (!instance) {
    return;
  }

  state.picking = instanceName;
  $('account-pick-subtitle').textContent =
    `「${instanceName}」以后用哪个账号启动。不指定时按最近使用的账号；账号簿为空就用实例玩家名。`;

  const list = $('account-pick-list');
  list.innerHTML = '';

  list.appendChild(makePickRow({
    id: '',
    name: '不指定（跟随最近使用的账号）',
    kind: 'none'
  }, instance.accountId ?? '', () => applyAccountPick('')));

  for (const account of state.accounts) {
    list.appendChild(makePickRow(account, instance.accountId ?? '', () => applyAccountPick(account.id)));
  }

  $('account-pick').hidden = false;
}

function makePickRow(account, currentId, onPick) {
  const row = document.createElement('button');
  row.className = 'pick-row';
  row.type = 'button';

  if ((account.id || '') === (currentId || '')) {
    row.classList.add('current');
  }

  if (account.kind === 'none') {
    const placeholder = document.createElement('div');
    placeholder.className = 'tile';
    placeholder.style.setProperty('--h', '220');
    row.appendChild(placeholder);
  } else {
    row.appendChild(makeAvatar());
  }

  const grow = document.createElement('div');
  grow.className = 'grow';

  const name = document.createElement('div');
  name.className = 'name';
  name.textContent = account.name;

  const meta = document.createElement('div');
  meta.className = 'meta';
  meta.textContent = account.kind === 'msa' ? '正版账号' : account.kind === 'offline' ? '离线账号' : '';

  grow.append(name, meta);
  row.appendChild(grow);

  row.addEventListener('click', onPick);
  return row;
}

async function applyAccountPick(accountId) {
  const name = state.picking;

  try {
    await send('instance.update', { name, accountId });
    $('account-pick').hidden = true;
    state.picking = null;
    await loadInstances(true);
    setStatus('instances-status', accountId ? '已给这个实例绑定账号。' : '这个实例不再指定账号。');
  } catch (error) {
    setStatus('instances-status', `设置账号失败：${error.message}`, true);
  }
}

function closeAccountPicker() {
  $('account-pick').hidden = true;
  state.picking = null;
}

// ── 首次启动 ──────────────────────────────────────────
//
// 只在「还没指定游戏目录」时出现。不做成向导、不强制走完：
// 用户可以直接忽略它去看设置页，也可以粘一个路径。

function showFirstRun() {
  $('first-run').hidden = false;
  detectGameRoot();
}

function hideFirstRun() {
  $('first-run').hidden = true;
}

// ── 事件 ──────────────────────────────────────────────

function handleEvent(name, data) {
  switch (name) {

    case 'launch.done': {
      // 只清这一个实例的状态：别的实例可能还在跑。
      if (data.instance) {
        state.running = state.running.filter((item) => item !== data.instance);
      } else {
        state.running = [];
      }

      const failed = data.ok === false;
      const message = failed
        ? `启动失败：${data.error}`
        : data.diagnosis === 'Killed'
          ? `已停止 · ${data.seconds}s`
          : `${data.exitCode === 0 ? '已退出' : '异常退出'} · 退出码 ${data.exitCode} · ${data.diagnosis} · ${data.seconds}s`;

      // 先重拉实例列表把按钮恢复，再把结果写进状态行——反过来会被 loadInstances 覆盖掉。
      loadInstances(true).finally(() => setStatus('instances-status', message, failed));
      break;
    }

    case 'update.progress':
      setStatus('update-status', '下载中 ' + data.percent + '%');
      break;

    case 'account.devicecode':
      showDeviceCode(data);
      setStatus('login-status', '等你在浏览器里完成授权…');
      break;

    case 'account.changed':
      closeLoginBox();

      if (data.added) {
        // 刚登录的是正版账号，切回正版那一栏。
        state.accountFilter = 'msa';
        state.accountFilterTouched = true;
      }

      renderAccounts(data);

      if (data.added) {
        showNotice('登录成功',
          `账号「${data.added.name}」已经加进列表了。\n\n去实例页点「账号 · …」就能给它绑定一个实例。`,
          'ok');
      }

      setStatus('account-status', data.added ? `已登录：${data.added.name}。` : '账号列表已更新。');
      break;

    case 'account.login.failed':
      showDeviceCode(null);
      setStatus('login-status', data.error, true);

      // 失败必须弹出来：状态行在弹窗底部，等于没说。
      showLoginFailure(data.error);
      break;

    case 'download.changed':
      renderDownloadTasks(data.tasks);
      break;

    case 'settings.changed':
      state.settings = data.settings;
      fillSettings(data.settings);
      break;

    default:
      // 日志类事件（log）只投给日志窗口；这里静默忽略。
      break;
  }
}

// ── 导航 ──────────────────────────────────────────────

const titles = {
  home: ['启动', '选好实例和账号，点一下就开始。'],
  instances: ['实例', '一个实例就是一套独立的存档、模组和设置。'],
  downloads: ['下载', '下载队列。一个任务可以包含多个文件，比如「版本 + 加载器」。'],
  plugins: ['插件', '数据目录 plugins/ 下的插件。启停立刻生效，停用会被记住。'],
  account: ['账号', '正版与离线账号都在这儿；启动用哪个，去实例行上选。'],
  settings: ['设置', 'Minecraft 目录、启动、下载、内存、Java、日志与关于。']
};

function switchTo(view) {
  for (const section of document.querySelectorAll('.panel')) {
    section.hidden = section.id !== `panel-${view}`;
  }

  for (const button of document.querySelectorAll('nav button')) {
    button.classList.toggle('active', button.dataset.nav === view);
  }

  // 页头右上角那块动作是插件页专用的，其它页不要占地方。
  $('plugin-actions').hidden = view !== 'plugins';

  // 保存条只属于设置页：它在滚动区外面，不跟着面板一起 hidden
  document.querySelector('.settings-footer').hidden = view !== 'settings';

  if (view === 'downloads') {
    loadDownloads();
  }

  // 内存条只在「设置 › 内存」这个子页有意义：进去才轮询，离开就停。
  if (view === 'settings' && !$('set-page-memory').hidden) {
    startMemoryPolling();
  } else {
    stopMemoryPolling();
  }

  // 插件页不在固定表里：从挂载时记下的清单里取，取不到也不能炸
  const pluginPage = state.pluginPages.find((page) => page.view === view);
  const [title, subtitle] = titles[view]
    ?? [pluginPage?.title ?? '插件页面', pluginPage ? `由插件 ${pluginPage.id} 提供` : ''];
  $('title').textContent = title;
  $('subtitle').textContent = subtitle;

  if (view === 'instances') {
    loadInstances();
  } else if (view === 'plugins') {
    loadPlugins();
  } else if (view === 'account') {
    loadAccounts();
  } else if (view === 'settings') {
    loadSettings();
    loadJavaList();
  }
}

// 事件委托：插件整页的入口是运行时才加进来的，逐个 addEventListener 绑不到它们。
document.querySelector('nav').addEventListener('click', (event) => {
  const button = event.target.closest('button[data-nav]');
  if (button) {
    switchTo(button.dataset.nav);
  }
});

$('open-new-instance').addEventListener('click', openNewInstance);
$('new-instance-cancel').addEventListener('click', closeNewInstance);
// ── 自绘标题栏 ────────────────────────────────────────
// 宿主把系统标题栏去掉了，这三个按钮和拖动只能由页面发起。
// 拖动不是自己算位移：交给宿主调系统的 HTCAPTION，贴边分屏、双击最大化才是系统行为。
function sendWindow(action) {
  window.chrome?.webview?.postMessage({ event: 'window', data: { action } });
}

for (const button of document.querySelectorAll('.win-btn')) {
  button.addEventListener('click', () => {
    const action = button.dataset.win;
    // 最大化按钮是"切换"：已经最大化了就还原
    // 关闭按钮不是真关进程，只是收进托盘（退出走托盘右键菜单）
    const mapped = action === 'maximize' ? 'toggle-maximize' : (action === 'close' ? 'hide' : action);

    sendWindow(mapped);
  });
}

// 双击标题栏空白处 = 最大化/还原
const titlebar = document.querySelector('.titlebar');

titlebar.addEventListener('dblclick', (event) => {
  if (!event.target.closest('.win-btn')) {
    sendWindow('toggle-maximize');
  }
});

titlebar.addEventListener('mousedown', (event) => {
  // 只处理左键，且按在按钮上时不拖
  if (event.button !== 0 || event.target.closest('.win-btn')) {
    return;
  }

  sendWindow('drag');
});

$('crash-open').addEventListener('click', async () => {
  try {
    await send('shell.openFolder', { path: $('crash-dir').value });
  } catch (error) {
    setStatus('crash-summary', `打不开：${error.message}`, true);
  }
});

$('home-launch').addEventListener('click', launchFromHome);
$('home-instance').addEventListener('change', updateHomeLaunch);

$('plugins-restart-go').addEventListener('click', async () => {
  try {
    await send('crash.restart', {});
  } catch (error) {
    setStatus('plugins-status', `重启失败：${error.message}`, true);
  }
});

$('crash-export').addEventListener('click', crashExport);
$('crash-restart').addEventListener('click', () => crashRestart({}));
$('crash-restart-without').addEventListener('click', () => crashRestart({ pluginId: crashReport?.activePlugin }));
$('crash-restart-safe').addEventListener('click', () => crashRestart({ all: true }));

$('download-new').addEventListener('click', openDownloadDialog);
$('download-new-cancel').addEventListener('click', closeDownloadDialog);
$('download-new-start').addEventListener('click', startDownload);
$('dl-threads').addEventListener('input', () => {
  $('dl-threads-value').textContent = $('dl-threads').value;
});
$('log-open').addEventListener('click', openLogDirectory);
$('log-clear').addEventListener('click', clearLogs);
$('game-root-add').addEventListener('click', addGameRoot);
$('game-root-note-save').addEventListener('click', saveGameRootNote);
$('game-root-note-cancel').addEventListener('click', closeGameRootNote);
$('create-instance').addEventListener('click', createInstance);
$('set-save').addEventListener('click', saveSettings);

for (const radio of document.querySelectorAll('input[name="mem-mode"]')) {
  radio.addEventListener('change', renderMemory);
}

$('mem-range').addEventListener('input', renderMemory);

// 设置页子导航：一次只显示一页
for (const tab of document.querySelectorAll('.set-tab')) {
  tab.addEventListener('click', () => {
    const target = tab.dataset.set;

    for (const other of document.querySelectorAll('.set-tab')) {
      other.classList.toggle('active', other === tab);
    }

    for (const page of document.querySelectorAll('.settings-page')) {
      page.hidden = page.id !== `set-page-${target}`;
    }

    if (target === 'memory') {
      startMemoryPolling();
    } else {
      stopMemoryPolling();
    }
  });
}

$('java-refresh').addEventListener('click', async () => {
  const button = $('java-refresh');
  const label = button.textContent;

  button.disabled = true;
  button.textContent = '扫描中…';
  setStatus('java-status', '正在重新扫描…', false);

  try {
    // 重新扫描 = 回到默认顺序（版本从高到低），把用户排过的优先级清掉
    const result = await send('settings.update', { javaOrder: [] });
    state.settings = result.settings;
    await loadJavaList();

    setStatus(
      'java-status',
      `已重新扫描：${(state.javaAll ?? []).length} 个 Java，顺序已重置为版本从高到低。`,
      false);
  } catch (error) {
    setStatus('java-status', `扫描失败：${error.message}`, true);
  } finally {
    button.disabled = false;
    button.textContent = label;
  }
});

// 手填框只服务「手动填写目录…」这一项，改内容时保持选中它
$('set-java').addEventListener('input', () => {
  state.javaChoice = '__manual__';
});

// 气泡提示：跟着鼠标走，靠近右/下边缘时翻到另一侧，避免出屏
let tipTarget = null;

function positionTip(event) {
  const tip = $('tip');
  const pad = 14;
  let x = event.clientX + pad;
  let y = event.clientY + pad;

  const rect = tip.getBoundingClientRect();
  if (x + rect.width > window.innerWidth - 8) {
    x = event.clientX - rect.width - pad;
  }

  if (y + rect.height > window.innerHeight - 8) {
    y = event.clientY - rect.height - pad;
  }

  tip.style.left = `${Math.max(8, x)}px`;
  tip.style.top = `${Math.max(8, y)}px`;
}

document.addEventListener('mouseover', (event) => {
  const target = event.target.closest('[data-tip]');
  if (!target || target === tipTarget) {
    return;
  }

  tipTarget = target;
  const tip = $('tip');
  tip.textContent = target.dataset.tip;
  tip.hidden = false;
  positionTip(event);
});

document.addEventListener('mousemove', (event) => {
  if (tipTarget) {
    positionTip(event);
  }
});

/// 收起气泡。滚动、点击、离开元素都会调它——只靠 mouseout 的话，
/// 元素被滚走或重新渲染时气泡会一直挂在那儿。
function hideTip() {
  if (tipTarget === null) {
    return;
  }

  tipTarget = null;
  $('tip').hidden = true;
}

document.addEventListener('mouseout', (event) => {
  const target = event.target.closest('[data-tip]');
  if (target && target === tipTarget) {
    hideTip();
  }
});

document.addEventListener('scroll', hideTip, true);
document.addEventListener('click', hideTip, true);
// 仓库地址还没定：留空时按钮点下去只说明情况，不假装能跳转
const REPOSITORY_URL = '';

$('about-repo').addEventListener('click', async () => {
  if (!REPOSITORY_URL) {
    setStatus('update-status', '仓库地址还没配置。', false);
    return;
  }

  try {
    await send('shell.openUrl', { url: REPOSITORY_URL });
  } catch (error) {
    setStatus('update-status', `打不开：${error.message}`, true);
  }
});

$('update-check').addEventListener('click', () => checkUpdate(true));
$('update-apply').addEventListener('click', applyUpdate);
$('account-login').addEventListener('click', openLoginBox);
$('account-cancel').addEventListener('click', cancelAccountLogin);
$('login-start').addEventListener('click', startAccountLogin);
$('login-open').addEventListener('click', openLoginPage);
$('login-copy').addEventListener('click', copyLoginCode);
$('login-close').addEventListener('click', closeLoginBox);
$('account-add-offline').addEventListener('click', openAddOffline);
$('account-add-save').addEventListener('click', submitAddOffline);
$('account-add-cancel').addEventListener('click', closeAddOffline);
$('account-pick-cancel').addEventListener('click', closeAccountPicker);
$('edit-save').addEventListener('click', saveInstanceEditor);
$('edit-cancel').addEventListener('click', closeInstanceEditor);
$('confirm-yes').addEventListener('click', () => state.confirmYes?.());
$('confirm-no').addEventListener('click', closeConfirm);
$('notice-ok').addEventListener('click', closeNotice);
$('plugins-reload').addEventListener('click', reloadPlugins);
$('detail-close').addEventListener('click', closePluginDetail);
$('detail-folder').addEventListener('click', async () => {
  if (!state.detailPlugin?.directory) {
    return;
  }

  try {
    await send('shell.openFolder', { path: state.detailPlugin.directory });
  } catch (error) {
    setStatus('plugins-status', error.message, true);
  }
});
$('detail-toggle').addEventListener('click', async () => {
  const plugin = state.detailPlugin;
  if (!plugin) {
    return;
  }

  closePluginDetail();
  await togglePlugin(plugin);
});
$('notice-action').addEventListener('click', async () => {
  if (!state.noticeAction) {
    return;
  }

  try {
    await send('shell.open', { url: state.noticeAction });
  } catch (error) {
    setStatus('login-status', error.message, true);
  }
});

// Esc 关掉最上层的浮层。编辑框不许点背景关掉——那会一声不响丢掉刚填的东西。
addEventListener('keydown', (event) => {
  if (event.key !== 'Escape') {
    return;
  }

  if (!$('plugin-detail').hidden) {
    closePluginDetail();
  } else if (!$('add-plugin').hidden) {
    closeAddPlugin();
  } else if (!$('notice').hidden) {
    closeNotice();
  } else if (!$('confirm').hidden) {
    closeConfirm();
  } else if (!$('account-login-box').hidden) {
    closeLoginBox();
  } else if (!$('account-pick').hidden) {
    closeAccountPicker();
  } else if (!$('account-add').hidden) {
    closeAddOffline();
  } else if (!$('instance-edit').hidden) {
    closeInstanceEditor();
  }
});
$('first-run-use').addEventListener('click', () => {
  const path = $('first-run-path').value.trim();
  if (!path) {
    setStatus('first-run-status', '先粘一个路径，或者点上面某个候选目录。', true);
    return;
  }
  applyGameRoot(path);
});

// ── 更新 ──────────────────────────────────────────────

/**
 * 检查更新。**失败不弹窗**：更新源没配、服务器挂了、当前不是安装形态，
 * 都是日常状态。手动点的那次才把结论写进状态行，自动检查静默。
 */
async function checkUpdate(manual) {
  if (manual) {
    setStatus('update-status', '正在检查…');
  }

  try {
    const result = await send('update.check');
    state.update = result;
    $('about-version').textContent = result.currentVersion || '—';
    $('update-apply').hidden = !result.canApply;

    if (manual || result.available) {
      setStatus('update-status', result.message, false);
    }
  } catch (error) {
    if (manual) {
      setStatus('update-status', error.message, true);
    }
  }
}

async function applyUpdate() {
  $('update-apply').disabled = true;
  setStatus('update-status', '开始下载…');

  try {
    const result = await send('update.apply');
    setStatus('update-status', result.available
      ? `${result.message}　更新器会等本窗口关闭后替换文件并重启。`
      : result.message, false);
  } catch (error) {
    setStatus('update-status', error.message, true);
  } finally {
    $('update-apply').disabled = false;
  }
}

// ── 主题 ──────────────────────────────────────────────
//
// 深色是默认，浅色可切换。状态写 cookie：宿主监听的是 127.0.0.1:0，
// 端口每次启动都不同，localStorage 会因 origin 变化而丢；cookie 只看 host，不区分端口。
// 已经打开的日志窗口通过 BroadcastChannel 同步，不用等它重开。

const THEME_KEY = 'ml_theme';
const themeChannel = typeof BroadcastChannel === 'function' ? new BroadcastChannel('ml-theme') : null;

function applyTheme(theme, persist) {
  const light = theme === 'light';

  if (light) {
    document.documentElement.dataset.theme = 'light';
  } else {
    document.documentElement.removeAttribute('data-theme');
  }

  if (persist) {
    document.cookie = `${THEME_KEY}=${light ? 'light' : 'dark'}; max-age=31536000; path=/; samesite=lax`;
  }

  const button = $('theme-toggle');
  if (button) {
    const label = light ? '切换到深色' : '切换到浅色';
    button.title = label;
    button.setAttribute('aria-label', label);
  }

  themeChannel?.postMessage(light ? 'light' : 'dark');
}

$('theme-toggle')?.addEventListener('click', () => {
  applyTheme(document.documentElement.dataset.theme === 'light' ? 'dark' : 'light', true);
});

// 首帧由 index.html 里的内联脚本套好，这里只负责把按钮的提示语对齐。
applyTheme(document.documentElement.dataset.theme === 'light' ? 'light' : 'dark', false);

// ── 启动 ──────────────────────────────────────────────

(async () => {
  try {
    const info = await send('app.info');
    state.dataRoot = info.dataRoot;
    state.settings = info.settings;
    state.gameRoot = info.gameRoot;
    fillSettings(info.settings);

    // 版本号在「关于」页要一直显示，不能等用户点"检查更新"才填上
    if (info.version) {
      $('about-version').textContent = info.version;
    }
  } catch (error) {
    // 主界面没有日志区了，这类次要信息只进控制台，不占界面。
    console.warn(`拿不到数据目录：${error.message}`);
  }

  await loadCrashState();
  await loadAccounts();
  await loadVersions();
  await loadInstances();

  // 插件界面片段：启动层必须在这一步之前挂上，否则主界面会先闪一下
  await loadPluginUi(true);

  // 首屏就是首页：进软件先看到"一键启动"，不是实例列表
  switchTo('home');
  renderHome();

  // 设置页的内存条要真实数据，启动时拉一次
  await loadMemoryInfo();

  // 首屏没指定游戏目录时 loadVersions 已经弹出首次启动面板，这里不要再弹一次。
})();