using Convers.Core;
using Microsoft.Extensions.Time.Testing;

namespace Convers.Core.Tests;

/// <summary>
/// The sans-IO presence model: <see cref="ConversHub.Advance"/> bridging local sessions ⇄ the one
/// uplink. Each test drives the hub with events and asserts on the returned <see cref="ConversAction"/>
/// fan-out and the model snapshot. No store unless a test exercises persistence.
/// </summary>
public sealed class ConversHubTests
{
    private const string MyHost = "GB7PDN";
    private static readonly DateTimeOffset T0 = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    private static ConversHub NewHub(ConversStore? store = null) =>
        new(MyHost, new FakeTimeProvider(T0), store);

    // ---------------------------------------------------------------- local join / leave

    [Fact]
    public void LocalJoin_EmitsUpstreamUserAndRecordsPresence()
    {
        ConversHub hub = NewHub();

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        ConversAction.SendUser send = Assert.Single(actions.OfType<ConversAction.SendUser>());
        Assert.Equal("M0LTE", send.User);
        Assert.Equal(MyHost, send.Host);
        Assert.Equal(-1, send.FromChannel); // fresh join
        Assert.Equal(3333, send.ToChannel);

        NetworkUser user = Assert.Single(hub.NetworkUsers);
        Assert.Equal("M0LTE", user.Name);
        Assert.Equal(MyHost, user.Host);
        Assert.Equal(3333, user.Channel);
    }

