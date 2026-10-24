using ML.Plugin.Runtime;

namespace ML.Core.Tests;

/// <summary>
/// 清单解析与加载顺序。这两块是纯逻辑，出错的表现都很隐蔽
/// （插件"消失了"、"顺序不对导致偶发失败"），所以单独钉住。
/// </summary>
public sealed class PluginManifestTests
{
    private static string Valid(string extra = "") =>
        $$"""
        {
          "schemaVersion": 1,
          "id": "ml.demo",
          "name": "示例",
          "version": "1.0.0",
          "apiVersion": "1.0",
          "type": "managed",
          "entry": "ml.demo.dll",
          "entryType": "Demo.Plugin"
          {{extra}}
        }
        """;

    [Fact]
    public void 合法清单能解析出来()
    {
        ManifestParseResult result = PluginManifestParser.Parse(Valid(), "ml.demo");

        Assert.NotNull(result.Manifest);
        Assert.Empty(result.Errors);
        Assert.Equal("ml.demo", result.Manifest!.Id);
        Assert.Equal("1.0", result.Manifest.ApiVersion);
        Assert.False(result.Manifest.IsDeclarative);
    }

    [Fact]
    public void apiVersion_主版本不匹配要拒绝()
    {
        // 主版本不同意味着契约已经改了：老插件按老接口写，加载进来只会 InvalidCast。
        string json = Valid().Replace("\"apiVersion\": \"1.0\"", "\"apiVersion\": \"2.0\"");

        ManifestParseResult result = PluginManifestParser.Parse(json, "ml.demo");

        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, e => e.Contains("主版本不匹配"));
    }

    [Fact]
    public void entry_不能逃出插件目录()
    {
        string json = Valid().Replace("\"ml.demo.dll\"", "\"../../evil.dll\"");

        ManifestParseResult result = PluginManifestParser.Parse(json, "ml.demo");

        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, e => e.Contains(".."));
    }

    [Fact]
    public void 声明式插件不需要_entry()
    {
        string json = """
        {
          "schemaVersion": 1,
          "id": "ml.args",
          "version": "1.0.0",
          "apiVersion": "1.0",
          "type": "declarative",
          "launch": { "jvmArgs": ["-Dfoo=1"] }
        }
        """;

        ManifestParseResult result = PluginManifestParser.Parse(json, "ml.args");

        Assert.NotNull(result.Manifest);
        Assert.True(result.Manifest!.IsDeclarative);
        Assert.Equal(["-Dfoo=1"], result.Manifest.JvmArguments);
    }

    [Fact]
    public void 依赖决定加载顺序()
    {
        PluginManifest a = PluginManifestParser.Parse(Valid(), "ml.demo").Manifest!;

        PluginManifest b = PluginManifestParser.Parse(
            Valid().Replace("\"ml.demo\"", "\"ml.other\"").Replace("\"Demo.Plugin\"", "\"Other.Plugin\""),
            "ml.other").Manifest! with
        {
            Dependencies = new Dictionary<string, string> { ["ml.demo"] = ">=1.0.0" }
        };

        // 故意把依赖者排在前面：解析器要把它挪到后面去。
        PluginLoadPlan plan = PluginResolver.Resolve([b, a]);

        Assert.Empty(plan.Rejected);
        Assert.Equal(["ml.demo", "ml.other"], plan.Order.Select(p => p.Id));
    }

    [Fact]
    public void 缺依赖的被拒绝且不影响别人()
    {
        PluginManifest a = PluginManifestParser.Parse(Valid(), "ml.demo").Manifest!;

        PluginManifest b = PluginManifestParser.Parse(
            Valid().Replace("\"ml.demo\"", "\"ml.other\"").Replace("\"Demo.Plugin\"", "\"Other.Plugin\""),
            "ml.other").Manifest! with
        {
            Dependencies = new Dictionary<string, string> { ["ml.missing"] = ">=1.0.0" }
        };

        PluginLoadPlan plan = PluginResolver.Resolve([a, b]);

        Assert.Single(plan.Order);
        Assert.Equal("ml.demo", plan.Order[0].Id);
        Assert.Contains("ml.missing", plan.Rejected["ml.other"]);
    }

    [Fact]
    public void 互相依赖成环时两个都不加载()
    {
        PluginManifest a = PluginManifestParser.Parse(Valid(), "ml.demo").Manifest!;
        PluginManifest b = PluginManifestParser.Parse(
            Valid().Replace("\"ml.demo\"", "\"ml.other\"").Replace("\"Demo.Plugin\"", "\"Other.Plugin\""),
            "ml.other").Manifest!;

        a = a with { Dependencies = new Dictionary<string, string> { ["ml.other"] = ">=1.0.0" } };
        b = b with { Dependencies = new Dictionary<string, string> { ["ml.demo"] = ">=1.0.0" } };

        PluginLoadPlan plan = PluginResolver.Resolve([a, b]);

        Assert.Empty(plan.Order);
        Assert.Contains("成环", plan.Rejected["ml.demo"]);
        Assert.Contains("成环", plan.Rejected["ml.other"]);
    }
}

/// <summary>
/// 权限白名单：哪些真的会拦、哪些只是预留。
/// 这条钉住的是"清单里写 ui:page 不能让插件真拿到 UI 权限"。
/// </summary>
public sealed class PluginPermissionTests
{
    [Fact]
    public void 执行级权限是启动参数两条加界面一条()
    {
        // ui:page 从 R4 起真的会挂界面（注入的脚本能碰整个页面），所以它也必须经过同意
        Assert.Equal(["launch:jvm-args", "launch:game-args", "ui:page"], PluginPermissions.Enforced);
    }

    [Fact]
    public void 预留权限只登记不执行()
    {
        Assert.True(PluginPermissions.IsKnown("fs:instance"));
        Assert.False(PluginPermissions.IsEnforced("fs:instance"));
    }

    [Fact]
    public void 未知权限既不是执行也不是预留()
    {
        Assert.False(PluginPermissions.IsKnown("root:everything"));
    }

    [Fact]
    public void 需要同意的权限只挑执行级的_且去重()
    {
        string[] declared = ["account:token", "launch:jvm-args", "launch:jvm-args", "ui:page"];

        Assert.Equal(["launch:jvm-args", "ui:page"], PluginPermissions.Required(declared));
    }
}
