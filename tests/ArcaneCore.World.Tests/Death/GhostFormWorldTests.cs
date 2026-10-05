using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Combat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

public sealed class GhostFormWorldTests
{
    private const uint OrdinaryAura = 962495;
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(1, true)]
    public async Task Release_CastsGhostAndKnownWisp_AndResurrectionRemovesThem(byte race, bool knowsWisp)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GFORM", "Ghostform", race: race);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ghostform")!;
            SpellFeature spells = Install(host);
            if (knowsWisp) spells.Spellbook.LearnSpell(player, 20585);
            player.Map!.Combat.KillPlayer(player);
        });
        await client.CollectAsync();
        await client.SendAsync(WorldOpcode.CmsgRepopRequest, [0]);
        await client.ReadUntilAsync(WorldOpcode.SmsgCorpseReclaimDelay);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ghostform")!;
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            Assert.True(spells.HasAura(player, 8326));
            Assert.Equal(knowsWisp, spells.HasAura(player, 20584));
            Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
            Assert.Equal(1, player.GetByte(UpdateFields.UnitFieldBytes1, 3) & 1);
            Assert.Single(player.Locomotion.Pending.Changes, c => c.Type == MovementChangeType.WaterWalk && c.Apply);
            player.Map!.Combat.ResurrectPlayer(player, 0.5f, false);
            Assert.False(spells.HasAura(player, 8326));
            Assert.False(spells.HasAura(player, 20584));
            Assert.False(player.Flags.HasFlag(PlayerFlags.Ghost));
            Assert.Equal(0, player.GetByte(UpdateFields.UnitFieldBytes1, 3) & 1);
            Assert.Single(player.Locomotion.Pending.Changes, c => c.Type == MovementChangeType.WaterWalk && !c.Apply);
        });
    }

    [Fact]
    public async Task PersistedGhostRelog_RestoresFormAndSingleWaterWalkOrder()
    {
        await using WorldTestHost host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player);
        await using WorldTestClient client = await host.EnterWorldAsync("GFORMLOGIN", "Ghostlogin");
        ulong guid = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ghostlogin")!;
            SpellFeature spells = Install(host);
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, OrdinaryAura, SpellCastTargets.ForSelf(), true));
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
            return player.Guid.Value;
        });
        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await client.LoginAsync(guid);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ghostlogin")!;
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            Assert.True(spells.HasAura(player, 8326));
            Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
            Assert.Equal(1, player.GetByte(UpdateFields.UnitFieldBytes1, 3) & 1);
            SpellAuraHolder ordinary = Assert.Single(spells.GetAuras(player), holder => holder.Spell.Id == OrdinaryAura);
            Assert.Equal(13, ordinary.Auras[0]!.Amount);
            Assert.Equal(60_000, ordinary.MaxDuration);
            Assert.InRange(ordinary.Duration, 1, 60_000);
            Assert.Single(player.Locomotion.Pending.Changes, c => c.Type == MovementChangeType.WaterWalk && c.Apply);
        });
    }

    [Fact]
    public async Task MissingGhostContent_KeepsFallbackGhostAndWaterWalk()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GFORMNONE", "Ghostnone");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ghostnone")!;
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
            Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
            Assert.False(host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(player, 8326));
            Assert.Single(player.Locomotion.Pending.Changes, c => c.Type == MovementChangeType.WaterWalk && c.Apply);
            player.Map!.Combat.ResurrectPlayer(player, 0.5f, false);
            Assert.Single(player.Locomotion.Pending.Changes, c => c.Type == MovementChangeType.WaterWalk && !c.Apply);
        });
    }

    [Fact]
    public async Task GhostAdapter_PreservesPreviouslyRegisteredCombatCallbacks()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GFORMHOOKS", "Ghosthooks");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ghosthooks")!;
            Install(host);
            var prior = new PriorHooks();
            CombatHooks.Register(host.World, prior);
            new WorldCombatHooksFeature(host.WorldServices, NullLogger<WorldCombatHooksFeature>.Instance).Attach(host.World);
            CombatHooks hooks = CombatHooks.For(host.World);
            Assert.True(hooks.IsFriendly(player, player));
            Assert.True(hooks.IsHostileTo(player, player));
            Assert.True(hooks.CanAttack(player, player));
            Assert.Equal(7, hooks.GetWeaponSkill(player, WeaponAttackType.BaseAttack, null));
            Assert.Equal(8, hooks.GetDefenseSkill(player, null));
            Assert.True(hooks.HasOffhandWeapon(player));
            Assert.True(hooks.PlayerCanParry(player));
            Assert.True(hooks.PlayerCanBlock(player));
            Assert.Equal(9u, hooks.GetShieldBlockValue(player));
            Assert.False(hooks.IsInstanceable(999));
            Assert.Same(player, hooks.FindUnit(player.Map!, player.Guid));
            hooks.OnKill(player, player);
            hooks.OnResurrected(player, true);
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
            player.Map!.Combat.ResurrectPlayer(player, 0.5f, false);
            Assert.Equal(1, prior.Kills);
            Assert.Equal(1, prior.Applied);
            Assert.Equal(1, prior.Removed);
            Assert.Equal(1, prior.Repops);
            Assert.Equal(2, prior.Resurrections);
        });
    }

    private sealed class PriorHooks : CombatHooks
    {
        public int Kills { get; private set; }
        public int Applied { get; private set; }
        public int Removed { get; private set; }
        public int Repops { get; private set; }
        public int Resurrections { get; private set; }
        public override bool IsFriendly(Unit a, Unit b) => true;
        public override bool IsHostileTo(Unit a, Unit b) => true;
        public override bool CanAttack(Unit attacker, Unit victim) => true;
        public override int GetWeaponSkill(Unit unit, WeaponAttackType attackType, Unit? victim) => 7;
        public override int GetDefenseSkill(Unit unit, Unit? attacker) => 8;
        public override bool HasOffhandWeapon(Unit unit) => true;
        public override bool PlayerCanParry(Player player) => true;
        public override bool PlayerCanBlock(Player player) => true;
        public override uint GetShieldBlockValue(Unit unit) => 9;
        public override void OnKill(Unit? killer, Unit victim) => Kills++;
        public override void ApplyGhostForm(Player player) => Applied++;
        public override void RemoveGhostForm(Player player) => Removed++;
        public override bool RepopAtGraveyard(Player player) { Repops++; return false; }
        public override void OnResurrected(Player player, bool applySickness) => Resurrections++;
        public override bool IsInstanceable(uint mapId) => false;
        public override Unit? FindUnit(ArcaneCore.Game.Maps.Map map, ObjectGuid guid) => map.FindPlayer(guid);
    }

    private static SpellFeature Install(WorldTestHost host)
    {
        SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
        spells.System.Store = new SpellStore(
        [
            .. spells.System.Store.All, Form(8326, true), Form(20584, false),
            Form(OrdinaryAura, false) with
            {
                Name = "Ordinary saved aura", Duration = new SpellDuration(60_000, 0, 60_000),
                Effects = [Aura(AuraType.Dummy) with { BasePoints = 12, BaseDice = 1, DieSides = 1 }],
            },
        ], [], []);
        return spells;
    }

    private static SpellInfo Form(uint id, bool waterWalk) => new()
    {
        Id = id, Name = $"Synthetic ghost form {id}", RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Attributes = SpellAttributes.AllowCastWhileDead,
        AttributesEx2 = SpellAttributesEx2.AllowDeadTarget,
        AttributesEx3 = 0x00100000,
        Duration = new SpellDuration(-1, 0, -1),
        Effects = waterWalk
            ? [Aura(AuraType.Ghost), Aura(AuraType.WaterWalk)]
            : [Aura(AuraType.Ghost)],
    };

    private static SpellEffectInfo Aura(AuraType type) => new()
    {
        Effect = SpellEffectName.ApplyAura, AuraType = type, TargetA = SpellImplicitTarget.UnitCaster,
    };
}
