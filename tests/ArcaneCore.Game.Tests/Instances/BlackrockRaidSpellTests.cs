using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.BlackrockSpire;
using ArcaneCore.Game.Instances.Scripts.BlackwingLair;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The Blackwing Lair, Molten Core and Upper Blackrock Spire event spells: the Random Aggro family, the Enraged Felguard Spawn despawn of the
/// Demon Portal, Dominion of Soul's item-use check, and the list proving every spell of the lane (spell-lane-bwl-mc-ubrs-casts) is implemented,
/// consumed or inert, exactly once.
/// </summary>
public sealed class BlackrockRaidSpellTests
{
    /// <summary>Every spell of the lane.</summary>
    private static readonly uint[] s_laneSpells =
    [
        22913, 23878, 28388, 30215, 23173, 23195, 23343, 22393, 16354, 16404, 23642, 16047, 15281, 15282, 16134, 16135, 16053, 2400, 17131,
    ];

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo DummyEffect(uint id, SpellImplicitTarget target = SpellImplicitTarget.UnitCaster)
        => Instant(SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.Dummy, 0, target)));

    private static SpellInfo Marker(uint id) =>
        Instant(SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(30_000, 0, 30_000),
        });

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        kit.System.Units = new MapObjectResolver();
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    /// <summary>Temporary creatures of the given entries on the kit's map, in order, each 10 000 health, on the player's row at the given X.</summary>
    private static Creature[] SpawnCreatures(SpellTestKit kit, params (uint Entry, float X)[] units)
    {
        CreatureTemplate[] templates = [.. units.Select(u => u.Entry).Distinct().Select(e => CreatureTestKit.Template(e, t =>
        {
            t.MinLevelHealth = 10_000;
            t.MaxLevelHealth = 10_000;
        }))];
        var content = new CreatureContent(templates, [], [], [], []);
        var system = new CreatureMapSystem(kit.World.GetMap(0), content, random: new Random(1));
        kit.World.GetMap(0).AddUpdater(system);
        kit.World.RunTick(50);
        return [.. units.Select(u => system.SpawnTemporary(content.FindTemplate(u.Entry)!, u.X, 0, 83.5f, 0))];
    }

    [Theory]
    [InlineData(22913u)]
    [InlineData(23878u)]
    [InlineData(28388u)]
    [InlineData(30215u)]
    public void RandomAggro_TheCasterPicksAnAttackerFromItsThreatListAndAddsOneHundredThreat(uint spell)
    {
        using SpellTestKit kit = Kit(DummyEffect(spell));
        (Player first, _) = kit.AddPlayer(1);
        (Player second, _) = kit.AddPlayer(2, 2, 0);
        Creature boss = SpawnCreatures(kit, (CreatureTestKit.WolfEntry, 4))[0];
        Assert.True(kit.World.GetMap(0).Combat.Attack(boss, first));
        boss.Combat.Threat.AddThreat(second, 1f);
        float before = boss.Combat.Threat.GetThreat(first) + boss.Combat.Threat.GetThreat(second);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(boss, spell, SpellCastTargets.ForSelf(), triggered: true));

        float after = boss.Combat.Threat.GetThreat(first) + boss.Combat.Threat.GetThreat(second);
        Assert.Equal(before + 100f, after, 1);
        Assert.Contains(boss.Combat.Victim, new Unit?[] { first, second });
    }

    [Fact]
    public void RandomAggro_ACasterWithoutAThreatListDoesNothing()
    {
        using SpellTestKit kit = Kit(DummyEffect(22913));
        (Player player, _) = kit.AddPlayer(1);
        Creature idle = SpawnCreatures(kit, (CreatureTestKit.WolfEntry, 4))[0];

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(idle, 22913, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(0f, idle.Combat.Threat.GetThreat(player));
        Assert.Null(idle.Combat.Victim);
    }

    [Fact]
    public void EnragedFelguardSpawn_TheDemonPortalItHitsDespawnsAndOtherCreaturesStay()
    {
        using SpellTestKit kit = Kit(DummyEffect(EnragedFelguardSpawnScript.EnragedFelguardSpawn, SpellImplicitTarget.Unit));
        kit.AddPlayer(1);
        Creature[] units = SpawnCreatures(kit, (14101, 1), (EnragedFelguardSpawnScript.DemonPortal, 3), (CreatureTestKit.WolfEntry, 5));
        (Creature felguard, Creature portal, Creature bystander) = (units[0], units[1], units[2]);
        CreatureMapSystem system = portal.System!;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(felguard, EnragedFelguardSpawnScript.EnragedFelguardSpawn,
            SpellCastTargets.ForUnit(bystander.Guid), triggered: true));
        Assert.Contains(bystander, system.Creatures);
        Assert.Contains(portal, system.Creatures);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(felguard, EnragedFelguardSpawnScript.EnragedFelguardSpawn,
            SpellCastTargets.ForUnit(portal.Guid), triggered: true));
        Assert.DoesNotContain(portal, system.Creatures);
        Assert.Contains(felguard, system.Creatures);
    }

    private static SpellCastResult DominionCheck(SpellTestKit kit, Player player, Item? item)
    {
        SpellInfo spell = kit.Store.Get(DominionOfSoulScript.DominionOfSoul)!;
        var context = new SpellCastCheckContext(kit.System, player, spell, SpellCastTargets.ForSelf(), null, false, true, item);
        return new DominionOfSoulScript().OnCheckCast(context);
    }

    [Fact]
    public void DominionOfSoul_TheOrbFailsWhileEmberstrifeIsAboveTenPercentOrAlreadyDominated()
    {
        using SpellTestKit kit = Kit(Marker(DominionOfSoulScript.DominionOfSoul));
        (Player player, _) = kit.AddPlayer(1);
        Creature emberstrife = SpawnCreatures(kit, (DominionOfSoulScript.Emberstrife, 6))[0];
        var orb = new Item(1, new ItemTemplate { Entry = 12300 }, player.Guid);

        Assert.Equal(SpellCastResult.TargetAurastate, DominionCheck(kit, player, orb));

        emberstrife.Health = emberstrife.MaxHealth / 10;
        Assert.Equal(SpellCastResult.CastOk, DominionCheck(kit, player, orb));

        Assert.True(kit.System.AddAura(emberstrife, DominionOfSoulScript.DominionOfSoul, permanent: true));
        Assert.Equal(SpellCastResult.TargetAurastate, DominionCheck(kit, player, orb));
    }

    [Fact]
    public void DominionOfSoul_OtherCastsAndAnAbsentOrFarEmberstrifeAreNotChecked()
    {
        using SpellTestKit kit = Kit(Marker(DominionOfSoulScript.DominionOfSoul));
        (Player player, _) = kit.AddPlayer(1);
        var orb = new Item(1, new ItemTemplate { Entry = 12300 }, player.Guid);
        Assert.Equal(SpellCastResult.CastOk, DominionCheck(kit, player, orb));

        Creature emberstrife = SpawnCreatures(kit, (DominionOfSoulScript.Emberstrife, DominionOfSoulScript.SearchRange + 10f))[0];
        Assert.True(emberstrife.Health > emberstrife.MaxHealth / 10);
        Assert.Equal(SpellCastResult.CastOk, DominionCheck(kit, player, orb));

        emberstrife.X = 6f;
        Assert.Equal(SpellCastResult.TargetAurastate, DominionCheck(kit, player, orb));
        Assert.Equal(SpellCastResult.CastOk, DominionCheck(kit, player, null));
    }

    [Fact]
    public void EveryLaneSpell_IsImplementedConsumedOrInert_ExactlyOnce()
    {
        HashSet<uint> scripted = [.. SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly).SpellIds];
        string consumers = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), "src", "ArcaneCore.Playerbots", "Qa", "PlayerbotQaSpellConsumers.BlackrockRaids.cs"));
        HashSet<uint> consumed = [.. Regex.Matches(consumers, @"new\((\d+),").Select(m => uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];

        foreach (uint id in s_laneSpells)
        {
            int ways = (scripted.Contains(id) ? 1 : 0) + (consumed.Contains(id) ? 1 : 0) + (DeliberatelyInertSpells.TryGet(id, out _) ? 1 : 0);
            Assert.True(ways == 1, $"spell {id}: implemented/consumed/inert {ways} times, expected exactly one");
        }
    }
}
