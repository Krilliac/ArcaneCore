using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// The demons of the hunter quest "The Stave of the Ancients" (7636), the shared part of mangos-classic ScriptDev2 <c>npc_klinfran</c>
/// (eastern_kingdoms/burning_steppes.cpp:347-569), <c>npc_solenor</c> (kalimdor/silithus.cpp:34-301) and <c>npc_artorius</c>
/// (kalimdor/winterspring.cpp:486-721). The friendly form has gossip; the player who asks "Show me your real face, demon." (with the quest
/// incomplete) stops it where it stands, it emotes after 5 s and turns into the demon (UpdateEntry) after 10 s. The demon fights one hunter: an
/// attacker that is not that hunter, or a second unit on its threat list, makes it vanish (respawn in 15 min) and leaves The Cleaner (14503) to
/// fight whoever was on its threat list. Left alone 20 minutes out of combat it vanishes the same way without The Cleaner. Its death sets a
/// two-hour respawn. Each demon adds its spells and its answer to one hunter sting or clip.
/// </summary>
public abstract class StaveDemonAI(Creature creature) : ScriptedAI(creature)
{
    public const uint QuestStaveOfTheAncients = 7636, NpcTheCleaner = 14503;
    public const uint TransformMs = 10_000, TransformEmoteMs = 5000, DemonLifetimeMs = 20 * 60 * 1000;
    public const uint RespawnAfterDeathSeconds = 2 * 60 * 60, RespawnAfterVanishSeconds = 15 * 60;

    private ObjectGuid _hunter;
    private bool _transform;
    private uint _transformMs, _transformEmoteMs, _despawnMs;

    /// <summary>The friendly form's entry (it has the gossip).</summary>
    public abstract uint FriendlyEntry { get; }

    /// <summary>The demon form's entry.</summary>
    public abstract uint DemonEntry { get; }

    /// <summary>The emote of the friendly form 5 s into the transformation.</summary>
    protected abstract uint TransformEmote { get; }

    public bool IsTransforming => _transform;

    public ObjectGuid Hunter => _hunter;

    public uint DespawnMs => _despawnMs;

    public bool IsDemon => Me.Entry == DemonEntry;

    /// <summary>Reset: the friendly form gets its gossip and its timers, the demon its 20 minutes (once) and its spell timers.</summary>
    protected sealed override void Reset()
    {
        if (Me.Entry == FriendlyEntry)
        {
            Me.NpcFlags = (uint)NpcFlags.Gossip;
            _transformMs = TransformMs;
            _transformEmoteMs = TransformEmoteMs;
            _transform = false;
            _despawnMs = 0;
            ResetFriendly();
        }
        else if (Me.Entry == DemonEntry)
        {
            bool first = _despawnMs == 0;
            if (first)
            {
                _despawnMs = DemonLifetimeMs;
            }

            _hunter = default;
            ResetDemon(first);
        }
    }

    /// <summary>The friendly form's own reset (beyond the gossip and the transformation timers).</summary>
    protected virtual void ResetFriendly()
    {
    }

    /// <summary>The demon's spell timers; <paramref name="first"/> on the reset right after the transformation.</summary>
    protected abstract void ResetDemon(bool first);

    /// <summary>The demon's spells in a fight, after the threat check.</summary>
    protected abstract void UpdateDemon(uint diffMs, Unit victim);

    /// <summary>GossipSelect: BeginEvent (the player, no movement, no gossip, the transformation starts).</summary>
    public void BeginEvent(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_transform || Me.Entry != FriendlyEntry)
        {
            return;
        }

