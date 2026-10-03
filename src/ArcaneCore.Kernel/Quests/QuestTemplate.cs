namespace ArcaneCore.Kernel.Quests;

/// <summary>
/// One row of the world database's <c>quest_template</c> (vmangos quest_template, the columns
/// this server uses; column names match vmangos/cmangos so content imports map by name —
/// ROADMAP § Content). Immutable once loaded; the Game layer derives a runtime quest from it.
/// </summary>
/// <remarks>
/// Not carried (no consumer yet):
/// StartScript/CompleteScript, Required*Script. See docs/areas/quests-npc.md.
/// </remarks>
public sealed class QuestTemplate
{
    /// <summary>quest_template.entry.</summary>
    public uint Entry { get; init; }

    /// <summary>quest_template.Method.</summary>
    public byte Method { get; init; }

    /// <summary>quest_template.ZoneOrSort.</summary>
    public int ZoneOrSort { get; init; }

    /// <summary>quest_template.MinLevel.</summary>
    public byte MinLevel { get; init; }

    /// <summary>quest_template.MaxLevel.</summary>
    public byte MaxLevel { get; init; }

    /// <summary>quest_template.QuestLevel.</summary>
    public int QuestLevel { get; init; }

    /// <summary>quest_template.Type.</summary>
    public uint Type { get; init; }

    /// <summary>quest_template.RequiredClasses.</summary>
    public uint RequiredClasses { get; init; }

    /// <summary>quest_template.RequiredRaces.</summary>
    public uint RequiredRaces { get; init; }

    /// <summary>quest_template.RequiredSkill.</summary>
    public uint RequiredSkill { get; init; }

    /// <summary>quest_template.RequiredSkillValue.</summary>
    public uint RequiredSkillValue { get; init; }

    /// <summary>quest_template.RepObjectiveFaction.</summary>
    public uint RepObjectiveFaction { get; init; }

    /// <summary>quest_template.RepObjectiveValue.</summary>
    public int RepObjectiveValue { get; init; }

    /// <summary>quest_template.RequiredMinRepFaction.</summary>
    public uint RequiredMinRepFaction { get; init; }

    /// <summary>quest_template.RequiredMinRepValue.</summary>
    public int RequiredMinRepValue { get; init; }

    /// <summary>quest_template.RequiredMaxRepFaction.</summary>
    public uint RequiredMaxRepFaction { get; init; }

    /// <summary>quest_template.RequiredMaxRepValue.</summary>
    public int RequiredMaxRepValue { get; init; }

    /// <summary>quest_template.SuggestedPlayers.</summary>
    public byte SuggestedPlayers { get; init; }

    /// <summary>quest_template.LimitTime.</summary>
    public uint LimitTime { get; init; }

    /// <summary>quest_template.QuestFlags.</summary>
    public uint QuestFlags { get; init; }

    /// <summary>quest_template.SpecialFlags.</summary>
    public byte SpecialFlags { get; init; }

    /// <summary>quest_template.PrevQuestId.</summary>
    public int PrevQuestId { get; init; }

    /// <summary>quest_template.NextQuestId.</summary>
    public int NextQuestId { get; init; }

    /// <summary>quest_template.ExclusiveGroup.</summary>
    public int ExclusiveGroup { get; init; }

    /// <summary>quest_template.BreadcrumbForQuestId.</summary>
    public uint BreadcrumbForQuestId { get; init; }

    /// <summary>quest_template.NextQuestInChain.</summary>
    public uint NextQuestInChain { get; init; }

    /// <summary>quest_template.SrcItemId.</summary>
    public uint SrcItemId { get; init; }

    /// <summary>quest_template.SrcItemCount.</summary>
    public byte SrcItemCount { get; init; }

    /// <summary>quest_template.SrcSpell.</summary>
    public uint SrcSpell { get; init; }

    /// <summary>quest_template.Title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>quest_template.Details.</summary>
    public string Details { get; init; } = string.Empty;

    /// <summary>quest_template.Objectives.</summary>
    public string Objectives { get; init; } = string.Empty;

