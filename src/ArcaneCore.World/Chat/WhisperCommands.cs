using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Chat;

/// <summary>
/// <c>.whispers [on|off]</c> (vmangos Chat.cpp:1285 SEC_MODERATOR, CharacterCommands.cpp:1283
/// HandleWhispersCommand): whether the game master accepts whispers from plain players. Without an
/// argument it reports the state; switching off also forgets the players the staff member whispered.
/// The state is not saved (docs/areas/chat.md: GM.WhisperingTo = 2).
/// </summary>
public sealed class WhisperCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("whispers", AccountSecurity.Moderator,
            "Syntax: .whispers on|off\n\nEnable/disable accepting whispers by GM from players. By default use the World:Chat:GmWhisperingTo setting.",
            Whispers),
    ];

    private static bool Whispers(CommandContext context, string args)
    {
        ChatFeature chat = context.Session.Services.GetRequiredService<ChatFeature>();
        string argument = args.Trim();
        if (argument.Length == 0)
        {
            context.Reply($"Accepting Whisper: {(chat.AcceptsWhispers(context.Player) ? "ON" : "OFF")}"); // mangos_string 284 with GetOnOffStr
            return true;
        }

        // ExtractOnOff (Chat.cpp:2954): the literal "on"/"ON"/"off"/"OFF" and nothing else.
        string word = argument.Split(' ', 2)[0];
        bool on;
        if (word is "on" or "ON")
        {
            on = true;
        }
        else if (word is "off" or "OFF")
        {
            on = false;
        }
        else
        {
            context.Reply("Incorrect value, use on or off"); // mangos_string 259 LANG_USE_BOL
            return true;
        }

        chat.SetAcceptWhispers(context.Player, on);
        if (!on)
        {
            chat.ClearAllowedWhisperers(context.Player);
        }

        context.Reply(on ? "Accepting Whisper: ON" : "Accepting Whisper: OFF"); // mangos_string 285 / 286
        return true;
    }
}