        _hunter = player.Guid;
        System?.MoveIdle(Me);
        Me.NpcFlags = 0;
        _transform = true;
    }

    /// <summary>Aggro: only the hunter (the first one, or the one who asked) may fight it.</summary>
    public override void OnAggro(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Class == Class.Hunter && (_hunter.IsEmpty || _hunter == target.Guid))
        {
            _hunter = target.Guid;
        }
        else
        {
            DemonDespawn();
        }
    }

    /// <summary>JustDied: a two-hour respawn.</summary>
    public override void OnDeath(Unit? killer) => System?.SetNextRespawnDelay(Me, RespawnAfterDeathSeconds);

    /// <summary>DemonDespawn: a 15-minute respawn; when triggered, The Cleaner takes on the living units of its threat list. Then it goes.</summary>
    protected void DemonDespawn(bool triggered = true)
    {
        if (System is not { } system)
        {
            return;
        }

        BeforeVanish();
        system.SetNextRespawnDelay(Me, RespawnAfterVanishSeconds);
        if (triggered && system.SummonDeadDespawn(Me, NpcTheCleaner, Me.X, Me.Y, Me.Z, 0f) is { } cleaner)
        {
            foreach (Unit unit in ThreatUnits(Me))
            {
                cleaner.AI?.AttackStart(unit);
            }
        }

        system.ForcedDespawn(Me, 0);
    }

    /// <summary>What goes before it vanishes (Solenor's guardians).</summary>
    protected virtual void BeforeVanish()
    {
    }

    /// <summary>The living units of a creature's threat list.</summary>
    protected static Unit[] ThreatUnits(Creature creature)
        => creature.Combat.HasThreatList ? [.. creature.Combat.Threat.Entries.Select(e => e.Target).Where(u => u.IsAlive)] : [];

    private static int ThreatCount(Creature creature) => creature.Combat.HasThreatList ? creature.Combat.Threat.Entries.Count : 0;

    /// <summary>UpdateAI: the transformation, the 20 minutes, then the demon's fight.</summary>
    public sealed override void OnUpdate(uint diffMs)
    {
        if (_transform)
        {
            if (_transformEmoteMs != 0)
            {
                if (_transformEmoteMs <= diffMs)
                {
                    System?.PlayEmote(Me, TransformEmote);
                    _transformEmoteMs = 0;
                }
                else
                {
                    _transformEmoteMs -= diffMs;
                }
            }

            if (_transformMs < diffMs)
            {
                _transform = false;
                if (System?.UpdateEntry(Me, DemonEntry) == true)
                {
                    Reset();
                }
            }
            else
            {
                _transformMs -= diffMs;
            }
        }

        if (_despawnMs != 0)
        {
            if (_despawnMs <= diffMs)
            {
                if (Me.IsAlive && !Me.Combat.IsInCombat)
                {
                    DemonDespawn(triggered: false);
                    return;
                }
            }
            else
            {
                _despawnMs -= diffMs;
            }
        }

        BeforeFight(diffMs);
        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (ThreatCount(Me) > 1)
        {
            DemonDespawn();
            return;
        }

        UpdateDemon(diffMs, victim);
    }

    /// <summary>Work done every update before the victim check (Solenor's delayed Soul Flame).</summary>
    protected virtual void BeforeFight(uint diffMs)
    {
    }

    /// <summary>3D distance with both bounding radii taken off (cmangos IsWithinDistInMap).</summary>
    protected bool WithinDist(Unit other, float range)
    {
        float dx = Me.X - other.X, dy = Me.Y - other.Y, dz = Me.Z - other.Z;
        float reach = range + Me.BoundingRadius + other.BoundingRadius;
        return (dx * dx) + (dy * dy) + (dz * dz) <= reach * reach;
    }
}

/// <summary>
/// Franklin the Friendly (14529) / Klinfran the Crazed (14534), Burning Steppes: <c>npc_klinfran</c> (burning_steppes.cpp:373-569). The
/// friendly form points 5 s into the change. Klinfran casts Demonic Frenzy (23257, with its emote) every 15 s after 5 s; Scorpid Sting (Rank 4,
/// 14277) takes the frenzy off and gives him Entropic Sting (23260).
/// </summary>
public sealed class KlinfranAI(Creature creature) : StaveDemonAI(creature)
{
    public const uint NpcFranklin = 14529, NpcKlinfran = 14534, SpellDemonicFrenzy = 23257, SpellEntropicSting = 23260, SpellScorpidStingRank4 = 14277;
    public const uint EmoteOneshotPoint = 25, FrenzyFirstMs = 5000, FrenzyMs = 15_000;
    public const int EmoteFrenzy = -1000001;

    private uint _frenzyMs;

    public override uint FriendlyEntry => NpcFranklin;

    public override uint DemonEntry => NpcKlinfran;

    protected override uint TransformEmote => EmoteOneshotPoint;

    protected override void ResetDemon(bool first) => _frenzyMs = FrenzyFirstMs;

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id == SpellScorpidStingRank4)
        {
            System?.RemoveAuras(Me, SpellDemonicFrenzy);
            DoCast(Me, SpellEntropicSting, triggered: true);
        }
    }

    protected override void UpdateDemon(uint diffMs, Unit victim)
    {
        if (_frenzyMs < diffMs)
        {
            if (DoCast(Me, SpellDemonicFrenzy) == CreatureCastResult.Ok)
            {
                System?.SayText(Me, EmoteFrenzy);
                _frenzyMs = FrenzyMs;
            }
        }
        else
        {
            _frenzyMs -= diffMs;
        }
    }
}