    /// <summary>quest_template.OfferRewardText.</summary>
    public string OfferRewardText { get; init; } = string.Empty;

    /// <summary>quest_template.RequestItemsText.</summary>
    public string RequestItemsText { get; init; } = string.Empty;

    /// <summary>quest_template.EndText.</summary>
    public string EndText { get; init; } = string.Empty;

    /// <summary>quest_template.ObjectiveText1.</summary>
    public string ObjectiveText1 { get; init; } = string.Empty;

    /// <summary>quest_template.ObjectiveText2.</summary>
    public string ObjectiveText2 { get; init; } = string.Empty;

    /// <summary>quest_template.ObjectiveText3.</summary>
    public string ObjectiveText3 { get; init; } = string.Empty;

    /// <summary>quest_template.ObjectiveText4.</summary>
    public string ObjectiveText4 { get; init; } = string.Empty;

    /// <summary>quest_template.ReqItemId1.</summary>
    public uint ReqItemId1 { get; init; }

    /// <summary>quest_template.ReqItemId2.</summary>
    public uint ReqItemId2 { get; init; }

    /// <summary>quest_template.ReqItemId3.</summary>
    public uint ReqItemId3 { get; init; }

    /// <summary>quest_template.ReqItemId4.</summary>
    public uint ReqItemId4 { get; init; }

    /// <summary>quest_template.ReqItemCount1.</summary>
    public uint ReqItemCount1 { get; init; }

    /// <summary>quest_template.ReqItemCount2.</summary>
    public uint ReqItemCount2 { get; init; }

    /// <summary>quest_template.ReqItemCount3.</summary>
    public uint ReqItemCount3 { get; init; }

    /// <summary>quest_template.ReqItemCount4.</summary>
    public uint ReqItemCount4 { get; init; }

    /// <summary>quest_template.ReqSourceId1.</summary>
    public uint ReqSourceId1 { get; init; }

    /// <summary>quest_template.ReqSourceId2.</summary>
    public uint ReqSourceId2 { get; init; }

    /// <summary>quest_template.ReqSourceId3.</summary>
    public uint ReqSourceId3 { get; init; }

    /// <summary>quest_template.ReqSourceId4.</summary>
    public uint ReqSourceId4 { get; init; }

    /// <summary>quest_template.ReqSourceCount1.</summary>
    public uint ReqSourceCount1 { get; init; }

    /// <summary>quest_template.ReqSourceCount2.</summary>
    public uint ReqSourceCount2 { get; init; }

    /// <summary>quest_template.ReqSourceCount3.</summary>
    public uint ReqSourceCount3 { get; init; }

    /// <summary>quest_template.ReqSourceCount4.</summary>
    public uint ReqSourceCount4 { get; init; }

    /// <summary>quest_template.ReqCreatureOrGOId1.</summary>
    public int ReqCreatureOrGOId1 { get; init; }

    /// <summary>quest_template.ReqCreatureOrGOId2.</summary>
    public int ReqCreatureOrGOId2 { get; init; }

    /// <summary>quest_template.ReqCreatureOrGOId3.</summary>
    public int ReqCreatureOrGOId3 { get; init; }

    /// <summary>quest_template.ReqCreatureOrGOId4.</summary>
    public int ReqCreatureOrGOId4 { get; init; }

    /// <summary>quest_template.ReqCreatureOrGOCount1.</summary>
    public uint ReqCreatureOrGOCount1 { get; init; }

    /// <summary>quest_template.ReqCreatureOrGOCount2.</summary>
    public uint ReqCreatureOrGOCount2 { get; init; }

    /// <summary>quest_template.ReqCreatureOrGOCount3.</summary>
    public uint ReqCreatureOrGOCount3 { get; init; }

    /// <summary>quest_template.ReqCreatureOrGOCount4.</summary>
    public uint ReqCreatureOrGOCount4 { get; init; }

    /// <summary>quest_template.ReqSpellCast1.</summary>
    public uint ReqSpellCast1 { get; init; }

    /// <summary>quest_template.ReqSpellCast2.</summary>
    public uint ReqSpellCast2 { get; init; }

