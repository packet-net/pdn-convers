using System.Globalization;
using System.Net;
using System.Text;
using Convers.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Convers.Host.Web;

/// <summary>Composition inputs for the web chat tile.</summary>
public sealed record WebChatOptions
{
    /// <summary>The web users' presence into the shared hub (the local-session seam — design decision 8).</summary>
    public required WebChatSessions Sessions { get; init; }

    /// <summary>The durable store — the append-only chat log the channel view renders scrollback from.</summary>
    public required ConversStore Store { get; init; }

    /// <summary>The leaf's own convers callsign / node name (display + the page title).</summary>
    public required string NodeCallsign { get; init; }

    /// <summary>The fixed default channel a first-time web user lands on.</summary>
    public int DefaultChannel { get; init; } = 3333;

    /// <summary>How many scrollback rows the channel view shows.</summary>
    public int ScrollbackRows { get; init; } = 60;
}

/// <summary>
/// The web chat tile (design decision 8) — the convers analogue of pdn-bbs webmail: server-rendered HTML
/// on the loopback bind, trusting the pdn app-gateway identity contract. Every request must carry
/// <c>X-Pdn-Gateway: 1</c> (else 403) and the owner/web-user identity arrives in <c>X-Pdn-User</c>; there
/// is <b>no login page</b> (the gateway header IS the auth boundary — mirroring webmail's
/// <c>WithCallsign</c>). The pdn username maps to a convers callsign via <see cref="WebIdentity"/> (the
/// username verbatim when it is a valid callsign, else a short "not a callsign" page).
///
/// <para>A web user is a <em>local session</em> like an RF user (<see cref="WebChatSessions"/>): joins fan
/// out, messages reach RF + web users and go upstream, and the web user shows up in <c>who</c>. The
/// channel view reads live presence from <see cref="Uplink.HostLink.SnapshotAsync"/> (never off the hub
/// loop) and durable scrollback from <see cref="ConversStore.QueryChatLog"/>; send / join / topic / msg /
/// away post through the same hub seam RF users use. Server-rendered and refresh-driven (a small
/// auto-refresh meta on the channel view) — no WebSockets/SSE, matching webmail.</para>
/// </summary>
public static class WebChat
{
    /// <summary>Maps the chat-tile routes and the gateway-trust middleware onto <paramref name="app"/>.</summary>
    public static void Map(WebApplication app, WebChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        app.Use(async (context, next) =>
        {
            // The node supervisor / deploy script polls /healthz (and /status, the uplink snapshot) with no
            // gateway identity — those are not part of the chat tile, so they bypass the gateway gate.
            if (context.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) ||
                context.Request.Path.Equals("/status", StringComparison.OrdinalIgnoreCase))
            {
                await next().ConfigureAwait(false);
                return;
            }

            // The gateway contract (packet.net docs/app-gateway.md): pdn strips any client-supplied copy
            // of these headers before injecting its own, and the loopback bind means only pdn reaches us.
            if (context.Request.Headers["X-Pdn-Gateway"] != "1")
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: pdn gateway only.").ConfigureAwait(false);
                return;
            }

            string user = context.Request.Headers["X-Pdn-User"].ToString();
            if (string.IsNullOrWhiteSpace(user))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: sign in to pdn first.").ConfigureAwait(false);
                return;
            }

            context.Items[PdnUserKey] = user;

            // The public mount point (e.g. "/apps/convers") when proxied through the gateway; absent
            // (→ "") on direct loopback access. Every absolute URL we render or redirect to carries it.
            context.Items[PrefixKey] = context.Request.Headers["X-Forwarded-Prefix"].ToString().TrimEnd('/');

