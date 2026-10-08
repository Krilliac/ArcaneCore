using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Instances.Scripts.RazorfenKraul;

/// <summary>ScriptDev2 npc_willix_the_importerAI (mangos-classic razorfen_kraul/razorfen_kraul.cpp:
/// QuestAccept_npc_willix_the_importer, Aggro, JustSummoned, WaypointReached).</summary>
public sealed class WillixAi(Creature creature) : EscortAI(creature)
{
    public const uint Entry = 4508, Quest = 1144, RagingAgamar = 4514;
    private static readonly (float X, float Y, float Z)[] Boars =
        [(2151.420f, 1733.18f, 52.10f), (2144.463f, 1726.89f, 51.93f),
         (1956.433f, 1597.97f, 81.75f), (1958.971f, 1599.01f, 81.44f)];
    private Player? _escortPlayer;

    /// <summary>Quest 1144 accept adapter; starts the walking escort and remembers who receives group credit.</summary>
    public bool AcceptQuest(Player player)
    {
        if (!Start()) return false;
        _escortPlayer = player;
        System?.SayText(Me, -1047000, player);
        return true;
    }

    protected override void Aggro(Unit target)
    {
        int line = Random.Shared.Next(7);
        if (line < 4) System?.SayText(Me, -1047009 - line, target);
    }

    public override void OnJustSummoned(Creature summoned) => summoned.AI?.AttackStart(Me);

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 3: Say(-1047001); break;
            case 7: Say(-1047002); break;
            case 10: Say(-1047003); break;
            case 15: Say(-1047004); SummonBoars(0); break;
            case 26: Say(-1047005); break;
            case 34: Say(-1047006); break;
            case 45: Say(-1047007); SummonBoars(2); break;
            case 46:
                Say(-1047008);
                Me.NpcFlags |= (uint)NpcFlags.QuestGiver;
                if (_escortPlayer is { } player) System?.AiServices.QuestEvents?.EventHappened(player, Quest, Me, rewardGroup: true);
                SetEscortPaused(true);
                break;
        }
    }

    private void Say(int text) => System?.SayText(Me, text);

    private void SummonBoars(int start)
    {
        if (System is not { } system) return;
        for (int i = start; i < start + 2; i++)
        {
            if (system.Content.FindTemplate(RagingAgamar) is not { } template) break;
            var pos = Boars[i];
            Creature boar = system.SpawnTemporary(template, pos.X, pos.Y, pos.Z, 0, Me);
            system.MarkTimedOutOfCombatDespawn(boar, 25_000);
        }
    }
}

/// <summary>ScriptDev2 npc_snufflenose_gopherAI (mangos-classic razorfen_kraul/razorfen_kraul.cpp: Reset, DoFindNewTubber, MovementInform,
/// UpdateAI). Entry 4781 is the guardian spell 6918 (Summon Snufflenose, SUMMON_GUARDIAN) gives the player; it is a ScriptedPetAI, so
/// outside its walk to a tubber it is the generic <see cref="PetAI"/> (follow the owner, defend it). The Snufflenose Command (8283) reaches
/// it through <see cref="SnufflenoseCommandSpell"/>.</summary>
public sealed class SnufflenoseGopherAi : CreatureAI, IScriptedPetAi
{
    public const uint Entry = 4781, BlueleafTubber = 20920, CommandSpell = 8283;
    public const float SearchRange = 40f;
    private readonly PetAI _pet;
    private ObjectGuid _target;
    private bool _moving;

    public SnufflenoseGopherAi(Creature creature) : base(creature) => _pet = new PetAI(creature);

    public PetAI Pet => _pet;
    public bool IsMovingToTubber => _moving;
    public ObjectGuid TargetTubber => _target;

    public override bool AggroesOnSight => _pet.AggroesOnSight;
    public override void OnRespawn() => _moving = false; // Reset
    public override void MoveInLineOfSight(Unit who) => _pet.MoveInLineOfSight(who);
    public override void OnAttackedBy(Unit attacker) => _pet.OnAttackedBy(attacker);
    public override void OnKilledUnit(Unit victim) => _pet.OnKilledUnit(victim);
    public override bool AttackStart(Unit target) => _pet.AttackTarget(target);

    /// <summary>DoFindNewTubber: the nearest unspawned tubber within 40 yd that may be dug up (GO_FLAG_INTERACT_COND) and is in sight.</summary>
    public bool FindNewTubber()
    {
        if (Me.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects) return false;
        GameObject? tubber = objects.GameObjects
            .Where(go => go.Entry == BlueleafTubber && DistanceSq(go, Me) <= SearchRange * SearchRange)
            .OrderBy(go => DistanceSq(go, Me))
            .FirstOrDefault(go => !go.IsSpawned && (go.Flags & GameObjectFlags.InteractCond) != 0 && Me.IsWithinLineOfSight(go));
        if (tubber is null) return false;
        _target = tubber.Guid;
        // The pet forgets where it was following, so PetAI walks it back to its owner once it is done here (ScriptedPetAI re-follows).
        Me.GetCharmInfo()?.ClearFlags();
        Me.Motion.Clear();
        Me.Motion.MovePoint(1, tubber.X, tubber.Y, tubber.Z, run: false);
        _moving = true;
        return true;
    }

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point || pointId == 0 || !_moving)
        {
            _pet.OnMovementInform(type, pointId);
            return;
        }

        if (Me.Map?.FindUpdater<GameObjectMapSystem>() is { } objects && objects.Find(_target) is { } tubber)
        {
            objects.SetRespawnIn(tubber, 300);
            tubber.Flags &= ~GameObjectFlags.InteractCond;
        }
        _moving = false;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!_moving) _pet.OnUpdate(diffMs);
    }

    private static float DistanceSq(WorldObject a, WorldObject b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
}

/// <summary>
/// ScriptDev2 SnufflenoseCommand (razorfen_kraul.cpp, spell 8283's OnEffectExecute calls DoFindNewTubber on its target). ClassicDB z2815 gives
/// 8283 one DUMMY effect at TARGET_SCRIPT (38) with spell_script_target 4781, which this spell system does not select (it has no
/// spell_script_target); the cast therefore finds its gopher itself as TARGET_SCRIPT would, the nearest living 4781 within the spell's range
/// (SpellRange 4: 30 yd), and sends it digging.
/// </summary>
[SpellScript(SnufflenoseGopherAi.CommandSpell)]
public sealed class SnufflenoseCommandSpell : ISpellScript
{
    public const float DefaultRange = 30f;

    public void OnCast(SpellCast cast)
    {
        float range = cast.Spell.Range.Max > 0 ? cast.Spell.Range.Max : DefaultRange;
        if (cast.Caster.Map?.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        if (creatures.CreaturesOfEntryInRange(cast.Caster, SnufflenoseGopherAi.Entry, range)
                .FirstOrDefault(c => c.IsAlive) is { AI: SnufflenoseGopherAi gopher })
            gopher.FindNewTubber();
    }
}