    [Fact]
    public void LocalJoin_RejectsInvalidChannel()
    {
        ConversHub hub = NewHub();
        Assert.Empty(hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 40000)));
        Assert.Equal(0, hub.LocalSessionCount);
    }

    [Fact]
    public void LocalJoin_NotifiesOtherLocalUsersOnTheSameChannel()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 3333));

        ConversAction.DeliverJoinNotice notice = Assert.Single(actions.OfType<ConversAction.DeliverJoinNotice>());
        Assert.Equal("s1", notice.SessionId); // the existing user is told, not the joiner
        Assert.Equal("G4ABC", notice.User);
    }

    [Fact]
    public void LocalLeave_SignsOffUpstreamAndNotifiesChannelAndForgetsTheUser()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalLeave("s2", "73"));

        ConversAction.SendUser send = Assert.Single(actions.OfType<ConversAction.SendUser>());
        Assert.Equal(-1, send.ToChannel); // sign-off
        Assert.Equal("73", send.Personal); // reason carried in the personal slot
        Assert.Contains(actions.OfType<ConversAction.DeliverLeaveNotice>(), n => n.SessionId == "s1");
        Assert.DoesNotContain(hub.NetworkUsers, u => u.Name == "G4ABC");
    }

    // ---------------------------------------------------------------- channel switch

    [Fact]
    public void LocalSwitchChannel_LeavesOldNotifiesNewAndEmitsUpstreamMove()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 100));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSwitchChannel("s1", 200));

        ConversAction.SendUser send = Assert.Single(actions.OfType<ConversAction.SendUser>());
        Assert.Equal(100, send.FromChannel);
        Assert.Equal(200, send.ToChannel);
        Assert.Equal(200, hub.GetSession("s1")!.Channel);
    }

    [Fact]
    public void LocalSwitchChannel_ToSameChannelIsANoOp()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 100));
        Assert.Empty(hub.Advance(new ConversEvent.LocalSwitchChannel("s1", 100)));
    }

    // ---------------------------------------------------------------- channel say

    [Fact]
    public void LocalSay_FansOutToOtherLocalUsersAndUpstream_NotBackToSpeaker()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 3333));
        hub.Advance(new ConversEvent.LocalJoin("s3", "G8XYZ", 9999)); // different channel

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSay("s1", "hello all"));

        ConversAction.SendChannelMessage send = Assert.Single(actions.OfType<ConversAction.SendChannelMessage>());
        Assert.Equal("M0LTE", send.User);
        Assert.Equal(3333, send.Channel);
        Assert.Equal("hello all", send.Text);

        ConversAction.DeliverChannelMessage deliver = Assert.Single(actions.OfType<ConversAction.DeliverChannelMessage>());
        Assert.Equal("s2", deliver.SessionId); // only the other same-channel user
        Assert.Equal("hello all", deliver.Text);
    }

    [Fact]
    public void LocalSay_EmptyTextIsIgnored()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        Assert.Empty(hub.Advance(new ConversEvent.LocalSay("s1", "")));
    }

    // ---------------------------------------------------------------- private message

    [Fact]
    public void LocalPrivateMessage_DeliversToLocalTargetAndMirrorsUpstream()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(
            new ConversEvent.LocalPrivateMessage("s1", "G4ABC", "psst"));

        ConversAction.DeliverPrivateMessage deliver = Assert.Single(actions.OfType<ConversAction.DeliverPrivateMessage>());
        Assert.Equal("s2", deliver.SessionId);
        Assert.Equal("M0LTE", deliver.FromUser);
        Assert.Single(actions.OfType<ConversAction.SendPrivateMessage>());
    }

    [Fact]
    public void LocalPrivateMessage_ToRemoteUserGoesUpstreamOnly()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(
            new ConversEvent.LocalPrivateMessage("s1", "DL9SAU", "hi"));

        Assert.Empty(actions.OfType<ConversAction.DeliverPrivateMessage>());
        ConversAction.SendPrivateMessage send = Assert.Single(actions.OfType<ConversAction.SendPrivateMessage>());
        Assert.Equal("DL9SAU", send.To);
    }

    [Fact]
    public void LocalPrivateMessage_ToAwayRemoteUserAddsAwayNotice()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.HostUser("DL9SAU", "DB0SAO", T0, -1, 3333, "Thomas", false));
        hub.Advance(new ConversEvent.HostAway("DL9SAU", "DB0SAO", T0, "back at 1800z"));

        IReadOnlyList<ConversAction> actions = hub.Advance(
            new ConversEvent.LocalPrivateMessage("s1", "DL9SAU", "hi"));

        ConversAction.DeliverAwayNotice notice = Assert.Single(actions.OfType<ConversAction.DeliverAwayNotice>());
        Assert.Equal("s1", notice.SessionId);
        Assert.Equal("back at 1800z", notice.Away);
    }

    // ---------------------------------------------------------------- personal / away (identity)

    [Fact]
    public void LocalSetPersonal_EmitsUdatUpstreamAndUpdatesPresence()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSetPersonal("s1", "Tom in Bath"));

        ConversAction.SendPersonal send = Assert.Single(actions.OfType<ConversAction.SendPersonal>());
        Assert.Equal("Tom in Bath", send.Text);
        Assert.Equal("Tom in Bath", hub.GetSession("s1")!.Personal);
        Assert.Equal("Tom in Bath", hub.NetworkUsers.Single().Personal);
    }

    [Fact]
    public void LocalSetPersonal_StripsTheUnauthenticatedTildeBrand()
    {
        // Decision 4: an RHP-authenticated ham is never presented with the conversd '~' brand.
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSetPersonal("s1", "~spoofed"));

        Assert.Equal("spoofed", actions.OfType<ConversAction.SendPersonal>().Single().Text);
    }

    [Fact]
    public void LocalSetAway_EmitsAwayUpstreamAndMarksUser()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSetAway("s1", "gone fishing"));

        ConversAction.SendAway send = Assert.Single(actions.OfType<ConversAction.SendAway>());
        Assert.Equal("gone fishing", send.Text);
        Assert.True(hub.GetSession("s1")!.IsAway);
    }

    // ---------------------------------------------------------------- topic

    [Fact]
    public void LocalSetTopic_EmitsTopiUpstreamAndNotifiesChannelAndUpdatesSnapshot()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalSetTopic("s1", "QSO party tonight"));

        ConversAction.SendTopic send = Assert.Single(actions.OfType<ConversAction.SendTopic>());
        Assert.Equal(3333, send.Channel);
        Assert.Equal("QSO party tonight", send.Text);
        Assert.Equal(2, actions.OfType<ConversAction.DeliverTopic>().Count()); // both members
        Assert.Equal("QSO party tonight", hub.GetChannel(3333).Topic);
    }

    [Fact]
    public void LocalJoin_DeliversExistingTopicToTheJoiner()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalSetTopic("s1", "the topic"));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 3333));

        ConversAction.DeliverTopic topic = Assert.Single(actions.OfType<ConversAction.DeliverTopic>());
        Assert.Equal("s2", topic.SessionId);
        Assert.Equal("the topic", topic.Topic);
    }

    // ---------------------------------------------------------------- host inbound: USER

    [Fact]
    public void HostUser_JoinAddsRemoteUserToTheTableAndNotifiesLocalChannel()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(
            new ConversEvent.HostUser("DL9SAU", "DB0SAO", T0, -1, 3333, "Thomas", false));

        Assert.Contains(hub.NetworkUsers, u => u is { Name: "DL9SAU", Host: "DB0SAO", Channel: 3333 });
        ConversAction.DeliverJoinNotice notice = Assert.Single(actions.OfType<ConversAction.DeliverJoinNotice>());
        Assert.Equal("s1", notice.SessionId);
        Assert.Equal("DL9SAU", notice.User);
    }

    [Fact]
    public void HostUser_SignOffRemovesUserAndNotifiesTheChannelWithReason()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.HostUser("DL9SAU", "DB0SAO", T0, -1, 3333, "Thomas", false));

        IReadOnlyList<ConversAction> actions = hub.Advance(
            new ConversEvent.HostUser("DL9SAU", "DB0SAO", T0, 3333, -1, "link down", false));

        Assert.DoesNotContain(hub.NetworkUsers, u => u.Name == "DL9SAU");
        ConversAction.DeliverLeaveNotice leave = Assert.Single(actions.OfType<ConversAction.DeliverLeaveNotice>());
        Assert.Equal("link down", leave.Reason);
    }

    [Fact]
    public void HostUser_PresenceClaimingOurOwnHostIsIgnored()
    {
        // Loop/echo guard: we are the authority for users on our own host name.
        ConversHub hub = NewHub();
        IReadOnlyList<ConversAction> actions = hub.Advance(
            new ConversEvent.HostUser("M0LTE", MyHost, T0, -1, 3333, "Tom", false));

        Assert.Empty(actions);
        Assert.Empty(hub.NetworkUsers);
    }

    [Fact]
    public void HostUser_ChannelSwitchMovesRemoteUser()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 200));
        hub.Advance(new ConversEvent.HostUser("DL9SAU", "DB0SAO", T0, -1, 100, "Thomas", false));

        hub.Advance(new ConversEvent.HostUser("DL9SAU", "DB0SAO", T0, 100, 200, "Thomas", false));

        NetworkUser remote = hub.NetworkUsers.Single(u => u.Name == "DL9SAU");
        Assert.Equal(200, remote.Channel);
    }

    // ---------------------------------------------------------------- host inbound: messages

    [Fact]
    public void HostChannelMessage_FansOutToLocalUsersOnThatChannelOnly()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalJoin("s2", "G4ABC", 9999));

        IReadOnlyList<ConversAction> actions = hub.Advance(
            new ConversEvent.HostChannelMessage("DL9SAU", 3333, "hi from the network"));

        ConversAction.DeliverChannelMessage deliver = Assert.Single(actions.OfType<ConversAction.DeliverChannelMessage>());
        Assert.Equal("s1", deliver.SessionId);
        Assert.Equal("DL9SAU", deliver.FromUser);
    }

    [Fact]
    public void HostChannelMessage_WithNoLocalListenersIsEmptyFanOut()
    {
        ConversHub hub = NewHub();
        Assert.Empty(hub.Advance(new ConversEvent.HostChannelMessage("DL9SAU", 3333, "anyone?")));
    }

    [Fact]
    public void HostPrivateMessage_DeliveredToAddressedLocalSession()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(
            new ConversEvent.HostPrivateMessage("DL9SAU", "M0LTE", "private hello"));

        ConversAction.DeliverPrivateMessage deliver = Assert.Single(actions.OfType<ConversAction.DeliverPrivateMessage>());
        Assert.Equal("s1", deliver.SessionId);
        Assert.Equal("DL9SAU", deliver.FromUser);
    }

    // ---------------------------------------------------------------- host inbound: topic newer-wins

    [Fact]
    public void HostTopic_AppliesAndNotifiesLocalChannel()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(
            new ConversEvent.HostTopic("DL9SAU", "DB0SAO", T0, 3333, "network topic"));

        Assert.Equal("network topic", hub.GetChannel(3333).Topic);
        Assert.Single(actions.OfType<ConversAction.DeliverTopic>());
    }

    [Fact]
    public void HostTopic_OlderTopicDoesNotOverwriteNewer()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.HostTopic("A", "H", T0, 3333, "newer"));

        IReadOnlyList<ConversAction> actions = hub.Advance(
            new ConversEvent.HostTopic("B", "H", T0 - TimeSpan.FromHours(1), 3333, "older"));

        Assert.Empty(actions);
        Assert.Equal("newer", hub.GetChannel(3333).Topic);
    }

    // ---------------------------------------------------------------- host inbound: personal / invite

    [Fact]
    public void HostPersonal_UpdatesRemoteUserPersonalText()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.HostUser("DL9SAU", "DB0SAO", T0, -1, 3333, "@", false));

        hub.Advance(new ConversEvent.HostPersonal("DL9SAU", "DB0SAO", "Thomas, near Stuttgart"));

        Assert.Equal("Thomas, near Stuttgart", hub.NetworkUsers.Single().Personal);
    }

    [Fact]
    public void HostInvite_DeliveredToAddressedLocalSession()
    {
        ConversHub hub = NewHub();
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));

        IReadOnlyList<ConversAction> actions = hub.Advance(new ConversEvent.HostInvite("DL9SAU", "M0LTE", 7000));

        ConversAction.DeliverInvite invite = Assert.Single(actions.OfType<ConversAction.DeliverInvite>());
        Assert.Equal("s1", invite.SessionId);
        Assert.Equal(7000, invite.Channel);
    }

    // ---------------------------------------------------------------- keepalive / loop guard

    [Fact]
    public void HostPing_AnswersWithAPong()
    {
        ConversHub hub = NewHub();
        ConversAction.SendPong pong = Assert.Single(hub.Advance(new ConversEvent.HostPing()).OfType<ConversAction.SendPong>());
        Assert.Equal(-1, pong.MillisecondsOrSentinel); // we do not measure (yet)
    }

    [Fact]
    public void HostPong_IsAccepted_NoFanOut()
    {
        ConversHub hub = NewHub();
        Assert.Empty(hub.Advance(new ConversEvent.HostPong(42)));
    }

    [Fact]
    public void HostLoop_DropsTheUplink()
    {
        ConversHub hub = NewHub();
        ConversAction.DropUplink drop = Assert.Single(
            hub.Advance(new ConversEvent.HostLoop("DB0SAO")).OfType<ConversAction.DropUplink>());
        Assert.Contains("DB0SAO", drop.Reason);
    }

    [Fact]
    public void HostUnknown_IsANoOpForAStrictLeaf()
    {
        ConversHub hub = NewHub();
        Assert.Empty(hub.Advance(new ConversEvent.HostUnknown("/ÿFROB whatever")));
    }

    // ---------------------------------------------------------------- persistence write-through

    [Fact]
    public void LocalSetPersonal_PersistsToStoreAndSurvivesRejoin()
    {
        using var ts = new TestStore();
        var hub = new ConversHub(MyHost, ts.Time, ts.Store);
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalSetPersonal("s1", "Tom, Bath IO81"));

        Assert.Equal("Tom, Bath IO81", ts.Store.GetProfile("M0LTE")!.Personal);

        // A fresh hub on the same store hydrates the persisted personal text on rejoin.
        var hub2 = new ConversHub(MyHost, ts.Time, ts.Store);
        IReadOnlyList<ConversAction> actions = hub2.Advance(new ConversEvent.LocalJoin("s9", "M0LTE", 100));
        Assert.Equal("Tom, Bath IO81", actions.OfType<ConversAction.SendUser>().Single().Personal);
        Assert.Equal("Tom, Bath IO81", hub2.GetSession("s9")!.Personal);
    }

    [Fact]
    public void LocalSetTopic_PersistsToStoreAndHydratesIntoANewHub()
    {
        using var ts = new TestStore();
        var hub = new ConversHub(MyHost, ts.Time, ts.Store);
        hub.Advance(new ConversEvent.LocalJoin("s1", "M0LTE", 3333));
        hub.Advance(new ConversEvent.LocalSetTopic("s1", "persisted topic"));

        Assert.Equal("persisted topic", ts.Store.GetTopic(3333)!.Topic);

        var hub2 = new ConversHub(MyHost, ts.Time, ts.Store);
        Assert.Equal("persisted topic", hub2.GetChannel(3333).Topic);
    }
}
