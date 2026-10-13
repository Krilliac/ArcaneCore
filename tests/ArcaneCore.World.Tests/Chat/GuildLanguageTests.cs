using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Chat;

/// <summary>
/// Who understands a guild line is the language on the packet (the 1.12 client garbles a tongue
/// the listener's language skill does not cover). vmangos HandleChatMessageOpcode keeps that
/// language unless two-side guild chat is on, and a MOD_LANGUAGE aura overwrites even that.
/// </summary>
public sealed class GuildLanguageTests
{
    private const uint LanguageAuraSpell = 930502;
    private const byte Dwarf = (byte)Race.Dwarf;

    [Fact]
    public async Task GuildChat_KeepsARacialLanguage_UnlessTwoSideGuildsForceUniversal_AndATongueCurseStillWins()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Keeper", AccountSecurity.GameMaster);
        await using WorldTestClient dwarf = await host.EnterWorldAsync("DWARF", "Dwarf", race: Dwarf);
        await using WorldTestClient human = await host.EnterWorldAsync("HUMAN", "Human");
        await host.WorldServices.GetRequiredService<SocialFeature>().GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        await DrainAsync(gm, dwarf, human);

        Assert.True(await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Dwarf")!.KnowsLanguage(Language.Dwarvish)));
        Assert.False(await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Human")!.KnowsLanguage(Language.Dwarvish)));

        Assert.Equal("Guild Stone created.", await CommandAsync(gm, ".guild create Dwarf \"Stone\""));
        Assert.Equal("Added to Stone.", await CommandAsync(gm, ".guild invite Human \"Stone\""));
        await DrainAsync(gm, dwarf, human);

        // A human has no Dwarvish skill. Universal would make the client show the words; Dwarvish makes it garble them.
        await dwarf.SendChatAsync(ChatType.Guild, Language.Dwarvish, "for Khaz Modan");
        ChatMessage racial = await human.ReadChatAsync();
        Assert.Equal((ChatType.Guild, Language.Dwarvish, "for Khaz Modan"), (racial.Type, racial.Language, racial.Text));

        host.WorldServices.GetRequiredService<SocialFeature>().Options.AllowTwoSideGuild = true;
        await dwarf.SendChatAsync(ChatType.Guild, Language.Common, "plain");
        Assert.Equal(Language.Universal, (await human.ReadChatAsync()).Language);

        await host.OnWorldAsync(() =>
        {
            Player speaker = host.World.FindOnlinePlayer("Dwarf")!;
            SpellFeature spells = ((WorldSession)speaker.Session).Services.GetRequiredService<SpellFeature>();
            var spell = new SpellInfo
            {
                Id = LanguageAuraSpell, RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Duration = new SpellDuration(-1, 0, -1),
                Effects =
                [
                    new SpellEffectInfo
                    {
                        Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModLanguage, MiscValue = (int)Language.Demonic,
                        TargetA = SpellImplicitTarget.UnitCaster,
                    },
                    new(), new(),
                ],
            };
            spells.System.Store = new SpellStore([.. spells.System.Store.All.Where(s => s.Id != LanguageAuraSpell), spell], [], []);
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(speaker, LanguageAuraSpell, SpellCastTargets.ForSelf(), triggered: true));
        });
        await DrainAsync(dwarf, human);

        // The aura overwrites the two-side Universal conversion (vmangos: "only single case used").
        await dwarf.SendChatAsync(ChatType.Guild, Language.Common, "cursed");
        Assert.Equal(Language.Demonic, (await human.ReadChatAsync()).Language);
    }

    private static async Task<string> CommandAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        ChatMessage reply = await client.ReadChatAsync();
        Assert.Equal(ChatType.System, reply.Type);
        return reply.Text;
    }

    private static async Task DrainAsync(params WorldTestClient[] clients)
    {
        foreach (WorldTestClient client in clients)
        {
            await client.CollectAsync();
        }
    }
}
