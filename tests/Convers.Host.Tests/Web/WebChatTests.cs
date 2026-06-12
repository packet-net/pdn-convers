using System.Net;
using Convers.Core;
using Convers.Host.Sessions;
using Convers.Host.Uplink;
using Convers.Host.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Convers.Host.Tests.Web;

/// <summary>
/// Drives the web chat tile over a real (loopback, ephemeral-port) <see cref="WebApplication"/>, mirroring
/// the pdn-bbs <c>WebmailTests</c> pattern: the gateway identity headers on the client and assertions made
/// through the same seams production uses — the hub via <see cref="HostLink.SnapshotAsync"/> and the
/// durable <see cref="ConversStore.QueryChatLog"/>. The tile is wired exactly as
/// <see cref="HostComposition"/> wires it (a <see cref="WebChatSessions"/> over a running
/// <see cref="HostLink"/> with no uplink), but without the RHP node link — the tile never touches it, so
/// the harness stays light and uncontended (no flakes under repeated runs). One smoke test boots the full
/// <see cref="HostComposition"/> to pin that the routes are mapped and <c>/healthz</c> still works.
/// Non-Interop.
/// </summary>
public sealed class WebChatTests : IAsyncDisposable
{
    private const int DefaultChannel = 3333;

    private readonly DirectoryInfo _dir;
    private readonly ConversStore _store;
    private readonly ConversHub _hub;
    private readonly LocalSessionRegistry _registry = new();
    private readonly HostLink _link;
    private readonly WebChatSessions _sessions;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _linkRun;
    private WebApplication? _app;

    public WebChatTests()
    {
        _dir = Directory.CreateTempSubdirectory("convers-webchat-test-");
        var time = TimeProvider.System;
        _store = ConversStore.Open(Path.Combine(_dir.FullName, "convers.db"), time);
        _hub = new ConversHub("G0WEB", time, _store);
        var chatLog = new ChatLogWriter(_store, NullLogger<ChatLogWriter>.Instance);
        var options = new HostLinkOptions { HostName = "G0WEB" };
        _link = new HostLink(options, NullUpstreamLinkFactory.Instance, _hub, time,
            NullLogger<HostLink>.Instance, _registry, chatLog);
        _linkRun = _link.RunAsync(_cts.Token);
        _sessions = new WebChatSessions(_link, _registry, chatLog, time, DefaultChannel);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _sessions.DisposeAsync();
        await _cts.CancelAsync();
        try
        {
            await _linkRun.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        await _link.DisposeAsync();
        _cts.Dispose();
        _store.Dispose();
        _dir.Delete(recursive: true);
    }

    private async Task<HttpClient> StartAsync(
        string? pdnUser = "M0LTE", bool gateway = true, string? forwardedPrefix = null, bool autoRedirect = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();

        // The healthz route the production composition keeps — so the gateway gate's bypass is exercised.
        _app.MapGet("/healthz", () => Results.Text("ok"));
        WebChat.Map(_app, new WebChatOptions
        {
            Sessions = _sessions,
            Store = _store,
            NodeCallsign = "G0WEB",
            DefaultChannel = DefaultChannel,
        });
        await _app.StartAsync();

        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = autoRedirect })
        {
            BaseAddress = new Uri(_app.Urls.First()),
        };
        if (gateway)
        {
            client.DefaultRequestHeaders.Add("X-Pdn-Gateway", "1");
        }

        if (pdnUser is not null)
        {
            client.DefaultRequestHeaders.Add("X-Pdn-User", pdnUser);
        }

        if (forwardedPrefix is not null)
        {
            client.DefaultRequestHeaders.Add("X-Forwarded-Prefix", forwardedPrefix);
        }

