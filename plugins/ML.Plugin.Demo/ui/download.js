// 整页脚本。除了 MLPlugin 那点 API，还能用 MLPlugin.call 调插件自己的 C# 代码。
// 这不是"页面在干活"，是页面在指挥插件干活——下载器就该长这样。

const out = MLPlugin.root.querySelector('[data-demo="out"]');
const instanceInput = MLPlugin.root.querySelector('[data-demo="inst"]');
const tag = MLPlugin.root.querySelector('[data-demo="page-tag"]');

if (tag) {
  tag.textContent = `页面由 ${MLPlugin.id} 提供`;
}

function show(text) {
  out.textContent = text;
}

async function run(command, payload) {
  try {
    show(await MLPlugin.call(command, payload));
  } catch (error) {
    show(`失败：${error.message}`);
  }
}

MLPlugin.root.querySelector('[data-demo="status"]')?.addEventListener('click', () => run('status'));
MLPlugin.root.querySelector('[data-demo="mods"]')?.addEventListener('click', () => run('mods', instanceInput.value.trim()));
MLPlugin.root.querySelector('[data-demo="drop"]')?.addEventListener('click', () => run('drop-marker', instanceInput.value.trim()));
