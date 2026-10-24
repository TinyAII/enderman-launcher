using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ML.Core.Tests;

/// <summary>
/// 测试用本地 HTTP 服务器。
/// 用 Kestrel 而不是 <c>HttpListener</c>：后者在非管理员账户下注册 URL ACL 会直接失败，
/// 会让测试在 CI 上莫名其妙地红。
/// </summary>
internal sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Probe _probe;

    private TestServer(WebApplication app, Probe probe, string root)
    {
        _app = app;
        _probe = probe;
        Root = root;
    }

    /// <summary>形如 <c>http://127.0.0.1:54321</c>（无尾斜杠）。</summary>
    public string Root { get; }

    public int RequestCount => Volatile.Read(ref _probe.Count);

    public IReadOnlyList<string?> RequestedRanges
    {
        get
        {
            lock (_probe.Ranges)
            {
                return _probe.Ranges.ToArray();
            }
        }
    }

    public Uri Url(string path) => new(Root.TrimEnd('/') + "/" + path.TrimStart('/'));

    public static async Task<TestServer> StartAsync(RequestDelegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var probe = new Probe();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.Run(async context =>
        {
            Interlocked.Increment(ref probe.Count);
            lock (probe.Ranges)
            {
                probe.Ranges.Add(context.Request.Headers.Range.ToString());
            }

            await handler(context).ConfigureAwait(false);
        });

        await app.StartAsync().ConfigureAwait(false);

        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses;

        return new TestServer(app, probe, addresses.First());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Probe
    {
        public int Count;

        public List<string?> Ranges { get; } = [];
    }
}
