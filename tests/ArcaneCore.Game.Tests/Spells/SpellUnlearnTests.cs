using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The spell-system seams talents hang on: <see cref="SpellSystem.RemoveSpell"/> (vmangos Player::RemoveSpell,
/// Player.cpp:3797-3885) and the ordered <see cref="ISpellLearnObserver"/> list called from LearnSpell.
/// </summary>
public sealed class SpellUnlearnTests
{
    private const uint Learned = 60001;

    private static SpellTestKit Kit() => new(
        Spell(Learned, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    [Fact]
    public void RemoveSpell_ForgetsTheSpell_SendsOneRemovedSpell_AndDropsItsAura()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        Assert.True(kit.System.LearnSpell(player, Learned));
        Assert.True(kit.System.HasAura(player, Learned));   // the learned passive is cast
        session.Clear();

        Assert.True(kit.System.RemoveSpell(player, Learned));

        Assert.False(kit.Spellbook.HasSpell(player, Learned));
        Assert.False(kit.System.HasAura(player, Learned));
        Assert.Equal(BitConverter.GetBytes((ushort)Learned), Packets(session, WorldOpcode.SmsgRemovedSpell).Single());
        Assert.Single(Opcodes(session), WorldOpcode.SmsgRemovedSpell);
    }

    [Fact]
    public void RemoveSpell_OfAnUnknownSpell_ReturnsFalseAndSendsNothing()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        Assert.False(kit.System.RemoveSpell(player, Learned));

        Assert.Empty(session.Sent);
    }

    [Fact]
    public void RemoveSpell_WithoutASpellbook_Refuses()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.System.Spellbook = null;

        Assert.False(kit.System.RemoveSpell(player, Learned));

        Assert.Empty(session.Sent);
    }

    [Fact]
    public void ABookThatCannotForget_MakesRemoveSpellReturnFalse()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        var book = new LearnOnlyBook();
        kit.System.Spellbook = book;
        Assert.True(kit.System.LearnSpell(player, Learned));
        session.Clear();

        Assert.False(kit.System.RemoveSpell(player, Learned));   // ISpellbook.ForgetSpell defaults to false

        Assert.True(book.HasSpell(player, Learned));
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void WhileAQuestSettlementIsPending_LearnAndRemoveRefuseAndMutateNothing()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, Learned);
        var observer = new RecordingObserver();
        kit.System.AddLearnObserver(observer);
        Guid operation = Guid.NewGuid();
        Assert.True(player.BeginQuestSettlement(operation));
        session.Clear();

        Assert.False(kit.System.RemoveSpell(player, Learned));
        Assert.False(kit.System.LearnSpell(player, CooldownSpell));

        Assert.True(kit.Spellbook.HasSpell(player, Learned));
        Assert.False(kit.Spellbook.HasSpell(player, CooldownSpell));
        Assert.Empty(session.Sent);
        Assert.Empty(observer.Events);

        Assert.True(player.EndQuestSettlement(operation));
        Assert.True(kit.System.RemoveSpell(player, Learned));   // positive control: the same call works once released
    }

    [Fact]
    public void Observers_RunBeforeTheBookAdd_AndAfterTheLearnedPacket()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        var observer = new RecordingObserver();
        kit.System.AddLearnObserver(observer);
        observer.OnBefore = () => Assert.False(kit.Spellbook.HasSpell(player, CooldownSpell));
        observer.OnAfter = () =>
        {
            Assert.True(kit.Spellbook.HasSpell(player, CooldownSpell));
            Assert.Single(Packets(session, WorldOpcode.SmsgLearnedSpell));   // the packet is already out
        };

        Assert.True(kit.System.LearnSpell(player, CooldownSpell));

        Assert.Equal(["before:" + CooldownSpell, "after:" + CooldownSpell], observer.Events);
    }

    [Fact]
    public void ObserversRunInRegistrationOrder_AndAVetoStopsTheLearnWithoutAPacket()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        var order = new List<string>();
        var first = new RecordingObserver { Tag = "first", Shared = order };
        var vetoing = new RecordingObserver { Tag = "veto", Shared = order, Veto = true };
        var last = new RecordingObserver { Tag = "last", Shared = order };
        kit.System.AddLearnObserver(first);
        kit.System.AddLearnObserver(vetoing);
        kit.System.AddLearnObserver(last);

        Assert.False(kit.System.LearnSpell(player, CooldownSpell));

        Assert.Equal(["first:before", "veto:before"], order);
        Assert.False(kit.Spellbook.HasSpell(player, CooldownSpell));
        Assert.Empty(session.Sent);

        vetoing.Veto = false;     // positive control: without the veto the learn goes through
        order.Clear();
        Assert.True(kit.System.LearnSpell(player, CooldownSpell));
        Assert.Equal(["first:before", "veto:before", "last:before", "first:after", "veto:after", "last:after"], order);
    }

    [Fact]
    public void AnAlreadyKnownSpell_IsNotAnnouncedToAfterLearn()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, CooldownSpell);
        var observer = new RecordingObserver();
        kit.System.AddLearnObserver(observer);

        Assert.False(kit.System.LearnSpell(player, CooldownSpell));

        Assert.Equal(["before:" + CooldownSpell], observer.Events);   // asked, then the book said "already known"
    }

    [Fact]
    public void RemoveSpell_NotifiesObserversAfterTheRemoval()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, CooldownSpell);
        var observer = new RecordingObserver();
        kit.System.AddLearnObserver(observer);
        observer.OnRemoved = () => Assert.False(kit.Spellbook.HasSpell(player, CooldownSpell));

        Assert.True(kit.System.RemoveSpell(player, CooldownSpell));

        Assert.Equal(["removed:" + CooldownSpell], observer.Events);
    }

    [Fact]
    public void AnObserverThatThrows_IsSurfaced_NotSwallowed()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.AddLearnObserver(new RecordingObserver { OnBefore = () => throw new InvalidOperationException("observer failed") });

        Assert.Throws<InvalidOperationException>(() => kit.System.LearnSpell(player, CooldownSpell));
    }

    [Fact]
    public void RemovedObserversAreNoLongerCalled()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        var observer = new RecordingObserver();
        kit.System.AddLearnObserver(observer);
        Assert.True(kit.System.RemoveLearnObserver(observer));
        Assert.False(kit.System.RemoveLearnObserver(observer));

        Assert.True(kit.System.LearnSpell(player, CooldownSpell));

        Assert.Empty(observer.Events);
    }

    private sealed class RecordingObserver : ISpellLearnObserver
    {
        public List<string> Events { get; } = [];

        public string? Tag { get; init; }

        public List<string>? Shared { get; init; }

        public bool Veto { get; set; }

        public Action? OnBefore { get; set; }

        public Action? OnAfter { get; set; }

        public Action? OnRemoved { get; set; }

        public bool BeforeLearn(Player player, uint spellId)
        {
            Events.Add("before:" + spellId);
            Shared?.Add(Tag + ":before");
            OnBefore?.Invoke();
            return !Veto;
        }

        public void AfterLearn(Player player, uint spellId)
        {
            Events.Add("after:" + spellId);
            Shared?.Add(Tag + ":after");
            OnAfter?.Invoke();
        }

        public void AfterRemove(Player player, uint spellId)
        {
            Events.Add("removed:" + spellId);
            OnRemoved?.Invoke();
        }
    }

    /// <summary>An <see cref="ISpellbook"/> written before ForgetSpell existed: it only has the two original members.</summary>
    private sealed class LearnOnlyBook : ISpellbook
    {
        private readonly HashSet<uint> _spells = [];

        public bool HasSpell(Player player, uint spellId) => _spells.Contains(spellId);

        public bool LearnSpell(Player player, uint spellId) => _spells.Add(spellId);
    }
}
