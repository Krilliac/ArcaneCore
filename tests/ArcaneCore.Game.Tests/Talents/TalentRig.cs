using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Talents;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Talents;

/// <summary>A rank chain from an explicit map (spell -> previous rank).</summary>
internal sealed class MapRankChain(Dictionary<uint, uint> previous) : IRankChain
{
    public uint PreviousRank(uint spellId) => previous.GetValueOrDefault(spellId);
}

/// <summary>Records what the service asks to persist.</summary>
internal sealed class RecordingTalentSink : ITalentSink
{
    public List<RespecState> Respecs { get; } = [];

    public List<(uint Spell, bool Disabled)> Disabled { get; } = [];

    public int CharacterChanges { get; private set; }

    public void RespecChanged(Player player, RespecState state) => Respecs.Add(state);

    public void DisabledChanged(Player player, uint spellId, bool disabled) => Disabled.Add((spellId, disabled));

    public void CharacterChanged(Player player) => CharacterChanges++;
}

/// <summary>
/// One warrior with a talent service over a synthetic catalog: tab 1 and tab 3 are warrior trees, tab 2 belongs to another
/// class. Talent 5 is an active ability talent (P1) whose higher ranks P2/P3 are trainer-learned ranks of the same spell chain
/// and PX a passive child of the chain; talent 6 (tab 3) has three ranks.
/// </summary>
internal sealed class TalentRig : IDisposable
{
    public const long Now = 1_800_000_000;
    public const uint Gold = 10000;

    public const uint P1 = 3041, P2 = 3042, P3 = 3043, PassiveChild = 3044;
    public const uint T6R1 = 3051, T6R2 = 3052, T6R3 = 3053;

    public static SpellInfo ActiveSpell(uint id) => Spell(id, Effect(SpellEffectName.Dummy, 0));

    public static SpellInfo[] Spells() =>
    [
        .. TalentServiceLearnTests.Spells(),
        ActiveSpell(P1), ActiveSpell(P2), ActiveSpell(P3), TalentServiceLearnTests.PassiveSpell(PassiveChild),
        TalentServiceLearnTests.PassiveSpell(T6R1), TalentServiceLearnTests.PassiveSpell(T6R2), TalentServiceLearnTests.PassiveSpell(T6R3),
        // a passive talent whose effect applies a triggered aura spell (removed with the talent)
        Spell(3061, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy, trigger: 3062)) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        TalentServiceLearnTests.PassiveSpell(3062),
    ];

    public static TalentCatalog Catalog() => new(
        [new TalentTabRecord(1, 1u << 0, 0), new TalentTabRecord(2, 1u << 7, 1), new TalentTabRecord(3, 1u << 0, 2)],
        [
            new TalentRecord(1, 1, 0, 0, [TalentServiceLearnTests.T1R1, TalentServiceLearnTests.T1R2, TalentServiceLearnTests.T1R3, 0, 0], 0, 0, 0),
            new TalentRecord(3, 1, 0, 1, [TalentServiceLearnTests.T3R1, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(5, 1, 0, 3, [P1, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(6, 3, 0, 0, [T6R1, T6R2, T6R3, 0, 0], 0, 0, 0),
            new TalentRecord(7, 3, 0, 1, [3061, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(9, 2, 0, 0, [TalentServiceLearnTests.MageR1, 0, 0, 0, 0], 0, 0, 0),
        ]);

    public TalentRig(AccountSecurity security = AccountSecurity.Player, byte level = 20, uint money = 100 * Gold, TalentOptions? options = null)
    {
        Kit = new SpellTestKit(Spells());
        Session = new FakeSession(1, security);
        Player = TestWorld.CreatePlayer(1, 0, 0, Session);
        Player.Level = level;
        Kit.World.AddPlayer(Player);
        Kit.World.RunTick(0);
        Player.Money = money;
        Session.Clear();
        Time = Now;
        Service = new TalentService(Catalog(), Kit.System, options ?? new TalentOptions(), () => Time)
        {
            Sink = Sink,
            RankChain = new MapRankChain(new Dictionary<uint, uint> { [P2] = P1, [P3] = P2, [PassiveChild] = P1 }),
            KnownSpells = p => Kit.Spellbook.Spells.GetValueOrDefault(p.Guid) ?? [],
        };
        Service.InitTalentForLevel(Player);
    }

    public long Time { get; set; }

    public SpellTestKit Kit { get; }

    public FakeSession Session { get; }

    public Player Player { get; }

    public RecordingTalentSink Sink { get; } = new();

    public TalentService Service { get; }

    public uint Free => Player.GetUInt32(UpdateFields.PlayerCharacterPoints1);

    public bool Has(uint spell) => Kit.Spellbook.HasSpell(Player, spell);

    public void Learn(uint talent, uint rank) => Assert.True(Service.LearnTalent(Player, talent, rank));

    public void Dispose()
    {
        Service.Dispose();
        Kit.Dispose();
    }
}
