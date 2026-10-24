namespace ML.Plugin.Runtime;

/// <summary>磁盘上发现的一个插件目录，以及它现在算不算"能装"。</summary>
public sealed record DiscoveredPlugin
{
    public required string Directory { get; init; }

    public required string FolderName { get; init; }

    public PluginManifest? Manifest { get; init; }

    /// <summary>
    /// <c>ready</c> 能加载；<c>no-manifest</c> 没清单；<c>invalid</c> 清单有问题。
    /// </summary>
    public required string State { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>扫目录，把每个子目录当成一个插件。</summary>
public static class PluginCatalog
{
    public static IReadOnlyList<DiscoveredPlugin> Scan(string pluginsRoot)
    {
        if (!Directory.Exists(pluginsRoot))
        {
            return [];
        }

        var found = new List<DiscoveredPlugin>();

        foreach (string directory in Directory.EnumerateDirectories(pluginsRoot)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string folder = Path.GetFileName(directory);
            string manifestPath = Path.Combine(directory, "manifest.json");

            if (!File.Exists(manifestPath))
            {
                found.Add(new DiscoveredPlugin
                {
                    Directory = directory,
                    FolderName = folder,
                    State = "no-manifest",
                    Errors = ["缺少 manifest.json"]
                });
                continue;
            }

            string json;
            try
            {
                json = File.ReadAllText(manifestPath);
            }
            catch (IOException ex)
            {
                found.Add(new DiscoveredPlugin
                {
                    Directory = directory,
                    FolderName = folder,
                    State = "invalid",
                    Errors = [$"读不了清单：{ex.Message}"]
                });
                continue;
            }

            ManifestParseResult parsed = PluginManifestParser.Parse(json, folder);

            found.Add(new DiscoveredPlugin
            {
                Directory = directory,
                FolderName = folder,
                Manifest = parsed.Manifest,
                State = parsed.Manifest is null ? "invalid" : "ready",
                Errors = parsed.Errors,
                Warnings = parsed.Warnings
            });
        }

        return found;
    }
}
