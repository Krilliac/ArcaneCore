using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Rogue;

/// <summary>
/// Stealth aura state, visibility and the 2000 ms detection pass (vmangos SpellAuras.cpp:3631-3693 HandleModStealth,
/// Unit.cpp:6321-6461 IsVisibleForOrDetect, Player.cpp:1141-1151 and 22007-22052 HandleStealthedUnitsDetection). Level-1 players:
/// a rogue with a stealth skill of 5 is detected at 9 yards by an equal-level player.
/// </summary>
public sealed class StealthVisibilityTests
{
    private const uint Stealth = 910301;
    private const uint SecondStealth = 910302;
    private const uint HuntersMark = 910303;
    private const uint SlowBolt = 910304;
    private const uint FriendBolt = 910305;

    private sealed class Rig : IDisposable
    {
        public SpellTestKit Kit { get; }

        public StealthRegistry Registry { get; } = new();

        public StealthDetectionUpdater Updater { get; }

        public Map Map { get; }

        public Player Rogue { get; }

        public Player Viewer { get; }

        public FakeSession ViewerSession { get; }

        public Rig(float viewerDistance = 5f, bool attachRules = true, StealthOptions? options = null)
        {
            static SpellInfo Perm(SpellInfo s) => s with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0 };
            Kit = new SpellTestKit(
                Perm(Spell(Stealth, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModStealth))
                    with { AuraInterruptFlags = (SpellAuraInterruptFlags)AuraInterruptMask.StealthFamily, Dispel = StealthBreakRules.DispelStealth }),
                Perm(Spell(SecondStealth, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModStealth))),
                Perm(Spell(HuntersMark, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.Unit, AuraType.ModStalked))
                    with { AttributesEx = (SpellAttributesEx)StealthBreakRules.AttributesExAllowWhileStealthed, RangeIndex = 4, Range = new SpellRange(0, 100) }),
                Spell(SlowBolt, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy))
                    with { CastTime = new SpellCastTime(2000, 0, 0), RangeIndex = 4, Range = new SpellRange(0, 100), StartRecoveryCategory = 0, StartRecoveryTime = 0 },
                Spell(FriendBolt, Effect(SpellEffectName.Heal, 5, SpellImplicitTarget.UnitFriend))
                    with { CastTime = new SpellCastTime(2000, 0, 0), RangeIndex = 4, Range = new SpellRange(0, 100), StartRecoveryCategory = 0, StartRecoveryTime = 0 });
            var updater = new StealthDetectionUpdater(Registry, _ => false);
            Updater = updater;
            Map = Kit.World.GetMap(0);
            if (attachRules)
            {
                Kit.System.RegisterAura(AuraType.ModStealth, StealthAuras.Handler(Registry));
                Kit.System.RegisterAura(AuraType.ModStalked, new AuraHandler(null, null));
                Map.AddVisibilityRule(new StealthVisibilityRule(Kit.System, Registry, options));
                Map.AddUpdater(updater);
            }

            (Viewer, ViewerSession) = Kit.AddPlayer(1);
            (Rogue, _) = Kit.AddPlayer(2, viewerDistance);
            Rogue.Relocate(viewerDistance, 0, 83.5f, 0f, 0);
            Rogue.NeedsVisibilityUpdate = true;
            Kit.World.RunTick(0);
        }

        public void Stealthed(uint spell = Stealth) => Kit.System.CastSpell(Rogue, spell, SpellCastTargets.ForSelf(), triggered: true);

        public bool ViewerSeesRogue => Viewer.VisibleObjects.Contains(Rogue.Guid);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void ApplyingStealth_RaisesTheCreepAndPlayerStealthBytes_RemovingTheLastOneClearsThem()
    {
        using var rig = new Rig();
        Assert.Equal(0, rig.Rogue.GetByte(UpdateFields.UnitFieldBytes1, 3));
        Assert.Equal(0, rig.Rogue.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x20);

        rig.Stealthed();
        Assert.Equal(0x02, rig.Rogue.GetByte(UpdateFields.UnitFieldBytes1, 3));
        Assert.Equal(0x20, rig.Rogue.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x20);

        rig.Kit.System.RemoveAuras(rig.Rogue, Stealth);
        Assert.Equal(0, rig.Rogue.GetByte(UpdateFields.UnitFieldBytes1, 3));
        Assert.Equal(0, rig.Rogue.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x20);
    }

    [Fact]
    public void RemovingOneOfTwoStealthAuras_KeepsTheFlags_TheLastRemovalClearsThem()
    {
        using var rig = new Rig();
        rig.Stealthed(Stealth);
        rig.Stealthed(SecondStealth);

        rig.Kit.System.RemoveAuras(rig.Rogue, Stealth);
        Assert.Equal(0x02, rig.Rogue.GetByte(UpdateFields.UnitFieldBytes1, 3));
        Assert.False(rig.ViewerSeesRogue);

        rig.Kit.System.RemoveAuras(rig.Rogue, SecondStealth);
        Assert.Equal(0, rig.Rogue.GetByte(UpdateFields.UnitFieldBytes1, 3));
        Assert.True(rig.ViewerSeesRogue);
    }

    [Fact]
    public void StealthHidesTheUnitFromAViewerAtOnce_AndRemovingItShowsItAgain()
    {
        using var rig = new Rig();
        Assert.True(rig.ViewerSeesRogue);
        rig.ViewerSession.Clear();

        rig.Stealthed();
        Assert.False(rig.ViewerSeesRogue);
        rig.Kit.World.RunTick(0);
        Assert.Contains(rig.ViewerSession.Sent, p => p.Opcode is WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgCompressedUpdateObject);

        rig.Kit.System.RemoveAuras(rig.Rogue, Stealth);
        Assert.True(rig.ViewerSeesRogue);
    }

    [Fact]
    public void WithoutTheRule_StealthDoesNotHideAnything_ProvingTheRuleIsLoadBearing()
    {
        using var rig = new Rig(attachRules: false);
        rig.Kit.System.RegisterAura(AuraType.ModStealth, new AuraHandler(null, null));

        rig.Stealthed();

        Assert.True(rig.ViewerSeesRogue);
    }

    [Fact]
    public void AGroupMateAndAGameMasterKeepSeeingAStealthedUnit_AStrangerDoesNot()
    {
        using var rig = new Rig();
        var groups = new FakeGroups();
        groups.Parties.Add([rig.Viewer.Guid, rig.Rogue.Guid]);
        rig.Kit.System.Groups = groups;
        (Player stranger, _) = rig.Kit.AddPlayer(3, 6);
        (Player gm, _) = rig.Kit.AddPlayer(4, 7);
        gm.SetUInt32(UpdateFields.PlayerFlags, (uint)PlayerFlags.Gm);
        rig.Kit.World.RunTick(0);
        Assert.Contains(rig.Rogue.Guid, stranger.VisibleObjects);

        rig.Stealthed();

        Assert.True(rig.ViewerSeesRogue); // same party
        Assert.Contains(rig.Rogue.Guid, gm.VisibleObjects);
        Assert.DoesNotContain(rig.Rogue.Guid, stranger.VisibleObjects);
    }

    [Fact]
    public void InARaid_OnlyTheSameSubGroupAutoSeesAStealthedPlayer_ByDefault()
    {
        // vmangos Visibility.GroupMode = 0 (World.cpp:689): Player::IsGroupVisibleFor -> IsInSameGroupWith -> SameSubGroup (Player.cpp:2924-2941)
        using var rig = new Rig();
        (Player otherSubGroup, _) = rig.Kit.AddPlayer(3, 6);
        var groups = new FakeGroups { Raid = true };
        groups.Parties.Add([rig.Viewer.Guid, rig.Rogue.Guid]);
        groups.Parties.Add([otherSubGroup.Guid]);
        rig.Kit.System.Groups = groups;
        rig.Kit.World.RunTick(0);
        Assert.Contains(rig.Rogue.Guid, otherSubGroup.VisibleObjects);

        rig.Stealthed();

        Assert.True(rig.ViewerSeesRogue); // same sub-group
        Assert.DoesNotContain(rig.Rogue.Guid, otherSubGroup.VisibleObjects); // same raid, other sub-group
    }

    [Fact]
    public void GroupMode1_WholeRaidAutoSees_AStealthedPlayer()
    {
        using var rig = new Rig(options: new StealthOptions { GroupVisibilityMode = StealthGroupVisibility.SameRaid });
        (Player otherSubGroup, _) = rig.Kit.AddPlayer(3, 6);
        (Player outsider, _) = rig.Kit.AddPlayer(4, 7);
        var groups = new FakeGroups { Raid = true };
        groups.Parties.Add([rig.Viewer.Guid, rig.Rogue.Guid]);
        groups.Parties.Add([otherSubGroup.Guid]);
        rig.Kit.System.Groups = groups;
        rig.Kit.World.RunTick(0);

        rig.Stealthed();

        Assert.Contains(rig.Rogue.Guid, otherSubGroup.VisibleObjects); // Player::IsInSameRaidWith (mode 1)
        Assert.DoesNotContain(rig.Rogue.Guid, outsider.VisibleObjects);
    }

    [Fact]
    public void GroupMode2_EveryPlayerOfTheSameTeamAutoSees_ButNotTheOtherTeam()
    {
        using var rig = new Rig(options: new StealthOptions { GroupVisibilityMode = StealthGroupVisibility.SameTeam });
        (Player sameTeam, _) = rig.Kit.AddPlayer(3, 6);
        Player horde = TestWorld.CreatePlayer(4, 7, 0, new FakeSession(4), race: Race.Orc);
        rig.Kit.World.AddPlayer(horde);
        rig.Kit.World.RunTick(0);

        rig.Stealthed();

        Assert.Contains(rig.Rogue.Guid, sameTeam.VisibleObjects); // GetTeam() == p->GetTeam() (mode 2)
        Assert.DoesNotContain(rig.Rogue.Guid, horde.VisibleObjects);
    }

    [Fact]
    public void TheDetectionPassRunsEveryTwoSeconds_FirstAfterOneSecond_AndRevealsAUnitInsideNineYards()
    {
        using var rig = new Rig(viewerDistance: 5f);
        rig.Stealthed();
        Assert.False(rig.ViewerSeesRogue);

        rig.Kit.World.RunTick(999);
        Assert.False(rig.ViewerSeesRogue); // the viewer's first pass is due at 1000 ms
        rig.Kit.World.RunTick(1);
        Assert.True(rig.ViewerSeesRogue);
    }

    [Fact]
    public void AUnitOutsideTheDetectionDistance_IsNotRevealedByThePass_NorByMovementDrivenUpdates()
    {
        using var rig = new Rig(viewerDistance: 12f); // the equal-level distance is 9
        rig.Stealthed();

        for (int i = 0; i < 6; i++)
        {
            rig.Rogue.NeedsVisibilityUpdate = true; // movement-driven update: detect = false
            rig.Kit.World.RunTick(1000);
        }

        Assert.False(rig.ViewerSeesRogue);
        Assert.True(rig.Updater.PassesRun >= 2);

        // Walking into range is noticed only at the next pass, never by the movement update itself.
        rig.Rogue.Relocate(5, 0, 83.5f, 0f, 0);
        rig.Rogue.NeedsVisibilityUpdate = true;
        rig.Kit.World.RunTick(0);
        Assert.False(rig.ViewerSeesRogue);
        rig.Kit.World.RunTick(2000);
        Assert.True(rig.ViewerSeesRogue);
    }

    [Fact]
    public void ADetectedUnit_StaysThroughMovementUpdates_UntilThePassHidesItAgain()
    {
        using var rig = new Rig(viewerDistance: 5f);
        rig.Stealthed();
        rig.Kit.World.RunTick(1000);
        Assert.True(rig.ViewerSeesRogue);

        rig.Rogue.Relocate(25, 0, 83.5f, 0f, 0);
        rig.Rogue.NeedsVisibilityUpdate = true;
        rig.Kit.World.RunTick(100); // regular update: stays because the viewer already sees it
        Assert.True(rig.ViewerSeesRogue);

        rig.Kit.World.RunTick(2000); // the next detection pass drops it
        Assert.False(rig.ViewerSeesRogue);
    }

    [Fact]
    public void WithoutAStealthedUnit_TheDetectionPassDoesNoWork()
    {
        using var rig = new Rig();
        rig.Kit.World.RunTick(1000);
        rig.Kit.World.RunTick(2000);
        rig.Kit.World.RunTick(2000);
        Assert.Equal(0, rig.Updater.PassesRun);

        rig.Stealthed();
        rig.Kit.World.RunTick(2000);
        Assert.True(rig.Updater.PassesRun > 0);
    }

    [Fact]
    public void TheHuntersMarkCaster_AlwaysSeesTheMarkedUnit()
    {
        using var rig = new Rig(viewerDistance: 50f);
        rig.Stealthed();
        Assert.False(rig.ViewerSeesRogue);

        rig.Kit.System.CastSpell(rig.Viewer, HuntersMark, SpellCastTargets.ForUnit(rig.Rogue.Guid), triggered: true);
        Assert.True(rig.Kit.System.HasAura(rig.Rogue, HuntersMark));
        Assert.True(rig.Kit.System.HasAura(rig.Rogue, Stealth)); // an allow-while-stealthed spell does not strip stealth
        rig.Rogue.NeedsVisibilityUpdate = true;
        rig.Kit.World.RunTick(1000);
        rig.Kit.World.RunTick(2000);

        Assert.True(rig.ViewerSeesRogue);
    }

    [Fact]
    public void StealthCancelsHostileCastsInProgress_ButNotFriendlyOnes()
    {
        using var rig = new Rig();
        (Player friend, _) = rig.Kit.AddPlayer(3, 3);
        rig.Kit.Spellbook.Teach(rig.Viewer, SlowBolt);
        rig.Kit.Spellbook.Teach(friend, FriendBolt);
        var relations = new FakeRelations();
        relations.Hostile.Add(rig.Rogue.Guid);
        relations.Hostile.Add(friend.Guid); // the rogue and its friend are on one side, the viewer on the other
        rig.Kit.System.Relations = relations;
        rig.Kit.System.HandleCastRequest(rig.Viewer, SlowBolt, SpellCastTargets.ForUnit(rig.Rogue.Guid));
        rig.Kit.System.HandleCastRequest(friend, FriendBolt, SpellCastTargets.ForUnit(rig.Rogue.Guid));
        Assert.NotNull(rig.Kit.System.GetState(rig.Viewer.Guid)!.CurrentCast);
        Assert.NotNull(rig.Kit.System.GetState(friend.Guid)!.CurrentCast);

        rig.Stealthed();

        Assert.Null(rig.Kit.System.GetState(rig.Viewer.Guid)?.CurrentCast);
        Assert.NotNull(rig.Kit.System.GetState(friend.Guid)!.CurrentCast); // Unit.cpp:10192: friendly spells are not interrupted
    }

    [Fact]
    public void RestoringThePersistedStealthAura_RaisesTheFlagsAndHidesTheUnit_Idempotently()
    {
        using var rig = new Rig();
        rig.Stealthed();
        SpellStateSnapshot snapshot = rig.Kit.System.CaptureState(rig.Rogue, 1_800_000_000_000);
        Assert.Contains(snapshot.Auras, a => a.SpellId == Stealth);

        (Player relogged, _) = rig.Kit.AddPlayer(9, 6);
        Assert.Contains(relogged.Guid, rig.Viewer.VisibleObjects);
        rig.Kit.System.RestoreAuras(relogged, snapshot.Auras, 1_800_000_001_000);

        Assert.Equal(0x02, relogged.GetByte(UpdateFields.UnitFieldBytes1, 3));
        Assert.Equal(0x20, relogged.GetByte(UpdateFields.PlayerFieldBytes2, 1) & 0x20);
        Assert.DoesNotContain(relogged.Guid, rig.Viewer.VisibleObjects);

        // Applying again (a second restore replaces the holder) leaves exactly one set of flags and no visibility flicker.
        rig.Kit.System.RestoreAuras(relogged, snapshot.Auras, 1_800_000_002_000);
        Assert.Equal(0x02, relogged.GetByte(UpdateFields.UnitFieldBytes1, 3));
        Assert.Equal(1, rig.Kit.System.GetAuras(relogged).Count(h => h.Spell.Id == Stealth));
    }

    [Fact]
    public void AStealthedUnitThatLeftTheWorld_IsPrunedFromTheRegistry()
    {
        using var rig = new Rig();
        rig.Stealthed();
        Assert.Equal(1, rig.Registry.Count);

        Assert.True(rig.Registry.AnyHidden);
        rig.Registry.Prune(_ => true);

        Assert.Equal(0, rig.Registry.Count);
        Assert.Equal(StealthVisibility.On, rig.Registry.VisibilityOf(rig.Rogue));
        Assert.False(rig.Registry.AnyHidden); // the visibility rule goes back to its no-lookup fast path
    }
}
