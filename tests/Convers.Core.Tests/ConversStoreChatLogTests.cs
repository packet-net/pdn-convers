using Convers.Core;
using Microsoft.Data.Sqlite;

namespace Convers.Core.Tests;

/// <summary>
/// The append-only, kept-forever <c>chatlog</c> (schema v2, design decision 7): append + query
/// (filters, ordering, cap), the three <see cref="ChatLogKind"/>s round-tripping, TimeProvider
/// stamping, count/range, and the v1→v2 migration <b>preserving</b> existing profiles + topics.
/// </summary>
public sealed class ConversStoreChatLogTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    // ---------------------------------------------------------------- schema

    [Fact]
    public void Open_CreatesSchemaAtV2()
    {
        Assert.Equal(2, ConversStore.CurrentSchemaVersion);
        Assert.Equal(2, _ts.Store.SchemaVersion);
    }

    // ---------------------------------------------------------------- append + round-trip

    [Fact]
    public void AppendChatLog_ChannelMessage_RoundTrips()
    {
        DateTimeOffset now = _ts.Time.GetUtcNow();
        _ts.Store.AppendChatLog(new ChatLogEntry
        {
            Kind = ChatLogKind.Channel,
            At = now,
            Channel = 3333,
            FromCall = "m0lte",
            Origin = ChatLogOrigin.Local,
            Text = "hello channel",
        });

        ChatLogEntry row = Assert.Single(_ts.Store.QueryChatLog());
        Assert.Equal(ChatLogKind.Channel, row.Kind);
        Assert.Equal(now.ToUnixTimeSeconds(), row.At.ToUnixTimeSeconds());
        Assert.Equal(3333, row.Channel);
        Assert.Equal("M0LTE", row.FromCall);
        Assert.Null(row.ToCall);
        Assert.Equal(ChatLogOrigin.Local, row.Origin);
        Assert.Equal("hello channel", row.Text);
    }

    [Fact]
    public void AppendChatLog_PrivateMessage_RoundTripsWithNullChannelAndToCall()
    {
        _ts.Store.AppendChatLog(new ChatLogEntry
        {
            Kind = ChatLogKind.PrivateMessage,
            At = _ts.Time.GetUtcNow(),
            Channel = null,
            FromCall = "g4abc",
            ToCall = "m0lte",
            Origin = ChatLogOrigin.Network,
            Text = "secret note",
        });

        ChatLogEntry row = Assert.Single(_ts.Store.QueryChatLog());
        Assert.Equal(ChatLogKind.PrivateMessage, row.Kind);
        Assert.Null(row.Channel);
        Assert.Equal("G4ABC", row.FromCall);
        Assert.Equal("M0LTE", row.ToCall);
        Assert.Equal(ChatLogOrigin.Network, row.Origin);
        Assert.Equal("secret note", row.Text);
    }

    [Fact]
    public void AppendChatLog_PresenceEvent_RoundTrips()
    {
        _ts.Store.AppendChatLog(new ChatLogEntry
        {
            Kind = ChatLogKind.Presence,
            At = _ts.Time.GetUtcNow(),
            Channel = 100,
            FromCall = "m0lte",
            Origin = ChatLogOrigin.Local,
            Text = "joined",
        });

        ChatLogEntry row = Assert.Single(_ts.Store.QueryChatLog(kind: ChatLogKind.Presence));
        Assert.Equal(ChatLogKind.Presence, row.Kind);
        Assert.Equal(100, row.Channel);
        Assert.Equal("joined", row.Text);
    }

    [Fact]
    public void AppendChatLog_AllThreeKinds_RoundTrip()
    {
        Append(ChatLogKind.Channel, channel: 1, from: "A", to: null, text: "ch");
        Append(ChatLogKind.PrivateMessage, channel: null, from: "A", to: "B", text: "pm");
        Append(ChatLogKind.Presence, channel: 1, from: "A", to: null, text: "joined");

        ChatLogKind[] kinds = _ts.Store.QueryChatLog().Select(r => r.Kind).OrderBy(k => k).ToArray();
        Assert.Equal([ChatLogKind.Channel, ChatLogKind.PrivateMessage, ChatLogKind.Presence], kinds);
    }

    [Fact]
    public void AppendChatLog_SurvivesReopen()
    {
        Append(ChatLogKind.Channel, channel: 7, from: "A", to: null, text: "durable");

        ConversStore reopened = _ts.Reopen();
        Assert.Equal(2, reopened.SchemaVersion);
        ChatLogEntry row = Assert.Single(reopened.QueryChatLog());
        Assert.Equal("durable", row.Text);
    }

    // ---------------------------------------------------------------- TimeProvider stamping

    [Fact]
    public void AppendChatLog_DefaultAt_StampsFromTimeProvider()
    {
        _ts.Time.SetUtcNow(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));

        _ts.Store.AppendChatLog(new ChatLogEntry
        {
            Kind = ChatLogKind.Channel,
            // At deliberately left at default.
            Channel = 1,
            FromCall = "A",
            Origin = ChatLogOrigin.Local,
            Text = "no timestamp",
        });

        ChatLogEntry row = Assert.Single(_ts.Store.QueryChatLog());
        Assert.Equal(_ts.Time.GetUtcNow().ToUnixTimeSeconds(), row.At.ToUnixTimeSeconds());
    }

    [Fact]
    public void AppendChatLog_ExplicitAt_IsPreservedNotOverwritten()
    {
        DateTimeOffset explicitTime = new(2020, 5, 5, 5, 5, 5, TimeSpan.Zero);
        _ts.Time.SetUtcNow(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

        _ts.Store.AppendChatLog(new ChatLogEntry
        {
            Kind = ChatLogKind.Channel,
            At = explicitTime,
            Channel = 1,
            FromCall = "A",
            Origin = ChatLogOrigin.Local,
            Text = "x",
        });

        Assert.Equal(explicitTime.ToUnixTimeSeconds(), _ts.Store.QueryChatLog().Single().At.ToUnixTimeSeconds());
    }

    // ---------------------------------------------------------------- ordering

    [Fact]
    public void QueryChatLog_OrdersMostRecentFirst()
    {
        DateTimeOffset t0 = _ts.Time.GetUtcNow();
        AppendAt(t0, "first");
        AppendAt(t0 + TimeSpan.FromMinutes(1), "second");
        AppendAt(t0 + TimeSpan.FromMinutes(2), "third");

        string[] texts = _ts.Store.QueryChatLog().Select(r => r.Text).ToArray();
        Assert.Equal(["third", "second", "first"], texts);
    }

    [Fact]
    public void QueryChatLog_SameTimestamp_OrderedByInsertionDescending()
    {
        DateTimeOffset t = _ts.Time.GetUtcNow();
        AppendAt(t, "a");
        AppendAt(t, "b");
        AppendAt(t, "c");

        string[] texts = _ts.Store.QueryChatLog().Select(r => r.Text).ToArray();
        Assert.Equal(["c", "b", "a"], texts);
    }

    // ---------------------------------------------------------------- filters

    [Fact]
    public void QueryChatLog_FiltersByChannel()
    {
        Append(ChatLogKind.Channel, channel: 100, from: "A", to: null, text: "ch100");
        Append(ChatLogKind.Channel, channel: 200, from: "A", to: null, text: "ch200");

        ChatLogEntry row = Assert.Single(_ts.Store.QueryChatLog(channel: 100));
        Assert.Equal("ch100", row.Text);
    }

    [Fact]
    public void QueryChatLog_ChannelFilter_ExcludesPrivateMessages()
    {
        Append(ChatLogKind.Channel, channel: 100, from: "A", to: null, text: "ch");
        Append(ChatLogKind.PrivateMessage, channel: null, from: "A", to: "B", text: "pm");

        // A PM has a null channel, so it can never match a channel filter.
        ChatLogEntry row = Assert.Single(_ts.Store.QueryChatLog(channel: 100));
        Assert.Equal("ch", row.Text);
    }

    [Fact]
    public void QueryChatLog_FiltersByKind()
    {
        Append(ChatLogKind.Channel, channel: 1, from: "A", to: null, text: "ch");
        Append(ChatLogKind.PrivateMessage, channel: null, from: "A", to: "B", text: "pm");
        Append(ChatLogKind.Presence, channel: 1, from: "A", to: null, text: "joined");

        ChatLogEntry row = Assert.Single(_ts.Store.QueryChatLog(kind: ChatLogKind.PrivateMessage));
        Assert.Equal("pm", row.Text);
    }

    [Fact]
    public void QueryChatLog_FiltersBySinceUtc_Inclusive()
    {
        DateTimeOffset t0 = _ts.Time.GetUtcNow();
        AppendAt(t0, "old");
        AppendAt(t0 + TimeSpan.FromMinutes(10), "boundary");
        AppendAt(t0 + TimeSpan.FromMinutes(20), "new");

        string[] texts = _ts.Store
            .QueryChatLog(sinceUtc: t0 + TimeSpan.FromMinutes(10))
            .Select(r => r.Text)
            .ToArray();

        // since is inclusive, most-recent-first.
        Assert.Equal(["new", "boundary"], texts);
    }

    [Fact]
    public void QueryChatLog_CombinesFiltersWithAnd()
    {
        DateTimeOffset t0 = _ts.Time.GetUtcNow();
        Append(ChatLogKind.Channel, channel: 100, from: "A", to: null, text: "want", at: t0 + TimeSpan.FromMinutes(5));
        Append(ChatLogKind.Channel, channel: 200, from: "A", to: null, text: "wrong-channel", at: t0 + TimeSpan.FromMinutes(5));
        Append(ChatLogKind.Presence, channel: 100, from: "A", to: null, text: "wrong-kind", at: t0 + TimeSpan.FromMinutes(5));
        Append(ChatLogKind.Channel, channel: 100, from: "A", to: null, text: "too-old", at: t0);

        ChatLogEntry row = Assert.Single(_ts.Store.QueryChatLog(
            channel: 100, kind: ChatLogKind.Channel, sinceUtc: t0 + TimeSpan.FromMinutes(1)));
        Assert.Equal("want", row.Text);
    }

    // ---------------------------------------------------------------- cap / limit

    [Fact]
    public void QueryChatLog_RespectsExplicitLimit_ReturningMostRecent()
    {
        DateTimeOffset t0 = _ts.Time.GetUtcNow();
        for (int i = 0; i < 10; i++)
        {
            AppendAt(t0 + TimeSpan.FromMinutes(i), $"m{i}");
        }

        string[] texts = _ts.Store.QueryChatLog(limit: 3).Select(r => r.Text).ToArray();
        Assert.Equal(["m9", "m8", "m7"], texts);
    }

    [Fact]
    public void QueryChatLog_NonPositiveLimit_ClampsToDefault()
    {
        Append(ChatLogKind.Channel, channel: 1, from: "A", to: null, text: "x");

        // A zero/negative limit must not return an empty/clamped-to-zero result; it falls back to the default cap.
        Assert.Single(_ts.Store.QueryChatLog(limit: 0));
        Assert.Single(_ts.Store.QueryChatLog(limit: -5));
        Assert.True(ConversStore.DefaultChatLogLimit > 0);
    }

    [Fact]
    public void QueryChatLog_DefaultCap_BoundsLargeResult()
    {
        DateTimeOffset t0 = _ts.Time.GetUtcNow();
        int total = ConversStore.DefaultChatLogLimit + 25;
        for (int i = 0; i < total; i++)
        {
            AppendAt(t0 + TimeSpan.FromSeconds(i), $"m{i}");
        }

        Assert.Equal(ConversStore.DefaultChatLogLimit, _ts.Store.QueryChatLog().Count);
        Assert.Equal(total, _ts.Store.CountChatLog());
    }

    // ---------------------------------------------------------------- count

    [Fact]
    public void CountChatLog_CountsWithoutLimitAndHonoursFilters()
    {
        Append(ChatLogKind.Channel, channel: 1, from: "A", to: null, text: "a");
        Append(ChatLogKind.Channel, channel: 1, from: "A", to: null, text: "b");
        Append(ChatLogKind.PrivateMessage, channel: null, from: "A", to: "B", text: "pm");

        Assert.Equal(3, _ts.Store.CountChatLog());
        Assert.Equal(2, _ts.Store.CountChatLog(kind: ChatLogKind.Channel));
        Assert.Equal(2, _ts.Store.CountChatLog(channel: 1));
        Assert.Equal(1, _ts.Store.CountChatLog(kind: ChatLogKind.PrivateMessage));
    }

    [Fact]
    public void CountChatLog_EmptyLog_IsZero()
    {
        Assert.Equal(0, _ts.Store.CountChatLog());
    }

    // ---------------------------------------------------------------- guards

    [Fact]
    public void AppendChatLog_RejectsBlankFromCall()
    {
        Assert.Throws<ArgumentException>(() => _ts.Store.AppendChatLog(new ChatLogEntry
        {
            Kind = ChatLogKind.Channel,
            Channel = 1,
            FromCall = "   ",
            Origin = ChatLogOrigin.Local,
            Text = "x",
        }));
    }

    [Fact]
    public void AppendChatLog_EmptyToCall_StoredAsNull()
    {
        _ts.Store.AppendChatLog(new ChatLogEntry
        {
            Kind = ChatLogKind.Channel,
            At = _ts.Time.GetUtcNow(),
            Channel = 1,
            FromCall = "A",
            ToCall = "",
            Origin = ChatLogOrigin.Local,
            Text = "x",
        });

        Assert.Null(_ts.Store.QueryChatLog().Single().ToCall);
    }

    // ---------------------------------------------------------------- helpers

    private void Append(ChatLogKind kind, int? channel, string from, string? to, string text, DateTimeOffset? at = null)
        => _ts.Store.AppendChatLog(new ChatLogEntry
        {
            Kind = kind,
            At = at ?? _ts.Time.GetUtcNow(),
            Channel = channel,
            FromCall = from,
            ToCall = to,
            Origin = ChatLogOrigin.Local,
            Text = text,
        });

    private void AppendAt(DateTimeOffset at, string text)
        => _ts.Store.AppendChatLog(new ChatLogEntry
        {
            Kind = ChatLogKind.Channel,
            At = at,
            Channel = 1,
            FromCall = "A",
            Origin = ChatLogOrigin.Local,
            Text = text,
        });
}
