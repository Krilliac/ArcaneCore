using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Skills;

/// <summary>
/// GM skill commands (vmangos CharacterCommands.cpp HandleMaxSkillCommand / HandleSetSkillCommand, lines
/// 826-899; both SEC_GAMEMASTER in Chat.cpp:1283-1284). Each acts on the selected player, or on the invoker
/// when nothing is selected.
/// </summary>
public sealed partial class SkillCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("maxskill", AccountSecurity.GameMaster,
            "Syntax: .maxskill — set every level-dependent skill of the selected player (or yourself) to its maximum for the level.", MaxSkill),
        new ChatCommand("setskill", AccountSecurity.GameMaster,
            "Syntax: .setskill #skill #level [#max] — set a skill the selected player (or yourself) already has.", SetSkill),
    ];

    [GeneratedRegex(@"Hskill:(\d+)")]
    private static partial Regex SkillLink();

    private static SkillsFeature Feature(CommandContext context) => context.Session.Services.GetRequiredService<SkillsFeature>();

    private static Player? Target(CommandContext context)
    {
        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply("No player selected.");
            return null;
        }

        return context.CanActOn(target) ? target : null;
    }

    private static bool MaxSkill(CommandContext context, string args)
    {
        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        if (target.Skills is not { } skills)
        {
            context.Reply($"{target.Name} has no skills (the skill system is not active).");
            return true;
        }

        skills.UpdateSkillsToMax();
        return true;
    }

    private static bool SetSkill(CommandContext context, string args)
    {
        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        // "|Hskill:id|h[name]|h" (shift-click) or a plain number, then the level and an optional maximum.
        string[] parts = SkillLink().Replace(args, m => m.Groups[1].Value).Replace("|h", " ").Replace("|r", " ")
            .Split([' ', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith('H') && !p.StartsWith('[') && !p.StartsWith("cff", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int skill)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level))
        {
            return false;
        }

        SkillsFeature feature = Feature(context);
        if (target.Skills is not { } skills)
        {
            context.Reply($"{target.Name} has no skills (the skill system is not active).");
            return true;
        }

        int maxSkill = skills.GetMaxPure((uint)Math.Max(skill, 0));
        if (parts.Length > 2 && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out maxSkill))
        {
            return false;
        }

        if (skill <= 0 || feature.Catalog.Line((uint)skill) is not { } line)
        {
            context.Reply($"Invalid skill id ({skill}).");
            return true;
        }

        if (skills.GetValue((uint)skill) == 0)
        {
            context.Reply($"{target.Name} does not have skill {skill} ({line.Name}).");
            return true;
        }

        if (level <= 0 || level > maxSkill || maxSkill <= 0)
        {
            return false;
        }

        skills.Set((uint)skill, (ushort)level, (ushort)maxSkill);
        context.Reply($"Skill {skill} ({line.Name}) of {target.Name} is now {level} of {maxSkill}.");
        return true;
    }
}
