using System.Text;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters.Creation;
using Xunit;

namespace ArcaneCore.World.Tests.Characters.Creation;

/// <summary>
/// The vmangos HandleCharCreateOpcode check order (CharacterHandler.cpp:185-322): one input per
/// row, the first failing check wins.
/// </summary>
public sealed class CharacterCreationRulesTests
{
    private sealed class Facts : ICharacterCreationFacts
    {
        public HashSet<string> Taken { get; } = [];
        public int Count { get; set; }
        public byte? FirstRace { get; set; }
        public bool HasStart { get; set; } = true;

        public Task<bool> IsNameTakenAsync(string name) => Task.FromResult(Taken.Contains(name));
        public Task<int> CountOnRealmAsync() => Task.FromResult(Count);
        public Task<byte?> FirstCharacterRaceAsync() => Task.FromResult(FirstRace);
        public Task<bool> HasStartInfoAsync(byte race, byte cls) => Task.FromResult(HasStart);
    }

    private static CharacterCreationRequest Req(string name = "Thrall", byte race = 1, byte cls = 1, byte gender = 0)
        => new(Encoding.UTF8.GetBytes(name), race, cls, gender);

    private static async Task<CharResult> Run(
        CharacterCreationRequest request,
        Facts? facts = null,
        AccountSecurity security = AccountSecurity.Player,
        CharacterCreationOptions? options = null,
        int perRealm = 10)
        => (await CharacterCreationRules.EvaluateAsync(request, security, options ?? new CharacterCreationOptions(), perRealm, facts ?? new Facts())).Result;

    [Fact]
    public async Task AValidRequestIsAcceptedWithTheNormalizedName()
    {
        CharacterCreationDecision decision = await CharacterCreationRules.EvaluateAsync(
            Req("tHRALL"), AccountSecurity.Player, new CharacterCreationOptions(), 10, new Facts());
        Assert.True(decision.Accepted);
        Assert.Equal("Thrall", decision.Name);
    }

    [Fact]
    public async Task DisabledMask_RefusesTheMatchingTeamForPlayersOnly()
    {
        var alliance = new CharacterCreationOptions { CharactersCreatingDisabled = 1 };
        var horde = new CharacterCreationOptions { CharactersCreatingDisabled = 2 };
        Assert.Equal(CharResult.CharCreateDisabled, await Run(Req(race: 1), options: alliance));
        Assert.Equal(CharResult.CharCreateSuccess, await Run(Req(race: 2), options: alliance));
        Assert.Equal(CharResult.CharCreateDisabled, await Run(Req(race: 2), options: horde));
        Assert.Equal(CharResult.CharCreateSuccess, await Run(Req(race: 1), options: horde));
        Assert.Equal(CharResult.CharCreateSuccess, await Run(Req(race: 1), security: AccountSecurity.Moderator, options: alliance));
    }

    [Fact]
    public async Task DisabledMask_IsCheckedBeforeEverythingElse()
        => Assert.Equal(CharResult.CharCreateDisabled, await Run(Req(name: "", race: 1), options: new CharacterCreationOptions { CharactersCreatingDisabled = 3 }));

    [Fact]
    public async Task UnknownRaceOrClass_IsFailed_NotPlayableRaceIsDisabled()
    {
        Assert.Equal(CharResult.CharCreateFailed, await Run(Req(race: 0)));
        Assert.Equal(CharResult.CharCreateFailed, await Run(Req(race: 10)));
        Assert.Equal(CharResult.CharCreateDisabled, await Run(Req(race: 9))); // goblin: NOT_PLAYABLE
        Assert.Equal(CharResult.CharCreateFailed, await Run(Req(cls: 6)));
        Assert.Equal(CharResult.CharCreateFailed, await Run(Req(cls: 10)));
        Assert.Equal(CharResult.CharCreateFailed, await Run(Req(cls: 12)));
        Assert.Equal(CharResult.CharCreateSuccess, await Run(Req(cls: 11)));
    }

    [Fact]
    public async Task GenderAboveOne_IsFailed()
        => Assert.Equal(CharResult.CharCreateFailed, await Run(Req(gender: 2)));

    [Theory]
    [InlineData("", CharResult.CharNameNoName)]
    [InlineData("Abcdefghijklmnop", CharResult.CharNameNoName)]
    [InlineData("Abcdefghijklm", CharResult.CharNameTooLong)]
    [InlineData("A", CharResult.CharNameTooShort)]
    [InlineData("Ab1", CharResult.CharNameMixedLanguages)]
    public async Task NameErrors_AreReported(string name, CharResult expected)
        => Assert.Equal(expected, await Run(Req(name: name)));

    [Fact]
    public async Task MinPlayerName_IsAppliedBeforeNameInUse()
    {
        var facts = new Facts();
        facts.Taken.Add("Abc");
        Assert.Equal(CharResult.CharNameTooShort, await Run(Req(name: "Abc"), facts, options: new CharacterCreationOptions { MinPlayerName = 4 }));
        Assert.Equal(CharResult.CharCreateNameInUse, await Run(Req(name: "Abc"), facts));
    }

    [Fact]
    public async Task NameInUse_IsCheckedBeforeTheRealmLimit()
    {
        var facts = new Facts { Count = 10 };
        facts.Taken.Add("Thrall");
        Assert.Equal(CharResult.CharCreateNameInUse, await Run(Req(), facts));
        Assert.Equal(CharResult.CharCreateServerLimit, await Run(Req(name: "Other"), facts));
    }

