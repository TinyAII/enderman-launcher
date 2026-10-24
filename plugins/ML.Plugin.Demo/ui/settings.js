// 设置片段的脚本。宿主额外给了一个 MLPlugin.settings：
//   MLPlugin.settings.values  打开时读到的键值快照（字符串）
//   MLPlugin.settings.set(k, v) 写回 plugin-data/<id>/settings.json
//
// 同一个文件，插件自己的 C# 代码也能通过 IPluginContext.Settings 读到。

const checkbox = MLPlugin.root.querySelector('[data-demo="auto"]');
const hint = MLPlugin.root.querySelector('[data-demo="saved"]');

function render() {
  const on = MLPlugin.settings.values.auto === '1';
  checkbox.checked = on;
  hint.textContent = on ? '当前：启动时检查更新（已开启）' : '当前：不检查';
}

checkbox.addEventListener('change', async () => {
  MLPlugin.settings.values.auto = checkbox.checked ? '1' : '0';
  await MLPlugin.settings.set('auto', MLPlugin.settings.values.auto);
  render();
  hint.textContent += ' · 已保存';
});

render();
