using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class WhiteThreatWorldTests
{
    private const uint PhysicalModifier = 993101;
    private const uint HolyModifier = 993102;

    [Fact]
    public async Task WhiteDamage_UsesWorldSpellThreatModifier_BySchoolWithoutChangingHealthDamage()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("WHITETHREAT", "Whitethreat");

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Whitethreat")!;
            SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
            Assert.NotNull(player.Map!.Combat.ThreatModifiers); // the threat feature binds the MOD_THREAT school multipliers
            spells.System.Store = new SpellStore([.. spells.System.Store.All, PhysicalAura(), HolyAura()], [], []);

            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, PhysicalModifier,
                SpellCastTargets.ForSelf(), triggered: true));
            Creature physical = AddTarget(player, 993111);
            uint before = physical.Health;
            Assert.Equal(100u, player.Map!.Combat.DealDamage(player, physical, 100));
            Assert.Equal(before - 100, physical.Health);
            Assert.Equal(80f, physical.Combat.Threat.GetThreat(player));

            spells.System.RemoveAuras(player, PhysicalModifier);
            Creature restored = AddTarget(player, 993112);
            before = restored.Health;
            Assert.Equal(100u, player.Map!.Combat.DealDamage(player, restored, 100));
            Assert.Equal(before - 100, restored.Health);
            Assert.Equal(100f, restored.Combat.Threat.GetThreat(player));

            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, HolyModifier,
                SpellCastTargets.ForSelf(), triggered: true));
            Creature holy = AddTarget(player, 993113);
            before = holy.Health;
            Assert.Equal(100u, player.Map!.Combat.DealDamage(player, holy, 100));
            Assert.Equal(before - 100, holy.Health);
            Assert.Equal(100f, holy.Combat.Threat.GetThreat(player));
        });
    }

    private static SpellInfo PhysicalAura() => new()
    {
        Id = PhysicalModifier, Name = "Synthetic physical threat reduction",
        RangeIndex = SpellConstants.RangeIndexSelfOnly, Duration = new SpellDuration(-1, 0, -1),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModThreat,
            BasePoints = -21, BaseDice = 1, DieSides = 1, MiscValue = 1,
            TargetA = SpellImplicitTarget.UnitCaster,
        }, new(), new()],
    };

    private static SpellInfo HolyAura() => new()
    {
        Id = HolyModifier, Name = "Synthetic holy-only threat reduction",
        RangeIndex = SpellConstants.RangeIndexSelfOnly, Duration = new SpellDuration(-1, 0, -1),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModThreat,
            BasePoints = -21, BaseDice = 1, DieSides = 1, MiscValue = 2,
            TargetA = SpellImplicitTarget.UnitCaster,
        }, new(), new()],
    };

    private static Creature AddTarget(Player player, uint guid)
    {
        var template = new CreatureTemplate
        {
            Entry = guid, Name = "Synthetic white-threat target", Faction = 14,
            MinLevel = 1, MaxLevel = 1, MinLevelHealth = 500, MaxLevelHealth = 500,
        };
        var spawn = new CreatureSpawn { Guid = guid, Entry = guid, MapId = player.MapId,
            X = player.X, Y = player.Y, Z = player.Z };
        var target = new Creature(guid, template, spawn,
            new CreatureContent([template], [spawn], [], [], []), new Random(1));
        player.Map!.AddObject(target);
        return target;
    }
}
