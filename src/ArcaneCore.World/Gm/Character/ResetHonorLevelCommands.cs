using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Character;

/// <summary>
/// <c>.reset honor [$name]</c> and <c>.reset level [$name]</c> (vmangos resetCommandTable, SEC_DEVELOPER; HandleResetHonorCommand and
/// HandleResetLevelCommand, CharacterCommands.cpp) for an online player.
/// <para>
/// Differences: <c>.reset level</c> only sets the start level with the experience cleared (as <c>.levelup</c> applies a level: base stats,
/// talent points); the display, faction and unit flag part of HandleResetStatsOrLevelHelper and the pet level sync are left out. Both
/// commands answer, where vmangos is silent.
/// </para>
/// </summary>
public sealed class ResetHonorLevelExtension : ICommandExtension
{
    public const string HonorUnavailable = "The honor system is disabled on this server.";

    public string Path => "reset";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("honor", AccountSecurity.Administrator, "Syntax: .reset honor [$playername]\nForget all honor of the selected or named player.",
            ResetHonor, RetailLevel: 5),
        new ChatCommand("level", AccountSecurity.Administrator, "Syntax: .reset level [$playername]\nSet the selected or named player back to the start level with no experience.",
            ResetLevel, RetailLevel: 5),
    ];

    public static string HonorReset(string name) => $"Honor of {GmStrings.PlayerLink(name)} reset.";

    public static string LevelReset(string name, int level) => $"Level of {GmStrings.PlayerLink(name)} reset to {level}.";

    private static bool ResetHonor(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!GmTargets.TryPlayer(context, args, out Player target) || !context.CanActOn(target))
        {
            return true;
        }

        if (context.Session.Services.GetService<HonorFeature>()?.ActiveService is not { } honor)
        {
            context.Reply(HonorUnavailable);
            return true;
        }

        honor.Reset(target);
        context.Reply(HonorReset(target.Name));
        return true;
    }

    private static bool ResetLevel(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!GmTargets.TryPlayer(context, args, out Player target) || !context.CanActOn(target)
            || context.Session.Services.GetService<ProgressionFeature>()?.Progression is not { } progression)
        {
            return true;
        }

        int start = context.Session.Services.GetService<CharacterCreationFeature>()?.Options.StartPlayerLevel ?? 1;
        int level = Math.Clamp(start, 1, Math.Max(1, (int)progression.MaxPlayerLevel));
        LevelCommands.ApplyLevel(context, target, progression, level);
        context.Reply(LevelReset(target.Name, level));
        return true;
    }
}