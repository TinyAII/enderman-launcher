using System.Text;

namespace ML.Core.Java;

/// <summary>
/// JDK/JRE 根目录下的 <c>release</c> 文件。<c>KEY="value"</c> 一行一对。
/// 这是唯一不启动 JVM 就能拿到厂商、版本、架构的途径——比跑 <c>java -version</c>
/// 快两个数量级，所以是首选；只有文件缺失时才退化成启动进程去问。
/// </summary>
public sealed class JavaRelease
{
    private readonly Dictionary<string, string> _values;

    private JavaRelease(Dictionary<string, string> values)
    {
        _values = values;
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    public string? Implementor => Get("IMPLEMENTOR");

    public string? ImplementorVersion => Get("IMPLEMENTOR_VERSION");

    public string? RuntimeName => Get("JAVA_RUNTIME_NAME");

    public string? VersionString => Get("JAVA_VERSION");

    public string? VersionDate => Get("JAVA_VERSION_DATE");

    public string? OsName => Get("OS_NAME");

    public string? OsArch => Get("OS_ARCH");

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    /// <summary>解析失败或文件不存在返回 <see langword="null"/>。</summary>
    public static JavaRelease? TryLoad(string homeDirectory)
    {
        var path = Path.Combine(homeDirectory, "release");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static JavaRelease Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = Unquote(line[(equals + 1)..].Trim());
            if (key.Length > 0)
            {
                values[key] = value;
            }
        }

        return new JavaRelease(values);
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var inner = value[1..^1];
        if (!inner.Contains('\\'))
        {
            return inner;
        }

        var builder = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                i++;
                builder.Append(inner[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    var other => other
                });
            }
            else
            {
                builder.Append(inner[i]);
            }
        }

        return builder.ToString();
    }
}
