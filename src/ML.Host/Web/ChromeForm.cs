using System.Runtime.InteropServices;

namespace ML.Host.Web;

/// <summary>
/// 无边框主窗体：自己去掉了系统标题栏，所以拖动、边缘缩放、最大化边界都得自己补。
/// </summary>
/// <remarks>
/// <para>
/// 不用 <c>-webkit-app-region: drag</c>：WebView2 不对它做特殊处理，网页里设了也不会有拖动。
/// 前端只在标题栏上把 mousedown 报上来（<c>window.drag</c>），由这里调
/// <c>ReleaseCapture + WM_NCLBUTTONDOWN(HTCAPTION)</c> 交给系统拖 —— 这样贴边分屏、双击最大化
/// 都是系统原生行为，不用自己模拟。
/// </para>
/// <para>
/// <c>WM_GETMINMAXINFO</c> 必须拦：无边框窗体最大化会盖住任务栏，这是 FormBorderStyle.None 的已知行为。
/// </para>
/// </remarks>
internal sealed class ChromeForm : Form
{
    /// <summary>边缘判定宽度（像素）。太窄会抓不住，太宽会吃掉页面上的可点区域。</summary>
    private const int ResizeBorder = 6;

    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    /// <summary>Win11 的窗口圆角属性（21H2+ 才有，旧系统调用会失败，忽略即可）。</summary>
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public ChromeForm()
    {
        FormBorderStyle = FormBorderStyle.None;
    }

    /// <summary>把鼠标交给系统去拖窗口。前端在标题栏按下时调这个。</summary>
    public void BeginSystemDrag()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    /// <summary>为 true 时 <c>Close()</c> 真的关窗（托盘菜单的退出会先置它）。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool CloseForReal { get; set; }

    /// <summary>
    /// 用户点 × / Alt+F4 只把窗口藏起来，进程和托盘都留着。
    /// </summary>
    /// <remarks>
    /// 判据用 <see cref="CloseReason.UserClosing"/> 而不是自己加标志位：
    /// 代码里那些 <c>form.Close()</c>（冒烟测试退出、启动失败）的 CloseReason 是 None，
    /// 不会被拦，测试路径不用改一行。
    /// </remarks>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!CloseForReal && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedCorners();

        // 无边框窗体最大化会盖住任务栏。WinForms 自带的 MaximizedBounds 就是干这个的，
        // 比自己处理 WM_GETMINMAXINFO 稳妥（实测后者在这个窗体上没生效）。
        MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 多显示器时可能被拖到另一块屏，每次显示都按当前屏幕重算
        MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
    }

    /// <summary>最大化/还原切换。无边框没有按钮，由前端标题栏调用。</summary>
    public void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;

    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST:
                // 只接管边缘；其余交回默认，否则网页收不到点击。
                if (TryHitTest(m, out int hit))
                {
                    m.Result = hit;
                    return;
                }

                break;

            case WM_GETMINMAXINFO:
                ClampToWorkingArea(m);
                break;
        }

        base.WndProc(ref m);
    }

    private bool TryHitTest(Message m, out int hit)
    {
        hit = HTCLIENT;

        if (WindowState == FormWindowState.Maximized)
        {
            // 最大化时不留缩放边，否则贴着屏幕边缘拖会莫名其妙还原
            return false;
        }

        int screenX = (short)(m.LParam.ToInt64() & 0xFFFF);
        int screenY = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);

        Point client = PointToClient(new Point(screenX, screenY));
        Size bounds = ClientSize;

        bool left = client.X <= ResizeBorder;
        bool right = client.X >= bounds.Width - ResizeBorder;
        bool top = client.Y <= ResizeBorder;
        bool bottom = client.Y >= bounds.Height - ResizeBorder;

        hit = (left, right, top, bottom) switch
        {
            (true, _, true, _) => HTTOPLEFT,
            (_, true, true, _) => HTTOPRIGHT,
            (true, _, _, true) => HTBOTTOMLEFT,
            (_, true, _, true) => HTBOTTOMRIGHT,
            (true, _, _, _) => HTLEFT,
            (_, true, _, _) => HTRIGHT,
            (_, _, true, _) => HTTOP,
            (_, _, _, true) => HTBOTTOM,
            _ => HTCLIENT
        };

        return hit != HTCLIENT;
    }

    private void ClampToWorkingArea(Message m)
    {
        Rectangle work = Screen.FromHandle(Handle).WorkingArea;
        var info = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);

        // 用屏幕坐标填：默认值是"整个显示器"，改成工作区就不会盖住任务栏。
        info.ptMaxPosition.x = work.Left;
        info.ptMaxPosition.y = work.Top;
        info.ptMaxSize.x = work.Width;
        info.ptMaxSize.y = work.Height;

        Marshal.StructureToPtr(info, m.LParam, fDeleteOld: false);
    }

    private void ApplyRoundedCorners()
    {
        int preference = DWMWCP_ROUND;

        try
        {
            // 老系统没有这个属性，返回非 0 就算了；不值得为此中断启动。
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // dwmapi 在 Windows 上都存在，这里只是防御性写法
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