/// <summary>
/// Nelson the Nice (14536) / Solenor the Slayer (14530), Silithus: <c>npc_solenor</c> (silithus.cpp:61-301). The friendly form laughs 5 s
/// into the change. Solenor casts Soul Flame (23272) on himself as he appears and again 150 ms later; in a fight Creeping Doom (23589) every
/// 15 s after 3-6 s (its guardians attack his victim), Dreadful Fright (23275) at a victim more than 5 yd away every 15-20 s after 10-15 s, and
/// Soul Flame goes while Frost Trap (13810) holds him. Wing Clip (Rank 3, 14268) gives him Crippling Clip (23279) with its emote. His
/// guardians go when he evades or vanishes.
/// </summary>
public sealed class SolenorAI(Creature creature) : StaveDemonAI(creature)
{
    public const uint NpcNelson = 14536, NpcSolenor = 14530, NpcCreepingDoom = 14761, SpellSoulFlame = 23272, SpellDreadfulFright = 23275;
    public const uint SpellCreepingDoom = 23589, SpellCripplingClip = 23279, SpellFrostTrap = 13810, SpellWingClipRank3 = 14268;
    public const uint EmoteOneshotLaugh = 11, SoulFlameRecastMs = 150, CreepingDoomMs = 15_000;
    public const int EmoteImmobilized = -1001252;

    private uint _dreadfulFrightMs, _creepingDoomMs, _soulFlameMs;

    public override uint FriendlyEntry => NpcNelson;

    public override uint DemonEntry => NpcSolenor;

    protected override uint TransformEmote => EmoteOneshotLaugh;

    public uint SoulFlameMs => _soulFlameMs;

    protected override void ResetFriendly() => _soulFlameMs = 0;

    protected override void ResetDemon(bool first)
    {
        if (first)
        {
            _soulFlameMs = SoulFlameRecastMs;
            DoCast(Me, SpellSoulFlame);
        }

        _dreadfulFrightMs = (uint)(System?.RandomInt(10_000, 15_000) ?? 10_000);
        _creepingDoomMs = (uint)(System?.RandomInt(3000, 6000) ?? 3000);
    }

    /// <summary>EnterEvadeMode: his guardians go first.</summary>
    public override void OnEvade()
    {
        System?.DespawnGuardians(Me, 0);
        base.OnEvade();
    }

    protected override void BeforeVanish() => System?.DespawnGuardians(Me, 0);

    /// <summary>JustSummoned: a summon attacks his victim.</summary>
    public override void OnJustSummoned(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        if (Victim is { } victim)
        {
            summoned.AI?.AttackStart(victim);
        }
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id == SpellWingClipRank3 && DoCast(Me, SpellCripplingClip, triggered: true) == CreatureCastResult.Ok)
        {
            System?.SayText(Me, EmoteImmobilized);
        }
    }

    protected override void BeforeFight(uint diffMs)
    {
        if (_soulFlameMs == 0)
        {
            return;
        }

        if (_soulFlameMs <= diffMs)
        {
            DoCast(Me, SpellSoulFlame); // delayed so the spell's animation is seen
            _soulFlameMs = 0;
        }
        else
        {
            _soulFlameMs -= diffMs;
        }
    }

    protected override void UpdateDemon(uint diffMs, Unit victim)
    {
        if (System is { } system && system.HasAura(Me, SpellSoulFlame) && system.HasAura(Me, SpellFrostTrap))
        {
            system.RemoveAuras(Me, SpellSoulFlame);
        }

        if (_creepingDoomMs < diffMs)
        {
            DoCast(Me, SpellCreepingDoom);
            _creepingDoomMs = CreepingDoomMs;
        }
        else
        {
            _creepingDoomMs -= diffMs;
        }

        if (_dreadfulFrightMs < diffMs)
        {
            if (Distance2D(victim) > 5f && DoCast(victim, SpellDreadfulFright) == CreatureCastResult.Ok)
            {
                _dreadfulFrightMs = (uint)(System?.RandomInt(15_000, 20_000) ?? 15_000);
            }
        }
        else
        {
            _dreadfulFrightMs -= diffMs;
        }
    }

    /// <summary>cmangos GetDistance(target, false): 2D, both bounding radii taken off, never below 0.</summary>
    private float Distance2D(Unit other)
    {
        float dx = Me.X - other.X, dy = Me.Y - other.Y;
        return MathF.Max(0f, MathF.Sqrt((dx * dx) + (dy * dy)) - Me.BoundingRadius - other.BoundingRadius);
    }
}

