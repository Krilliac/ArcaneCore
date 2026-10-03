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
    /// vmangos <c>ChatHandler::isValidChatMessage</c> for severities 1 and 2 (Chat.cpp:2165-2208).
    /// Every '|' must be followed by one of <c>c H h r |</c>; at severity 2 the commands
    /// <c>c H h h r</c> must additionally occur in that cyclic order (an escaped <c>||</c> is
    /// always fine). A sequence still open at the end of the message is accepted, as in vmangos.
    /// Severity 3 (catalog checks) is not implemented and is treated as 2.
    /// </summary>
    public static bool IsValidChatMessage(string message, int severity)
    {
        severity = Math.Min(severity, 2);
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
            if (severity == 2)
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
        }

        return true;
    }
}
