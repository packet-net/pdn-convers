using System.Text;

namespace Convers.Core;

/// <summary>
/// Parsing and formatting of the convers channel-mode toggle string (<c>/..MODE &lt;channel&gt;
/// &lt;options&gt;</c>, SPECS line 95). The wire form is a sequence of <c>+</c>/<c>-</c> signs and
/// the letters <c>silptm</c>: a sign selects set/clear for the letters that follow it, defaulting to
/// set. Letter ↔ flag mapping mirrors <c>conversd</c>'s <c>mode_command()</c> in <c>user.c</c> and the
/// <c>M_CHAN_*</c> bits: <c>s</c>=secret, <c>p</c>=private, <c>t</c>=topic-locked, <c>i</c>=invisible,
/// <c>m</c>=moderated, <c>l</c>=local. Unknown letters and stray whitespace are ignored, matching the
/// reference's tolerant loop. Case-insensitive.
/// </summary>
public static class ChannelModes
{
    /// <summary>
    /// Applies a toggle string to a starting mode set and returns the result. A leading <c>+</c> (or
    /// no sign) sets, a <c>-</c> clears, until the next sign. Unrecognised characters are skipped.
    /// Channel 0 is special in <c>conversd</c> (it ignores every letter except <c>+t</c>); pass
    /// <paramref name="isChannelZero"/> true to honour that rule.
    /// </summary>
    public static ChannelMode Apply(ChannelMode start, string options, bool isChannelZero = false)
    {
        ArgumentNullException.ThrowIfNull(options);

        ChannelMode modes = start;
        bool removing = false;
        foreach (char raw in options)
        {
            switch (raw)
            {
                case '+':
                    removing = false;
                    continue;
                case '-':
                    removing = true;
                    continue;
            }

            ChannelMode? flag = char.ToLowerInvariant(raw) switch
            {
                's' => ChannelMode.Secret,
                'p' => ChannelMode.Private,
                't' => ChannelMode.TopicLocked,
                'i' => ChannelMode.Invisible,
                'm' => ChannelMode.Moderated,
                'l' => ChannelMode.Local,
                _ => null,
            };

            if (flag is not { } bit)
            {
                continue; // tolerate whitespace / unknown letters, as conversd does
            }

            // Channel 0 ignores all modes except the topic lock (+t), per conversd's mode_command().
            if (isChannelZero && bit != ChannelMode.TopicLocked)
            {
                continue;
            }

            modes = removing ? modes & ~bit : modes | bit;
        }

        return modes;
    }

    /// <summary>
    /// The canonical full mode string for a mode set, in <c>conversd</c>'s <c>get_mode_flags()</c>
    /// order (<c>s p t i m l</c>) with a single leading <c>+</c> (e.g. <c>"+mt"</c>), or <c>"-"</c>
    /// when no modes are set. This is the form sent upstream so the whole network converges on the
    /// same letters regardless of how a toggle was originally expressed.
    /// </summary>
    public static string ToWire(ChannelMode modes)
    {
        var sb = new StringBuilder();
        if ((modes & ChannelMode.Secret) != 0)
        {
            sb.Append('s');
        }

        if ((modes & ChannelMode.Private) != 0)
        {
            sb.Append('p');
        }

        if ((modes & ChannelMode.TopicLocked) != 0)
        {
            sb.Append('t');
        }

        if ((modes & ChannelMode.Invisible) != 0)
        {
            sb.Append('i');
        }

        if ((modes & ChannelMode.Moderated) != 0)
        {
            sb.Append('m');
        }

        if ((modes & ChannelMode.Local) != 0)
        {
            sb.Append('l');
        }

        return sb.Length == 0 ? "-" : "+" + sb;
    }

    /// <summary>True when the channel hides its existence/number from listings (<c>+s</c> secret or <c>+i</c> invisible).</summary>
    public static bool IsHidden(ChannelMode modes) =>
        (modes & (ChannelMode.Secret | ChannelMode.Invisible)) != 0;

    /// <summary>
    /// True when joining the channel requires a standing invitation (<c>+p</c> private, and — per the
    /// W7a brief that treats <c>+i</c> as invite-only — <c>+i</c> invisible).
    /// </summary>
    public static bool RequiresInvite(ChannelMode modes) =>
        (modes & (ChannelMode.Private | ChannelMode.Invisible)) != 0;
}
