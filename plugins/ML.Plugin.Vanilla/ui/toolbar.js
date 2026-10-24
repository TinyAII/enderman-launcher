// 实例页工具栏上的入口。按钮是本插件贡献的——停用插件它就没了，宿主恢复原状。
(function () {
  const button = document.getElementById('ml-download-instance');

  if (!button) {
    return;
  }

  button.addEventListener('click', () => {
    // 跳到自己那一页：那里能选版本、看下载进度
    MLPlugin.navigate('plugin:ml.vanilla');
  });
})();
