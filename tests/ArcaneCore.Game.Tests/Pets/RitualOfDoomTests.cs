using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// SPELL_EFFECT_SUMMON_DEMON with a summoning ritual as the spell's object target (Ritual of Doom, vmangos Spell::EffectSummonDemon,
/// SpellEffects.cpp:5802-5805): the demon appears at the ritual game object, not at the caster, and the spell script's OnSummon still runs
/// (SpellEffects.cpp:5818, scripts/spells/spell_warlock.cpp:244-255). A use through GameObjectMapSystem with the real spell system behind it
/// shows the demon at the ritual and the ritual finishing (GameObjectMapSystem.FinishRitual).
/// </summary>
public sealed class RitualOfDoomTests
{
    private const uint DemonEntry = PetTestKit.WildEntry;
    private const uint DoomSpell = 18541; // Ritual of Doom, the spell of Doom Portal (go 177193, vmangos-db data1)
    private const uint DoomAnimSpell = 18540; // data2
    private const uint DoomSacrificeSpell = 20625; // data4
    private const uint DoomPortalEntry = 177193;
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

    /// <summary>The world's <see cref="IGameObjectSpells"/> reduced to what a ritual needs: the ritual spell goes to the real spell system.</summary>
    private sealed class RealRitualSpells(SpellSystem system) : IGameObjectSpells
    {
        public List<(uint Spell, Unit Target)> Casts { get; } = [];

        public bool Cast(GameObject source, uint spellId, Unit target, Unit? unitCaster)
        {
            Casts.Add((spellId, target));
            return true;
        }

        public float? MaxRange(uint spellId) => null;

        public bool IsChanneling(Unit unit) => false;

        public void StartRitualAnimation(Player helper, uint animSpellId, GameObject ritual)
        {
        }

        public bool CastRitualSpell(GameObject ritual, uint spellId, Unit caster, ObjectGuid summonTarget)
            => system.CastRitualSpell(ritual, spellId, caster, null) == SpellCastResult.CastOk;

        public void StartCreatingSpellCooldown(Player owner, uint spellId)
        {
        }
    }

    [Fact]
    public void UsingTheDoomPortal_SummonsTheDemonAtThePortal_AndFinishesTheRitual()
    {
        using var kit = new PetTestKit(Spells());
        GameObjectTemplate template = GameObjectTestKit.GoTemplate(DoomPortalEntry, GameObjectType.SummoningRitual,
            (0, 5), (1, DoomSpell), (2, DoomAnimSpell), (4, DoomSacrificeSpell));
        var system = new GameObjectMapSystem(kit.Map, new GameObjectContent([template], [], [], [], []));
        kit.Map.AddUpdater(system);
        var spells = new RealRitualSpells(kit.Spells.System);
        system.Spells = spells;

        var players = new List<Player>();
        for (uint i = 0; i < 5; i++)
        {
            (Player player, _) = kit.AddPlayer(1 + i, i % 2, i / 2);
            player.Level = 40;
            players.Add(player);
        }

        GameObject ritual = system.Summon(DoomPortalEntry, 3, 0, players[0].Z, 0)!;
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(GameObjectUseResult.Ok, system.Use(players[i], ritual.Guid));
            Assert.DoesNotContain(kit.Creatures.Creatures, c => c.Entry == DemonEntry);
        }

        // The fifth participant completes the ritual: the first user casts the ritual spell at the portal.
        Assert.Equal(GameObjectUseResult.Ok, system.Use(players[4], ritual.Guid));

        Creature demon = Assert.Single(kit.Creatures.Creatures, c => c.Entry == DemonEntry);
        Assert.Equal(ritual.X, demon.X);
        Assert.Equal(ritual.Y, demon.Y);
        Assert.Equal(ritual.Z, demon.Z);
        Assert.NotEqual(players[0].X, demon.X);

        // FinishRitual: a ritual that is not persistent (data3 = 0) is used up and one participant takes the sacrifice spell (data4).
        Assert.Equal(GameObjectLootState.JustDeactivated, ritual.LootState);
        (uint sacrifice, Unit victim) = Assert.Single(spells.Casts);
        Assert.Equal(DoomSacrificeSpell, sacrifice);
        Assert.Contains(victim, players);
        kit.Spells.World.RunTick(50);
        Assert.Null(system.Find(ritual.Guid));
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
