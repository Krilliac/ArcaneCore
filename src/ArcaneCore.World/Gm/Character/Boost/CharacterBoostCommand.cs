using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>
/// <c>.character boost [$playername] #level</c> (ArcaneCore operator command; vmangos and mangos have no equivalent, so the level
/// is not a retail one): brings an online character to a level with the class's trainer spells, a geared and equipped kit, filled
/// action bars and a modest purse, for provisioning playerbots and test characters. The target is the named online player, else the
/// selected player, else the invoker. Nothing changes unless every check passes, and a level below the character's own is refused
/// (<c>.levelup -N</c> lowers a level) so "spells up to the level" stays true. See docs/areas/character-boost.md.
/// <para>
/// Added under the <c>.character</c> root another feature defines. The steps and their rules live in <see cref="CharacterBoost"/>.
/// </para>
/// </summary>
public sealed class CharacterBoostExtension : ICommandExtension
{
    public string Path => "character";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("boost", AccountSecurity.Administrator,
            "Syntax: .character boost [$playername] #level\nBring the named online player, the selected player or yourself to the level: class trainer spells, a level-appropriate equipped gear kit, filled action bars and some money.",
            Boost),
    ];

    private static bool Boost(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? nameArg = args.ExtractOptNotLastArg();
        if (!args.ExtractInt32(out int requested) || !args.IsEmpty)
        {
            return false;
        }

        ProgressionFeature? progression = context.Session.Services.GetService<ProgressionFeature>();
        if (progression is null)
        {
            context.Reply("Boost refused: progression is not available.");
            return true;
        }

        int maxLevel = progression.Progression.MaxPlayerLevel;
        if (requested < 1 || requested > maxLevel)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Boost refused: level must be between 1 and {maxLevel}."));
            return true;
        }

        if (!GmTargets.TryPlayer(context, nameArg, out Player target) || !context.CanActOn(target))
        {
            return true;
        }

        string name = target.Name;
        string? refusal = !target.IsInWorld ? "the character is not in the world"
            : !target.IsAlive ? "the character is dead"
            : target.Combat.IsInCombat ? "the character is in combat"
            : !target.CanMutateQuestSettlementState ? "the character is settling a quest reward"
            : requested < target.Level ? string.Create(CultureInfo.InvariantCulture, $"the character is level {target.Level}, above {requested}")
            : null;
        if (refusal is null)
        {
            BoostEnvironment? environment = BoostEnvironment.Resolve(context.Session.Services, out string missing);
            if (environment is null)
            {
                refusal = missing;
            }
            else
            {
                Reply(context, target, CharacterBoost.Run(context, target, (byte)requested, environment));
                return true;
            }
        }

        context.Reply($"Boost refused for {name}: {refusal}.");
        return true;
    }

    private static void Reply(CommandContext context, Player target, BoostReport report)
    {
        string name = target.Name;
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Boost {name}: level {report.OldLevel}->{report.NewLevel}, {report.SpellsLearned} spells learned, {report.Equipped + report.Kept} items equipped ({report.Kept} kept, {report.MovedToBags} moved to bags), {report.ButtonsSet} action buttons set, money {report.Money}."));
        if (report.EmptySlots.Count > 0)
        {
            context.Reply($"Boost {name}: no item for {string.Join(", ", report.EmptySlots.Select(BoostGearPlanner.SlotName))}.");
        }

        foreach ((byte slot, ArcaneCore.Game.Items.InventoryResult result) in report.SlotFailures)
        {
            context.Reply($"Boost {name}: {BoostGearPlanner.SlotName(slot)} unchanged ({result}).");
        }

        if (!ReferenceEquals(target, context.Player))
        {
            target.SendSystemMessage(string.Create(CultureInfo.InvariantCulture,
                $"{GmStrings.PlayerLink(context.Player.Name)} boosted you to level {report.NewLevel}."));
        }
    }
}
