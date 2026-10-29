using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Tests.Rules.Creatures.Parity;

/// <summary>
/// The clean-room fixture creatures of the parity harness: hand-written EventAI rows on synthetic entries, with the stimuli that drive them.
/// The scenarios are grouped by what they exercise (events, actions, the engine's edge cases) in the partial files of this class; together
/// they use every EventAI event type, action type and target type the executor supports (see <c>ParityCoverageTests</c>).
/// </summary>
internal static partial class ParityFixtures
{
    /// <summary>EventAI flags (cmangos EventFlags).</summary>
    internal const uint Repeatable = 0x01;
    internal const uint RandomAction = 0x20;
    internal const uint DebugOnly = 0x80;
    internal const uint RangedOnly = 0x100;
    internal const uint MeleeOnly = 0x200;
    internal const uint CombatAction = 0x400;

    /// <summary>EventAI target types.</summary>
    internal const int Self = 0;
    internal const int Victim = 1;
    internal const int SecondOnThreat = 2;
    internal const int LastOnThreat = 3;
    internal const int RandomOnThreat = 4;
    internal const int RandomNotTop = 5;
    internal const int Invoker = 6;
    internal const int InvokerOwner = 7;
    internal const int RandomPlayer = 8;
    internal const int RandomNotTopPlayer = 9;
    internal const int Sender = 10;
    internal const int Spawner = 11;
    internal const int EventTarget = 12;
    internal const int DefaultTarget = 15;

    private static IReadOnlyList<ParityScenario>? s_all;

    /// <summary>Every scenario, in a fixed order.</summary>
    public static IReadOnlyList<ParityScenario> All => s_all ??= [.. EventScenarios(), .. ActionScenarios(), .. EdgeScenarios()];

    internal static CreatureAiEvent Row(uint id, int eventType, int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0, int p5 = 0, int p6 = 0,
        uint inversePhase = 0, byte chance = 100, uint flags = 0, CreatureAiAction a1 = default, CreatureAiAction a2 = default,
        CreatureAiAction a3 = default, uint guid = 0)
        => new()
        {
            Id = id,
            CreatureId = guid == 0 ? ParityScenario.Entry : 0,
            CreatureGuid = guid,
            EventType = (byte)eventType,
            InversePhaseMask = inversePhase,
            Chance = chance,
            Flags = flags,
            Param1 = p1,
            Param2 = p2,
            Param3 = p3,
            Param4 = p4,
            Param5 = p5,
            Param6 = p6,
            Action1 = a1,
            Action2 = a2,
            Action3 = a3,
        };

    internal static CreatureAiAction Act(int type, int p1 = 0, int p2 = 0, int p3 = 0) => new((byte)type, p1, p2, p3);

    /// <summary>ACTION_T_CAST: spell, target, cast flags.</summary>
    internal static CreatureAiAction Cast(int spell, int target = Self, int flags = 0) => Act(11, spell, target, flags);

    /// <summary>ACTION_T_SET_PHASE.</summary>
    internal static CreatureAiAction Phase(int phase) => Act(22, phase);

    internal static ParityStep Step(string label, Action<ParityContext> run) => new(label, run);

    internal static ParityStep Wait(uint ms) => new($"wait {ms} ms", c => c.Wait(ms));

    internal static ParityStep Pull(uint damage = 1) => new($"pull ({damage})", c => c.Pull(damage));

    /// <summary>Damages the creature so that <paramref name="percent"/> of its health is left.</summary>
    internal static ParityStep HealthTo(int percent) => new($"health to {percent}%", c =>
    {
        uint wanted = (uint)(c.Wolf.MaxHealth * percent / 100);
        if (c.Wolf.Health > wanted)
        {
            c.Damage(c.Wolf.Health - wanted);
        }
    });
}
