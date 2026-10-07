using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Inspection;

/// <summary>Read-only inspection of the live aura holders on an online player.</summary>
public sealed class AuraInspectionCommands : ICommandGroup
{
    private const int PageSize = 12;
    private const int MaxPage = 100;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("auras", AccountSecurity.GameMaster,
            "Syntax: .auras [page]\nDisplay active auras on the selected player or yourself (12 per page).", Auras),
    ];

    private static bool Auras(CommandContext context, string args)
    {
        string text = args.Trim();
        int page = 1;
        if (text.Length != 0 && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page)
            || page < 1 || page > MaxPage))
        {
            return false;
        }

        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply(GmStrings.NoCharSelected);
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        IReadOnlyList<SpellAuraHolder> active = context.Session.Services.GetRequiredService<SpellFeature>().System
            .GetAuras(target).Where(holder => !holder.IsRemoved).ToArray();
        if (active.Count == 0)
        {
            context.Reply($"{target.Name} has no active auras.");
            return true;
        }

        int start = (page - 1) * PageSize;
        if (start >= active.Count)
        {
            context.Reply($"Aura page {page} is empty; {active.Count} active aura holders total.");
            return true;
        }

        IReadOnlyList<SpellAuraHolder> pageHolders = active.Skip(start).Take(PageSize).ToArray();
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Active auras on {target.Name}: page {page}, {pageHolders.Count} shown of {active.Count}"));
        foreach (SpellAuraHolder holder in pageHolders)
        {
            string effects = string.Join(", ", holder.Auras.OfType<SpellAura>().Take(3).Select(aura =>
                string.Create(CultureInfo.InvariantCulture,
                    $"effect {aura.EffectIndex} type {aura.Type}({(byte)aura.Type}) amount {aura.Amount} misc {aura.MiscValue}")));
            string name = holder.Spell.Name.Replace('\r', ' ').Replace('\n', ' ');
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"spell {holder.Spell.Id} name {name}, caster {holder.CasterGuid.Value}, duration {holder.Duration}ms, {effects}"));
        }

        if (start + pageHolders.Count < active.Count)
        {
            context.Reply($"Additional aura holders available on page {page + 1}.");
        }

        return true;
    }
}
