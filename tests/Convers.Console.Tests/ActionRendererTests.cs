using Convers.Console;
using Convers.Core;

namespace Convers.Console.Tests;

/// <summary>The Core action → display-text mapping (pure, no I/O).</summary>
public class ActionRendererTests
{
    private const string Me = "sess-1";
    private const string Other = "sess-2";

    [Fact]
    public void ChannelMessage_ToMe_RendersAngleBracketShape()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverChannelMessage(Me, 100, "G4ABC", "hello"), Me);
        Assert.Equal("<G4ABC>: hello", line);
    }

    [Fact]
    public void PrivateMessage_ToMe_RendersStarShape()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverPrivateMessage(Me, "G4ABC", "psst"), Me);
        Assert.Equal("<*G4ABC*>: psst", line);
    }

    [Fact]
    public void ModeChange_ToMe_RendersWireModeString()
    {
        // ToWire renders in conversd's canonical s-p-t-i-m-l order, so t precedes m.
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverModeChange(Me, 100, ChannelMode.Moderated | ChannelMode.TopicLocked), Me);
        Assert.Equal("*** Channel 100 modes: +tm", line);
    }

    [Fact]
    public void ModeNotice_ToMe_RendersReason()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverModeNotice(Me, 100, "You must be a channel operator to set modes."), Me);
        Assert.Equal("*** You must be a channel operator to set modes.", line);
    }

    [Fact]
    public void ModeChange_ToAnotherSession_IsNull() =>
        Assert.Null(ActionRenderer.RenderOne(
            new ConversAction.DeliverModeChange(Other, 100, ChannelMode.Secret), Me));

    [Fact]
    public void JoinNotice_RendersSentence()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverJoinNotice(Me, 100, "G4ABC"), Me);
        Assert.Equal("*** G4ABC joined channel 100", line);
    }

    [Fact]
    public void LeaveNotice_WithReason_IncludesReason()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverLeaveNotice(Me, 100, "G4ABC", "switched channel"), Me);
        Assert.Equal("*** G4ABC left channel 100 (switched channel)", line);
    }

    [Fact]
    public void LeaveNotice_NoReason_OmitsParens()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverLeaveNotice(Me, 100, "G4ABC", ""), Me);
        Assert.Equal("*** G4ABC left channel 100", line);
    }

    [Fact]
    public void Topic_Set_RendersWithSetter()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverTopic(Me, 100, "ragchew", "G4ABC"), Me);
        Assert.Equal("*** Topic of channel 100: ragchew (set by G4ABC)", line);
    }

    [Fact]
    public void Topic_Cleared_RendersCleared()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverTopic(Me, 100, "", "G4ABC"), Me);
        Assert.Equal("*** Topic of channel 100 cleared", line);
    }

    [Fact]
    public void AwayNotice_RendersAway()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverAwayNotice(Me, "G4ABC", "out to lunch"), Me);
        Assert.Equal("*** G4ABC is away: out to lunch", line);
    }

    [Fact]
    public void Invite_RendersInvitation()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverInvite(Me, "G4ABC", 100), Me);
        Assert.Equal("*** G4ABC invites you to channel 100", line);
    }

    [Fact]
    public void ActionForAnotherSession_IsNotRendered()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.DeliverChannelMessage(Other, 100, "G4ABC", "hello"), Me);
        Assert.Null(line);
    }

    [Fact]
    public void UplinkAction_IsNotRendered()
    {
        string? line = ActionRenderer.RenderOne(
            new ConversAction.SendChannelMessage("G4ABC", 100, "hello"), Me);
        Assert.Null(line);
    }

    [Fact]
    public void Render_FiltersToMineInOrder()
    {
        ConversAction[] actions =
        [
            new ConversAction.SendChannelMessage("G4ABC", 100, "up"),    // uplink, skipped
            new ConversAction.DeliverChannelMessage(Other, 100, "X", "for other"), // other, skipped
            new ConversAction.DeliverChannelMessage(Me, 100, "G4ABC", "one"),
            new ConversAction.DeliverJoinNotice(Me, 100, "G4XYZ"),
        ];

        List<string> lines = ActionRenderer.Render(actions, Me);
        Assert.Equal(["<G4ABC>: one", "*** G4XYZ joined channel 100"], lines);
    }
}
