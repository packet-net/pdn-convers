using Convers.Core;
using Microsoft.Extensions.Time.Testing;

namespace Convers.Core.Tests;

/// <summary>
/// W7a SHOULD semantics: channel-mode ingest + enforcement (<c>+i/+l/+m/+p/+s/+t</c>), away
/// set/clear/propagation/in-who, and topic persistence/propagation with <c>+t</c> enforcement. Each
/// test drives <see cref="ConversHub.Advance"/> with the new mode/oper events and asserts on the
/// returned <see cref="ConversAction"/> fan-out and the channel/user snapshots. Sans-IO; no store
/// unless a test exercises persistence.
/// </summary>
public sealed class ConversHubModesTests
{
    private const string MyHost = "GB7PDN";
    private static readonly DateTimeOffset T0 = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    private static ConversHub NewHub(ConversStore? store = null) =>
        new(MyHost, new FakeTimeProvider(T0), store);

    // ---------------------------------------------------------------- mode parsing / formatting

    [Theory]
    [InlineData("+m", ChannelMode.Moderated)]
    [InlineData("+mt", ChannelMode.Moderated | ChannelMode.TopicLocked)]
    [InlineData("+i+s", ChannelMode.Invisible | ChannelMode.Secret)]
    [InlineData("+l", ChannelMode.Local)]
    [InlineData("+p", ChannelMode.Private)]
    public void ChannelModes_Apply_SetsTheExpectedFlags(string options, ChannelMode expected)
    {
        Assert.Equal(expected, ChannelModes.Apply(ChannelMode.None, options));
    }

    [Fact]
    public void ChannelModes_Apply_MinusClearsAndDefaultsToSet()
    {
        ChannelMode after = ChannelModes.Apply(ChannelMode.Moderated | ChannelMode.TopicLocked, "-m");
        Assert.Equal(ChannelMode.TopicLocked, after);

        // No leading sign defaults to "set".
        Assert.Equal(ChannelMode.Moderated, ChannelModes.Apply(ChannelMode.None, "m"));
    }

    [Fact]
    public void ChannelModes_Apply_ChannelZeroIgnoresEverythingButTopicLock()
    {
        ChannelMode after = ChannelModes.Apply(ChannelMode.None, "+mst", isChannelZero: true);
        Assert.Equal(ChannelMode.TopicLocked, after);
    }

    [Fact]
    public void ChannelModes_ToWire_IsCanonicalOrderOrDashWhenEmpty()
    {
        // conversd get_mode_flags() order is s p t i m l, so t precedes m.
        Assert.Equal("+tm", ChannelModes.ToWire(ChannelMode.Moderated | ChannelMode.TopicLocked));
        Assert.Equal("-", ChannelModes.ToWire(ChannelMode.None));
    }

    // ---------------------------------------------------------------- /..MODE ingest

