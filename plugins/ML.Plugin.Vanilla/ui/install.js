// 原版安装页。宿主只给了 MLPlugin 这一个对象，别的全局都拿不到。
(function () {
  const select = document.getElementById('pi-version');
  const install = document.getElementById('pi-install');
  const refresh = document.getElementById('pi-refresh');
  const status = document.getElementById('pi-status');

  let installed = new Set();

  function say(text, bad) {
    status.textContent = text ?? '';
    status.classList.toggle('bad', Boolean(bad));
  }

  async function load() {
    say('正在拉取版本清单…');
    refresh.disabled = true;
    install.disabled = true;

    try {
      const raw = await MLPlugin.call('versions');
      const data = JSON.parse(raw || '{}');

      if (!data.ok) {
        say(data.error ?? '拉不到版本清单', true);
        return;
      }

      // 已经装过的标出来：同一个版本再装一次只是校验补齐，没什么意义
      try {
        const mine = JSON.parse(await MLPlugin.call('installed') || '{}');
        installed = new Set(mine.versions ?? []);
      } catch {
        installed = new Set();
      }

      select.replaceChildren();

      for (const version of data.versions ?? []) {
        const option = document.createElement('option');
        option.value = version.id;
        option.textContent = version.id + (installed.has(version.id) ? '（已安装）' : '');
        select.appendChild(option);
      }

      install.disabled = select.options.length === 0;
      say(`共 ${data.count ?? 0} 个正式版`);
    } catch (error) {
      say(`拉清单失败：${error.message}`, true);
    } finally {
      refresh.disabled = false;
    }
  }

  async function startInstall() {
    const versionId = select.value;

    if (!versionId) {
      return;
    }

    install.disabled = true;
    say(`正在安装 ${versionId}…进度看「下载」页`);

    try {
      const raw = await MLPlugin.call('install', { versionId });
      const data = JSON.parse(raw || '{}');

      if (data.ok) {
        say(`装好了：${versionId}（下载 ${data.downloaded} 个文件，${Math.round((data.bytes ?? 0) / 1024)} KB）`);
        await load();
      } else {
        say(`安装失败：${data.error ?? '未知原因'}`, true);
      }
    } catch (error) {
      say(`安装失败：${error.message}`, true);
    } finally {
      install.disabled = false;
    }
  }

  refresh.addEventListener('click', load);
  install.addEventListener('click', startInstall);

  load();
})();