    /// <summary>quest_template.ReqSpellCast3.</summary>
    public uint ReqSpellCast3 { get; init; }

    /// <summary>quest_template.ReqSpellCast4.</summary>
    public uint ReqSpellCast4 { get; init; }

    /// <summary>quest_template.RewChoiceItemId1.</summary>
    public uint RewChoiceItemId1 { get; init; }

    /// <summary>quest_template.RewChoiceItemId2.</summary>
    public uint RewChoiceItemId2 { get; init; }

    /// <summary>quest_template.RewChoiceItemId3.</summary>
    public uint RewChoiceItemId3 { get; init; }

    /// <summary>quest_template.RewChoiceItemId4.</summary>
    public uint RewChoiceItemId4 { get; init; }

    /// <summary>quest_template.RewChoiceItemId5.</summary>
    public uint RewChoiceItemId5 { get; init; }

    /// <summary>quest_template.RewChoiceItemId6.</summary>
    public uint RewChoiceItemId6 { get; init; }

    /// <summary>quest_template.RewChoiceItemCount1.</summary>
    public uint RewChoiceItemCount1 { get; init; }

    /// <summary>quest_template.RewChoiceItemCount2.</summary>
    public uint RewChoiceItemCount2 { get; init; }

    /// <summary>quest_template.RewChoiceItemCount3.</summary>
    public uint RewChoiceItemCount3 { get; init; }

    /// <summary>quest_template.RewChoiceItemCount4.</summary>
    public uint RewChoiceItemCount4 { get; init; }

    /// <summary>quest_template.RewChoiceItemCount5.</summary>
    public uint RewChoiceItemCount5 { get; init; }

    /// <summary>quest_template.RewChoiceItemCount6.</summary>
    public uint RewChoiceItemCount6 { get; init; }

    /// <summary>quest_template.RewItemId1.</summary>
    public uint RewItemId1 { get; init; }

    /// <summary>quest_template.RewItemId2.</summary>
    public uint RewItemId2 { get; init; }

    /// <summary>quest_template.RewItemId3.</summary>
    public uint RewItemId3 { get; init; }

    /// <summary>quest_template.RewItemId4.</summary>
    public uint RewItemId4 { get; init; }

    /// <summary>quest_template.RewItemCount1.</summary>
    public uint RewItemCount1 { get; init; }

    /// <summary>quest_template.RewItemCount2.</summary>
    public uint RewItemCount2 { get; init; }

    /// <summary>quest_template.RewItemCount3.</summary>
    public uint RewItemCount3 { get; init; }

    /// <summary>quest_template.RewItemCount4.</summary>
    public uint RewItemCount4 { get; init; }

    /// <summary>quest_template.RewXP.</summary>
    public uint RewXP { get; init; }

    /// <summary>quest_template.RewOrReqMoney.</summary>
    public int RewOrReqMoney { get; init; }

    /// <summary>quest_template.RewMoneyMaxLevel.</summary>
    public uint RewMoneyMaxLevel { get; init; }

    /// <summary>quest_template.RewSpell.</summary>
    public uint RewSpell { get; init; }

    /// <summary>quest_template.RewSpellCast.</summary>
    public uint RewSpellCast { get; init; }

    /// <summary>quest_template.RewRepFaction1 (Faction.dbc id; zero = no reward in this slot).</summary>
    public uint RewRepFaction1 { get; init; }

    /// <summary>quest_template.RewRepValue1 (signed reputation points before rate and level scaling).</summary>
    public int RewRepValue1 { get; init; }

    /// <summary>quest_template.RewRepFaction2 (Faction.dbc id; zero = no reward in this slot).</summary>
    public uint RewRepFaction2 { get; init; }

    /// <summary>quest_template.RewRepValue2 (signed reputation points before rate and level scaling).</summary>
    public int RewRepValue2 { get; init; }

    /// <summary>quest_template.RewRepFaction3 (Faction.dbc id; zero = no reward in this slot).</summary>
    public uint RewRepFaction3 { get; init; }

