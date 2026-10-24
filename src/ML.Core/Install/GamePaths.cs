namespace ML.Core.Install;

/// <summary>
/// 游戏目录布局。所有路径都从这里算，避免各处 <c>Path.Combine</c> 拼出不一致的结构。
/// </summary>
public sealed class GamePaths
{
    public GamePaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>游戏根目录（PCL 叫 .minecraft，这里允许任意路径）。</summary>
    public string Root { get; }

    public string Versions => Path.Combine(Root, "versions");

    public string Libraries => Path.Combine(Root, "libraries");

    public string Assets => Path.Combine(Root, "assets");

    /// <summary>资源对象仓库：<c>assets/objects/ab/abcdef...</c>，按内容寻址，跨版本共享。</summary>
    public string AssetObjects => Path.Combine(Assets, "objects");

    public string AssetIndexes => Path.Combine(Assets, "indexes");

    /// <summary>1.6 及更早版本的虚拟资源目录。</summary>
    public string VirtualAssets(string indexId) => Path.Combine(Assets, "virtual", indexId);

    /// <summary>1.6 及更早版本把资源摊平到这里。</summary>
    public string Resources => Path.Combine(Root, "resources");

    /// <summary>
    /// 未做旧版摊平的资源索引目录。
    /// </summary>
    public string AssetIndex(string indexId) => Path.Combine(AssetIndexes, indexId + ".json");

    /// <summary>
    /// log4j 配置文件的落盘位置。名字取 <c>logging.client.file.id</c>，
    /// 不叫 "client-1.12.xml"——那是 URL 里的名字，多个版本会撞。
    /// </summary>
    public string LogConfig(string id) => Path.Combine(Assets, "log_configs", id);

    public string VersionDirectory(string id) => Path.Combine(Versions, id);

    public string VersionJson(string id) => Path.Combine(VersionDirectory(id), id + ".json");

    public string ClientJar(string id) => Path.Combine(VersionDirectory(id), id + ".jar");

    /// <summary>
    /// natives 解压目录。每个版本一个，因为不同版本原生库版本不同，
    /// 共用目录迟早会互相踩。
    /// </summary>
    public string Natives(string id) => Path.Combine(VersionDirectory(id), "natives");

    public string NativesManifest(string id) => Path.Combine(Natives(id), ".ml-natives.json");

    /// <summary><paramref name="relativePath"/> 是仓库内相对路径（正斜杠）。</summary>
    public string LibraryFile(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return Path.Combine(Libraries, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>资源对象路径：前两位做目录。哈希必须是 SHA1（40 位十六进制）。</summary>
    public string AssetObject(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length < 2)
        {
            throw new ArgumentException($"资源哈希过短：{hash}", nameof(hash));
        }

        return Path.Combine(AssetObjects, hash[..2], hash);
    }
}
