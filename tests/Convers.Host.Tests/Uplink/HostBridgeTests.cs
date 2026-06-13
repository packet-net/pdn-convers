using Convers.Core;
using Convers.Host.Uplink;
using Convers.Protocol;

namespace Convers.Host.Tests.Uplink;

/// <summary>
/// Unit tests for the sans-IO <see cref="HostBridge"/> translation between the Protocol wire commands and
/// the Core domain events/actions — the seam that lets Core and Protocol stay independent (design.md).
/// </summary>
public class HostBridgeTests
{
    [Fact]
    public void ToEvent_HostUser_MapsAllFieldsAndTimestamp()
    {
        var cmd = new HostUser("G4ABC", "REMOTE", 1_700_000_000, -1, 3333, "hi there");

        ConversEvent? evt = HostBridge.ToEvent(cmd);

        var hu = Assert.IsType<ConversEvent.HostUser>(evt);
        Assert.Equal("G4ABC", hu.User);
        Assert.Equal("REMOTE", hu.Host);
        Assert.Equal(3333, hu.ToChannel);
        Assert.Equal(-1, hu.FromChannel);
        Assert.Equal("hi there", hu.Personal);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), hu.Timestamp);
        Assert.False(hu.Observer);
    }

    [Fact]
    public void ToEvent_Observer_SetsObserverFlag()
    {
        var cmd = new HostUser("OBS", "REMOTE", 1, 0, 100, "@", IsObserver: true);

        var hu = Assert.IsType<ConversEvent.HostUser>(HostBridge.ToEvent(cmd));

        Assert.True(hu.Observer);
    }

    [Fact]
    public void ToEvent_Handshake_IsConsumedByLink_ReturnsNull()
    {
        var cmd = new HostHandshake("ORACLE", "saupp1.62a", Facilities.PingPong);

        Assert.Null(HostBridge.ToEvent(cmd));
    }

    [Fact]
    public void ToEvent_PingAndPong_MapToKeepaliveEvents()
    {
        Assert.IsType<ConversEvent.HostPing>(HostBridge.ToEvent(new HostPing()));
        var pong = Assert.IsType<ConversEvent.HostPong>(HostBridge.ToEvent(new HostPong(42)));
        Assert.Equal(42, pong.MillisecondsOrSentinel);
    }

    [Fact]
    public void ToEvent_UnknownVerb_BecomesHostUnknown_WithFormattedLine()
    {
        var cmd = new UnknownHostCommand("ZZZZ", "some payload");

        var unknown = Assert.IsType<ConversEvent.HostUnknown>(HostBridge.ToEvent(cmd));

        Assert.StartsWith(ConversCommand.HostCommandPrefix, unknown.Raw);
        Assert.Contains("ZZZZ some payload", unknown.Raw);
    }

    [Fact]
    public void ToEvent_Mode_MapsToHostModeEvent()
    {
        var cmd = new HostMode(3333, "+mt");

        var mode = Assert.IsType<ConversEvent.HostMode>(HostBridge.ToEvent(cmd));

        Assert.Equal(3333, mode.Channel);
        Assert.Equal("+mt", mode.Options);
    }

    [Fact]
    public void ToEvent_Oper_MapsToHostOperEvent_AsAGrant()
    {
        // W7b wires /..OPER into hub state so the leaf can track operator status. The wire form carries the
        // granter (FromName) and the affected user but not the user's host; the host is left blank.
        var cmd = new HostOper("conversd", 3333, "g4abc");

        var oper = Assert.IsType<ConversEvent.HostOper>(HostBridge.ToEvent(cmd));

        Assert.Equal("g4abc", oper.User);
        Assert.Equal(3333, oper.Channel);
        Assert.True(oper.Grant);
    }

    [Fact]
    public void ToHostCommand_SendMode_MapsToMode()
    {
        var cmd = HostBridge.ToHostCommand(new ConversAction.SendMode(3333, "+mt"));

        var mode = Assert.IsType<HostMode>(cmd);
        Assert.Equal(3333, mode.Channel);
        Assert.Equal("+mt", mode.Options);
    }

    [Fact]
    public void ToHostCommand_SendUser_RoundTripsThroughTheWire()
    {
        var action = new ConversAction.SendUser(
            "M0LTE", "PDNCONV", DateTimeOffset.FromUnixTimeSeconds(1_700_000_500), -1, 3333, "from the leaf");

        HostCommand? cmd = HostBridge.ToHostCommand(action);

        var hu = Assert.IsType<HostUser>(cmd);
        Assert.Equal("M0LTE", hu.User);
        Assert.Equal("PDNCONV", hu.Host);
        Assert.Equal(1_700_000_500, hu.Timestamp);
        Assert.Equal(3333, hu.ToChannel);
        Assert.Equal("from the leaf", hu.Text);

        // And the formatted line carries the real prefix bytes.
        string line = HostCommandCodec.Format(hu);
        Assert.StartsWith(ConversCommand.HostCommandPrefix, line);
    }

    [Fact]
    public void ToHostCommand_SendChannelMessage_MapsToCmsg()
    {
        var cmd = HostBridge.ToHostCommand(new ConversAction.SendChannelMessage("M0LTE", 3333, "hello"));

        var m = Assert.IsType<HostChannelMessage>(cmd);
        Assert.Equal("M0LTE", m.User);
        Assert.Equal(3333, m.Channel);
        Assert.Equal("hello", m.Text);
    }

    [Fact]
    public void ToHostCommand_SendPong_MapsToPong()
    {
        var cmd = HostBridge.ToHostCommand(new ConversAction.SendPong(-1));

        Assert.Equal(-1, Assert.IsType<HostPong>(cmd).Time);
    }

    [Theory]
    [InlineData(typeof(ConversAction.DeliverChannelMessage))]
    public void ToHostCommand_LocalDeliveryActions_AreNotWireCommands(Type actionType)
    {
        ConversAction action = actionType == typeof(ConversAction.DeliverChannelMessage)
            ? new ConversAction.DeliverChannelMessage("s1", 3333, "g4abc", "hi")
            : throw new ArgumentOutOfRangeException(nameof(actionType));

        Assert.Null(HostBridge.ToHostCommand(action));
    }

    [Fact]
    public void ToHostCommand_DropUplink_IsLinkControl_NotAWireCommand()
    {
        Assert.Null(HostBridge.ToHostCommand(new ConversAction.DropUplink("loop")));
    }

    [Fact]
    public void ToUnix_ClampsNegativeToZero()
    {
        Assert.Equal(0, HostBridge.ToUnix(DateTimeOffset.UnixEpoch - TimeSpan.FromDays(1)));
    }
}