            await next().ConfigureAwait(false);
        });

        // The channel view — the user's current channel: who's present (live hub snapshot) + recent
        // messages (durable chat-log scrollback). An optional ?channel= just sets the view target.
        app.MapGet("/", (HttpContext ctx, int? channel, string? oper, CancellationToken ct) =>
            WithCallsign(ctx, (prefix, call) => ChannelViewAsync(options, prefix, call, channel, oper, ct)));

        // Send a message to the current channel (the SAME hub seam RF users use).
        app.MapPost("/say", async (HttpContext ctx, CancellationToken ct) =>
        {
            string? call = PdnCallsign(ctx);
            string prefix = Prefix(ctx);
            if (call is null)
            {
                return Unmapped(ctx);
            }

            IFormCollection form = await ctx.Request.ReadFormAsync(ct).ConfigureAwait(false);
            await options.Sessions.SayAsync(call, form["text"].ToString().Trim(), ct).ConfigureAwait(false);
            return Results.Redirect(U(prefix, "/"));
        });

        // Join / switch channel.
        app.MapPost("/join", async (HttpContext ctx, CancellationToken ct) =>
        {
            string? call = PdnCallsign(ctx);
            string prefix = Prefix(ctx);
            if (call is null)
            {
                return Unmapped(ctx);
            }

            IFormCollection form = await ctx.Request.ReadFormAsync(ct).ConfigureAwait(false);
            if (int.TryParse(form["channel"].ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch)
                && ChannelNumber.IsValid(ch))
            {
                await options.Sessions.JoinChannelAsync(call, ch, ct).ConfigureAwait(false);
                return Results.Redirect(U(prefix, Inv($"/?channel={ch}")));
            }

            return Results.Redirect(U(prefix, "/"));
        });

        // Set the topic of the current channel.
        app.MapPost("/topic", async (HttpContext ctx, CancellationToken ct) =>
        {
            string? call = PdnCallsign(ctx);
            string prefix = Prefix(ctx);
            if (call is null)
            {
                return Unmapped(ctx);
            }

            IFormCollection form = await ctx.Request.ReadFormAsync(ct).ConfigureAwait(false);
            await options.Sessions.SetTopicAsync(call, form["topic"].ToString().Trim(), ct).ConfigureAwait(false);
            return Results.Redirect(U(prefix, "/"));
        });

        // Send a private message to another user (local or remote).
        app.MapPost("/msg", async (HttpContext ctx, CancellationToken ct) =>
        {
            string? call = PdnCallsign(ctx);
            string prefix = Prefix(ctx);
            if (call is null)
            {
                return Unmapped(ctx);
            }

            IFormCollection form = await ctx.Request.ReadFormAsync(ct).ConfigureAwait(false);
            await options.Sessions.PrivateMessageAsync(
                call, form["to"].ToString().Trim(), form["text"].ToString().Trim(), ct).ConfigureAwait(false);
            return Results.Redirect(U(prefix, "/"));
        });

        // Set the modes of the current channel (the hub enforces operator status; a non-op is refused).
        app.MapPost("/mode", async (HttpContext ctx, CancellationToken ct) =>
        {
            string? call = PdnCallsign(ctx);
            string prefix = Prefix(ctx);
            if (call is null)
            {
                return Unmapped(ctx);
            }

            IFormCollection form = await ctx.Request.ReadFormAsync(ct).ConfigureAwait(false);
            await options.Sessions.SetModeAsync(call, form["options"].ToString().Trim(), ct).ConfigureAwait(false);
            return Results.Redirect(U(prefix, "/"));
        });

        // Set / clear the web user's away message.
        app.MapPost("/away", async (HttpContext ctx, CancellationToken ct) =>
        {
            string? call = PdnCallsign(ctx);
            string prefix = Prefix(ctx);
            if (call is null)
            {
                return Unmapped(ctx);
            }

            IFormCollection form = await ctx.Request.ReadFormAsync(ct).ConfigureAwait(false);
            await options.Sessions.SetAwayAsync(call, form["text"].ToString().Trim(), ct).ConfigureAwait(false);
            return Results.Redirect(U(prefix, "/"));
        });

        // Operator login (/..OPER semantics) — the node operator secret grants operator status.
        app.MapPost("/oper", async (HttpContext ctx, CancellationToken ct) =>
        {
            string? call = PdnCallsign(ctx);
            string prefix = Prefix(ctx);
            if (call is null)
            {
                return Unmapped(ctx);
            }

            IFormCollection form = await ctx.Request.ReadFormAsync(ct).ConfigureAwait(false);
            bool ok = await options.Sessions.TryOperAsync(call, form["secret"].ToString(), ct).ConfigureAwait(false);
            return Results.Redirect(U(prefix, ok ? "/?oper=ok" : "/?oper=fail"));
        });

        // The who page — everyone present on the user's current channel (and the whole network).
        app.MapGet("/who", (HttpContext ctx, CancellationToken ct) =>
            WithCallsign(ctx, (prefix, call) => WhoAsync(options, prefix, call, ct)));
    }

    private const string PdnUserKey = "pdnUser";
    private const string PrefixKey = "pdnPrefix";

    private static string PdnUser(HttpContext ctx) => (string)ctx.Items[PdnUserKey]!;

    private static string Prefix(HttpContext ctx) => (string)ctx.Items[PrefixKey]!;

    /// <summary>The mapped convers callsign for the request's pdn user, or null when unmapped.</summary>
    private static string? PdnCallsign(HttpContext ctx) => WebIdentity.CallsignFor(PdnUser(ctx));

    /// <summary>Roots an absolute path under the gateway mount prefix ("" when direct).</summary>
    private static string U(string prefix, string path) => prefix + path;

    /// <summary>
    /// Resolves the request's callsign and dispatches, or renders the "not a callsign" page when the pdn
    /// username has no mapping (mirrors webmail's <c>WithCallsign</c>, minus the claim form since identity
    /// here is the callsign itself).
    /// </summary>
    private static Task<IResult> WithCallsign(HttpContext ctx, Func<string, string, Task<IResult>> handler)
    {
        string? call = PdnCallsign(ctx);
        return call is null
            ? Task.FromResult(Unmapped(ctx))
            : handler(Prefix(ctx), call);
    }

    private static IResult Unmapped(HttpContext ctx)
    {
        string user = PdnUser(ctx);
        string prefix = Prefix(ctx);
        return Html($$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>convers — no callsign</title>{{Style}}</head>
            <body><main>
            <h1>convers</h1>
            <p>Hello <b>{{H(user)}}</b>. Your pdn username isn't a valid amateur callsign, so it can't be
            used as a convers identity. Sign in to pdn with your callsign as your username to use convers.</p>
            <p class="dim">de a node at {{H(prefix.Length == 0 ? "/" : prefix)}}</p>
            </main></body></html>
            """, StatusCodes.Status403Forbidden);
    }

    // ---------------------------------------------------------------- pages

    private static async Task<IResult> ChannelViewAsync(
        WebChatOptions o, string prefix, string call, int? requestedChannel, string? operResult, CancellationToken ct)
    {
        // Opening the tile makes the web user present (joined) on a channel, exactly like an RF user who is
        // present the moment they connect — so RF users see them in `who` and their messages fan out. A
        // requested ?channel= switches them to it; otherwise they land on (or stay where they are on) a
        // channel via EnsureJoined.
        int channel;
        if (requestedChannel is { } req && ChannelNumber.IsValid(req))
        {
            await o.Sessions.JoinChannelAsync(call, req, ct).ConfigureAwait(false);
            channel = req;
        }
        else
        {
            channel = await o.Sessions.EnsureJoinedAsync(call, ct).ConfigureAwait(false);
        }

        // Live presence + topic + modes from the hub (read on its owning loop via the link seam).
        (IReadOnlyList<NetworkUser> present, string topic, string topicBy, ChannelMode modes) =
            await o.Sessions.SnapshotChannelAsync(channel, ct).ConfigureAwait(false);
        bool isOperator = await o.Sessions.IsOperatorAsync(call, ct).ConfigureAwait(false);

        // Durable scrollback from the chat log (channel messages + presence for this channel).
        IReadOnlyList<ChatLogEntry> log = o.Store.QueryChatLog(channel: channel, limit: o.ScrollbackRows);

        string who = RenderPresent(present, call);
        string scrollback = RenderScrollback(log);
        string topicLine = topic.Length == 0
            ? "<span class=\"dim\">no topic set</span>"
            : Inv($"{H(topic)}{(topicBy.Length == 0 ? "" : Inv($" <span class=\"dim\">(set by {H(topicBy)})</span>"))}");
        string modeLine = Inv($"<code>{H(ChannelModes.ToWire(modes))}</code> {H(DescribeModes(modes))}");
        string operFeedback = operResult switch
        {
            "ok" => "<p class=\"ok\">You are now an operator.</p>",
            "fail" => "<p class=\"err\">Operator access denied.</p>",
            _ => "",
        };

        // The mode + operator control. A non-operator sees an operator-login box; an operator gets a
        // mode-set box (the hub still enforces, so the box is a convenience, not the gate).
        string control = isOperator
            ? $"""
                <p class="dim">You are an operator.</p>
                <form method="post" action="{U(prefix, "/mode")}" class="modeform">
                <input name="options" placeholder="+mt / -s …" size="10">
                <button type="submit">Set modes</button></form>
                """
            : $"""
                <form method="post" action="{U(prefix, "/oper")}" class="operform">
                <input name="secret" type="password" placeholder="operator secret" size="14">
                <button type="submit">Become operator</button></form>
                """;

        string body = $"""
            <div class="bar">
            <form method="post" action="{U(prefix, "/join")}" class="inline">
            <label>Channel <input name="channel" value="{channel}" size="6" inputmode="numeric"></label>
            <button type="submit">Go</button></form>
            <span class="ch">on channel <b>{channel}</b></span>
            </div>
            <div class="cols">
            <section class="chat">
            <h2>Channel {channel}</h2>
            <p class="topic">Topic: {topicLine}</p>
            <p class="modes">Modes: {modeLine}</p>
            <form method="post" action="{U(prefix, "/topic")}" class="topicform">
            <input name="topic" placeholder="set the topic…" size="48">
            <button type="submit">Set topic</button></form>
            {scrollback}
            <form method="post" action="{U(prefix, "/say")}" class="say">
            <input name="text" placeholder="type a message and press Enter…" autocomplete="off" autofocus size="60" required>
            <button type="submit">Send</button></form>
            </section>
            <aside class="who">
            <h3>Here now <span class="dim">({present.Count})</span></h3>
            {who}
            <h3>You</h3>
            {operFeedback}
            <form method="post" action="{U(prefix, "/away")}" class="awayform">
            <input name="text" placeholder="away message (blank = back)" size="18">
            <button type="submit">Set away</button></form>
            {control}
            <h3>Private message</h3>
            <form method="post" action="{U(prefix, "/msg")}" class="pm">
            <input name="to" placeholder="callsign" size="10" required>
            <input name="text" placeholder="message" size="20" required>
            <button type="submit">Send</button></form>
            </aside>
            </div>
            """;

        return Html(Page(o, prefix, call, Inv($"Channel {channel}"), body, autoRefresh: true));
    }

    private static async Task<IResult> WhoAsync(WebChatOptions o, string prefix, string call, CancellationToken ct)
    {
        int channel = await o.Sessions.CurrentChannelAsync(call, ct).ConfigureAwait(false);
        (IReadOnlyList<NetworkUser> here, IReadOnlyList<NetworkUser> network) =
            await o.Sessions.SnapshotWhoAsync(channel, ct).ConfigureAwait(false);

        string body = $"""
            <h2>Who</h2>
            <h3>On channel {channel} <span class="dim">({here.Count})</span></h3>
            {RenderPresent(here, call)}
            <h3>On the network <span class="dim">({network.Count})</span></h3>
            {RenderNetwork(network, call)}
            """;
        return Html(Page(o, prefix, call, "Who", body, autoRefresh: false));
    }

    // ---------------------------------------------------------------- rendering

    private static string RenderScrollback(IReadOnlyList<ChatLogEntry> log)
    {
        if (log.Count == 0)
        {
            return "<div class=\"log\"><p class=\"dim\">No messages yet on this channel.</p></div>";
        }

        var sb = new StringBuilder("<div class=\"log\">");
        // QueryChatLog returns newest-first; render oldest-first so the newest sits at the bottom.
        for (int i = log.Count - 1; i >= 0; i--)
        {
            ChatLogEntry e = log[i];
            string time = e.At.ToString("HH:mm", CultureInfo.InvariantCulture);
            string origin = e.Origin == ChatLogOrigin.Local ? "local" : "net";
            if (e.Kind == ChatLogKind.Presence)
            {
                sb.Append(Inv(
                    $"""<div class="ev"><span class="t">{H(time)}</span> <span class="who2">{H(e.FromCall)}</span> <span class="dim">{H(e.Text)}</span></div>"""));
            }
            else
            {
                sb.Append(Inv(
                    $"""<div class="msg"><span class="t">{H(time)}</span> <span class="from" data-o="{H(origin)}">{H(e.FromCall)}:</span> {H(e.Text)}</div>"""));
            }
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>A short human description of the set channel modes (empty when none set).</summary>
    private static string DescribeModes(ChannelMode modes)
    {
        if (modes == ChannelMode.None)
        {
            return "(no modes set)";
        }

        var parts = new List<string>();
        if ((modes & ChannelMode.Secret) != 0)
        {
            parts.Add("secret");
        }

        if ((modes & ChannelMode.Private) != 0)
        {
            parts.Add("private");
        }

        if ((modes & ChannelMode.TopicLocked) != 0)
        {
            parts.Add("topic-locked");
        }

        if ((modes & ChannelMode.Invisible) != 0)
        {
            parts.Add("invisible");
        }

        if ((modes & ChannelMode.Moderated) != 0)
        {
            parts.Add("moderated");
        }

        if ((modes & ChannelMode.Local) != 0)
        {
            parts.Add("local");
        }

        return string.Join(", ", parts);
    }

    private static string RenderPresent(IReadOnlyList<NetworkUser> users, string me)
    {
        if (users.Count == 0)
        {
            return "<p class=\"dim\">Nobody here.</p>";
        }

        var sb = new StringBuilder("<ul class=\"present\">");
        foreach (NetworkUser u in users)
        {
            string meTag = Callsigns.Equal(u.Name, me) ? " <span class=\"dim\">(you)</span>" : "";
            string away = u.IsAway ? " <span class=\"away\">(away)</span>" : "";
            string pers = u.Personal.Length == 0 ? "" : Inv($" <span class=\"dim\">— {H(u.Personal)}</span>");
            sb.Append(Inv($"<li><b>{H(u.Name)}</b>{meTag}{away}{pers}</li>"));
        }

        sb.Append("</ul>");
        return sb.ToString();
    }

    private static string RenderNetwork(IReadOnlyList<NetworkUser> users, string me)
    {
        if (users.Count == 0)
        {
            return "<p class=\"dim\">Nobody on the network.</p>";
        }

        var sb = new StringBuilder("<table><tr><th>User</th><th>Host</th><th>Ch</th><th></th></tr>");
        foreach (NetworkUser u in users)
        {
            string meTag = Callsigns.Equal(u.Name, me) ? " (you)" : "";
            string away = u.IsAway ? "away" : "";
            sb.Append(Inv($"<tr><td><b>{H(u.Name)}</b>{H(meTag)}</td><td>{H(u.Host)}</td><td>{u.Channel}</td><td class=\"dim\">{H(away)}</td></tr>"));
        }

        sb.Append("</table>");
        return sb.ToString();
    }

    private static string Page(WebChatOptions o, string prefix, string call, string title, string body, bool autoRefresh)
    {
        string refresh = autoRefresh ? """<meta http-equiv="refresh" content="15">""" : "";
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{H(o.NodeCallsign)}} convers — {{H(title)}}</title>{{refresh}}{{Style}}</head>
            <body><main>
            <h1>{{H(o.NodeCallsign)}} <span class="dim">convers</span></h1>
            <nav><a href="{{U(prefix, "/")}}">Channel</a> · <a href="{{U(prefix, "/who")}}">Who</a>
            <span class="dim">— de {{H(call)}}</span></nav>
            {{body}}
            </main></body></html>
            """;
    }

    private const string Style = """
        <style>
        body{font-family:system-ui,sans-serif;margin:0;background:#f4f3ef;color:#1c1c1a}
        main{max-width:64rem;margin:0 auto;padding:1.5rem}
        h1{font-size:1.3rem}h1 .dim,nav .dim,h2 .dim,h3 .dim{color:#8a857c;font-weight:normal}
        nav{margin-bottom:1rem}
        a{color:#1c5fa8}
        .bar{display:flex;align-items:center;gap:1rem;margin-bottom:.75rem}
        .inline{display:inline}.ch{color:#8a857c}
        .cols{display:flex;gap:1.5rem;align-items:flex-start}
        .chat{flex:1 1 auto;min-width:0}.who{flex:0 0 16rem}
        .topic{margin:.2rem 0 .6rem}.topicform{margin-bottom:.8rem}
        .log{background:#fff;border:1px solid #ddd8cf;padding:.6rem;height:24rem;overflow-y:auto;font-size:.92rem}
        .msg,.ev{padding:.1rem 0;white-space:pre-wrap;word-break:break-word}
        .msg .t,.ev .t{color:#b3ada2;font-variant-numeric:tabular-nums;margin-right:.3rem}
        .msg .from{font-weight:bold;color:#1c5fa8}
        .msg .from[data-o="net"]{color:#7a4ea8}
        .ev .who2{font-weight:bold}
        .say{display:flex;gap:.4rem;margin-top:.6rem}.say input{flex:1 1 auto}
        ul.present{list-style:none;padding:0;margin:0}ul.present li{padding:.15rem 0}
        .away{color:#a32014}
        .pm{display:flex;flex-wrap:wrap;gap:.3rem}
        table{border-collapse:collapse;width:100%}
        th,td{text-align:left;padding:.25rem .5rem;border-bottom:1px solid #ddd8cf;font-size:.92rem}
        .err{color:#a32014}
        .ok{color:#1c7a2e}
        .modes code{background:#eee9df;padding:0 .3rem;border-radius:3px}
        .awayform,.operform,.modeform{margin:.4rem 0}
        input,select,textarea{font:inherit}
        button{font:inherit;padding:.2rem .8rem}
        .dim{color:#8a857c}
        </style>
        """;

    private static IResult Html(string html, int statusCode = StatusCodes.Status200OK) =>
        Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);

    private static string H(string text) => WebUtility.HtmlEncode(text);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