    [Theory]
    [InlineData(25, 10, CharResult.CharCreateServerLimit)] // clamped to 10
    [InlineData(25, 9, CharResult.CharCreateSuccess)]
    [InlineData(0, 1, CharResult.CharCreateServerLimit)] // clamped to 1
    [InlineData(0, 0, CharResult.CharCreateSuccess)]
    public async Task CharactersPerRealm_IsClampedTo1Through10(int configured, int existing, CharResult expected)
        => Assert.Equal(expected, await Run(Req(), new Facts { Count = existing }, perRealm: configured));

    private static CharacterCreationOptions Pvp() => new() { GameType = RealmGameType.PvP };

    [Fact]
    public async Task PvpRealm_RefusesTheOtherFactionOnTheSameAccount()
    {
        var facts = new Facts { FirstRace = 2 }; // an Orc exists
        Assert.Equal(CharResult.CharCreatePvpTeamsViolation, await Run(Req(race: 1), facts, options: Pvp()));
        Assert.Equal(CharResult.CharCreateSuccess, await Run(Req(race: 8), facts, options: Pvp())); // another Horde race
    }

    [Fact]
    public async Task PvpRealm_Bypasses()
    {
        var facts = new Facts { FirstRace = 2 };
        Assert.Equal(CharResult.CharCreateSuccess, await Run(Req(race: 1), facts, security: AccountSecurity.GameMaster, options: Pvp()));
        Assert.Equal(CharResult.CharCreateSuccess, await Run(Req(race: 1), facts, options: new CharacterCreationOptions { GameType = RealmGameType.PvP, AllowTwoSideAccounts = true }));
        Assert.Equal(CharResult.CharCreateSuccess, await Run(Req(race: 1), facts, options: new CharacterCreationOptions { GameType = RealmGameType.Normal }));
        Assert.Equal(CharResult.CharCreateSuccess, await Run(Req(race: 1), facts, options: new CharacterCreationOptions { GameType = RealmGameType.Rp }));
    }

    [Theory]
    [InlineData(RealmGameType.PvP)]
    [InlineData(RealmGameType.RpPvP)]
    [InlineData(RealmGameType.FfaPvP)]
    public async Task EveryPvpRealmTypeAppliesTheRule(RealmGameType type)
        => Assert.Equal(CharResult.CharCreatePvpTeamsViolation,
            await Run(Req(race: 1), new Facts { FirstRace = 2 }, options: new CharacterCreationOptions { GameType = type }));

    [Fact]
    public async Task PvpRealm_AnAccountWithoutCharactersMayPickAnyFaction()
        => Assert.Equal(CharResult.CharCreateSuccess, await Run(Req(race: 1), new Facts { FirstRace = null }, options: Pvp()));

    [Fact]
    public async Task PvpRealm_OnlyTheFirstCharacterIsCompared()
    {
        // Orc #1 and Human #2 exist: the first character (Orc) still decides, so a Human is refused
        // and an Orc accepted (CharacterHandler.cpp:296-304, "need to check team only for first character").
        var facts = new Facts { FirstRace = 2 };
        Assert.Equal(CharResult.CharCreatePvpTeamsViolation, await Run(Req(race: 1), facts, options: Pvp()));
        Assert.Equal(CharResult.CharCreateSuccess, await Run(Req(race: 2), facts, options: Pvp()));
    }

    [Fact]
    public async Task PvpRealm_AFirstCharacterOfRaceZeroAlwaysViolates()
        => Assert.Equal(CharResult.CharCreatePvpTeamsViolation, await Run(Req(race: 1), new Facts { FirstRace = 0 }, options: Pvp()));

    [Fact]
    public async Task NoStartRow_IsCreateError()
        => Assert.Equal(CharResult.CharCreateError, await Run(Req(), new Facts { HasStart = false }));

    [Fact]
    public void StartLevelAndMoney_AreClamped()
    {
        var o = new CharacterCreationOptions { StartPlayerLevel = 5, GmStartLevel = 3, StartPlayerMoney = 100 };
        Assert.Equal(5, o.StartLevelFor(staff: false, maxPlayerLevel: 60));
        Assert.Equal(5, o.StartLevelFor(staff: true, maxPlayerLevel: 60)); // GM.StartLevel >= StartPlayerLevel
        Assert.Equal(100u, o.StartMoney);
        var g = new CharacterCreationOptions { StartPlayerLevel = 99, GmStartLevel = 80 };
        Assert.Equal(60, g.StartLevelFor(false, 60));
        Assert.Equal(80, g.StartLevelFor(true, 60));
        // GM.StartLevel ceiling is MAX_LEVEL 100 (DBCEnums.h:34, World.cpp:677), not PLAYER_STRONG_MAX_LEVEL 255.
        Assert.Equal(CharacterCreationOptions.MaxGmStartLevel, 100);
        Assert.Equal(100, new CharacterCreationOptions { GmStartLevel = 200 }.StartLevelFor(true, 255));
        Assert.Equal(1, new CharacterCreationOptions { GmStartLevel = 200 }.StartLevelFor(false, 255));
        // vmangos checks the minimum first (World.cpp:3002), so a start level above 100 wins.
        Assert.Equal(150, new CharacterCreationOptions { StartPlayerLevel = 150, GmStartLevel = 120 }.StartLevelFor(true, 255));
        Assert.Equal(1, new CharacterCreationOptions { StartPlayerLevel = 0 }.StartLevelFor(false, 60));
        Assert.Equal(0x7FFFFFFEu, new CharacterCreationOptions { StartPlayerMoney = long.MaxValue }.StartMoney);
    }
}
