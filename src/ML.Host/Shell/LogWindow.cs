using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ML.Host.Shell;

/// <summary>
/// 游戏日志窗口：独立的小窗口，只在真的启动游戏时出现。
/// </summary>
/// <remarks>
/// 做成独立窗口而不是主界面的一个页签，有两个理由：
/// 一边玩一边看日志和「在启动器里翻页」是两件事；而且日志经常要看一整屏，
/// 能把它拖到副屏上、主窗口同时还能操作，比页签有用得多。
/// </remarks>
internal sealed class LogWindow : IAsyncDisposable
{
    /// <summary>页面还没加载完时最多先囤多少条。超了丢最旧的——宁可少几行，也不能把内存吃掉。</summary>
    private const int MaxBuffered = 4000;

    private readonly Form _form;
    private readonly WebView2 _webView;
    private readonly Func<WebView2, Task<CoreWebView2>> _initialize;
    private readonly string _url;
    private readonly Queue<string> _buffer = [];
    private readonly object _gate = new();

    private bool _ready;
    private bool _closed;

    public LogWindow(string title, string url, Func<WebView2, Task<CoreWebView2>> initialize)
    {
        _url = url;
        _initialize = initialize;

        _webView = new WebView2 { Dock = DockStyle.Fill };
        _form = new Form
        {
            Text = title,
            Icon = ML.Host.AppIcon.Value,
            Width = 900,
            Height = 560,
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(520, 300)
        };

        _form.Controls.Add(_webView);
        _form.FormClosed += (_, _) => { lock (_gate) { _closed = true; } };

        // 先显示窗口再初始化 WebView2：用户点了「启动」要立刻有反应。
        // 空白窗口也比「主界面毫无变化」诚实——否则用户会以为按钮没点着。
        _form.Show();
        _ = StartAsync();
    }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return !_closed && !_form.IsDisposed;
            }
        }
    }

    /// <summary>新一次启动前清空，并把标题换成这次的实例名。</summary>
    /// <summary>跟着主窗一起藏起来（「启动器可见性」选隐藏时用）。</summary>
    public void Hide()
    {
        if (!_form.IsDisposed)
        {
            _form.Hide();
        }
    }

    /// <summary>恢复显示。窗口被用户手动关掉后不复活，所以先看 IsOpen。</summary>
    public void Show()
    {
        if (!_form.IsDisposed && !_form.Visible)
        {
            _form.Show();
            _form.Activate();
        }
    }

    public void Reset(string? title = null)
    {
        if (title is not null && !_form.IsDisposed)
        {
            _form.Text = title;
        }

        lock (_gate)
        {
            _buffer.Clear();
        }

        Post("""{"event":"log.reset","data":{}}""");
    }

    /// <summary>推一条已有的事件（原样是宿主发给界面的 JSON）。</summary>
    public void Post(string json)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (!_ready)
            {
                _buffer.Enqueue(json);
                while (_buffer.Count > MaxBuffered)
                {
                    _buffer.Dequeue();
                }

                return;
            }
        }

        PostNow(json);
    }

    public void Close()
    {
        try
        {
            if (!_form.IsDisposed)
            {
                _form.Close();
            }
        }
        catch (ObjectDisposedException)
        {
            // 已经关了，没什么要做的。
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        _form.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task StartAsync()
    {
        try
        {
            CoreWebView2 core = await _initialize(_webView).ConfigureAwait(true);

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    return;
                }

                lock (_gate)
                {
                    _ready = true;
                }

                Flush();

                // 诊断用：ML_LOG_SHOT=<png 路径> 会在窗口稳定后把页面内容截下来。
                // 存在的理由：WebView2 的内容是合成层，屏幕截图会被上层窗口盖住，
                // PrintWindow 又会漏掉顶部一条（不是页面缺陷，是抓取缺陷）。
                // 只有 CapturePreviewAsync 拿到的是页面真正的样子。
                if (Environment.GetEnvironmentVariable("ML_LOG_SHOT") is { Length: > 0 } shotPath)
                {
                    _ = CaptureAsync(shotPath);
                }
            };

            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            // 日志窗口起不来不能连累主流程：游戏照样在跑，只是少了实时输出。
            Console.Error.WriteLine($"[WARN] 日志窗口起不来：{ex.GetType().Name}: {ex.Message}");
            Close();
        }
    }

    private async Task CaptureAsync(string path)
    {
        // 等游戏把日志刷出来。截早了只能拍到「准备中…」。
        await Task.Delay(TimeSpan.FromSeconds(14)).ConfigureAwait(true);

        try
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            await using FileStream stream = File.Create(full);
            await _webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
                .ConfigureAwait(true);

            Console.WriteLine($"[OK] 日志窗口截图 → {full}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] 日志窗口截图失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Flush()    {
        string[] pending;

        lock (_gate)
        {
            pending = [.. _buffer];
            _buffer.Clear();
        }

        foreach (string json in pending)
        {
            PostNow(json);
        }
    }

    private void PostNow(string json)
    {
        if (_form.IsDisposed)
        {
            return;
        }

        void Send()
        {
            try
            {
                _webView.CoreWebView2?.PostWebMessageAsJson(json);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // 窗口正在关：投递失败是正常的，不是错误。
            }
        }

        if (_form.InvokeRequired)
        {
            _form.BeginInvoke(Send);
        }
        else
        {
            Send();
        }
    }
}
