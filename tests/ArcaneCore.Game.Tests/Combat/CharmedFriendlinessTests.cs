using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Combat;

/// <summary>
/// A mind-controlled player is judged by who controls it, not by its native race team (vmangos WorldObject::GetReactionTo:
/// same GetCharmerOrOwnerOrOwnGuid is always friendly, Object.cpp:3614-3617; a player-controlled pair is then compared through the
/// affecting players and falls to the template reaction, Object.cpp:3640-3663). Player.Team never changes under charm, so the player-pair
/// shortcut must not be taken for a charmed player.
/// </summary>
public sealed class CharmedFriendlinessTests
{
    private const uint AllianceTemplate = 1;
    private const uint CreatureTemplate = 14;

    private static readonly FactionTemplateCatalog Catalog = new(
    [
        new FactionTemplateRecord(AllianceTemplate, AllianceTemplate, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
        new FactionTemplateRecord(CreatureTemplate, CreatureTemplate, 0, OwnMask: 8, FriendlyMask: 8, HostileMask: 2),
    ]);

    public static TheoryData<string> HookKinds => new() { "default", "faction" };

    private sealed class Scene : IDisposable
    {
        public SpellTestKit Kit { get; } = new();

        /// <summary>Human, the victim's side.</summary>
        public Player Ally { get; }

        /// <summary>Human, mind controlled by <see cref="Charmer"/> once <see cref="CharmThrallByPlayer"/> runs.</summary>
        public Player Thrall { get; }

        /// <summary>Orc.</summary>
        public Player Charmer { get; }

        /// <summary>Orc, the charmer's teammate.</summary>
        public Player Mate { get; }

        /// <summary>Human, the victim's teammate.</summary>
        public Player AllyMate { get; }

        public CombatHooks Hooks { get; }

        public Scene(string kind)
        {
            (Ally, _) = Kit.AddPlayer(1, 10, 10);
            (Thrall, _) = Kit.AddPlayer(2, 12, 10);
            (Charmer, _) = Kit.AddPlayer(3, 14, 10, race: Race.Orc);
            (Mate, _) = Kit.AddPlayer(4, 16, 10, race: Race.Orc);
            (AllyMate, _) = Kit.AddPlayer(5, 18, 10);
            Hooks = kind == "faction" ? new FactionCombatHooks(Catalog) : CombatHooks.Default;
            Ally.Map!.Combat.Hooks = Hooks;
            SpellTestKit.FlagForPvp(Charmer);
            SpellTestKit.FlagForPvp(Mate);
            SpellTestKit.FlagForPvp(Thrall);
        }

        public void CharmThrallByPlayer()
        {
            Thrall.SetUInt64(UpdateFields.UnitFieldCharmedby, Charmer.Guid.Value);
            Charmer.SetUInt64(UpdateFields.UnitFieldCharm, Thrall.Guid.Value);
        }

        public void Dispose() => Kit.Dispose();
    }

    [Theory]
    [MemberData(nameof(HookKinds))]
    public void UncharmedSameTeamPair_KeepsToday_FriendlyAndNotAttackable(string kind)
    {
        using var s = new Scene(kind);

        Assert.True(s.Hooks.IsFriendly(s.Ally, s.AllyMate));
        Assert.False(s.Hooks.CanAttack(s.Ally, s.AllyMate));
        Assert.False(s.Hooks.IsFriendly(s.Ally, s.Charmer));
        Assert.True(s.Hooks.CanAttack(s.Ally, s.Charmer)); // the orc is PvP flagged
    }

    [Theory]
    [MemberData(nameof(HookKinds))]
    public void MindControlledAlly_IsNotFriendlyToItsOldAllies_AndAttackable(string kind)
    {
        using var s = new Scene(kind);
        s.CharmThrallByPlayer();

        Assert.False(s.Hooks.IsFriendly(s.Ally, s.Thrall));
        Assert.False(s.Hooks.IsFriendly(s.Thrall, s.Ally));
        Assert.True(s.Hooks.CanAttack(s.Ally, s.Thrall)); // the thrall acts for the flagged orc charmer
        Assert.True(s.Hooks.CanAttack(s.AllyMate, s.Thrall));
    }

    [Theory]
    [MemberData(nameof(HookKinds))]
    public void Charmer_IsFriendlyToItsThrall_AndCannotAttackIt(string kind)
    {
        using var s = new Scene(kind);
        s.CharmThrallByPlayer();

        Assert.True(s.Hooks.IsFriendly(s.Charmer, s.Thrall));
        Assert.True(s.Hooks.IsFriendly(s.Thrall, s.Charmer));
        Assert.False(s.Hooks.CanAttack(s.Charmer, s.Thrall));
        Assert.False(s.Hooks.CanAttack(s.Thrall, s.Charmer));
    }

    [Theory]
    [MemberData(nameof(HookKinds))]
    public void CharmersTeammate_CanAssistTheThrall_ButTheVictimsTeammatesCannot(string kind)
    {
        using var s = new Scene(kind);
        s.CharmThrallByPlayer();
        CombatHookRelations relations = CombatHookRelations.Instance;

        Assert.True(relations.CanAssist(s.Mate, s.Thrall));
        Assert.True(relations.CanAssist(s.Charmer, s.Thrall));
        Assert.False(relations.CanAssist(s.Ally, s.Thrall));
        Assert.False(relations.CanAssist(s.AllyMate, s.Thrall));
    }

    [Fact]
    public void CreatureCharmedPlayer_IsAttackableByItsOldTeam_UnderFactionHooks()
    {
        using var s = new Scene("faction");
        // a creature (not in the map) charms the thrall and the charm gives it the creature's template (CharmService.ApplyCharm)
        s.Thrall.SetUInt64(UpdateFields.UnitFieldCharmedby, ObjectGuid.WithEntry(HighGuid.Unit, 5006, 999).Value);
        s.Thrall.FactionTemplate = CreatureTemplate;

        Assert.False(s.Hooks.IsFriendly(s.Ally, s.Thrall));
        Assert.False(s.Hooks.IsFriendly(s.AllyMate, s.Thrall));
        Assert.True(s.Hooks.CanAttack(s.Ally, s.Thrall));
        Assert.True(s.Hooks.CanAttack(s.AllyMate, s.Thrall));
    }

    [Fact]
    public void CreatureCharmedPlayer_IsNotFriendlyToItsOldTeam_UnderDefaultHooks()
    {
        using var s = new Scene("default");
        s.Thrall.SetUInt64(UpdateFields.UnitFieldCharmedby, ObjectGuid.WithEntry(HighGuid.Unit, 5006, 999).Value);

        Assert.False(s.Hooks.IsFriendly(s.Ally, s.Thrall));
        Assert.False(s.Hooks.IsFriendly(s.Thrall, s.Ally));
    }

    [Fact]
    public void FactionHooks_IsHostileTo_IsFalseBetweenCharmerAndThrall()
    {
        using var s = new Scene("faction");
        s.CharmThrallByPlayer();
        s.Thrall.FactionTemplate = CreatureTemplate; // hostile to the charmer's own template, as a template-only check would say
        s.Charmer.FactionTemplate = AllianceTemplate;
        var faction = (FactionCombatHooks)s.Hooks;

        Assert.False(faction.IsHostileTo(s.Charmer, s.Thrall));
        Assert.False(faction.IsHostileTo(s.Thrall, s.Charmer));
    }
}
