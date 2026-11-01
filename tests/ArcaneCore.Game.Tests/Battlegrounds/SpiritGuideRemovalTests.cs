using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.WorldState.Events;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>
/// A battleground spirit guide that is removed (an Arathi Basin node assaulted or captured: its event creature is despawned by the spawn gate)
/// sends the dead around it that carry Waiting to Resurrect (2584) to a graveyard again, so the next graveyard is searched without the lost
/// node (vmangos npc_spirit_guideAI::CorpseRemoved, battleground.cpp:74-93: players within 20 yards that wait and are dead are repopped).
/// </summary>
public sealed class SpiritGuideRemovalTests : IDisposable
{
    private const uint GuideEntry = 13116;
    private const uint GuideSpawn = 700;
    private const float GuideZ = 83.5f;

    private readonly SpellTestKit _spells = new(
        Spell(BattlegroundConstants.SpellWaitingToResurrect, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Attributes = SpellAttributes.AllowCastWhileDead,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    private readonly RecordingGraveyards _graveyards = new();
    private readonly NodeGate _gate = new();
    private readonly CreatureMapSystem _creatures;
    private readonly Map _map;

    public SpiritGuideRemovalTests()
    {
        _map = _spells.World.GetMap(0);
        _map.Combat.SpellMitigation = _spells.System;
        _creatures = new CreatureMapSystem(
            _map,
            Content(
                [Template(GuideEntry, b => b.NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.SpiritGuide))],
                [Spawn(GuideSpawn, GuideEntry, 0, 0, GuideZ)]),
            random: new Random(1));
        _map.AddUpdater(_creatures);
        _creatures.SpawnGate = _gate;
    }

    public void Dispose() => _spells.Dispose();

    private Player Ghost(uint guid, float x, bool waiting)
    {
        (Player ghost, _) = _spells.AddPlayer(guid, x, 0);
        _map.Combat.KillPlayer(ghost);
        _spells.World.RunTick(100);
        Assert.True(_map.Combat.RepopPlayer(ghost));
        if (waiting)
        {
            Assert.Equal(SpellCastResult.CastOk, _spells.System.CastSpell(ghost, BattlegroundConstants.SpellWaitingToResurrect, SpellCastTargets.ForSelf(), triggered: true));
        }

        return ghost;
    }

    /// <summary>The node changes hands: the spawn gate no longer lets the guide's creature spawn and the creature system is refreshed (what SpawnEvent does).</summary>
    private void RemoveGuide()
    {
        Assert.NotNull(_creatures.FindBySpawnGuid(GuideSpawn));
        foreach (Player p in _spells.World.OnlinePlayers.ToArray())
        {
            p.Relocate(p.X, p.Y, GuideZ);
        }

        DeathSeams.Of(_spells.World).TryRegisterGraveyards(_graveyards);
        _gate.Allowed = false;
        _creatures.RefreshSpawns([GuideSpawn]);
    }

    [Fact]
    public void ANodeCapture_RepopsAWaitingGhostWithinTwentyYards_AndLeavesOneTwentyFiveYardsAwayAlone()
    {
        Player near = Ghost(1, 5, waiting: true);
        Player far = Ghost(2, 25, waiting: true);

        RemoveGuide();

        Assert.Null(_creatures.FindBySpawnGuid(GuideSpawn));
        Assert.Equal([near.Guid], _graveyards.Repopped);
        Assert.DoesNotContain(far.Guid, _graveyards.Repopped);
    }

    [Fact]
    public void AGhostThatIsNotWaiting_AndALivingPlayer_AreNotRepopped()
    {
        Ghost(1, 5, waiting: false);
        _spells.AddPlayer(2, 6, 0);

        RemoveGuide();

        Assert.Empty(_graveyards.Repopped);
    }

    [Fact]
    public void RemovingSomethingThatIsNotASpiritGuide_RepopsNobody()
    {
        Player near = Ghost(1, 5, waiting: true);
        _creatures.FindBySpawnGuid(GuideSpawn)!.NpcFlags = 0;

        RemoveGuide();

        Assert.DoesNotContain(near.Guid, _graveyards.Repopped);
    }

    private sealed class NodeGate : ISpawnGate
    {
        public bool Allowed { get; set; } = true;

        public bool AllowsCreature(uint spawnGuid) => spawnGuid != GuideSpawn || Allowed;

        public bool AllowsGameObject(uint spawnGuid) => true;

        public IEnumerable<uint> GatedCreatures => [GuideSpawn];

        public IEnumerable<uint> GatedGameObjects => [];
    }

    private sealed class RecordingGraveyards : IGraveyardRepop
    {
        public List<ObjectGuid> Repopped { get; } = [];

        public bool RepopAtGraveyard(Player player)
        {
            Repopped.Add(player.Guid);
            return true;
        }

        public bool RelocateLeavingPlayer(Player player) => false;

        public bool TeleportToCorpseGraveyard(Player player, CorpsePlace? corpse) => false;
    }
}
