using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>M6 player state (chat tag, GM mode, AFK/DND, logout, action bar, languages) and ranged broadcasts.</summary>
public sealed class M6PlayerTests
{
    [Fact]
    public void ChatTag_IsGmBadgeThenDndThenAfk_AndTheBadgeNeedsStaff()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1, AccountSecurity.Player));
        Player staff = TestWorld.CreatePlayer(2, 0, 0, new FakeSession(2, AccountSecurity.Moderator));

        Assert.Equal(ChatTag.None, player.ChatTag);
        Assert.True(player.ToggleAfk());
        Assert.Equal(ChatTag.Afk, player.ChatTag);
        Assert.True(player.ToggleDnd());
        Assert.Equal(ChatTag.Dnd, player.ChatTag); // vmangos Player::GetChatTag checks DND before AFK
        player.GmChat = true;
        Assert.Equal(ChatTag.Dnd, player.ChatTag); // the badge only shows for moderators and up

        staff.GmChat = true;
        staff.ToggleAfk();
        Assert.Equal(ChatTag.Gm, staff.ChatTag);
        Assert.False(player.ToggleDnd());
        Assert.False(player.ToggleAfk());
        Assert.Equal(ChatTag.None, player.ChatTag);
    }

    [Fact]
    public void GameMasterMode_SetsTheFlagAndFaction_AndRestoresTheRaceFaction()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1, AccountSecurity.GameMaster));
        uint raceFaction = player.FactionTemplate;

        player.SetGameMaster(true);
        Assert.True(player.IsGameMaster);
        Assert.True((player.Flags & PlayerFlags.Gm) != 0);
        Assert.Equal(Player.GameMasterFactionTemplate, player.FactionTemplate);
        Assert.True(player.KnowsLanguage(Language.Orcish)); // GMs speak every language

        player.SetGameMaster(false);
        Assert.False(player.IsGameMaster);
        Assert.Equal(raceFaction, player.FactionTemplate);
        Assert.False(player.KnowsLanguage(Language.Orcish));
    }

    [Theory]
    [InlineData(Race.Human, new[] { Language.Common })]
    [InlineData(Race.Orc, new[] { Language.Orcish })]
    [InlineData(Race.Dwarf, new[] { Language.Common, Language.Dwarvish })]
    [InlineData(Race.NightElf, new[] { Language.Common, Language.Darnassian })]
    [InlineData(Race.Undead, new[] { Language.Orcish, Language.Gutterspeak })]
    [InlineData(Race.Tauren, new[] { Language.Orcish, Language.Taurahe })]
    [InlineData(Race.Gnome, new[] { Language.Common, Language.Gnomish })]
    [InlineData(Race.Troll, new[] { Language.Orcish, Language.Troll })]
    public void Languages_AreTheRacesStartingLanguages(Race race, Language[] known)
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), race: race);
        foreach (Language language in Enum.GetValues<Language>().Where(l => l is not Language.Addon))
        {
            bool expected = language == Language.Universal || known.Contains(language);
            Assert.Equal(expected, player.KnowsLanguage(language));
        }
    }

    [Fact]
    public void Logout_SitsRootsAndStuns_ThenCancelUndoesIt()
    {
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);

        player.BeginLogout(1000);
        Assert.Equal((WorldOpcode.SmsgStandstateUpdate, new byte[] { (byte)StandState.Sit }), session.Next(), new PacketComparer());
        (WorldOpcode op, byte[] root) = session.Next();
        Assert.Equal(WorldOpcode.SmsgForceMoveRoot, op);
        Assert.Equal(new byte[] { 0x01, 0x01, 0, 0, 0, 0 }, root); // packed GUID 1, counter 0
        Assert.True(player.IsRooted && player.IsLoggingOut && (player.UnitFlags & UnitFlags.Stunned) != 0);
        Assert.Equal(0x04, player.GetByte(UpdateFields.PlayerFieldBytes, 0) & 0x04);

        Assert.False(player.IsLogoutDue(1000 + 19_999, Player.DefaultLogoutDelayMs));
        Assert.True(player.IsLogoutDue(1000 + 20_000, Player.DefaultLogoutDelayMs));

        player.CancelLogout();
        (op, byte[] unroot) = session.Next();
        Assert.Equal((WorldOpcode.SmsgForceMoveUnroot, new byte[] { 0x01, 0x01, 1, 0, 0, 0 }), (op, unroot), new PacketComparer());
        Assert.Equal(WorldOpcode.SmsgStandstateUpdate, session.Next().Opcode);
        Assert.Equal(StandState.Stand, player.StandState);
        Assert.False(player.IsRooted || player.IsLoggingOut || (player.UnitFlags & UnitFlags.Stunned) != 0);
        Assert.Equal(0, player.GetByte(UpdateFields.PlayerFieldBytes, 0) & 0x04);
        Assert.False(player.IsLogoutDue(uint.MaxValue, 0));
    }

    [Fact]
    public void LogoutTimer_SurvivesTheMillisecondClockWrapping()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        player.BeginLogout(uint.MaxValue - 4_999);
        Assert.False(player.IsLogoutDue(14_999, 20_000)); // 19 999 ms later, across the wrap
        Assert.True(player.IsLogoutDue(15_000, 20_000));
    }

    [Fact]
    public void Logout_DoesNotSitDown_WhileSwimming()
    {
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        player.ApplyClientMovement(new MovementInfo { Flags = MovementFlags.Swimming }, 10);

        player.BeginLogout(10);
        Assert.Equal(WorldOpcode.SmsgForceMoveRoot, session.Next().Opcode); // no stand-state change first
        Assert.Equal(StandState.Stand, player.StandState);
    }

    [Fact]
    public void ActionButtons_AcceptTheVmangosTypes_AndSnapshotOnlyWhenChanged()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        Assert.Null(player.CreateSnapshot(0).ActionButtons);

        Assert.True(player.SetActionButton(0, 6603));                        // spell
        Assert.True(player.SetActionButton(1, 0x40000000 | 3));              // macro
        Assert.True(player.SetActionButton(2, 0x41000000 | 4));              // client macro
        Assert.True(player.SetActionButton(3, 0x80000000 | 2516));           // item
        Assert.False(player.SetActionButton(4, 0x01000000 | 5));             // "click" alone is not settable
        Assert.False(player.SetActionButton(120, 6603));                     // no such slot

        CharacterState state = player.CreateSnapshot(0);
        Assert.Equal(
            new[] { new ActionButton(0, 6603, 0x00), new ActionButton(1, 3, 0x40), new ActionButton(2, 4, 0x41), new ActionButton(3, 2516, 0x80) },
            state.ActionButtons);
        Assert.Null(player.CreateSnapshot(0).ActionButtons); // already captured

        Assert.True(player.SetActionButton(0, 0));
        Assert.Equal(3, player.CreateSnapshot(0).ActionButtons!.Count);
    }

    [Fact]
    public void Snapshot_CarriesMoneyTogglesPlayedTimeAndHome()
    {
        var character = new CharacterRecord
        {
            Id = 9, AccountId = 1, Name = "Saver", Race = 1, Class = 1, Level = 3, PlayedTime = 100, LevelPlayedTime = 40,
            Money = 25, ActionBarToggles = 0x05, HomeMapId = 1, HomeZoneId = 14, HomeX = 1, HomeY = 2, HomeZ = 3,
        };
        var appearance = new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400);
        var player = new Player(character, appearance, new FakeSession());
        Assert.Equal((25u, (byte)0x05), (player.Money, player.ActionBarToggles));

        using WorldRuntime world = TestWorld.CreateRuntime();
        world.AddPlayer(player);
        uint now = world.NowMs + 5000;
        CharacterState state = player.CreateSnapshot(now);
        Assert.InRange(state.PlayedTime, 104u, 106u);
        Assert.InRange(state.LevelPlayedTime, 44u, 46u);
        Assert.Equal(new HomeBind(1, 14, 1, 2, 3), state.Home);
        Assert.Equal((25u, (byte)0x05), (state.Money, state.ActionBarToggles));
    }

    [Fact]
    public void StandState_ChangesSendTheUpdate_OnlyWhenTheyChange()
    {
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);

        player.SetStandState(StandState.Kneel);
        Assert.Equal((WorldOpcode.SmsgStandstateUpdate, new byte[] { 8 }), session.Next(), new PacketComparer());
        player.SetStandState(StandState.Kneel);
        Assert.True(session.Sent.IsEmpty);
    }

    [Fact]
    public void BroadcastInRange_Uses3DDistancePlusRadii_TeamFilterAndZeroAsMapWide()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var sources = new FakeSession(1);
        var near = new FakeSession(2);
        var above = new FakeSession(3);
        var orc = new FakeSession(4);
        var far = new FakeSession(5);
        Player source = TestWorld.CreatePlayer(1, 0, 0, sources);
        world.AddPlayer(source);
        world.AddPlayer(TestWorld.CreatePlayer(2, 20, 0, near));
        Player high = TestWorld.CreatePlayer(3, 0, 0, above);
        world.AddPlayer(high);
        world.AddPlayer(TestWorld.CreatePlayer(4, 10, 0, orc, race: Race.Orc));
        world.AddPlayer(TestWorld.CreatePlayer(5, 26, 0, far)); // 26 > 25 + 2 × 0.389
        high.Relocate(0, 0, 83.5f + 30, 0, 0); // straight above: 30 yards in 3D
        world.RunTick(50);
        foreach (FakeSession s in new[] { sources, near, above, orc, far })
        {
            s.Clear();
        }

        Map map = source.Map!;
        map.BroadcastInRange(source, 25, WorldOpcode.SmsgMessagechat, [1], includeSelf: true);
        Assert.Equal((1, 1, 0, 1, 0), (sources.Sent.Count, near.Sent.Count, above.Sent.Count, orc.Sent.Count, far.Sent.Count));

        map.BroadcastInRange(source, 25, WorldOpcode.SmsgMessagechat, [2], includeSelf: false, onlyTeam: Team.Alliance);
        Assert.Equal((1, 2, 0, 1, 0), (sources.Sent.Count, near.Sent.Count, above.Sent.Count, orc.Sent.Count, far.Sent.Count));

        map.BroadcastInRange(source, 0, WorldOpcode.SmsgMessagechat, [3], includeSelf: false);
        Assert.Equal((1, 3, 1, 2, 1), (sources.Sent.Count, near.Sent.Count, above.Sent.Count, orc.Sent.Count, far.Sent.Count));
    }

    /// <summary>Compares (opcode, payload) pairs by payload content.</summary>
    private sealed class PacketComparer : IEqualityComparer<(WorldOpcode Opcode, byte[] Payload)>
    {
        public bool Equals((WorldOpcode Opcode, byte[] Payload) x, (WorldOpcode Opcode, byte[] Payload) y)
            => x.Opcode == y.Opcode && x.Payload.AsSpan().SequenceEqual(y.Payload);

        public int GetHashCode((WorldOpcode Opcode, byte[] Payload) obj) => obj.Opcode.GetHashCode();
    }
}
