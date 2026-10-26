using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>What a kiting bot does this think.</summary>
internal enum KiteDecision : byte
{
    /// <summary>Not kiting: fight on as usual (the risk judge still decides on a retreat).</summary>
    None,

    /// <summary>Cast a slow, root, stun or fear at the nearest pursuer.</summary>
    Slow,

    /// <summary>Walk a short leg away from the pursuers to the bot's casting range.</summary>
    Step,

    /// <summary>Kiting is not working in this fight (step cap, time cap, no spot): stop trying until the fight ends.</summary>
    GiveUp,
}

/// <summary>
/// What <see cref="PlayerbotKiteModel.Decide"/> reads: a plain snapshot, so the choice is a pure function (unit tested without a world).
/// </summary>
/// <param name="HealthPct">The bot's health percentage.</param>
/// <param name="HealthLimitPct"><c>World:Playerbots:Combat:Kite:HealthPct</c>: kite at or below it.</param>
/// <param name="Pursuers">Live creatures attacking the bot within the kite start distance.</param>
/// <param name="NearestCaster">The nearest pursuer casts spells of its own (it hurts from range, so backing away buys nothing).</param>
/// <param name="NearestImmobile">The nearest pursuer is rooted or stunned.</param>
/// <param name="NearestSpeed">The nearest pursuer's run speed (auras included).</param>
/// <param name="BotSpeed">The bot's run speed (auras included).</param>
/// <param name="SlowReady">A slow of the bot's class is known and castable at the nearest pursuer now.</param>
/// <param name="MsSinceStep">Milliseconds since the last step ended (<see cref="uint.MaxValue"/>: none yet this fight).</param>
/// <param name="Steps">Steps taken this fight.</param>
/// <param name="MaxSteps"><c>World:Playerbots:Combat:Kite:MaxStepsPerFight</c>.</param>
/// <param name="KiteMs">Milliseconds since the first kite action of this fight (0: none yet).</param>
/// <param name="FailedSpots">Steps in a row for which no spot was found or the walk was refused.</param>
internal readonly record struct KiteFacts(float HealthPct, float HealthLimitPct, int Pursuers, bool NearestCaster, bool NearestImmobile,
    float NearestSpeed, float BotSpeed, bool SlowReady, uint MsSinceStep, int Steps, int MaxSteps, uint KiteMs, int FailedSpots);

/// <summary>
/// Kiting for a hurt bot that can attack from range (pure): slow the creature on it, then walk back out to the bot's casting range
/// while the slow lasts, so the rotation keeps casting in the same think the bot arrives. The spells come from the bot's class
/// (<see cref="SlowsOf"/>) and their effect from the spell data (<see cref="IsSlow"/>, <see cref="PlayerbotEscapes.EffectOf"/>), never a
/// table of numbers. It is the fight-on branch of the risk model: a retreat (<c>PlayerbotRisk.ObserveFight</c>) always wins.
/// </summary>
internal static class PlayerbotKiteModel
{
    /// <summary>A pursuer this close (yards) makes the bot kite (a hunter: its dead zone plus 2, see <see cref="StartYards"/>).</summary>
    internal const float KiteStartYards = 8f;

    /// <summary>The 8-yard minimum of Auto Shot.</summary>
    internal const float HunterDeadZone = PlayerbotClassRotation.HunterDeadZone;

    /// <summary>Frost Nova only freezes what stands within this many yards of the mage.</summary>
    internal const float FrostNovaYards = 10f;

    /// <summary>More pursuers than this and kiting cannot hold them all: the bot fights on.</summary>
    internal const int MaxPursuers = 2;

    /// <summary>A pursuer at or under this fraction of the bot's run speed is out-walked without any slow.</summary>
    internal const float SlowSpeedRatio = 0.8f;

    /// <summary>Milliseconds between two steps.</summary>
    internal const uint StepCooldownMs = 1500;

    /// <summary>The longest a fight may be kited for (milliseconds since the first kite action).</summary>
    internal const uint MaxKiteMs = 30_000;

    /// <summary>Steps in a row with no spot (or a refused walk) after which the bot gives up.</summary>
    internal const int MaxFailedSpots = 2;

    /// <summary>A candidate leg is accepted when the clipped walk (<see cref="PlayerbotMoveCheck.TryClip"/>) reaches at least this fraction of it.</summary>
    internal const float MinClipFraction = 0.75f;

    /// <summary>The farthest the floor under a candidate end may be from the bot's own height (yards).</summary>
    internal const float MaxFloorDeltaYards = 3f;

    /// <summary>Yards added to a creature's aggro radius when a leg is checked against it.</summary>
    internal const float ThreatMarginYards = 2f;

    /// <summary>Yards a candidate end keeps inside every pursuer's leash zone (the bot stays well within the creature's reach).</summary>
    internal const float LeashMarginYards = 8f;

    /// <summary>Spacing of the samples along a candidate leg (yards).</summary>
    internal const float SampleYards = 2f;

