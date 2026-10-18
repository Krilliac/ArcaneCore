using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Maws (15571), the Azshara sea beast that the Arcanite Buoy (event 9542, <see cref="Instances.Scripts.World.KalimdorWorldMapData"/>) calls up:
/// mangos-classic ScriptDev2 <c>boss_maws</c> (kalimdor/azshara.cpp:400-497). Thrash (8876) on every reset, if it is not already on. In a
/// fight: Frenzy (19812, with the generic frenzy emote) after 25 s, then every 25 s, or every 15 s below 20% health. Rampage (25744) on the
/// victim after a random 20-120 s, then again after 20-120 s, or after 12 s below 20%. Dark Water (25743) every 15 s while below 20%. When it
/// dies, or after 150 minutes when it despawns, the Theatric Lightning (183356) within 200 yd goes and the zone sees "The beast returns from
/// whence it came" (-1010000).
/// </summary>
public sealed class MawsAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 15571, GoTheatricLightning = 183356;
    public const uint SpellThrash = 8876, SpellFrenzy = 19812, SpellDarkWater = 25743, SpellRampage = 25744;
    public const uint FrenzyMs = 25_000, FrenzyLowMs = 15_000, DarkWaterMs = 15_000, RampageLowMs = 12_000, LifetimeMs = 150 * 60 * 1000;
    public const int EmoteFrenzy = -1000001, EmoteMawsKill = -1010000;
    public const float LowHealthPercent = 20f, LightningRange = 200f;

    private uint _frenzyMs, _darkWaterMs, _rampageMs, _despawnMs;

    public uint DespawnMs => _despawnMs;

    protected override void Reset()
    {
        _frenzyMs = FrenzyMs;
        _darkWaterMs = DarkWaterMs;
        _rampageMs = RandomRampageMs();
        _despawnMs = LifetimeMs;
        if (System is { } system && !system.HasAura(Me, SpellThrash))
        {
            DoCast(Me, SpellThrash, triggered: true);
        }
    }

    private uint RandomRampageMs() => (uint)(System?.RandomInt(20, 120) ?? 20) * 1000;

    private bool Low => Me.HealthPercent < LowHealthPercent;

    public override void OnDeath(Unit? killer) => Farewell();

    /// <summary>The lightning goes and the zone emote.</summary>
    private void Farewell()
    {
        if (Me.Map?.FindUpdater<GameObjectMapSystem>() is { } objects
            && objects.ClosestOfEntry(Me, GoTheatricLightning, LightningRange) is { } lightning)
        {
            objects.DespawnForRespawn(lightning);
        }

        System?.SayText(Me, EmoteMawsKill);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_despawnMs < diffMs)
        {
            Farewell();
            System?.ForcedDespawn(Me, 0);
            return;
        }

        _despawnMs -= diffMs;
        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (_frenzyMs < diffMs)
        {
            DoCast(Me, SpellFrenzy);
            System?.SayText(Me, EmoteFrenzy);
            _frenzyMs = Low ? FrenzyLowMs : FrenzyMs;
        }
        else
        {
            _frenzyMs -= diffMs;
        }

        if (_rampageMs < diffMs)
        {
            DoCast(victim, SpellRampage);
            _rampageMs = Low ? RampageLowMs : RandomRampageMs();
        }
        else
        {
            _rampageMs -= diffMs;
        }

        if (Low)
        {
            if (_darkWaterMs < diffMs)
            {
                DoCast(Me, SpellDarkWater);
                _darkWaterMs = DarkWaterMs;
            }
            else
            {
                _darkWaterMs -= diffMs;
            }
        }
    }
}
