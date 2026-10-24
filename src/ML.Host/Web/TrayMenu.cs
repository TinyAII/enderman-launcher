using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ML.Host.Web;

/// <summary>
/// 托盘右键菜单：自己画的一个无边框小窗口。
/// </summary>
/// <remarks>
/// <para>
/// 不用 <see cref="ContextMenuStrip"/>：它的圆角做不干净（只能靠 Region 裁，边缘有锯齿），
/// 配色也跟界面那一套（自绘标题栏、浅色主题）对不上。既然标题栏都自己画了，菜单也自己画。
/// </para>
/// <para>
/// 圆角交给 DWM（Win11 原生圆角，带抗锯齿），阴影用 <c>CS_DROPSHADOW</c>，
/// 所以这里只负责画背景和条目。
/// </para>
/// </remarks>
internal sealed class TrayMenu : Form
{
    private const int CS_DROPSHADOW = 0x00020000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const int ItemHeight = 34;
    private const int CornerRadius = 10;
    private const int Padding = 6;
    private const int MenuWidth = 116;

    private static readonly Color Back = Color.FromArgb(255, 255, 255);
    private static readonly Color Text = Color.FromArgb(31, 35, 40);
    private static readonly Color Hover = Color.FromArgb(244, 245, 247);
    private static readonly Color Danger = Color.FromArgb(200, 52, 44);
    private static readonly Color Border = Color.FromArgb(226, 230, 236);

    private readonly string _label;
    private bool _hover;

    public TrayMenu(string label)
    {
        _label = label;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Back;

        Size = new Size(MenuWidth, ItemHeight + Padding * 2);
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>菜单项被点中时触发。</summary>
    public event Action<string>? Chosen;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= CS_DROPSHADOW;

            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        int preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        ApplyRoundedRegion();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRoundedRegion();
    }

    /// <summary>老系统 DWM 圆角不生效，再用 Region 裁一次，保证任何版本都没有直角。</summary>
    private void ApplyRoundedRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width, Height), CornerRadius);
        Region?.Dispose();
        Region = new Region(path);
    }

    /// <summary>
    /// 把菜单画成 PNG（只看外观用）。
    /// </summary>
    /// <remarks>
    /// 不能用 <c>DrawToBitmap</c>：窗体没 Show 过就没有句柄，它会一直等消息循环，把进程卡死（实测卡了两分钟）。
    /// 这里直接往 Bitmap 上调用同一份绘制逻辑。
    /// </remarks>
    public void SavePreview(string path, bool hover)
    {
        using var bitmap = new Bitmap(Width, Height);

        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 圆角是窗口合成时才裁的，导出图得自己裁一下，否则看不出效果
            using GraphicsPath clip = RoundedRect(new Rectangle(0, 0, Width, Height), CornerRadius);
            g.SetClip(clip);
            g.Clear(Back);

            Render(g, hover);
        }

        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>在鼠标位置弹出来：右下角对齐光标，超出屏幕就翻到另一边。</summary>
    public void ShowAt(Point cursor)
    {
        Rectangle screen = Screen.FromPoint(cursor).WorkingArea;
        int x = cursor.X - Width;
        int y = cursor.Y - Height;

        if (x < screen.Left)
        {
            x = screen.Left + 4;
        }

        if (y < screen.Top)
        {
            y = cursor.Y;
        }

        Location = new Point(x, y);
        Show();
        Activate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        Rectangle item = ItemBounds();
        bool inside = item.Contains(e.Location);

        if (inside != _hover)
        {
            _hover = inside;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hover)
        {
            _hover = false;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left || !ItemBounds().Contains(e.Location))
        {
            return;
        }

        Chosen?.Invoke(_label);
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Render(e.Graphics, _hover);
    }

    private void Render(Graphics g, bool hover)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle item = ItemBounds();

        using (var border = new Pen(Border, 1))
        using (var outline = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius))
        {
            g.DrawPath(border, outline);
        }

        if (hover)
        {
            using var brush = new SolidBrush(Hover);
            using var path = RoundedRect(item, 6);
            g.FillPath(brush, path);
        }

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using var textBrush = new SolidBrush(hover ? Danger : Text);
        using var format = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Near,
            FormatFlags = StringFormatFlags.NoWrap
        };

        g.DrawString(
            _label,
            Font,
            textBrush,
            new RectangleF(item.X + 14, item.Y, item.Width - 14, item.Height),
            format);
    }

    private Rectangle ItemBounds() => new(Padding, Padding, Width - Padding * 2, ItemHeight);

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
