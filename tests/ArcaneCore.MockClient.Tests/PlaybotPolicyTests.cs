using ArcaneCore.Game;
using ArcaneCore.MockClient.Playbots;
using ArcaneCore.MockClient.Scenarios;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class PlaybotPolicyTests
{
    [Fact]
    public void ProtocolCredentialLimitsAreCheckedBeforeAnyClientTransport()
    {
        var realm = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 3724);
        Assert.Throws<ArgumentException>(() => AutonomousPlaybot.Validate(new(realm, "BOT", new string('X', 17), "Botuser")));
        Assert.Throws<ArgumentException>(() => AutonomousPlaybot.Validate(new(realm, new string('X', 17), "PASS", "Botuser")));
    }
    private static PlaybotState State => new(1, 60, 60, false, new(0, 0, 0), new(0, 0, 0),
        [new(0xF130000006000001, 6, 30, 0, 0, new(2, 0, 0))], new HashSet<ulong>(), new HashSet<ulong>(),
        0, null, false, false, false, false, 0, 0);

    [Fact]
    public void CombatRequiresOptInKnownHealthAndEligibleObservedNpc()
    {
        Assert.DoesNotContain(PlaybotPolicy.Candidates(State, 0, true), c => c.Kind == PlaybotActionKind.Attack);
        Assert.Contains(PlaybotPolicy.Candidates(State, 6, true), c => c.Kind == PlaybotActionKind.Attack);
        Assert.DoesNotContain(PlaybotPolicy.Candidates(State with { Health = null }, 6, true), c => c.Kind == PlaybotActionKind.Attack);
        Assert.DoesNotContain(PlaybotPolicy.Candidates(State with { Objects = [State.Objects[0] with { Flags = (uint)UnitFlags.ImmuneToPlayer }] }, 6, true), c => c.Kind == PlaybotActionKind.Attack);
        Assert.DoesNotContain(PlaybotPolicy.Candidates(State with { Objects = [State.Objects[0] with { Guid = 17 }] }, 6, true), c => c.Kind == PlaybotActionKind.Attack);
    }

    [Fact]
    public void InjuredAttackerStopsAndDeadPlayerReleasesOnlyOnce()
    {
        var injured = State with { Health = 10, InCombat = true, AttackTarget = State.Objects[0].Guid };
        var candidates = PlaybotPolicy.Candidates(injured, 6, true);
        Assert.Equal(PlaybotActionKind.StopAttack, DeterministicPlaybotSelector.Choose(new(1, 10, 60, true, candidates)).Kind);
        Assert.DoesNotContain(candidates, c => c.Kind == PlaybotActionKind.Move);
        Assert.Contains(PlaybotPolicy.Candidates(State with { Health = 0 }, 6, true), c => c.Kind == PlaybotActionKind.ReleaseSpirit);
        Assert.Single(PlaybotPolicy.Candidates(State with { Health = 0, Released = true }, 6, true));
    }

    [Fact]
    public void ApproachIsThreeYardsAndRejectsUnobservedVerticalOrDistantTargets()
    {
        var distant = State with { Objects = [State.Objects[0] with { Position = new(12, 0, 0) }] };
        PlaybotCandidate move = Assert.Single(PlaybotPolicy.Candidates(distant, 6, true), c => c.Kind == PlaybotActionKind.Move);
        Assert.Equal(3, move.X);
        Assert.Equal(0, move.Y);
        Assert.DoesNotContain(PlaybotPolicy.Candidates(distant, 6, false), c => c.Kind == PlaybotActionKind.Move);
        Assert.DoesNotContain(PlaybotPolicy.Candidates(distant with { MoveCount = 20 }, 6, true), c => c.Kind == PlaybotActionKind.Move);
        Assert.DoesNotContain(PlaybotPolicy.Candidates(distant with { Objects = [distant.Objects[0] with { Position = new(12, 0, 20) }] }, 6, true), c => c.Target != 0 && c.Kind == PlaybotActionKind.Move);
    }

    [Fact]
    public void LootWaitsForAcknowledgementAndUsesOnlyDecodedNormalSlots()
    {
        var loot = new StartingZoneLoot.LootWindow(State.Objects[0].Guid, 1, 7,
            [new(2, 117, 1, 1, 0, 0, 0), new(3, 118, 1, 1, 0, 0, 1)]);
        var state = State with { Loot = loot };
        Assert.Equal((uint)2, Assert.Single(PlaybotPolicy.Candidates(state, 0, false), c => c.Kind == PlaybotActionKind.LootItem).Value);
        Assert.Single(PlaybotPolicy.Candidates(state with { LootPending = true }, 0, false));
        Assert.Contains(PlaybotPolicy.Candidates(state with { Loot = loot with { Items = [] } }, 0, false), c => c.Kind == PlaybotActionKind.LootMoney);
        Assert.Contains(PlaybotPolicy.Candidates(state with { Loot = loot with { Items = [], Gold = 0 } }, 0, false), c => c.Kind == PlaybotActionKind.CloseLoot);
    }

    [Fact]
    public void HeroicStrikeRequiresObservedSpellAndNaturalRageAndIsQueuedOnce()
    {
        var fighting = State with { AttackTarget = State.Objects[0].Guid, InCombat = true, Rage = 150, KnowsHeroic = true };
        Assert.Contains(PlaybotPolicy.Candidates(fighting, 6, true), c => c.Kind == PlaybotActionKind.CastKnownSpell && c.Value == 78);
        Assert.DoesNotContain(PlaybotPolicy.Candidates(fighting with { KnowsHeroic = false }, 6, true), c => c.Kind == PlaybotActionKind.CastKnownSpell);
        Assert.DoesNotContain(PlaybotPolicy.Candidates(fighting with { HeroicQueued = true }, 6, true), c => c.Kind == PlaybotActionKind.CastKnownSpell);
    }

    [Fact]
    public void InitialSpellsRespect5875CooldownWidthAndRejectTruncation()
    {
        Assert.Contains((uint)78, AutonomousPlaybot.ReadKnownSpells([0, 1, 0, 78, 0, 0, 0, 0, 0]));
        Assert.ThrowsAny<IOException>(() => AutonomousPlaybot.ReadKnownSpells([0, 1, 0, 78]));
        Assert.ThrowsAny<IOException>(() => AutonomousPlaybot.ReadKnownSpells([0, 0, 0, 1, 0]));
    }

    [Fact]
    public void DefensivePolicyUsesBothObservedTargetWordsAndDoesNotWaitOnAnEligibleAttacker()
    {
        const ulong player = 0x100000001;
        var fields = new Dictionary<int, uint> { [UpdateFields.UnitFieldTarget] = 1 };
        Assert.Null(AutonomousPlaybot.ReadGuid(fields, UpdateFields.UnitFieldTarget));
        fields[UpdateFields.UnitFieldTarget + 1] = 1;
        Assert.Equal(player, AutonomousPlaybot.ReadGuid(fields, UpdateFields.UnitFieldTarget));
        var state = State with { CharacterGuid = player, InCombat = true,
            Objects = [State.Objects[0] with { Flags = (uint)UnitFlags.InCombat, TargetGuid = player }] };
        PlaybotCandidate candidate = Assert.Single(PlaybotPolicy.Candidates(state, 6, false));
        Assert.Equal(PlaybotActionKind.Attack, candidate.Kind);
        Assert.Equal(State.Objects[0].Guid, candidate.Target);
        fields[UpdateFields.UnitFieldTarget + 1] = 2;
        Assert.DoesNotContain(PlaybotPolicy.Candidates(state with { Objects = [state.Objects[0] with {
            TargetGuid = AutonomousPlaybot.ReadGuid(fields, UpdateFields.UnitFieldTarget) }] }, 6, false),
            c => c.Kind == PlaybotActionKind.Attack);
    }
}