    private static readonly string[] MageSlows = [PlayerbotEscapes.FrostNova, PlayerbotEscapes.ConeOfCold, PlayerbotEscapes.Frostbolt];
    private static readonly string[] HunterSlows = [PlayerbotEscapes.ConcussiveShot, PlayerbotEscapes.WingClip];
    private static readonly string[] WarlockSlows = [PlayerbotEscapes.CurseOfExhaustion, PlayerbotEscapes.DeathCoil];
    private static readonly string[] ShamanSlows = [PlayerbotEscapes.FrostShock];
    private static readonly string[] DruidSlows = [PlayerbotEscapes.EntanglingRoots];
    private static readonly string[] WarriorSlows = [PlayerbotEscapes.Hamstring];
    private static readonly string[] RogueSlows = [PlayerbotEscapes.Gouge];
    private static readonly string[] NoSlows = [];

    /// <summary>The slows of a class in the order they are tried (names resolved from the bot's book; empty for a class with none). Shared arrays: do not write.</summary>
    internal static string[] SlowsOf(Class playerClass) => playerClass switch
    {
        Class.Mage => MageSlows,
        Class.Hunter => HunterSlows,
        Class.Warlock => WarlockSlows,
        Class.Shaman => ShamanSlows,
        Class.Druid => DruidSlows,
        Class.Warrior => WarriorSlows,
        Class.Rogue => RogueSlows,
        _ => NoSlows,
    };

    /// <summary>
    /// Whether <paramref name="spell"/>, as the escape named <paramref name="name"/>, holds or slows its target: a root, stun or fear
    /// with a duration, or a negative movement-speed aura (<see cref="PlayerbotEscapes.EffectOf"/>, from the spell's own data). A spell
    /// that did not resolve, or lacks the data, is not a slow.
    /// </summary>
    internal static bool IsSlow(string name, SpellInfo? spell)
    {
        PlayerbotEscapes.EscapeEffect effect = PlayerbotEscapes.EffectOf(name, spell);
        return effect.FreeSeconds > 0f || effect.VictimSpeed < 1f;
    }

    /// <summary>How near a pursuer must be for the bot to kite: 8 yards, a hunter's dead zone plus 2.</summary>
    internal static float StartYards(bool hunter) => hunter ? HunterDeadZone + 2f : KiteStartYards;

    /// <summary>
    /// The distance the bot wants between itself and the pursuer after a step: its casting range less 3, within 10 and 22 yards; a
    /// hunter's within its dead zone plus 6 and 28.
    /// </summary>
    internal static float WantedYards(float preferredRange, bool hunter)
        => hunter ? Math.Clamp(preferredRange - 3f, HunterDeadZone + 6f, 28f) : Math.Clamp(preferredRange - 3f, 10f, 22f);

    /// <summary>One step's length: the yards still wanted, within 4 and <paramref name="stepYards"/>.</summary>
    internal static float StepLength(float wantedYards, float nearestYards, float stepYards)
        => Math.Clamp(wantedYards - nearestYards, 4f, MathF.Max(4f, stepYards));

    /// <summary>The turns off straight away from the pursuers a step tries, in order (degrees): away, then 35, 70 and 105 either side.</summary>
    internal static ReadOnlySpan<float> FanDegrees => [0f, 35f, -35f, 70f, -70f, 105f, -105f];

    /// <summary>The unit direction <paramref name="degrees"/> off <paramref name="away"/> (a flat unit vector).</summary>
    internal static Vector2 Turned(Vector2 away, float degrees)
    {
        float radians = degrees * MathF.PI / 180f;
        float cos = MathF.Cos(radians), sin = MathF.Sin(radians);
        return new Vector2((away.X * cos) - (away.Y * sin), (away.X * sin) + (away.Y * cos));
    }

    /// <summary>
    /// The choice of this think (see the type summary). The caller has checked that kiting is on, that the bot is alive, in combat, not
    /// casting, rooted, swimming or out of control, and that its class qualifies; this applies the health, the pursuers, the caps and the
    /// slow.
    /// </summary>
    internal static KiteDecision Decide(in KiteFacts facts)
    {
        if (facts.Pursuers <= 0 || facts.Pursuers > MaxPursuers || facts.NearestCaster) return KiteDecision.None;
        if (!(facts.HealthPct <= facts.HealthLimitPct)) return KiteDecision.None;
        if (facts.Steps >= facts.MaxSteps || facts.KiteMs >= MaxKiteMs || facts.FailedSpots >= MaxFailedSpots) return KiteDecision.GiveUp;

        bool outWalked = facts.NearestImmobile || (facts.BotSpeed > 0f && facts.NearestSpeed <= facts.BotSpeed * SlowSpeedRatio);
        if (outWalked) return facts.MsSinceStep < StepCooldownMs ? KiteDecision.None : KiteDecision.Step;
        return facts.SlowReady ? KiteDecision.Slow : KiteDecision.None;
    }
}
