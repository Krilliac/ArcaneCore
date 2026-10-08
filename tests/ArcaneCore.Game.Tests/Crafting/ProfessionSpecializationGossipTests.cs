using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Skills;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>mangos-classic ScriptDev2 npc_professions.cpp: GossipHello/Select_npc_prof_blacksmith and GossipHello/Select_npc_prof_leather.</summary>
public sealed class ProfessionSpecializationGossipTests
{
    private static SpellInfo Teacher(uint id, uint learned)
        => SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.LearnSpell, 1, trigger: learned));

    [Fact]
    public void Blacksmith_MustHaveTheSkillAndFriendlyReputation_AndCannotLearnBothBranches()
    {
        using var kit = new SpellTestKit(Teacher(9790, 9788), Teacher(9789, 9787), SpellTestKit.Spell(9788), SpellTestKit.Spell(9787));
        var (player, _) = kit.AddPlayer(1);
        player.Level = 60;
        uint skill = 224;
        byte rank = 4;
        var gossip = new ProfessionSpecializationGossip(kit.System,
            (_, id) => id == SkillIds.Blacksmithing ? skill : 0,
            (_, _) => false,
            (_, _) => rank);
        NpcInfo smith = Npc(11145);

        Assert.Empty(gossip.Hello(player, smith)!.Items);
        skill = 225;
        rank = 3;
        Assert.Empty(gossip.Hello(player, smith)!.Items);
        rank = 4;
        Assert.Contains(gossip.Hello(player, smith)!.Items, line => line.Action == 1001);
        Assert.Contains(gossip.Hello(player, smith)!.Items, line => line.Action == 1002);

        gossip.SelectReply(player, smith, 1, 1001);
        Assert.True(kit.Spellbook.HasSpell(player, 9788));
        Assert.DoesNotContain(gossip.Hello(player, smith)!.Items, line => line.Action == 1002);
        gossip.SelectReply(player, smith, 1, 1002); // stale client selection cannot cross the specialization branch
        Assert.False(kit.Spellbook.HasSpell(player, 9787));
    }

    [Fact]
    public void Weaponsmith_Subdiscipline_NeedsLevelSkillAndNoOtherSubdiscipline()
    {
        using var kit = new SpellTestKit(Teacher(17044, 17040), SpellTestKit.Spell(17040));
        var (player, _) = kit.AddPlayer(2);
        kit.Spellbook.Teach(player, 9787);
        uint skill = 250;
        var gossip = new ProfessionSpecializationGossip(kit.System,
            (_, id) => id == SkillIds.Blacksmithing ? skill : 0, (_, _) => false, (_, _) => null);
        NpcInfo hammer = Npc(11191);

        player.Level = 49;
        Assert.Empty(gossip.Hello(player, hammer)!.Items);
        player.Level = 50;
        skill = 249;
        Assert.Empty(gossip.Hello(player, hammer)!.Items);
        skill = 250;
        Assert.Contains(gossip.Hello(player, hammer)!.Items, line => line.Action == 1005);

        gossip.SelectReply(player, hammer, 50, 1005);
        Assert.True(kit.Spellbook.HasSpell(player, 17040));
        Assert.DoesNotContain(gossip.Hello(player, Npc(11192))!.Items, line => line.Action == 1006);
    }

    [Fact]
    public void Leatherworker_NeedsOneRewardedQuest_AndCannotChooseASecondBranch()
    {
        using var kit = new SpellTestKit(Teacher(10657, 10656), Teacher(10659, 10658), SpellTestKit.Spell(10656), SpellTestKit.Spell(10658));
        var (player, _) = kit.AddPlayer(3);
        player.Level = 40;
        bool rewarded = false;
        var gossip = new ProfessionSpecializationGossip(kit.System,
            (_, id) => id == SkillIds.Leatherworking ? 225u : 0u,
            (_, quest) => rewarded && quest == 5141,
            (_, _) => null);
        NpcInfo dragon = Npc(7866);

        Assert.Empty(gossip.Hello(player, dragon)!.Items);
        rewarded = true;
        Assert.Contains(gossip.Hello(player, dragon)!.Items, line => line.Action == 1002);
        gossip.SelectReply(player, dragon, 52, 1002);
        Assert.True(kit.Spellbook.HasSpell(player, 10656));
        Assert.DoesNotContain(gossip.Hello(player, Npc(7868))!.Items, line => line.Action == 1004);
        gossip.SelectReply(player, Npc(7868), 52, 1004);
        Assert.False(kit.Spellbook.HasSpell(player, 10658));
    }

    [Fact]
    public void GossipPacketSelection_TeachesTheSpecialization_AndClosesTheMenu()
    {
        using var npc = new NpcServiceKit(NpcFlags.Gossip);
        npc.Npc = npc.Npc with { Entry = 11145, Guid = ObjectGuid.WithEntry(HighGuid.Unit, 11145, 77) };
        npc.Player.Level = 60;
        var book = new MemorySpellbook();
        var system = new SpellSystem(new SpellStore(
            [Teacher(9790, 9788), SpellTestKit.Spell(9788)], [], []), () => 0u, spellbook: book);
        npc.Services.GossipScript = new ProfessionSpecializationGossip(system,
            (_, id) => id == SkillIds.Blacksmithing ? 225u : 0u,
            (_, _) => false, (_, _) => 4);

        npc.Services.GossipHello(npc.Player, npc.Npc.Guid);
        int choice = npc.State.Menu.GossipItems.ToList().FindIndex(item => item.ScriptAction == 1001);
        Assert.True(choice >= 0);
        Assert.True(npc.Sent(ArcaneCore.Protocol.WorldOpcode.SmsgGossipMessage));
        npc.Drain();

        npc.Services.GossipSelectOption(npc.Player, npc.Npc.Guid, (uint)choice, null);

        Assert.True(book.HasSpell(npc.Player, 9788));
        Assert.Contains(npc.Drain(), packet => packet.Opcode == ArcaneCore.Protocol.WorldOpcode.SmsgGossipComplete);
    }

    [Fact]
    public void AQualifiedSpecialist_CanOpenTheOrdinaryTrainerListFromTheScriptedMenu()
    {
        const uint teaching = 99100;
        var book = new MemorySpellbook();
        var system = new SpellSystem(new SpellStore(
            [Teacher(teaching, 99101), SpellTestKit.Spell(99101)], [], []), () => 0u, spellbook: book);
        NpcContent content = NpcContent.Empty with
        {
            TrainerSpells = [new TrainerSpell { Entry = 11146, Spell = teaching, SpellCost = 10 }],
        };
        using var npc = new NpcServiceKit(NpcFlags.Gossip | NpcFlags.Trainer, content,
            new QuestNpcDependencies(Spells: new SpellSystemLearner(() => system, new SkillLineAbilityCatalog([]))));
        npc.Npc = npc.Npc with
        {
            Entry = 11146, Guid = ObjectGuid.WithEntry(HighGuid.Unit, 11146, 77),
            TrainerType = TrainerType.TradeSkills, TrainerSpell = 9787,
        };
        book.Teach(npc.Player, 9787);
        npc.Services.GossipScript = new ProfessionSpecializationGossip(system,
            (_, id) => id == SkillIds.Blacksmithing ? 225u : 0u,
            (_, _) => false, (_, _) => null);

        npc.Services.GossipHello(npc.Player, npc.Npc.Guid);
        int choice = npc.State.Menu.GossipItems.ToList().FindIndex(item => item.ScriptAction == 2);
        Assert.True(choice >= 0);
        npc.Drain();

        npc.Services.GossipSelectOption(npc.Player, npc.Npc.Guid, (uint)choice, null);

        Assert.Contains(npc.Drain(), packet => packet.Opcode == ArcaneCore.Protocol.WorldOpcode.SmsgTrainerList);
    }

    [Fact]
    public void OtherCreatures_KeepTheGossipScriptInstalledBeforeIt()
    {
        // CraftingFeature wraps the script already on QuestNpcServices (the Alterac Valley collectors): a creature that is not a
        // profession trainer must still reach it, at hello and at selection.
        using var kit = new SpellTestKit();
        var (player, _) = kit.AddPlayer(4);
        var inner = new RecordingScript();
        var gossip = new ProfessionSpecializationGossip(kit.System, (_, _) => 0, (_, _) => false, (_, _) => null, inner);

        Assert.Same(RecordingScript.Menu, gossip.Hello(player, Npc(13176)));
        Assert.Equal(new ScriptedGossipReply(77, Close: true), gossip.SelectReply(player, Npc(13176), 1, 9));
        Assert.Equal(2, inner.Calls);

        Assert.NotSame(RecordingScript.Menu, gossip.Hello(player, Npc(11145)));
        Assert.Equal(2, inner.Calls);
    }

    private sealed class RecordingScript : INpcGossipScript
    {
        public static readonly ScriptedGossipMenu Menu = new(false, 5, []);
        public int Calls { get; private set; }

        public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
        {
            Calls++;
            return Menu;
        }

        public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 77;

        public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
        {
            Calls++;
            return new ScriptedGossipReply(77, Close: true);
        }
    }

    private static NpcInfo Npc(uint entry) => new(ObjectGuid.WithEntry(HighGuid.Unit, entry, 1), entry, 1,
        NpcFlags.Gossip, 0, 0, 0, 0, 0.5f, true, false, false, false, 0);
}