        return client;
    }

    // ---------------------------------------------------------------- gateway identity (the auth boundary)

    [Fact]
    public async Task MissingGatewayHeader_Returns403()
    {
        using HttpClient client = await StartAsync(gateway: false);
        HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MissingUserHeader_Returns403()
    {
        using HttpClient client = await StartAsync(pdnUser: null);
        HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_BypassesTheGatewayGate()
    {
        // The W0 liveness contract survives the gateway gate (the supervisor polls it with no headers).
        using HttpClient client = await StartAsync(pdnUser: null, gateway: false);
        HttpResponseMessage response = await client.GetAsync(new Uri("/healthz", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UsernameThatIsNotACallsign_IsRejectedWithExplanation()
    {
        using HttpClient client = await StartAsync(pdnUser: "tom");
        HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("callsign", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UsernameThatIsACallsign_MapsStraightToIt()
    {
        using HttpClient client = await StartAsync(pdnUser: "m0lte"); // lowercase username
        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("de M0LTE", page, StringComparison.Ordinal); // canonicalised
    }

    // ---------------------------------------------------------------- channel view + presence

    [Fact]
    public async Task ChannelView_ShowsDefaultChannel_AndMakesTheWebUserPresent()
    {
        using HttpClient client = await StartAsync();
        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.Contains("Channel 3333", page, StringComparison.Ordinal);
        Assert.Contains("M0LTE", page, StringComparison.Ordinal); // appears in the "here now" list

        // Opening the tile joined the web user as a local session on the default channel — like an RF user.
        bool present = await _link.SnapshotAsync(
            hub => hub.GetChannel(3333).Users.Any(u => u.Name == "M0LTE"), CancellationToken.None);
        Assert.True(present);
    }

    [Fact]
    public async Task Scrollback_RendersFromTheChatLog()
    {
        // Seed durable scrollback (a prior network-origin message on this channel) directly in the store.
        _store.AppendChatLog(new ChatLogEntry
        {
            Kind = ChatLogKind.Channel,
            Channel = 3333,
            FromCall = "G8NET",
            Origin = ChatLogOrigin.Network,
            Text = "earlier traffic from the network",
        });

        using HttpClient client = await StartAsync();
        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.Contains("earlier traffic from the network", page, StringComparison.Ordinal);
        Assert.Contains("G8NET", page, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- send (the same hub seam RF users use)

    [Fact]
    public async Task Say_PostsAMessageThatReachesTheHub_AndIsLogged()
    {
        using HttpClient client = await StartAsync();
        await client.GetStringAsync(new Uri("/", UriKind.Relative)); // join the channel first

        HttpResponseMessage say = await client.PostAsync(
            new Uri("/say", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("text", "hello channel")]));
        say.EnsureSuccessStatusCode(); // redirect to / followed

        // The message went through the SAME seam RF users use (HostLink.SubmitLocalEventAsync) and was
        // chat-logged — assert via the durable store.
        ChatLogEntry? entry = null;
        await PollUntilAsync(() =>
        {
            entry = _store.QueryChatLog(channel: 3333, kind: ChatLogKind.Channel)
                .FirstOrDefault(e => e.Text == "hello channel");
            return entry is not null;
        });
        Assert.NotNull(entry);
        Assert.Equal("M0LTE", entry.FromCall);
        Assert.Equal(ChatLogOrigin.Local, entry.Origin);
        Assert.Equal(3333, entry.Channel);
    }

    [Fact]
    public async Task SaidMessage_RendersInTheScrollbackOnReload()
    {
        using HttpClient client = await StartAsync();
        await client.GetStringAsync(new Uri("/", UriKind.Relative));

        await client.PostAsync(new Uri("/say", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("text", "round table is open")]));

        await PollUntilAsync(() => _store.QueryChatLog(channel: 3333, kind: ChatLogKind.Channel)
            .Any(e => e.Text == "round table is open"));

        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("round table is open", page, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- join / topic / who / msg

    [Fact]
    public async Task Join_SwitchesTheWebUsersChannel()
    {
        using HttpClient client = await StartAsync();
        await client.GetStringAsync(new Uri("/", UriKind.Relative)); // join default

        HttpResponseMessage join = await client.PostAsync(
            new Uri("/join", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("channel", "4000")]));
        join.EnsureSuccessStatusCode();

        await PollUntilAsync(async () => await _link.SnapshotAsync(
            hub => hub.GetChannel(4000).Users.Any(u => u.Name == "M0LTE"), CancellationToken.None));

        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("Channel 4000", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Topic_SetsTheChannelTopic_VisibleInTheView()
    {
        using HttpClient client = await StartAsync();
        await client.GetStringAsync(new Uri("/", UriKind.Relative));

        HttpResponseMessage topic = await client.PostAsync(
            new Uri("/topic", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("topic", "ragchew welcome")]));
        topic.EnsureSuccessStatusCode();

        await PollUntilAsync(async () => await _link.SnapshotAsync(
            hub => hub.GetChannel(3333).Topic == "ragchew welcome", CancellationToken.None));

        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("ragchew welcome", page, StringComparison.Ordinal);
        Assert.Contains("(set by M0LTE)", page, StringComparison.Ordinal); // the attribution branch
        // It also persisted to the durable topic store (the hub write-through).
        Assert.Equal("ragchew welcome", _store.GetTopic(3333)!.Topic);
    }

    [Fact]
    public async Task Who_ListsTheWebUserOnTheirChannel()
    {
        using HttpClient client = await StartAsync();
        await client.GetStringAsync(new Uri("/", UriKind.Relative)); // join

        string who = await client.GetStringAsync(new Uri("/who", UriKind.Relative));
        Assert.Contains("Who", who, StringComparison.Ordinal);
        Assert.Contains("On channel 3333", who, StringComparison.Ordinal);
        Assert.Contains("M0LTE", who, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrivateMessage_BetweenTwoWebUsers_IsLogged()
    {
        using HttpClient sender = await StartAsync(pdnUser: "M0LTE");
        await sender.GetStringAsync(new Uri("/", UriKind.Relative));

        HttpResponseMessage pm = await sender.PostAsync(
            new Uri("/msg", UriKind.Relative),
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("to", "G4ABC"),
                new KeyValuePair<string, string>("text", "eyeball?"),
            ]));
        pm.EnsureSuccessStatusCode();

        await PollUntilAsync(() => _store.QueryChatLog(kind: ChatLogKind.PrivateMessage)
            .Any(e => e.FromCall == "M0LTE" && e.ToCall == "G4ABC" && e.Text == "eyeball?"));
    }

    [Fact]
    public async Task TwoWebUsers_AreBothPresent_AndOneHearsTheOthersMessageInScrollback()
    {
        using HttpClient a = await StartAsync(pdnUser: "M0LTE");
        await a.GetStringAsync(new Uri("/", UriKind.Relative));
        await a.PostAsync(new Uri("/say", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("text", "anyone about?")]));

        await PollUntilAsync(() => _store.QueryChatLog(channel: 3333, kind: ChatLogKind.Channel)
            .Any(e => e.Text == "anyone about?"));

        // A second web user opens the tile and sees the first user present + the message in scrollback.
        using HttpClient b = await StartAsync(pdnUser: "G4ABC");
        string page = await b.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("anyone about?", page, StringComparison.Ordinal);
        Assert.Contains("M0LTE", page, StringComparison.Ordinal);

        bool bothPresent = await _link.SnapshotAsync(hub =>
        {
            var names = hub.GetChannel(3333).Users.Select(u => u.Name).ToHashSet();
            return names.Contains("M0LTE") && names.Contains("G4ABC");
        }, CancellationToken.None);
        Assert.True(bothPresent);
    }

    // ---------------------------------------------------------------- X-Forwarded-Prefix (gateway mount)

    [Fact]
    public async Task ForwardedPrefix_LinksAndFormsCarryThePrefix()
    {
        using HttpClient client = await StartAsync(forwardedPrefix: "/apps/convers");
        string page = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.Contains("action=\"/apps/convers/say\"", page, StringComparison.Ordinal);
        Assert.Contains("action=\"/apps/convers/join\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/apps/convers/who\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("action=\"/say\"", page, StringComparison.Ordinal); // nothing escapes the mount
    }

    [Fact]
    public async Task ForwardedPrefix_JoinRedirect_CarriesThePrefix()
    {
        using HttpClient client = await StartAsync(forwardedPrefix: "/apps/convers", autoRedirect: false);
        await client.GetStringAsync(new Uri("/", UriKind.Relative));

        HttpResponseMessage join = await client.PostAsync(
            new Uri("/join", UriKind.Relative),
            new FormUrlEncodedContent([new KeyValuePair<string, string>("channel", "5000")]));
        Assert.Equal(HttpStatusCode.Found, join.StatusCode);
        Assert.Equal("/apps/convers/?channel=5000", join.Headers.Location!.OriginalString);
    }

    /// <summary>
    /// Polls an async condition until it holds. Background fan-out (the hub loop applies submitted events
    /// asynchronously) is observed via a real-time poll with a generous CI-safe ceiling — never a bare
    /// time-advance racing a background effect.
    /// </summary>
    private static async Task PollUntilAsync(Func<Task<bool>> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The expected hub/store effect never appeared.");
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private static Task PollUntilAsync(Func<bool> condition) => PollUntilAsync(() => Task.FromResult(condition()));
}

/// <summary>
/// Pins that the W5b web tile is wired into the production <see cref="HostComposition"/>: the
/// <see cref="WebChatSessions"/> seam is registered in the composed container. Uses the cheap
/// <c>start: false</c> harness (no web server / RHP listen) so it adds no startup contention to the suite;
/// the tile's HTTP behaviour is covered by <see cref="WebChatTests"/> over a real loopback app.
/// </summary>
public sealed class WebChatCompositionSmokeTests
{
    [Fact]
    public async Task ComposedHost_RegistersTheWebChatSeam()
    {
        await using var host = await ComposedHost.BuildAsync(start: false);
        WebChatSessions sessions = host.App.Services.GetRequiredService<WebChatSessions>();
        Assert.NotNull(sessions);
        Assert.Equal(0, sessions.Count); // no web users yet
    }
}
