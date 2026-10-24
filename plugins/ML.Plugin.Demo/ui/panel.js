// 插件注入到页面里的脚本。
//
// 宿主只传进来一个对象 MLPlugin：
//   MLPlugin.id      插件 id
//   MLPlugin.root    这个片段挂载的容器（想找元素请从这里往下找，别去翻整个页面）
//   MLPlugin.notice  弹一个提示框
//   MLPlugin.status  写插件页的状态行
//
// 注意：脚本和页面同上下文，理论上能碰整个 document。这是契约，不是沙箱。

MLPlugin.root.querySelector('[data-demo="hello"]')?.addEventListener('click', () => {
  MLPlugin.notice('示例插件', '这个按钮来自插件注入的界面片段，不是启动器自带的。');
});

const tag = MLPlugin.root.querySelector('[data-demo="tag"]');
if (tag) {
  tag.textContent = `由 ${MLPlugin.id} 注入`;
}
