namespace Convers.Core;

/// <summary>
/// Convers channel modes (<c>/..MODE &lt;channel&gt; &lt;options&gt;</c>, SPECS line 95; the
/// <c>M_CHAN_*</c> bitfield in <c>conversd.h struct channel</c>). A leaf honours these for display
/// and the <c>+l</c> forwarding suppression; full enforcement (e.g. <c>+m</c> moderation) is a W7
/// SHOULD. The flag values mirror the C <c>M_CHAN_*</c> bits so a future wire mapper is mechanical.
/// </summary>
[Flags]
public enum ChannelMode
{
    /// <summary>No modes set.</summary>
    None = 0,

    /// <summary><c>+s</c> secret channel — its number is not displayed (<c>M_CHAN_S</c>).</summary>
    Secret = 0x01,

    /// <summary><c>+p</c> private channel — join by invitation only (<c>M_CHAN_P</c>).</summary>
    Private = 0x02,

    /// <summary><c>+t</c> topic settable by channel operators only (<c>M_CHAN_T</c>).</summary>
    TopicLocked = 0x04,

    /// <summary><c>+i</c> invisible channel — existence not displayed except to operators (<c>M_CHAN_I</c>).</summary>
    Invisible = 0x08,

    /// <summary><c>+m</c> moderated channel — only operators may write (<c>M_CHAN_M</c>).</summary>
    Moderated = 0x10,

    /// <summary><c>+l</c> local channel — no text forwarding to links (<c>M_CHAN_L</c>).</summary>
    Local = 0x20,
}
