using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Talents;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Talents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Talents.TalentWorldFixture;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>
/// Talent spending through the bot's out-of-combat upkeep (<see cref="PlayerbotEquipment.Update"/>) and the real
/// CMSG_LEARN_TALENT handler, against the synthetic warrior catalog of the talent feature's own tests: talent 1 (row 0,
/// column 0, three ranks), talent 5 (row 0, column 3, one rank), talent 2 (row 1).
/// </summary>
public sealed class PlayerbotTalentsWorldTests
{
    private static WorldTestHost Start(TalentWorldFixture? fixture)
    {
        TalentTestServices.Current.Value = fixture;
        QuestInteractionTestServices.Current.Value = fixture is null ? null : new QuestInteractionFixture();
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            TalentTestServices.Current.Value = null;
            QuestInteractionTestServices.Current.Value = null;
        }
    }

    [Fact]
    public async Task ALevelTwelveWarrior_SpendsThreePoints_OneRankPerThink_ThenStops()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            TalentService talents = host.WorldServices.GetRequiredService<TalentFeature>().Service!;
            SpellbookCache book = host.WorldServices.GetRequiredService<SpellFeature>().Spellbook;
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 12;
                talents.InitTalentForLevel(player);
                Assert.Equal(3u, talents.FreePoints(player));
                var upkeep = new PlayerbotEquipment(session);
                for (uint spent = 1; spent <= 3; spent++)
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(upkeep.Update(player));
                    Assert.Equal(0, session.ManagedBudget.Remaining);
                    Assert.Equal(spent, talents.UsedPoints(player));
                }

                // Talent 1 to its third rank: every warrior build either starts there (Arms) or, with no talent at its first
                // position in this catalog, falls back to tier order, which starts there too. Old ranks are replaced.
                Assert.True(book.HasSpell(player, T1R3));
                Assert.False(book.HasSpell(player, T1R1) || book.HasSpell(player, T1R2));
                Assert.Equal(0u, talents.FreePoints(player));
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(upkeep.Update(player));
                Assert.Equal(1, session.ManagedBudget.Remaining);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ARefusedRequest_IsRemembered_AndTheNextThinkTakesAnotherTalent()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            TalentService talents = host.WorldServices.GetRequiredService<TalentFeature>().Service!;
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 10;
                talents.InitTalentForLevel(player);
                Assert.Equal(1u, talents.FreePoints(player));
                // The server refuses the first rank of talent 1 for a reason the bot cannot foresee (the learn is vetoed).
                var veto = new Veto(T1R1);
                spells.AddLearnObserver(veto);
                var upkeep = new PlayerbotEquipment(session);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(upkeep.Update(player)); // sent, refused: no reply exists, so the bot reads its spellbook
                Assert.Equal(1, veto.Vetoed);
                Assert.Equal(0u, talents.UsedPoints(player));
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(upkeep.Update(player)); // not the same request again: talent 5 (row 0) is next in tier order
                Assert.Equal(1, veto.Vetoed);
                Assert.True(talents.HasSpell(player, P1));
                Assert.Equal(0u, talents.FreePoints(player));
                spells.RemoveLearnObserver(veto);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task WithoutATalentCatalog_NothingIsSpent_AndNothingFaults()
    {
        await using WorldTestHost host = Start(null);
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Assert.Null(host.WorldServices.GetRequiredService<TalentFeature>().Service);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 60;
                player.SetUInt32(UpdateFields.PlayerCharacterPoints1, 51);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(new PlayerbotEquipment(session).Update(player));
                Assert.Equal(1, session.ManagedBudget.Remaining);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private sealed class Veto(uint spell) : ISpellLearnObserver
    {
        public int Vetoed { get; private set; }

        public bool BeforeLearn(Player player, uint spellId)
        {
            if (spellId != spell) return true;
            Vetoed++;
            return false;
        }
    }
}
