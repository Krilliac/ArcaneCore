using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// SPELL_EFFECT_SUMMON_DEMON with a summoning ritual as the spell's object target (Ritual of Doom, vmangos Spell::EffectSummonDemon,
/// SpellEffects.cpp:5802-5805): the demon appears at the ritual game object, not at the caster, and the spell script's OnSummon still runs
/// (SpellEffects.cpp:5818, scripts/spells/spell_warlock.cpp:244-255). The ritual finishing after a spell that went off is covered by
/// SummoningRitualTests (GameObjectMapSystem.FinishRitual).
/// </summary>
public sealed class RitualOfDoomTests
{
    private const uint DemonEntry = PetTestKit.WildEntry;
    private const uint DoomSpell = 971_301;
    private const float RitualX = 40f;
    private const float RitualY = 30f;
    private const float RitualZ = 12f;

    private static IEnumerable<SpellInfo> Spells() =>
    [
        Spell(DoomSpell, Effect(SpellEffectName.SummonDemon, 0, SpellImplicitTarget.LocationCasterDest, misc: (int)DemonEntry)),
        Spell(WarlockInfernoScript.Inferno, Effect(SpellEffectName.SummonDemon, 0, SpellImplicitTarget.LocationCasterDest, misc: (int)DemonEntry)) with
        {
            Duration = new SpellDuration(3_600_000, 0, 3_600_000),
        },
        Spell(WarlockInfernoScript.EnslaveDemon, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, aura: AuraType.Dummy)) with
        {
            Duration = new SpellDuration(300_000, 0, 300_000),
        },
        Spell(WarlockInfernoScript.InfernoRoot, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Duration = new SpellDuration(300_000, 0, 300_000),
        },
        Spell(WarlockInfernoScript.InfernoEffect, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Duration = new SpellDuration(300_000, 0, 300_000),
        },
    ];

    private static GameObject AddObject(PetTestKit kit, GameObjectType type)
    {
        var template = new GameObjectTemplate
        {
            Entry = 990_901, Type = (uint)type, DisplayId = 1, Name = "Ritual", Data = new uint[GameObjectTemplate.DataCount],
        };
        var go = new GameObject(1, template, spawn: null);
        go.SetPosition(RitualX, RitualY, RitualZ, 0f);
        kit.Map.AddObject(go);
        return go;
    }

    private static SpellCastTargets At(GameObject go) => new() { Mask = SpellCastTargetFlags.GameObject, GameObject = go.Guid };

    [Fact]
    public void RitualSpell_SummonsTheDemonAtTheRitual_NotAtTheWarlock()
    {
        using var kit = new PetTestKit(Spells());
        (Player warlock, _) = kit.AddPlayer(1, 5, 6);
        warlock.Level = 40;
        GameObject ritual = AddObject(kit, GameObjectType.SummoningRitual);

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastRitualSpell(ritual, DoomSpell, warlock, null));

        Creature demon = Assert.Single(kit.Creatures.Creatures, c => c.Entry == DemonEntry);
        Assert.Equal(RitualX, demon.X);
        Assert.Equal(RitualY, demon.Y);
        Assert.Equal(RitualZ, demon.Z);
        Assert.Equal(warlock.Level, demon.Level);
    }

    [Fact]
    public void RitualSpell_RunsTheSpellScriptsOnSummon_ForTheDemonAtTheRitual()
    {
        using var kit = new PetTestKit(Spells());
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        (Player warlock, _) = kit.AddPlayer(1, 5, 6);
        GameObject ritual = AddObject(kit, GameObjectType.SummoningRitual);

        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastRitualSpell(ritual, WarlockInfernoScript.Inferno, warlock, null));

        Creature demon = Assert.Single(kit.Creatures.Creatures, c => c.Entry == DemonEntry);
        Assert.Equal(RitualX, demon.X);
        Assert.Equal(RitualY, demon.Y);
        SpellAuraHolder enslave = Assert.Single(kit.Spells.System.GetAuras(demon), h => h.Spell.Id == WarlockInfernoScript.EnslaveDemon);
        Assert.Equal(warlock.Guid, enslave.CasterGuid);
    }

    [Fact]
    public void ANonRitualObjectTarget_LeavesTheDemonAtTheCaster()
    {
        using var kit = new PetTestKit(Spells());
        (Player warlock, _) = kit.AddPlayer(1, 5, 6);
        GameObject crate = AddObject(kit, GameObjectType.Generic);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(warlock, DoomSpell, At(crate)));

        Creature demon = Assert.Single(kit.Creatures.Creatures, c => c.Entry == DemonEntry);
        Assert.Equal(warlock.X, demon.X);
        Assert.Equal(warlock.Y, demon.Y);
    }
}
