using ArcaneCore.Data.Npc;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Combat;

/// <summary>
/// Registers faction-aware combat and the production ghost-form spell adapter (an
/// <see cref="IWorldFeature"/>, discovered). The catalog is a registered
/// <see cref="FactionTemplateCatalog"/>, else <c>Creatures:FactionTemplateDbcPath</c> (the same
/// source the creature feature uses). With no loaded catalog combat keeps its default faction rules.
/// Faction registration never overrides earlier hooks. When spells are available a decorator adds
/// ghost form while forwarding every other callback to the selected hooks.
/// </summary>
public sealed class WorldCombatHooksFeature(IServiceProvider services, ILogger<WorldCombatHooksFeature> logger) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        var options = new CreatureOptions();
        services.GetService<IConfiguration>()?.GetSection(CreatureOptions.SectionName).Bind(options);
        FactionTemplateCatalog? catalog = services.GetService<FactionTemplateCatalog>()
            ?? (string.IsNullOrWhiteSpace(options.FactionTemplateDbcPath) ? null : FactionTemplateDbcReader.Load(options.FactionTemplateDbcPath));
        if (catalog is null || catalog.Count == 0)
        {
            logger.LogWarning("No faction templates loaded: combat keeps the permissive default hooks (players can attack any non-player unit)");
        }
        else if (!CombatHooks.TryRegister(world, new FactionCombatHooks(catalog)))
        {
            logger.LogWarning("Combat hooks were already registered by another feature: faction-aware hooks not installed");
        }

        // SpellFeature is constructed before attachment and loads its store later. Keep the singleton
        // rather than the current store so imported content and character spellbooks are read at use.
        if (services.GetService<SpellFeature>() is { } spells)
        {
            CombatHooks.Register(world, new GhostCombatHooks(CombatHooks.For(world), spells));
        }
    }

    private sealed class GhostCombatHooks(CombatHooks inner, SpellFeature spells) : CombatHooks
    {
        public override bool IsFriendly(Unit a, Unit b) => inner.IsFriendly(a, b);
        public override bool IsHostileTo(Unit a, Unit b) => inner.IsHostileTo(a, b);
        public override bool CanAttack(Unit attacker, Unit victim) => inner.CanAttack(attacker, victim);
        public override int GetWeaponSkill(Unit unit, WeaponAttackType attackType, Unit? victim) => inner.GetWeaponSkill(unit, attackType, victim);
        public override int GetDefenseSkill(Unit unit, Unit? attacker) => inner.GetDefenseSkill(unit, attacker);
        public override bool HasOffhandWeapon(Unit unit) => inner.HasOffhandWeapon(unit);
        public override bool PlayerCanParry(Player player) => inner.PlayerCanParry(player);
        public override bool PlayerCanBlock(Player player) => inner.PlayerCanBlock(player);
        public override uint GetShieldBlockValue(Unit unit) => inner.GetShieldBlockValue(unit);
        public override void OnKill(Unit? killer, Unit victim) => inner.OnKill(killer, victim);
        public override bool RepopAtGraveyard(Player player) => inner.RepopAtGraveyard(player);
        public override void OnResurrected(Player player, bool applySickness) => inner.OnResurrected(player, applySickness);
        public override bool IsInstanceable(uint mapId) => inner.IsInstanceable(mapId);
        public override Unit? FindUnit(Map map, ObjectGuid guid) => inner.FindUnit(map, guid);

        public override void ApplyGhostForm(Player player)
        {
            inner.ApplyGhostForm(player);
            if (spells.Spellbook.HasSpell(player, 20585))
            {
                spells.System.CastSpell(player, 20584, SpellCastTargets.ForSelf(), triggered: true);
            }

            spells.System.CastSpell(player, 8326, SpellCastTargets.ForSelf(), triggered: true);
        }

        public override void RemoveGhostForm(Player player)
        {
            if (spells.Spellbook.HasSpell(player, 20585))
            {
                spells.System.RemoveAuras(player, 20584);
            }

            spells.System.RemoveAuras(player, 8326);
            inner.RemoveGhostForm(player);
        }
    }
}
