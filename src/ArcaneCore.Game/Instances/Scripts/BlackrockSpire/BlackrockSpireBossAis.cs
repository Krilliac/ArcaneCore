using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockSpire;

/// <summary>boss_gythAI (mangos-classic blackrock_spire/boss_gyth.cpp: Reset, UpdateAI).</summary>
public sealed class GythAI(Creature creature) : ScriptDevBossAI(creature)
{
    private uint _acid, _freeze, _breath, _knock;
    private bool _chromaticChaos, _summonedRend;

    public override void OnRespawn()
    {
        _acid = 8_000;
        _freeze = 11_000;
        _breath = 4_000;
        _knock = 23_000;
        _chromaticChaos = _summonedRend = false;
        Me.InvincibilityHpThreshold = 1; // ScriptedAI::SetDeathPrevention(true)
        DoCast(Me, 16167, triggered: true); // Rend mounts Gyth
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Template.Entry == BlackrockSpireInstance.NpcRend)
        {
            (Me.Map?.FindUpdater<BlackrockSpireInstance>())?.TrackRend(summoned);
            summoned.System?.SayText(summoned, -1229019);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        if (!_chromaticChaos && (ulong)Me.Health * 2 < Me.MaxHealth
            && Me.Map?.FindUpdater<CreatureMapSystem>()?.Creatures.FirstOrDefault(c => c.Template.Entry == BlackrockSpireInstance.NpcNefarius && c.IsAlive) is { } nefarius)
        {
            if (nefarius.System?.CastSpell(nefarius, 16337, Me, triggered: true) == CreatureCastResult.Ok)
            {
                nefarius.System.SayText(nefarius, -1229017);
                _chromaticChaos = true;
            }
        }

        CastWhenReady(ref _acid, diffMs, Me, 16359, 7_000, 7_000);
        CastWhenReady(ref _freeze, diffMs, Me, 16350, 16_000, 16_000);
        CastWhenReady(ref _breath, diffMs, Me, 16390, 10_500, 10_500);
        CastWhenReady(ref _knock, diffMs, Victim, 10101, 23_000, 23_000);

        if (!_summonedRend && (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 11)
        {
            CreatureCastResult cast = DoCast(Me, 16328);
            // Spell 16328 is the retail summon. The fallback covers a missing spell or
            // summon target while using the same NPC entry; an ordinary cast failure retries.
            if (cast is CreatureCastResult.Ok or CreatureCastResult.UnknownSpell or CreatureCastResult.NoSpellSystem)
            {
                CreatureMapSystem? creatures = Me.Map?.FindUpdater<CreatureMapSystem>();
                bool rendPresent = creatures?.Creatures.Any(c => c.Template.Entry == BlackrockSpireInstance.NpcRend && c.Spawn is null && c.IsAlive) == true;
                if (!rendPresent && creatures?.SummonCorpseDespawn(Me, BlackrockSpireInstance.NpcRend, Me.X, Me.Y, Me.Z, Me.Orientation) is { } rend)
                {
                    Me.Map?.FindUpdater<BlackrockSpireInstance>()?.TrackRend(rend);
                    rendPresent = true;
                }

                if (rendPresent)
                {
                    _summonedRend = true;
                    Me.InvincibilityHpThreshold = 0;
                    System?.RemoveAuras(Me, 16167);
                }
            }
        }
    }
}

/// <summary>boss_pyroguard_emberseerAI (mangos-classic blackrock_spire/boss_pyroguard_emberseer.cpp: Reset, DoHandleEmberseerGrowing, UpdateAI).</summary>
public sealed class PyroguardEmberseerAI(Creature creature) : ScriptDevBossAI(creature)
{
    private uint _encage, _nova, _buffet, _pyro;
    private int _growingStacks;

    public override void OnRespawn()
    {
        _encage = 10_000;
        _nova = 6_000;
        _buffet = 3_000;
        _pyro = 14_000;
        _growingStacks = 0;
        Me.UnitFlags |= UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer;
        DoCast(Me, 13377, triggered: true);
    }

    public override void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue)
    {
        if (eventType != 5 || ++_growingStacks > 20)
        {
            return;
        }

        if (_growingStacks == 10)
        {
            System?.SayText(Me, -1229001);
        }
        else if (_growingStacks == 20)
        {
            System?.SayText(Me, -1229002);
            System?.SayText(Me, -1229003);
            DoCast(Me, 16047, triggered: true);
            DoCast(Me, 16534, triggered: true);
            DoCast(Me, 16052, triggered: true);
            Me.Map?.FindUpdater<BlackrockSpireInstance>()?.UseEmberseerRunes(reset: false);
            Me.UnitFlags &= ~(UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer);
        }
    }

    public override void OnReachedHome()
        => Me.Map?.FindUpdater<BlackrockSpireInstance>()?.SetData(BlackrockSpireInstance.TypeEmberseer, EncounterState.Fail);

    public override void OnDeath(Unit? killer)
        => Me.Map?.FindUpdater<BlackrockSpireInstance>()?.SetData(BlackrockSpireInstance.TypeEmberseer, EncounterState.Done);

    public override void OnUpdate(uint diffMs)
    {
        if (_encage > 0)
        {
            if (diffMs >= _encage)
            {
                foreach (Creature incarcerator in System?.Creatures.Where(c => c.Template.Entry == 10316 && c.IsAlive) ?? [])
                {
                    incarcerator.System?.CastSpell(incarcerator, 15281, Me, triggered: false);
                }

                _encage = 0;
            }
            else
            {
                _encage -= diffMs;
            }
        }

        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _nova, diffMs, Me, 23462, 6_000, 6_000);
        CastWhenReady(ref _buffet, diffMs, Me, 23341, 14_000, 14_000);
        CastWhenReady(ref _pyro, diffMs, RandomThreatTarget(), 20228, 15_000, 15_000);
    }
}

/// <summary>boss_overlordwyrmthalakAI (mangos-classic blackrock_spire/boss_overlord_wyrmthalak.cpp: Reset, JustSummoned, UpdateAI).</summary>
public sealed class OverlordWyrmthalakAI(Creature creature) : ScriptDevBossAI(creature)
{
    private static readonly uint[] Adds = [9583, 9268, 9216];
    private uint _blast, _shout, _cleave, _knock;
    private bool _summoned;

    public override void OnRespawn()
    {
        _blast = 20_000;
        _shout = 2_000;
        _cleave = 6_000;
        _knock = 12_000;
        _summoned = false;
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (Victim is { } target)
        {
            summoned.AI?.AttackStart(target);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _blast, diffMs, Me, 11130, 20_000, 20_000);
        CastWhenReady(ref _shout, diffMs, Me, 23511, 10_000, 10_000);
        CastWhenReady(ref _cleave, diffMs, Victim, 20691, 7_000, 7_000);
        CastWhenReady(ref _knock, diffMs, Me, 20686, 14_000, 14_000);

        if (!_summoned && (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 51)
        {
            _summoned = true;
            for (int i = 0; i < 2; i++)
            {
                uint entry = Adds[System?.RandomInt(0, 2) ?? 0];
                float x = i == 0 ? -51.6805f : -54.4554f;
                if (System?.SummonCorpseDespawn(Me, entry, x, -439.8f, 78.288f, 4.657f) is { } add)
                {
                    add.Motion.MovePoint(0, i == 0 ? -39.355381f : -49.875881f,
                        i == 0 ? -513.456482f : -511.896942f, i == 0 ? 88.472046f : 88.195160f, run: true);
                }
            }
        }
    }
}