/// <summary>
/// Artorius the Amiable (14531) / Artorius the Doombringer (14535), Winterspring: <c>npc_artorius</c> (winterspring.cpp:515-721). The friendly
/// form roars 5 s into the change. The Doombringer casts Demonic Frenzy (23257) every 15-20 s after 5-8 s and tries Demonic Doom (23298) on a
/// victim within 25 yd every 7.5 s. Serpent Sting (Rank 8 or 9, 13555 / 25295) gives him Stinging Trauma (23299) with its emote.
/// </summary>
public sealed class ArtoriusAI(Creature creature) : StaveDemonAI(creature)
{
    public const uint NpcAmiable = 14531, NpcDoombringer = 14535, SpellDemonicFrenzy = 23257, SpellDemonicDoom = 23298, SpellStingingTrauma = 23299;
    public const uint SpellSerpentStingRank8 = 13555, SpellSerpentStingRank9 = 25295, EmoteOneshotRoar = 15, DemonicDoomMs = 7500;
    public const float DemonicDoomRange = 25f;
    public const int EmotePoison = -1001251;

    private uint _demonicDoomMs, _frenzyMs;

    public override uint FriendlyEntry => NpcAmiable;

    public override uint DemonEntry => NpcDoombringer;

    protected override uint TransformEmote => EmoteOneshotRoar;

    protected override void ResetDemon(bool first)
    {
        _demonicDoomMs = DemonicDoomMs;
        _frenzyMs = (uint)(System?.RandomInt(5000, 8000) ?? 5000);
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id is SpellSerpentStingRank8 or SpellSerpentStingRank9 && DoCast(Me, SpellStingingTrauma, triggered: true) == CreatureCastResult.Ok)
        {
            System?.SayText(Me, EmotePoison);
        }
    }

    protected override void UpdateDemon(uint diffMs, Unit victim)
    {
        if (_frenzyMs < diffMs)
        {
            if (DoCast(Me, SpellDemonicFrenzy) == CreatureCastResult.Ok)
            {
                _frenzyMs = (uint)(System?.RandomInt(15_000, 20_000) ?? 15_000);
            }
        }
        else
        {
            _frenzyMs -= diffMs;
        }

        if (_demonicDoomMs < diffMs)
        {
            _demonicDoomMs = DemonicDoomMs; // once every 7.5 s whatever the outcome, to give the hunter some leeway
            if (WithinDist(victim, DemonicDoomRange))
            {
                DoCast(victim, SpellDemonicDoom);
            }
        }
        else
        {
            _demonicDoomMs -= diffMs;
        }
    }
}

/// <summary>
/// The Cleaner (14503): mangos-classic <c>npc_the_cleaner</c> (world/npcs_special.cpp:994-1050). Immune (29230) from every reset, it calls out
/// on aggro, goes when it evades, and from 3 s on goes as soon as its threat list is empty.
/// </summary>
public sealed class TheCleanerAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 14503, SpellImmunity = 29230, EmptyCheckAfterMs = 3000;
    public const int SayAggro = -1001253;

    private uint _despawnMs;

    protected override void Reset()
    {
        DoCast(Me, SpellImmunity, triggered: true);
        _despawnMs = EmptyCheckAfterMs;
    }

    public override void OnAggro(Unit target) => System?.SayText(Me, SayAggro);

    public override void OnEvade()
    {
        base.OnEvade();
        System?.ForcedDespawn(Me, 0);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_despawnMs < diffMs)
        {
            if (!Me.Combat.HasThreatList || Me.Combat.Threat.Entries.Count == 0)
            {
                System?.ForcedDespawn(Me, 0);
                return;
            }
        }
        else
        {
            _despawnMs -= diffMs;
        }

        UpdateVictim();
    }
}

/// <summary>
/// GossipHello / GossipSelect of the three Stave demons: "Show me your real face, demon." while The Stave of the Ancients (7636) is incomplete,
/// over the creature's own gossip text; choosing it closes the menu and begins the transformation.
/// </summary>
public sealed class StaveDemonGossip(Func<Player, uint, bool> questIncomplete) : INpcGossipScript
{
    public const string OptionRealFace = "Show me your real face, demon.";
    public const uint SenderMain = 1, ActionRealFace = 1000;

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(npc);
        bool showQuests = (npc.NpcFlags & NpcFlags.QuestGiver) != 0;
        return questIncomplete(player, StaveDemonAI.QuestStaveOfTheAncients)
            ? new ScriptedGossipMenu(showQuests, 0, [new ScriptedGossipItem(0, OptionRealFace, SenderMain, ActionRealFace)])
            : new ScriptedGossipMenu(showQuests, 0, []);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(npc);
        if (action != ActionRealFace)
        {
            return default;
        }

        if (player.Map?.FindUpdater<CreatureMapSystem>()?.FindCreature(npc.Guid)?.AI is StaveDemonAI demon)
        {
            demon.BeginEvent(player);
        }

        return new ScriptedGossipReply(0, Close: true);
    }
}
