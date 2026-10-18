using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// Tame Beast end to end with the Spell.dbc shape of 1515 / 13535 / 13481: the 20 s channel's periodic trigger (aura 23 on the beast, 20 s
/// amplitude) casts 13535 from the hunter, whose dummy script casts the real tame effect 13481 at the channelled beast (vmangos
/// SpellEffects.cpp:804-820). Pins that the last tick lands before the channel ends and that the beast becomes the hunter's pet.
/// </summary>
public sealed class TameBeastEndToEndTests
{
    private const uint BeastEntry = 5110;

    private static IEnumerable<SpellInfo> TameSpells() =>
    [
        Spell(1515,
            Effect(SpellEffectName.Threat, 10, SpellImplicitTarget.UnitEnemy),
            Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicTriggerSpell, amplitude: 20_000, trigger: 13535),
            Effect(SpellEffectName.ApplyAura, -100, SpellImplicitTarget.UnitCaster, AuraType.ModResistancePct, misc: 1)) with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(20_000, 0, 20_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(13535, Effect(SpellEffectName.Dummy, 1, SpellImplicitTarget.UnitCaster)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        Spell(13481, Effect(SpellEffectName.Tamecreature, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
    ];

    [Fact]
    public void TameBeastChannel_RunsOut_AndTheBeastBecomesThePet()
    {
        using var kit = new PetTestKit(TameSpells(), extraTemplates:
        [
            CreatureTestSupport.Template(BeastEntry, b =>
            {
                b.MinLevel = 20;
                b.MaxLevel = 20;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
                b.Faction = 14;
            }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 },
        ]);
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player hunter, _) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 30;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, hunter.Z, 0);
        kit.Run(200);

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(hunter, 1515, SpellCastTargets.ForUnit(beast.Guid), triggered: false));
        var hits = new List<string>();
        kit.Spells.System.SpellHit += (c, t, s) => hits.Add($"{s.Id}@{kit.Spells.Now}");
        kit.Spells.Advance(10_000);
        string mid = string.Join(',', kit.Spells.System.GetAuras(beast).Select(h => h.Spell.Id + ":" + h.Duration));
        kit.Spells.Advance(11_000);
        Assert.True(hunter.GetPet() is not null, $"hits {string.Join(' ', hits)} mid {mid} tame {kit.Service.CheckTaming(hunter, beast)}");

        Assert.NotNull(hunter.GetPet());
        Assert.Equal(BeastEntry, Assert.IsType<Creature>(hunter.GetPet()).Template.Entry);
    }
}
