using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Quests;

/// <summary>
/// World schema version 6 (quests and NPC services, docs/integration/quests-npc.md): quest
/// templates and creature quest relations, gossip menus and texts, vendor and trainer lists,
/// flight nodes and paths. Table and column names follow vmangos/cmangos so the classic-db
/// content maps by name (ROADMAP § Content); the taxi tables mirror TaxiNodes.dbc/TaxiPath.dbc.
/// </summary>
public sealed class QuestNpcWorldModule : IDataModule
{
    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => 6;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("quest_template"),
        new CreateTableChange("creature_questrelation"),
        new CreateTableChange("creature_involvedrelation"),
        new CreateTableChange("npc_gossip"),
        new CreateTableChange("gossip_menu"),
        new CreateTableChange("gossip_menu_option"),
        new CreateTableChange("npc_text"),
        new CreateTableChange("npc_vendor"),
        new CreateTableChange("npc_trainer"),
        new CreateTableChange("taxi_nodes"),
        new CreateTableChange("taxi_path"),
        new CreateTableChange("race_taxi_start"),
        new CreateTableChange("points_of_interest"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QuestTemplate>(entity =>
        {
            entity.ToTable("quest_template");
            entity.HasKey(q => q.Entry);
            entity.Property(q => q.Entry).HasColumnName("entry").ValueGeneratedNever();
        });

        modelBuilder.Entity<CreatureQuestStarterRow>(entity =>
        {
            entity.ToTable("creature_questrelation");
            entity.HasKey(r => new { r.Id, r.Quest });
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.Quest).HasColumnName("quest");
        });

