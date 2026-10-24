namespace ML.Core.Maven;

/// <summary>
/// Maven 坐标：<c>group:artifact:version[:classifier][@extension]</c>。
/// version.json 的 <c>libraries[].name</c> 就是这种字符串，合并去重也按它。
/// </summary>
public readonly record struct MavenCoordinate(
    string Group,
    string Artifact,
    string Version,
    string? Classifier,
    string Extension)
{
    /// <summary>去重用的规范键：补全 classifier 与 extension，避免 "a:b:1" 与 "a:b:1:jar" 被当成两条。</summary>
    public string Key =>
        $"{Group}:{Artifact}:{Version}:{Classifier ?? string.Empty}:{Extension}";

    /// <summary>仓库内的相对路径。</summary>
    public string RelativePath
    {
        get
        {
            string fileName = Classifier is null
                ? $"{Artifact}-{Version}.{Extension}"
                : $"{Artifact}-{Version}-{Classifier}.{Extension}";
            return $"{Group.Replace('.', '/')}/{Artifact}/{Version}/{fileName}";
        }
    }

    /// <summary>
    /// 解析 Maven 坐标。格式不合法时返回 false，而不是抛异常——
    /// 第三方版本 JSON 里出现脏数据是常态，调用方要能跳过单条坏库继续跑。
    /// </summary>
    public static bool TryParse(string? name, out MavenCoordinate coordinate)
    {
        coordinate = default;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string body = name.Trim();
        string extension = "jar";

        int at = body.LastIndexOf('@');
        if (at >= 0)
        {
            if (at == body.Length - 1)
            {
                return false;
            }

            extension = body[(at + 1)..];
            body = body[..at];
        }

        string[] parts = body.Split(':');
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        for (int i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0)
            {
                return false;
            }
        }

        string? classifier = parts.Length == 4 && parts[3].Length > 0 ? parts[3] : null;

        coordinate = new MavenCoordinate(parts[0], parts[1], parts[2], classifier, extension);
        return true;
    }
}
