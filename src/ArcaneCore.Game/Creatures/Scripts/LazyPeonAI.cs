using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Lazy Peon (entry 10556, Valley of Trials), "Lazy Peons" (quest 5441): mangos-classic ScriptDev2 <c>npc_lazy_peon</c> (kalimdor/durotar.cpp).
/// The peon sleeps (17743) beside his tree. The Foreman's Blackjack (19938) from a player wakes him: he says his line, gives that player the kill
/// credit and runs to the nearest lumber pile (175784, 15 yd), kneels there, and works. After 80 s awake he walks back to his spawn and sleeps
/// again; a peon nobody wakes gets up on his own after 100-120 s and walks to the pile.
/// Differences from SD2: arrival is polled each update (no point movement inform), and he works at the pile with the chop-wood emote state
/// rather than walking his z2815 waypoint loop to a tree; his work sound (6197) is not played.
/// </summary>
public sealed class LazyPeonAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 10556, QuestLazyPeons = 5441, SpellPeonSleep = 17743, SpellAwakenPeon = 19938, GoLumberpile = 175784;
    public const int SayPeonAwoken = -1000795;
    public const uint SoundAwake1 = 6292, SoundAwake2 = 6294;
    public const uint EmoteOneshotKneel = 16, EmoteStateWorkChopWood = 234;
    public const uint SleepAfterMs = 80_000, KneelDelayMs = 2_500, GetUpMs = 3_000;
    private const float PileRange = 15f, ContactDistance = 1.5f, Arrived = 1f;

    private enum Phase { Asleep, ToPile, AtPile, Working, ToSpawn }

    private Phase _phase;
    private uint _wakeMs, _sleepMs, _kneelMs, _getUpMs;
    private (float X, float Y, float Z)? _target;

    public bool IsAsleep => _phase == Phase.Asleep;

    public bool IsHeadingToPile => _phase == Phase.ToPile;

    public bool IsHeadingHome => _phase == Phase.ToSpawn;

    protected override void Reset()
    {
        _phase = Phase.Asleep;
        _wakeMs = (uint)(System?.RandomInt(100_000, 120_000) ?? 100_000);
        _sleepMs = _kneelMs = _getUpMs = 0;
        _target = null;
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id == SpellAwakenPeon && _phase == Phase.Asleep)
        {
            Awaken(caster);
        }
    }

    /// <summary>npc_lazy_peonAI::Awaken: a player gets the line, the sound and the credit, and the peon runs; on his own he walks.</summary>
    public void Awaken(Unit invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        bool byPlayer = invoker is Player;
        if (invoker is Player player && System is { } system)
        {
            system.SayText(Me, SayPeonAwoken, player);
            system.PlayDirectSound(Me, system.RandomInt(0, 1) == 0 ? SoundAwake1 : SoundAwake2);
            system.KilledMonsterCredit(player, Me.Entry, Me.Guid);
        }

        System?.RemoveAuras(Me, SpellPeonSleep);
        Me.StandState = StandState.Stand;
        _wakeMs = 0;
        _sleepMs = SleepAfterMs;
        GoLumberPile(run: byPlayer);
    }

    private void GoLumberPile(bool run)
    {
        Me.SetUInt32(UpdateFields.UnitNpcEmotestate, 0);
        GameObject? pile = Me.Map?.FindUpdater<GameObjectMapSystem>()?.GameObjects
            .Where(g => g.Entry == GoLumberpile && Dist(g.X, g.Y, g.Z) <= PileRange * PileRange)
            .OrderBy(g => Dist(g.X, g.Y, g.Z)).FirstOrDefault();
        if (pile is null)
        {
            _phase = Phase.Working; // no pile in range: SD2 logs a script error and he stays put
            return;
        }

        // GetContactPoint: the side of the pile facing the peon.
        float dx = Me.X - pile.X, dy = Me.Y - pile.Y;
        float len = MathF.Max(MathF.Sqrt((dx * dx) + (dy * dy)), 0.001f);
        _target = (pile.X + (dx / len * ContactDistance), pile.Y + (dy / len * ContactDistance), pile.Z);
        _phase = Phase.ToPile;
        _kneelMs = KneelDelayMs;
        System?.MoveTo(Me, _target.Value.X, _target.Value.Y, _target.Value.Z, run, finalOrientation: null);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (UpdateVictim() && Victim is not null)
        {
            return;
        }

        switch (_phase)
        {
            case Phase.Asleep:
                if (Tick(ref _wakeMs, diffMs))
                {
                    Awaken(Me);
                }
                else if (System is { } system && !system.HasAura(Me, SpellPeonSleep))
                {
                    DoCast(Me, SpellPeonSleep); // reapply aura if it fell off while the peon was unloaded
                }

                return;
            case Phase.ToPile when ArrivedAtTarget():
                _phase = Phase.AtPile;
                break;
            case Phase.AtPile:
                if (_kneelMs > 0 && Tick(ref _kneelMs, diffMs))
                {
                    System?.PlayEmote(Me, EmoteOneshotKneel);
                    _getUpMs = GetUpMs;
                }
                else if (_getUpMs > 0 && Tick(ref _getUpMs, diffMs))
                {
                    _phase = Phase.Working;
                    Me.SetUInt32(UpdateFields.UnitNpcEmotestate, EmoteStateWorkChopWood);
                }

                break;
            case Phase.ToSpawn when ArrivedAtTarget():
                Me.SetUInt32(UpdateFields.UnitNpcEmotestate, 0);
                System?.SetFacingTo(Me, Me.Home.Orientation);
                DoCast(Me, SpellPeonSleep);
                Me.StandState = StandState.Sleep;
                Reset();
                return;
        }

        if (_phase is Phase.ToPile or Phase.AtPile or Phase.Working && Tick(ref _sleepMs, diffMs))
        {
            Me.SetUInt32(UpdateFields.UnitNpcEmotestate, 0);
            _target = (Me.Home.X, Me.Home.Y, Me.Home.Z);
            _phase = Phase.ToSpawn;
            System?.MoveTo(Me, Me.Home.X, Me.Home.Y, Me.Home.Z, run: false, finalOrientation: Me.Home.Orientation);
        }
    }

    private bool ArrivedAtTarget()
        => _target is { } t && Dist(t.X, t.Y, t.Z) <= Arrived * Arrived;

    private float Dist(float x, float y, float z)
    {
        float dx = Me.X - x, dy = Me.Y - y, dz = Me.Z - z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static bool Tick(ref uint timer, uint diffMs)
    {
        if (timer == 0)
        {
            return false;
        }

        if (timer <= diffMs)
        {
            timer = 0;
            return true;
        }

        timer -= diffMs;
        return false;
    }
}
