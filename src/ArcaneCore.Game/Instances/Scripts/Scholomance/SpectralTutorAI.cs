using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Scholomance;

/// <summary>npc_spectral_tutorAI (mangos-classic eastern_kingdoms/scholomance/scholomance.cpp:35-121, ClassicDB ScriptName on entry 10498).</summary>
public sealed class SpectralTutorAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellImageProjection = 17651, SpellImageProjectionHeal = 17652, SpellImageProjectionSummon = 17653,
        SpellManaBurn = 17630, SpellSilence = 12528;

    private uint _manaBurn, _silence, _projection, _projectionEnd;

    /// <summary>True while Image Projection is up: the tutor does nothing else and refuses to evade.</summary>
    public bool Projecting => _projectionEnd != 0;

    public override void OnRespawn()
    {
        _projectionEnd = 0;
        _manaBurn = (uint)(System?.RandomInt(4_000, 19_000) ?? 4_000);
        _silence = (uint)(System?.RandomInt(0, 3_000) ?? 0);
        _projection = (uint)(System?.RandomInt(12_000, 13_000) ?? 12_000);
    }

    public override void OnEvade() => OnRespawn();

    /// <summary>EnterEvadeMode override: no evade while the projection runs.</summary>
    public override bool OnEnterEvadeMode() => _projectionEnd != 0;

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        if (_projectionEnd != 0)
        {
            if (_projectionEnd <= diffMs)
            {
                if (DoCast(Me, SpellImageProjectionHeal) == CreatureCastResult.Ok)
                {
                    _projectionEnd = 0;
                }
            }
            else
            {
                _projectionEnd -= diffMs;
            }

            return; // no other actions during Image Projection
        }

        CastWhenReady(ref _manaBurn, diffMs, Victim, SpellManaBurn, 9_000, 26_000);
        CastWhenReady(ref _silence, diffMs, Me, SpellSilence, 12_000, 26_000);
        if (CastWhenReady(ref _projection, diffMs, Me, SpellImageProjection, 18_000, 25_000))
        {
            DoCast(Me, SpellImageProjectionSummon, triggered: true);
            _projectionEnd = 1_000;
        }
    }
}