    /// <summary>quest_template.RewRepValue3 (signed reputation points before rate and level scaling).</summary>
    public int RewRepValue3 { get; init; }

    /// <summary>quest_template.RewRepFaction4 (Faction.dbc id; zero = no reward in this slot).</summary>
    public uint RewRepFaction4 { get; init; }

    /// <summary>quest_template.RewRepValue4 (signed reputation points before rate and level scaling).</summary>
    public int RewRepValue4 { get; init; }

    /// <summary>quest_template.RewRepFaction5 (Faction.dbc id; zero = no reward in this slot).</summary>
    public uint RewRepFaction5 { get; init; }

    /// <summary>quest_template.RewRepValue5 (signed reputation points before rate and level scaling).</summary>
    public int RewRepValue5 { get; init; }

    /// <summary>quest_template.PointMapId.</summary>
    public uint PointMapId { get; init; }

    /// <summary>quest_template.PointX.</summary>
    public float PointX { get; init; }

    /// <summary>quest_template.PointY.</summary>
    public float PointY { get; init; }

    /// <summary>quest_template.PointOpt.</summary>
    public uint PointOpt { get; init; }

    /// <summary>quest_template.DetailsEmote1.</summary>
    public uint DetailsEmote1 { get; init; }

    /// <summary>quest_template.DetailsEmote2.</summary>
    public uint DetailsEmote2 { get; init; }

    /// <summary>quest_template.DetailsEmote3.</summary>
    public uint DetailsEmote3 { get; init; }

    /// <summary>quest_template.DetailsEmote4.</summary>
    public uint DetailsEmote4 { get; init; }

    /// <summary>quest_template.DetailsEmoteDelay1.</summary>
    public uint DetailsEmoteDelay1 { get; init; }

    /// <summary>quest_template.DetailsEmoteDelay2.</summary>
    public uint DetailsEmoteDelay2 { get; init; }

    /// <summary>quest_template.DetailsEmoteDelay3.</summary>
    public uint DetailsEmoteDelay3 { get; init; }

    /// <summary>quest_template.DetailsEmoteDelay4.</summary>
    public uint DetailsEmoteDelay4 { get; init; }

    /// <summary>quest_template.IncompleteEmote.</summary>
    public uint IncompleteEmote { get; init; }

    /// <summary>quest_template.CompleteEmote.</summary>
    public uint CompleteEmote { get; init; }

    /// <summary>quest_template.OfferRewardEmote1.</summary>
    public uint OfferRewardEmote1 { get; init; }

    /// <summary>quest_template.OfferRewardEmote2.</summary>
    public uint OfferRewardEmote2 { get; init; }

    /// <summary>quest_template.OfferRewardEmote3.</summary>
    public uint OfferRewardEmote3 { get; init; }

    /// <summary>quest_template.OfferRewardEmote4.</summary>
    public uint OfferRewardEmote4 { get; init; }

    /// <summary>quest_template.OfferRewardEmoteDelay1.</summary>
    public uint OfferRewardEmoteDelay1 { get; init; }

    /// <summary>quest_template.OfferRewardEmoteDelay2.</summary>
    public uint OfferRewardEmoteDelay2 { get; init; }

    /// <summary>quest_template.OfferRewardEmoteDelay3.</summary>
    public uint OfferRewardEmoteDelay3 { get; init; }

    /// <summary>quest_template.OfferRewardEmoteDelay4.</summary>
    public uint OfferRewardEmoteDelay4 { get; init; }

    /// <summary>quest_template.RequiredCondition.</summary>
    public uint RequiredCondition { get; init; }

    /// <summary>quest_template.RewMailTemplateId: the mail template mailed on turn-in; negative = sent by the quest giver instead of the ender (vmangos Player.cpp:13147-13163, ObjectMgr.cpp:6107-6123 uses abs); 0 = none.</summary>
    public int RewMailTemplateId { get; init; }

    /// <summary>quest_template.RewMailDelaySecs: seconds the reward mail is delayed (vmangos Player.cpp:13145-13163).</summary>
    public uint RewMailDelaySecs { get; init; }
}
