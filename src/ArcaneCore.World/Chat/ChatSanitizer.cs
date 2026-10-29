using System.Globalization;
using System.Text;

namespace ArcaneCore.World.Chat;

/// <summary>
/// The message clean-up vmangos runs on every non-addon chat message before commands and delivery
/// (WorldSession::SanitizeChatMessage, ChatHandler.cpp:44-64).
/// </summary>
public static class ChatSanitizer
{
    /// <summary>The longest message the link check accepts (vmangos isValidChatMessage: <c>msg.length() &gt; 255</c>, in bytes).</summary>
    public const int MaxCheckedMessageBytes = 255;

    /// <summary>The order of the pipe commands of a link: colour, hyperlink start, text start, text end, reset (vmangos "cHhhr").</summary>
    private const string ValidSequence = "cHhhr";

    /// <summary>
    /// vmangos <c>stripLineInvisibleChars</c> (shared/Util.cpp:134-163): every run of space, tab,
    /// bell (0x07) and newline becomes one space; nothing is trimmed.
    /// </summary>
    public static string StripInvisibleChars(string message)
    {
        var text = new StringBuilder(message.Length);
        bool space = false;
        foreach (char c in message)
        {
            if (c is ' ' or '\t' or '\a' or '\n')
            {
                if (!space)
                {
                    text.Append(' ');
                    space = true;
                }
            }
            else
            {
                text.Append(c);
                space = false;
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Cuts <paramref name="message"/> to at most <paramref name="maxLength"/> UTF-16 units without breaking what the client renders as one
    /// piece: the cut never falls between the halves of a surrogate pair, and never inside a colour (<c>|c ... |r</c>) or hyperlink
    /// (<c>|H ... |h text |h</c>) sequence or after a lone pipe; such a sequence is dropped whole, so the result can be shorter than
    /// <paramref name="maxLength"/> (even empty). A message that already fits is returned unchanged. Mirrors the pipe grammar of
    /// <see cref="IsValidChatMessage(string, int)"/> (<c>||</c> is an escaped pipe).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is negative.</exception>
    public static string TruncateAtSafeBoundary(string message, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        if (message.Length <= maxLength)
        {
            return message;
        }

        int cut = maxLength;
        if (cut > 0 && char.IsHighSurrogate(message[cut - 1]) && char.IsLowSurrogate(message[cut]))
        {
            cut--;
        }

        int open = -1; // start of the colour or hyperlink sequence still open at the cut
        bool colour = false;
        bool link = false;
        int linkPipes = 0; // |h seen in the open hyperlink: the first ends the target, the second ends the text
        int i = 0;
        while (i < cut)
        {
            if (message[i] != '|')
            {
                i++;
                continue;
            }

            if (i + 1 >= cut)
            {
                // A pipe is the last unit that stays: its command (or its escaped partner) would be cut off.
                cut = open >= 0 ? open : i;
                break;
            }

            char command = message[i + 1];
            if ((command is 'c' or 'H') && open < 0)
            {
                open = i;
            }

            switch (command)
            {
                case 'c':
                    colour = true;
                    break;
                case 'H':
                    link = true;
                    linkPipes = 0;
                    break;
                case 'h':
                    if (link && ++linkPipes == 2)
                    {
                        link = false;
                    }

                    break;
                case 'r':
                    colour = false;
                    break;
                default:
                    break; // '||' or something the link check rejects anyway
            }

            if (!colour && !link)
            {
                open = -1;
            }

            i += 2;
        }

        if (i >= cut && open >= 0)
        {
            cut = open;
        }

        return message[..cut];
    }

    /// <summary>
    /// vmangos <c>ChatHandler::isValidChatMessage</c> for severities 1 and 2 (Chat.cpp:2165-2208).
    /// Every '|' must be followed by one of <c>c H h r |</c>; at severity 2 the commands
    /// <c>c H h h r</c> must additionally occur in that cyclic order (an escaped <c>||</c> is
    /// always fine). A sequence still open at the end of the message is accepted, as in vmangos.
    /// Without a catalog delegate, severity 3 falls back to severity 2.
    /// </summary>
    public static bool IsValidChatMessage(string message, int severity)
        => IsValidChatMessage(message, severity, linkExists: null);

    /// <summary>
    /// vmangos <c>ChatHandler::isValidChatMessage</c> for severities 1, 2, and 3 (Chat.cpp:2165-2270).
    /// At severity 3 with <paramref name="linkExists"/> supplied, every <c>|H&lt;type&gt;:&lt;payload&gt;|h</c>
    /// hyperlink must also carry a positive entry id accepted by <paramref name="linkExists"/>.
    /// </summary>
    public static bool IsValidChatMessage(string message, int severity, Func<string, uint, bool>? linkExists)
    {
        int checkSeverity = linkExists is null ? Math.Min(severity, 2) : Math.Min(severity, 3);
        if (Encoding.UTF8.GetByteCount(message) > MaxCheckedMessageBytes)
        {
            return false;
        }

        int sequence = 0;
        int at = 0;
        while (at < message.Length)
        {
            int pipe = message.IndexOf('|', at);
            if (pipe < 0)
            {
                return true;
            }

            at = pipe + 1;
            char command = at < message.Length ? message[at] : '\0'; // a trailing pipe reads the terminator: not a valid command
            if (command is not ('c' or 'H' or 'h' or 'r' or '|'))
            {
                return false;
            }

            at++;
            if (checkSeverity >= 2)
            {
                if (command == ValidSequence[sequence])
                {
                    sequence = sequence == ValidSequence.Length - 1 ? 0 : sequence + 1;
                }
                else if (command != '|')
                {
                    return false;
                }
            }

            if (command == 'H' && checkSeverity >= 3)
            {
                int nextPipe = message.IndexOf('|', at);
                if (nextPipe <= at)
                {
                    return false;
                }

                ReadOnlySpan<char> linkSpan = message.AsSpan(at, nextPipe - at);
                int colon = linkSpan.IndexOf(':');
                if (colon <= 0 || colon + 1 >= linkSpan.Length)
                {
                    return false;
                }

                string linkType = linkSpan[..colon].ToString();
                ReadOnlySpan<char> payload = linkSpan[(colon + 1)..];
                if (!ValidateLinkPayload(linkType, payload, checkSeverity, linkExists))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Validates the colon-delimited hyperlink payload following <c>|H&lt;type&gt;:</c> at severity 3.
    /// </summary>
    public static bool ValidateLinkPayload(string linkType, ReadOnlySpan<char> payload, int severity, Func<string, uint, bool>? linkExists)
    {
        if (severity < 3 || linkExists is null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(linkType) || payload.IsEmpty)
        {
            return false;
        }

        int colon = payload.IndexOf(':');
        ReadOnlySpan<char> idSpan = colon >= 0 ? payload[..colon] : payload;
        if (!uint.TryParse(idSpan, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0)
        {
            return false;
        }

        return linkExists(linkType, id);
    }
}

