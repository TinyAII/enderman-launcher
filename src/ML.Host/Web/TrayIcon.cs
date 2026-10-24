using System.Drawing;

namespace ML.Host.Web;

/// <summary>
/// 托盘图标。启动就在，点关闭只藏窗口，从这里能叫回来。
/// </summary>
/// <remarks>
/// 图标直接取 exe 自己的（csproj 里 <c>ApplicationIcon</c> 那套 7 帧 ico），
/// 不另外维护一份 tray.ico —— 两份图迟早会不一致。
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Action _exit;

    public TrayIcon(Form form, Action exit)
    {
        Icon? icon = null;

        try
        {
            icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // 取不到就用系统默认图标，总比托盘上出现一个空洞强
        }

        _exit = exit;

        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "Enderman Launcher",
            Visible = true
        };

        // 单击回到前台；右键弹自己的菜单（不用 ContextMenuStrip，它的直角改不掉）
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindow(form);
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                ShowMenu();
            }
        };
    }

    /// <summary>把窗口叫回来：藏过的要先 Show，最小化的先还原，最后提到最前。</summary>
    public static void ShowWindow(Form form)
    {
        if (!form.Visible)
        {
            form.Show();
        }

        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }

        form.Activate();
        form.BringToFront();
    }

    private void ShowMenu()
    {
        var menu = new TrayMenu("退出");
        menu.Chosen += _ => _exit();
        menu.ShowAt(Cursor.Position);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
