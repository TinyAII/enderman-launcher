using System.Drawing;

namespace ML.Host;

/// <summary>
/// 应用图标：从 exe 里提取（就是 csproj 的 <c>ApplicationIcon</c> 嵌进去的那个）。
/// </summary>
/// <remarks>
/// WinForms 不会自动把 exe 图标用到窗口上——不显式设 <see cref="Form.Icon"/>，
/// 主窗口和子窗口显示的都是系统默认的那个空白图标。
/// </remarks>
internal static class AppIcon
{
    private static Icon? _value;

    public static Icon Value => _value ??= Load();

    private static Icon Load()
    {
        try
        {
            string? exe = Environment.ProcessPath;

            if (exe is not null && Icon.ExtractAssociatedIcon(exe) is { } extracted)
            {
                return extracted;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // 提取不到就用系统默认的，总比窗口上一个空图标强
        }

        return SystemIcons.Application;
    }
}
