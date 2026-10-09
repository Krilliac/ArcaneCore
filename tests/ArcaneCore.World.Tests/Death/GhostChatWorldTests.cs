using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Skills;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

/// <summary>
/// A dead character still talks to the server: vmangos WorldSession::HandleMessagechatOpcode parses '.' commands
/// (ChatHandler::ParseCommands) for the living and the dead alike, and only Player::Say / Yell / TextEmote are gated on
/// being alive. Found in the wave-10 rehearsal: a released GM's <c>.server shutdown</c> was silently dropped. The cause was
/// not the chat path: a character that logged in as a ghost never got its passive spells (the dead-caster check refused them,
/// where vmangos Spell::CheckCast exempts SPELL_ATTR_PASSIVE), so with real content it knew no language and every line it
/// sent in Common was refused with "You don't know that language" before command parsing.
/// </summary>
public sealed class GhostChatWorldTests
{
    private static async Task KillAndReleaseAsync(WorldTestHost host, string name)
    {
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(name)!;
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
        });
        await host.WaitForWorldAsync(() => (host.World.FindOnlinePlayer(name)!.Flags & PlayerFlags.Ghost) != 0, "the spirit to be a ghost");
    }

    [Fact]
    public async Task AReleasedGhostGm_RunsCommands()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GHOSTGM", "Ghostgm", AccountSecurity.Administrator);
        await KillAndReleaseAsync(host, "Ghostgm");
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".server info");

        Assert.StartsWith("Core revision: ", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task ADeadPlayerNotYetReleased_RunsCommands()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("CORPSEGM", "Corpsegm");
        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Corpsegm")!;
            p.Health = 0;
            p.Map!.Combat.KillPlayer(p);
        });
        await player.CollectAsync();

        await player.SendChatAsync(ChatType.Say, Language.Common, ".server info");

        Assert.StartsWith("Core revision: ", (await player.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task AGhostsSay_IsNotHeardByTheLiving()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient ghost = await host.EnterWorldAsync("GHOSTSAY", "Ghostsay");
        await using WorldTestClient living = await host.EnterWorldAsync("LIVESAY", "Livesay");
        await KillAndReleaseAsync(host, "Ghostsay");
        await ghost.CollectAsync();
        await living.CollectAsync();

        await ghost.SendChatAsync(ChatType.Say, Language.Common, "boo");

        Assert.DoesNotContain((await living.CollectAsync()), p => p.Opcode == WorldOpcode.SmsgMessagechat);
    }

    [Fact]
    public async Task AGhost_CanWhisperTheLiving()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient ghost = await host.EnterWorldAsync("GHOSTWSP", "Ghostwsp");
        await using WorldTestClient living = await host.EnterWorldAsync("LIVEWSP", "Livewsp");
        await KillAndReleaseAsync(host, "Ghostwsp");
        await ghost.CollectAsync();
        await living.CollectAsync();

        await ghost.SendChatAsync(ChatType.Whisper, Language.Common, "hello from beyond", "Livewsp");

        ChatMessage heard = await living.ReadChatAsync();
        Assert.Equal(ChatType.Whisper, heard.Type);
        Assert.Equal("hello from beyond", heard.Text);
    }

    private const uint CommonSpell = 668;

    [Fact]
    public async Task AGmWhoLogsInAsAGhost_KnowsItsLanguages_AndRunsCommands()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureLanguages);
        byte[] key = await host.AddAccountAsync("GHOSTREL", AccountSecurity.Administrator);
        await using (WorldTestClient creator = await host.ConnectAsync())
        {
            await creator.AuthenticateAsync("GHOSTREL", key);
            await creator.CreateCharacterAsync("Ghostrel");
        }

        CharacterRecord record = (await host.Characters.GetByIdAsync(1))!;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        host.Characters.SetLife(1, new CharacterLife(1, [0, 0, 0, 0, 0], 0, now + 300, true,
            new CorpseSnapshot(record.MapId, record.X, record.Y, record.Z, record.Orientation, now - 5, (byte)CorpseType.ResurrectablePve)));

        await using WorldTestClient gm = await host.ConnectAsync();
        await gm.AuthenticateAsync("GHOSTREL", key);
        await gm.LoginAsync(1);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Ghostrel") is { IsAlive: false }, "the character to be a ghost");
        Assert.True(await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ghostrel")!.KnowsLanguage(Language.Common)));
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".server info");

        Assert.StartsWith("Core revision: ", (await gm.ReadChatAsync()).Text);
    }

    private static void ConfigureLanguages(IServiceCollection services)
    {
        services.AddSingleton(new SkillCatalog(
            [new SkillLineRecord(SkillIds.LanguageCommon, SkillCategories.Languages, "Common", 0)],
            [new SkillRaceClassInfoRecord(SkillIds.LanguageCommon, 0, 0, 0, 0, 0)],
            [],
            [new SkillLineAbilityRecord(1, SkillIds.LanguageCommon, CommonSpell, 0, 0, 0, 0, 2, 0, 0)],
            SpellLearnSkillTable.Build([])));
        var common = new SpellTemplateRow
        {
            Id = CommonSpell, SpellName = "Language Common", RangeIndex = 1, EffectImplicitTargetA1 = 1,
            Attributes = 0x40 | 0x80, // vanilla 668: PASSIVE | HIDDEN_CLIENTSIDE, no ALLOW_CAST_WHILE_DEAD
            Effect1 = 39, EffectMiscValue1 = 7, // SPELL_EFFECT_LANGUAGE, LANG_COMMON
        };
        services.AddSingleton<ISpellContentStore>(new InMemorySkillSpellContentStore(new SpellContent(
            [common], [], [], [new SpellRangeRow { Id = 1 }], [],
            [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = CommonSpell }],
            [])));
        services.AddSingleton<ICharacterSkillStore, InMemoryCharacterSkillStore>();
    }
}