    [Fact]
    public void LocalSetMode_ByChannelOperator_AppliesModesEmitsUpstreamAndNotifies()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 3333));
        hub.Advance(new ConversEvent.LocalSetOperator("s1", 3333, Grant: true));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSetMode("s1", 3333, "+m"));

        ConversAction.SendMode send = Assert.Single(actions.OfType<ConversAction.SendMode>());
        Assert.Equal(3333, send.Channel);
        Assert.Equal("+m", send.Options);
        Assert.Equal(ChannelMode.Moderated, hub.GetChannel(3333).Modes);
        Assert.Equal(2, actions.OfType<ConversAction.DeliverModeChange>().Count()); // both members told
    }

    [Fact]
    public void LocalSetMode_ByNonOperator_IsRejectedAndChangesNothing()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSetMode("s1", 3333, "+m"));

        Assert.Single(actions.OfType<ConversAction.DeliverModeNotice>());
        Assert.Empty(actions.OfType<ConversAction.SendMode>());
        Assert.Equal(ChannelMode.None, hub.GetChannel(3333).Modes);
    }

    [Fact]
    public void HostMode_FromUplink_IsAuthoritative_NoOperatorCheck()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.HostMode(3333, "+mt"));

        Assert.Equal(ChannelMode.Moderated | ChannelMode.TopicLocked, hub.GetChannel(3333).Modes);
        Assert.Single(actions.OfType<ConversAction.DeliverModeChange>());
    }

    // ---------------------------------------------------------------- +m moderated

    [Fact]
    public void Moderated_DropsAnUnvoicedSpeaker_WithANotice()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 3333));
        hub.Advance(new ConversEvent.HostMode(3333, "+m"));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSay("s2", "can I talk?"));

        Assert.Empty(actions.OfType<ConversAction.SendChannelMessage>());
        Assert.Empty(actions.OfType<ConversAction.DeliverChannelMessage>());
        ConversAction.DeliverModeNotice notice = Assert.Single(actions.OfType<ConversAction.DeliverModeNotice>());
        Assert.Equal("s2", notice.SessionId);
    }

    [Fact]
    public void Moderated_AllowsAChannelOperatorToWrite()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 3333));
        hub.Advance(new ConversEvent.HostMode(3333, "+m"));
        hub.Advance(new ConversEvent.LocalSetOperator("s1", 3333, Grant: true));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSay("s1", "ops can speak"));

        Assert.Single(actions.OfType<ConversAction.SendChannelMessage>());
        Assert.Single(actions.OfType<ConversAction.DeliverChannelMessage>()); // to s2
        Assert.Empty(actions.OfType<ConversAction.DeliverModeNotice>());
    }

    // ---------------------------------------------------------------- +l local (not forwarded upstream)

    [Fact]
    public void Local_ChannelMessage_IsNotForwardedUpstream_ButStillDeliveredLocally()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 3333));
        hub.Advance(new ConversEvent.HostMode(3333, "+l"));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSay("s1", "stays local"));

        Assert.Empty(actions.OfType<ConversAction.SendChannelMessage>()); // suppressed upstream
        ConversAction.DeliverChannelMessage deliver = Assert.Single(actions.OfType<ConversAction.DeliverChannelMessage>());
        Assert.Equal("s2", deliver.SessionId);
        Assert.True(hub.GetChannel(3333).IsLocal);
    }

    // ---------------------------------------------------------------- +p / +i invite-only

    [Fact]
    public void InviteOnly_BlocksAnUninvitedJoin()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.HostMode(7000, "+i"));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 7000));

        Assert.Single(actions.OfType<ConversAction.DeliverModeNotice>());
        Assert.Empty(actions.OfType<ConversAction.SendUser>());
        Assert.Equal(0, hub.LocalSessionCount);
    }

    [Fact]
    public void InviteOnly_AllowsAnInvitedJoin()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.HostMode(7000, "+p"));
        // An inviter on another channel invites M0LTE to the private channel.
        hub.Advance(new ConversEvent.HostInvite("DL9SAU", "M0LTE", 7000));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 7000));

        Assert.Single(actions.OfType<ConversAction.SendUser>());
        Assert.Equal(1, hub.LocalSessionCount);
        Assert.Empty(actions.OfType<ConversAction.DeliverModeNotice>());
    }

    [Fact]
    public void InviteOnly_BlocksAnUninvitedChannelSwitch()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 100));
        hub.Advance(new ConversEvent.HostMode(7000, "+p"));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSwitchChannel("s1", 7000));

        Assert.Single(actions.OfType<ConversAction.DeliverModeNotice>());
        Assert.Equal(100, hub.GetSession("s1")!.Channel); // did not move
    }

    // ---------------------------------------------------------------- +s / +i hidden from listings

    [Fact]
    public void SecretAndInvisibleChannels_AreHiddenFromTheChannelListing_ButQueryableDirectly()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));   // visible
        hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 4444));   // will be secret
        hub.Advance(new ConversEvent.LocalJoin("s3", "G8XYZ", 5555));   // will be invisible
        hub.Advance(new ConversEvent.HostMode(4444, "+s"));
        hub.Advance(new ConversEvent.HostMode(5555, "+i"));

        IReadOnlyList<Channel> listed = hub.ListChannels();

        Assert.Contains(listed, c => c.Number == 3333);
        Assert.DoesNotContain(listed, c => c.Number == 4444);
        Assert.DoesNotContain(listed, c => c.Number == 5555);

        // An operator/sysop view can include them, and a direct lookup always works.
        Assert.Contains(hub.ListChannels(includeHidden: true), c => c.Number == 4444);
        Assert.Equal(ChannelMode.Secret, hub.GetChannel(4444).Modes);
    }

    // ---------------------------------------------------------------- +t topic-locked

    [Fact]
    public void TopicLocked_BlocksANonOperatorTopicSet()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.HostMode(3333, "+t"));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSetTopic("s1", "I am not an op"));

        Assert.Single(actions.OfType<ConversAction.DeliverModeNotice>());
        Assert.Empty(actions.OfType<ConversAction.SendTopic>());
        Assert.Equal(string.Empty, hub.GetChannel(3333).Topic);
    }

    [Fact]
    public void TopicLocked_AllowsAChannelOperatorTopicSet()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.HostMode(3333, "+t"));
        hub.Advance(new ConversEvent.LocalSetOperator("s1", 3333, Grant: true));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSetTopic("s1", "op topic"));

        Assert.Single(actions.OfType<ConversAction.SendTopic>());
        Assert.Equal("op topic", hub.GetChannel(3333).Topic);
    }

    // ---------------------------------------------------------------- /..OPER tracking

    [Fact]
    public void HostOper_GrantsChannelOperatorAndReflectsInSnapshot()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.HostUser("DL9SAU", "DB0SAO", T0, -1, 3333, "Thomas", false));

        hub.Advance(new ConversEvent.HostOper("DL9SAU", "DB0SAO", 3333, Grant: true));

        Assert.True(hub.NetworkUsers.Single(u => u.Name == "DL9SAU").IsOperator);
    }

    [Fact]
    public void HostOper_GlobalOperator_CanSetModesAndBypassInviteOnly()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.HostMode(7000, "+p"));
        hub.Advance(new ConversEvent.HostOper("M0LTE", MyHost, -1, Grant: true)); // global op, before join

        IReadOnlyList<ConversAction> join = hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 7000));
        Assert.Single(join.OfType<ConversAction.SendUser>()); // global op bypasses invite-only

        // And the global op can set modes on a channel they are on.
        IReadOnlyList<ConversAction> mode = hub.Advance(new ConversEvent.LocalSetMode("s1", 7000, "+m"));
        Assert.Single(mode.OfType<ConversAction.SendMode>());
    }

    // ---------------------------------------------------------------- away (set / clear / who)

    [Fact]
    public void LocalSetAway_ThenClear_TogglesAwayAndPropagatesUpstream()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        ConversAction.SendAway set = Assert.Single(
            hub.Advance(new ConversEvent.LocalSetAway("s1", "gone fishing")).OfType<ConversAction.SendAway>());
        Assert.Equal("gone fishing", set.Text);
        Assert.True(hub.GetSession("s1")!.IsAway);
        Assert.True(hub.NetworkUsers.Single().IsAway); // reflected in the who/snapshot

        ConversAction.SendAway clear = Assert.Single(
            hub.Advance(new ConversEvent.LocalSetAway("s1", "")).OfType<ConversAction.SendAway>());
        Assert.Equal(string.Empty, clear.Text);
        Assert.False(hub.GetSession("s1")!.IsAway);
        Assert.False(hub.NetworkUsers.Single().IsAway);
    }

    [Fact]
    public void HostAway_FromUplink_MarksRemoteUser_AndClearsOnEmptyText()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.HostUser("DL9SAU", "DB0SAO", T0, -1, 3333, "Thomas", false));

        hub.Advance(new ConversEvent.HostAway("DL9SAU", "DB0SAO", T0, "back at 1800z"));
        Assert.True(hub.NetworkUsers.Single().IsAway);
        Assert.Equal("back at 1800z", hub.NetworkUsers.Single().Away);

        hub.Advance(new ConversEvent.HostAway("DL9SAU", "DB0SAO", T0, ""));
        Assert.False(hub.NetworkUsers.Single().IsAway);
    }

    // ---------------------------------------------------------------- topic persistence + newest-wins + propagate

    [Fact]
    public void LocalSetTopic_PersistsPropagatesAndNewestWinsAcrossAReopen()
    {
        using var ts = new TestStore();
        var hub = new ConversHub(MyHost, ts.Time, ts.Store);
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSetTopic("s1", "newest topic"));
        Assert.Single(actions.OfType<ConversAction.SendTopic>());                 // propagated upstream
        Assert.Single(actions.OfType<ConversAction.DeliverTopic>());              // and to the channel
        Assert.Equal("newest topic", ts.Store.GetTopic(3333)!.Topic);            // persisted

        // An older host topic must not overwrite the newer stored one (SPECS /..TOPI newest-wins).
        var hub2 = new ConversHub(MyHost, ts.Time, ts.Store);
        Assert.Equal("newest topic", hub2.GetChannel(3333).Topic);               // hydrated from store
        IReadOnlyList<ConversAction> older = hub2.Advance(
            new ConversEvent.HostTopic("DL9SAU", "DB0SAO", T0 - TimeSpan.FromHours(1), 3333, "older topic"));
        Assert.Empty(older);
        Assert.Equal("newest topic", hub2.GetChannel(3333).Topic);
    }
}