        modelBuilder.Entity<CreatureQuestEnderRow>(entity =>
        {
            entity.ToTable("creature_involvedrelation");
            entity.HasKey(r => new { r.Id, r.Quest });
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.Quest).HasColumnName("quest");
        });

        modelBuilder.Entity<NpcGossip>(entity =>
        {
            entity.ToTable("npc_gossip");
            entity.HasKey(r => r.NpcGuid);
            entity.Property(r => r.NpcGuid).HasColumnName("npc_guid").ValueGeneratedNever();
            entity.Property(r => r.TextId).HasColumnName("textid");
        });

        modelBuilder.Entity<GossipMenu>(entity =>
        {
            entity.ToTable("gossip_menu");
            entity.HasKey(r => new { r.Entry, r.TextId, r.ConditionId });
            entity.Property(r => r.Entry).HasColumnName("entry");
            entity.Property(r => r.TextId).HasColumnName("text_id");
            entity.Property(r => r.ConditionId).HasColumnName("condition_id");
        });

        modelBuilder.Entity<GossipMenuOption>(entity =>
        {
            entity.ToTable("gossip_menu_option");
            entity.HasKey(r => new { r.MenuId, r.Id });
            entity.Property(r => r.MenuId).HasColumnName("menu_id");
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.OptionIcon).HasColumnName("option_icon");
            entity.Property(r => r.OptionText).HasColumnName("option_text");
            entity.Property(r => r.OptionId).HasColumnName("option_id");
            entity.Property(r => r.NpcOptionNpcFlag).HasColumnName("npc_option_npcflag");
            entity.Property(r => r.ActionMenuId).HasColumnName("action_menu_id");
            entity.Property(r => r.ActionPoiId).HasColumnName("action_poi_id");
            entity.Property(r => r.BoxCoded).HasColumnName("box_coded");
            entity.Property(r => r.BoxText).HasColumnName("box_text");
            entity.Property(r => r.ConditionId).HasColumnName("condition_id");
        });

        modelBuilder.Entity<NpcTextRow>(entity =>
        {
            entity.ToTable("npc_text");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Id).HasColumnName("ID");
            entity.Property(r => r.Text0_0).HasColumnName("text0_0");
            entity.Property(r => r.Text0_1).HasColumnName("text0_1");
            entity.Property(r => r.Lang0).HasColumnName("lang0");
            entity.Property(r => r.Prob0).HasColumnName("prob0");
            entity.Property(r => r.Em0_0).HasColumnName("em0_0");
            entity.Property(r => r.Em0_1).HasColumnName("em0_1");
            entity.Property(r => r.Em0_2).HasColumnName("em0_2");
            entity.Property(r => r.Em0_3).HasColumnName("em0_3");
            entity.Property(r => r.Em0_4).HasColumnName("em0_4");
            entity.Property(r => r.Em0_5).HasColumnName("em0_5");
            entity.Property(r => r.Text1_0).HasColumnName("text1_0");
            entity.Property(r => r.Text1_1).HasColumnName("text1_1");
            entity.Property(r => r.Lang1).HasColumnName("lang1");
            entity.Property(r => r.Prob1).HasColumnName("prob1");
            entity.Property(r => r.Em1_0).HasColumnName("em1_0");
            entity.Property(r => r.Em1_1).HasColumnName("em1_1");
            entity.Property(r => r.Em1_2).HasColumnName("em1_2");
            entity.Property(r => r.Em1_3).HasColumnName("em1_3");
            entity.Property(r => r.Em1_4).HasColumnName("em1_4");
            entity.Property(r => r.Em1_5).HasColumnName("em1_5");
            entity.Property(r => r.Text2_0).HasColumnName("text2_0");
            entity.Property(r => r.Text2_1).HasColumnName("text2_1");
            entity.Property(r => r.Lang2).HasColumnName("lang2");
            entity.Property(r => r.Prob2).HasColumnName("prob2");
            entity.Property(r => r.Em2_0).HasColumnName("em2_0");
            entity.Property(r => r.Em2_1).HasColumnName("em2_1");
            entity.Property(r => r.Em2_2).HasColumnName("em2_2");
            entity.Property(r => r.Em2_3).HasColumnName("em2_3");
            entity.Property(r => r.Em2_4).HasColumnName("em2_4");
            entity.Property(r => r.Em2_5).HasColumnName("em2_5");
            entity.Property(r => r.Text3_0).HasColumnName("text3_0");
            entity.Property(r => r.Text3_1).HasColumnName("text3_1");
            entity.Property(r => r.Lang3).HasColumnName("lang3");
            entity.Property(r => r.Prob3).HasColumnName("prob3");
            entity.Property(r => r.Em3_0).HasColumnName("em3_0");
            entity.Property(r => r.Em3_1).HasColumnName("em3_1");
            entity.Property(r => r.Em3_2).HasColumnName("em3_2");
            entity.Property(r => r.Em3_3).HasColumnName("em3_3");
            entity.Property(r => r.Em3_4).HasColumnName("em3_4");
            entity.Property(r => r.Em3_5).HasColumnName("em3_5");
            entity.Property(r => r.Text4_0).HasColumnName("text4_0");
            entity.Property(r => r.Text4_1).HasColumnName("text4_1");
            entity.Property(r => r.Lang4).HasColumnName("lang4");
            entity.Property(r => r.Prob4).HasColumnName("prob4");
            entity.Property(r => r.Em4_0).HasColumnName("em4_0");
            entity.Property(r => r.Em4_1).HasColumnName("em4_1");
            entity.Property(r => r.Em4_2).HasColumnName("em4_2");
            entity.Property(r => r.Em4_3).HasColumnName("em4_3");
            entity.Property(r => r.Em4_4).HasColumnName("em4_4");
            entity.Property(r => r.Em4_5).HasColumnName("em4_5");
            entity.Property(r => r.Text5_0).HasColumnName("text5_0");
            entity.Property(r => r.Text5_1).HasColumnName("text5_1");
            entity.Property(r => r.Lang5).HasColumnName("lang5");
            entity.Property(r => r.Prob5).HasColumnName("prob5");
            entity.Property(r => r.Em5_0).HasColumnName("em5_0");
            entity.Property(r => r.Em5_1).HasColumnName("em5_1");
            entity.Property(r => r.Em5_2).HasColumnName("em5_2");
            entity.Property(r => r.Em5_3).HasColumnName("em5_3");
            entity.Property(r => r.Em5_4).HasColumnName("em5_4");
            entity.Property(r => r.Em5_5).HasColumnName("em5_5");
            entity.Property(r => r.Text6_0).HasColumnName("text6_0");
            entity.Property(r => r.Text6_1).HasColumnName("text6_1");
            entity.Property(r => r.Lang6).HasColumnName("lang6");
            entity.Property(r => r.Prob6).HasColumnName("prob6");
            entity.Property(r => r.Em6_0).HasColumnName("em6_0");
            entity.Property(r => r.Em6_1).HasColumnName("em6_1");
            entity.Property(r => r.Em6_2).HasColumnName("em6_2");
            entity.Property(r => r.Em6_3).HasColumnName("em6_3");
            entity.Property(r => r.Em6_4).HasColumnName("em6_4");
            entity.Property(r => r.Em6_5).HasColumnName("em6_5");
            entity.Property(r => r.Text7_0).HasColumnName("text7_0");
            entity.Property(r => r.Text7_1).HasColumnName("text7_1");
            entity.Property(r => r.Lang7).HasColumnName("lang7");
            entity.Property(r => r.Prob7).HasColumnName("prob7");
            entity.Property(r => r.Em7_0).HasColumnName("em7_0");
            entity.Property(r => r.Em7_1).HasColumnName("em7_1");
            entity.Property(r => r.Em7_2).HasColumnName("em7_2");
            entity.Property(r => r.Em7_3).HasColumnName("em7_3");
            entity.Property(r => r.Em7_4).HasColumnName("em7_4");
            entity.Property(r => r.Em7_5).HasColumnName("em7_5");
        });

        modelBuilder.Entity<VendorItem>(entity =>
        {
            entity.ToTable("npc_vendor");
            entity.HasKey(r => new { r.Entry, r.Item });
            entity.Property(r => r.Entry).HasColumnName("entry");
            entity.Property(r => r.Item).HasColumnName("item");
            entity.Property(r => r.MaxCount).HasColumnName("maxcount");
            entity.Property(r => r.IncrTime).HasColumnName("incrtime");
            entity.Property(r => r.Slot).HasColumnName("slot");
            entity.Property(r => r.ConditionId).HasColumnName("condition_id");
        });

        modelBuilder.Entity<TrainerSpell>(entity =>
        {
            entity.ToTable("npc_trainer");
            entity.HasKey(r => new { r.Entry, r.Spell });
            entity.Property(r => r.Entry).HasColumnName("entry");
            entity.Property(r => r.Spell).HasColumnName("spell");
            entity.Property(r => r.SpellCost).HasColumnName("spellcost");
            entity.Property(r => r.ReqSkill).HasColumnName("reqskill");
            entity.Property(r => r.ReqSkillValue).HasColumnName("reqskillvalue");
            entity.Property(r => r.ReqLevel).HasColumnName("reqlevel");
        });

        modelBuilder.Entity<TaxiNode>(entity =>
        {
            entity.ToTable("taxi_nodes");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.MapId).HasColumnName("map_id");
            entity.Property(r => r.X).HasColumnName("x");
            entity.Property(r => r.Y).HasColumnName("y");
            entity.Property(r => r.Z).HasColumnName("z");
            entity.Property(r => r.Name).HasColumnName("name");
            entity.Property(r => r.MountHorde).HasColumnName("mount_horde");
            entity.Property(r => r.MountAlliance).HasColumnName("mount_alliance");
        });

        modelBuilder.Entity<TaxiPath>(entity =>
        {
            entity.ToTable("taxi_path");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.FromNode).HasColumnName("from_node");
            entity.Property(r => r.ToNode).HasColumnName("to_node");
            entity.Property(r => r.Price).HasColumnName("price");
        });

        modelBuilder.Entity<RaceTaxiStart>(entity =>
        {
            entity.ToTable("race_taxi_start");
            entity.HasKey(r => r.Race);
            entity.Property(r => r.Race).HasColumnName("race").ValueGeneratedNever();
            entity.Property(r => r.Mask).HasColumnName("mask");
        });

        modelBuilder.Entity<PointOfInterest>(entity =>
        {
            entity.ToTable("points_of_interest");
            entity.HasKey(r => r.Entry);
            entity.Property(r => r.Entry).HasColumnName("entry").ValueGeneratedNever();
            entity.Property(r => r.X).HasColumnName("x");
            entity.Property(r => r.Y).HasColumnName("y");
            entity.Property(r => r.Icon).HasColumnName("icon");
            entity.Property(r => r.Flags).HasColumnName("flags");
            entity.Property(r => r.Data).HasColumnName("data");
            entity.Property(r => r.IconName).HasColumnName("icon_name");
        });
    }

    public void AddServices(IServiceCollection services)
    {
        services.AddScoped<IQuestContentStore, EfQuestContentStore>();
        services.AddScoped<INpcContentStore, EfNpcContentStore>();
    }
}
