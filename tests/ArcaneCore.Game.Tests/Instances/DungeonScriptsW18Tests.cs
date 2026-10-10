using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Scholomance;
using ArcaneCore.Game.Instances.Scripts.Stratholme;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>Wave 18 dungeon scripts: the Stratholme postboxes, the Scholomance Spectral Tutor and the Stratholme citizens.</summary>
public sealed class DungeonScriptsW18Tests
{
    private static DungeonScriptHarness Strath(ICreatureSpellCaster? caster = null, uint[]? spawns = null)
        => new(map => new StratholmeInstance(map), [StratholmeInstance.NpcUndeadPostman, .. spawns ?? []], spawns ?? [], null,
            new CreatureAiServices { Spells = caster },
            [.. StratholmeInstance.GoPostboxes.Take(3).Select(e => (e, GameObjectType.Goober))]);

    [Fact]
    public void Postboxes_ThreeEachBringPostmen_AndTheThirdSummonsMalown()
    {
        // GOUse_go_stratholme_postbox + instance_stratholme::SetData(TYPE_POSTMASTER) (stratholmeScripts.cpp:95-124, stratholme.cpp:463-474).
        using DungeonScriptHarness run = Strath();
        var strath = (StratholmeInstance)run.Data;
        var cast = new List<uint>();
        strath.CastPlayerSpell = (_, spell) => cast.Add(spell);
        uint[] boxes = [.. StratholmeInstance.GoPostboxes.Take(3)];
        void Use(uint entry)
        {
            GameObject box = run.Object(entry);
            run.Player.Relocate(box.X + 1, box.Y, box.Z, 0, 0);
            Assert.Equal(GameObjectUseResult.Ok, run.Objects.Use(run.Player, box.Guid));
        }

        Use(boxes[0]);
        Assert.Equal(EncounterState.InProgress, strath.GetData(StratholmeInstance.TypePostmaster));
        Assert.Equal(3, run.Creatures.Creatures.Count(c => c.Template.Entry == StratholmeInstance.NpcUndeadPostman));

        run.Objects.Use(run.Player, run.Object(boxes[0]).Guid); // the same box again counts for nothing
        Assert.Equal(1, strath.PostboxesUsed);

        Use(boxes[1]);
        Assert.Equal(EncounterState.Special, strath.GetData(StratholmeInstance.TypePostmaster));
        Assert.Empty(cast);

        Use(boxes[2]);
        Assert.Equal([StratholmeInstance.SpellSummonPostmaster], cast);
        Assert.Equal(EncounterState.Done, strath.GetData(StratholmeInstance.TypePostmaster));
        Assert.Equal(9, run.Creatures.Creatures.Count(c => c.Template.Entry == StratholmeInstance.NpcUndeadPostman));
    }

    [Fact]
    public void SpectralTutor_ImageProjectionStopsEverythingForASecond_ThenHeals_AndBlocksEvade()
    {
        // npc_spectral_tutorAI::UpdateAI / EnterEvadeMode (scholomance.cpp:52-115).
        var caster = new DungeonTestCaster();
        using DungeonScriptHarness run = Strath(caster, [10498]);
        Creature tutor = run.Creature(10498);
        run.Player.Relocate(tutor.X + 1, tutor.Y, tutor.Z, 0, 0);
        Assert.True(run.Creatures.AttackStart(tutor, run.Player));
        tutor.Combat.Threat.AddThreat(run.Player, 100);
        var ai = new SpectralTutorAI(tutor);
        ai.OnRespawn();

        for (int i = 0; i < 14 && !ai.Projecting; i++)
        {
            ai.OnUpdate(1_000);
        }

        Assert.True(ai.Projecting);
        Assert.Contains(caster.Casts, c => c.Spell == SpectralTutorAI.SpellImageProjectionSummon);
        Assert.True(ai.OnEnterEvadeMode());
        int before = caster.Casts.Count;
        ai.OnUpdate(500);
        Assert.Equal(before, caster.Casts.Count); // nothing else during the projection
        ai.OnUpdate(500);
        Assert.Equal(SpectralTutorAI.SpellImageProjectionHeal, caster.Casts[^1].Spell);
        Assert.False(ai.Projecting);
        Assert.False(ai.OnEnterEvadeMode());
    }

    [Fact]
    public void GhostlyCitizen_EganBlasterFreesTheSoul_AndEmotesAreAnswered()
    {
        // mobs_spectral_ghostly_citizenAI (stratholmeScripts.cpp:205-300).
        var caster = new DungeonTestCaster();
        using DungeonScriptHarness run = Strath(caster, [10385]);
        Creature citizen = run.Creature(10385);
        var ai = new SpectralGhostlyCitizenAI(citizen);
        ai.OnRespawn();
        Assert.Contains(caster.Casts, c => c.Spell == SpectralGhostlyCitizenAI.SpellIncorporealDefense);

        run.Player.Relocate(citizen.X + 1, citizen.Y, citizen.Z, 0, 0);
        ai.OnReceiveEmote(run.Player, SpectralGhostlyCitizenAI.TextEmoteRude);
        Assert.Contains(caster.Casts, c => c.Spell == SpectralGhostlyCitizenAI.SpellSlap && ReferenceEquals(c.Target, run.Player));

        ai.OnSpellHit(run.Player, new SpellInfo { Id = 1 });
        Assert.False(ai.Tagged);
        ai.OnSpellHit(run.Player, new SpellInfo { Id = SpectralGhostlyCitizenAI.SpellEganBlaster });
        Assert.True(ai.Tagged);
        Assert.Contains(caster.Casts, c => c.Spell == SpectralGhostlyCitizenAI.SpellSoulFreed);
        Assert.False(ai.CombatMovement);

        ai.OnUpdate(1_999);
        Assert.True(citizen.IsAlive);
        ai.OnUpdate(2);
        Assert.Contains(caster.Casts, c => c.Spell == SpectralGhostlyCitizenAI.SpellSummonFreedSoul);
        Assert.False(citizen.IsAlive);
    }

    [Theory]
    [InlineData(289u, 10498u, typeof(SpectralTutorAI))]
    [InlineData(329u, 10384u, typeof(SpectralGhostlyCitizenAI))]
    [InlineData(329u, 10385u, typeof(SpectralGhostlyCitizenAI))]
    public void DungeonBossAis_SelectsTheNewScriptsByMapAndEntry(uint mapId, uint entry, Type expected)
    {
        using DungeonScriptHarness run = Strath(null, [entry]);
        Assert.IsType(expected, DungeonBossAis.Create(run.Creature(entry), mapId));
    }
}
