namespace WowWorldMessages.Vanilla;
public partial class UpdateMask {
    public void SetObjectGuid(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 0, value);
    }

    public void SetObjectType(uint value) {
        UpdateMaskUtils.AddUInt(_values, 2, value);
    }

    public void SetObjectEntry(uint value) {
        UpdateMaskUtils.AddUInt(_values, 3, value);
    }

    public void SetObjectScaleX(float value) {
        UpdateMaskUtils.AddFloat(_values, 4, value);
    }

    public void SetItemOwner(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 6, value);
    }

    public void SetItemContained(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 8, value);
    }

    public void SetItemCreator(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 10, value);
    }

    public void SetItemGiftcreator(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 12, value);
    }

    public void SetItemStackCount(uint value) {
        UpdateMaskUtils.AddUInt(_values, 14, value);
    }

    public void SetItemDuration(uint value) {
        UpdateMaskUtils.AddUInt(_values, 15, value);
    }

    public void SetItemSpellCharges(uint value) {
        UpdateMaskUtils.AddUInt(_values, 16, value);
    }

    public void SetItemFlags(uint value) {
        UpdateMaskUtils.AddUInt(_values, 21, value);
    }

    public void SetItemEnchantment(uint value) {
        UpdateMaskUtils.AddUInt(_values, 22, value);
    }

    public void SetItemPropertySeed(uint value) {
        UpdateMaskUtils.AddUInt(_values, 43, value);
    }

    public void SetItemRandomPropertiesId(uint value) {
        UpdateMaskUtils.AddUInt(_values, 44, value);
    }

    public void SetItemItemTextId(uint value) {
        UpdateMaskUtils.AddUInt(_values, 45, value);
    }

    public void SetItemDurability(uint value) {
        UpdateMaskUtils.AddUInt(_values, 46, value);
    }

    public void SetItemMaxdurability(uint value) {
        UpdateMaskUtils.AddUInt(_values, 47, value);
    }

    public void SetContainerNumSlots(uint value) {
        UpdateMaskUtils.AddUInt(_values, 48, value);
    }

    public void SetContainerSlot1(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 50, value);
    }

    public void SetUnitCharm(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 6, value);
    }

    public void SetUnitSummon(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 8, value);
    }

    public void SetUnitCharmedby(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 10, value);
    }

    public void SetUnitSummonedby(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 12, value);
    }

    public void SetUnitCreatedby(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 14, value);
    }

    public void SetUnitTarget(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 16, value);
    }

    public void SetUnitPersuaded(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 18, value);
    }

    public void SetUnitChannelObject(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 20, value);
    }

    public void SetUnitHealth(uint value) {
        UpdateMaskUtils.AddUInt(_values, 22, value);
    }

    public void SetUnitPower1(uint value) {
        UpdateMaskUtils.AddUInt(_values, 23, value);
    }

    public void SetUnitPower2(uint value) {
        UpdateMaskUtils.AddUInt(_values, 24, value);
    }

    public void SetUnitPower3(uint value) {
        UpdateMaskUtils.AddUInt(_values, 25, value);
    }

    public void SetUnitPower4(uint value) {
        UpdateMaskUtils.AddUInt(_values, 26, value);
    }

    public void SetUnitPower5(uint value) {
        UpdateMaskUtils.AddUInt(_values, 27, value);
    }

    public void SetUnitMaxhealth(uint value) {
        UpdateMaskUtils.AddUInt(_values, 28, value);
    }

    public void SetUnitMaxpower1(uint value) {
        UpdateMaskUtils.AddUInt(_values, 29, value);
    }

    public void SetUnitMaxpower2(uint value) {
        UpdateMaskUtils.AddUInt(_values, 30, value);
    }

    public void SetUnitMaxpower3(uint value) {
        UpdateMaskUtils.AddUInt(_values, 31, value);
    }

    public void SetUnitMaxpower4(uint value) {
        UpdateMaskUtils.AddUInt(_values, 32, value);
    }

    public void SetUnitMaxpower5(uint value) {
        UpdateMaskUtils.AddUInt(_values, 33, value);
    }

    public void SetUnitLevel(uint value) {
        UpdateMaskUtils.AddUInt(_values, 34, value);
    }

    public void SetUnitFactiontemplate(uint value) {
        UpdateMaskUtils.AddUInt(_values, 35, value);
    }

    public void SetUnitBytes0(Race race, Class classType, Gender gender, Power power) {
        UpdateMaskUtils.AddBytes(_values, 36, (byte)race, (byte)classType, (byte)gender, (byte)power);
    }

    public void SetUnitVirtualItemSlotDisplay(uint value) {
        UpdateMaskUtils.AddUInt(_values, 37, value);
    }

    public void SetUnitVirtualItemInfo(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 40, a, b, c, d);
    }

    public void SetUnitFlags(uint value) {
        UpdateMaskUtils.AddUInt(_values, 46, value);
    }

    public void SetUnitAura(uint value) {
        UpdateMaskUtils.AddUInt(_values, 47, value);
    }

    public void SetUnitAuraflags(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 95, a, b, c, d);
    }

    public void SetUnitAuralevels(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 101, a, b, c, d);
    }

    public void SetUnitAuraapplications(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 113, a, b, c, d);
    }

    public void SetUnitAurastate(uint value) {
        UpdateMaskUtils.AddUInt(_values, 125, value);
    }

    public void SetUnitBaseattacktime(uint value) {
        UpdateMaskUtils.AddUInt(_values, 126, value);
    }

    public void SetUnitRangedattacktime(uint value) {
        UpdateMaskUtils.AddUInt(_values, 128, value);
    }

    public void SetUnitBoundingradius(float value) {
        UpdateMaskUtils.AddFloat(_values, 129, value);
    }

    public void SetUnitCombatreach(float value) {
        UpdateMaskUtils.AddFloat(_values, 130, value);
    }

    public void SetUnitDisplayid(uint value) {
        UpdateMaskUtils.AddUInt(_values, 131, value);
    }

    public void SetUnitNativedisplayid(uint value) {
        UpdateMaskUtils.AddUInt(_values, 132, value);
    }

    public void SetUnitMountdisplayid(uint value) {
        UpdateMaskUtils.AddUInt(_values, 133, value);
    }

    public void SetUnitMindamage(float value) {
        UpdateMaskUtils.AddFloat(_values, 134, value);
    }

    public void SetUnitMaxdamage(float value) {
        UpdateMaskUtils.AddFloat(_values, 135, value);
    }

    public void SetUnitMinoffhanddamage(float value) {
        UpdateMaskUtils.AddFloat(_values, 136, value);
    }

    public void SetUnitMaxoffhanddamage(float value) {
        UpdateMaskUtils.AddFloat(_values, 137, value);
    }

    public void SetUnitBytes1(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 138, a, b, c, d);
    }

    public void SetUnitPetnumber(uint value) {
        UpdateMaskUtils.AddUInt(_values, 139, value);
    }

    public void SetUnitPetNameTimestamp(uint value) {
        UpdateMaskUtils.AddUInt(_values, 140, value);
    }

    public void SetUnitPetexperience(uint value) {
        UpdateMaskUtils.AddUInt(_values, 141, value);
    }

    public void SetUnitPetnextlevelexp(uint value) {
        UpdateMaskUtils.AddUInt(_values, 142, value);
    }

    public void SetUnitDynamicFlags(uint value) {
        UpdateMaskUtils.AddUInt(_values, 143, value);
    }

    public void SetUnitChannelSpell(uint value) {
        UpdateMaskUtils.AddUInt(_values, 144, value);
    }

    public void SetUnitModCastSpeed(float value) {
        UpdateMaskUtils.AddFloat(_values, 145, value);
    }

    public void SetUnitCreatedBySpell(uint value) {
        UpdateMaskUtils.AddUInt(_values, 146, value);
    }

    public void SetUnitNpcFlags(uint value) {
        UpdateMaskUtils.AddUInt(_values, 147, value);
    }

    public void SetUnitNpcEmotestate(uint value) {
        UpdateMaskUtils.AddUInt(_values, 148, value);
    }

    public void SetUnitTrainingPoints(ushort a, ushort b) {
        UpdateMaskUtils.AddTwoShort(_values, 149, a, b);
    }

    public void SetUnitStrength(uint value) {
        UpdateMaskUtils.AddUInt(_values, 150, value);
    }

    public void SetUnitAgility(uint value) {
        UpdateMaskUtils.AddUInt(_values, 151, value);
    }

    public void SetUnitStamina(uint value) {
        UpdateMaskUtils.AddUInt(_values, 152, value);
    }

    public void SetUnitIntellect(uint value) {
        UpdateMaskUtils.AddUInt(_values, 153, value);
    }

    public void SetUnitSpirit(uint value) {
        UpdateMaskUtils.AddUInt(_values, 154, value);
    }

    public void SetUnitNormalResistance(uint value) {
        UpdateMaskUtils.AddUInt(_values, 155, value);
    }

    public void SetUnitHolyResistance(uint value) {
        UpdateMaskUtils.AddUInt(_values, 156, value);
    }

    public void SetUnitFireResistance(uint value) {
        UpdateMaskUtils.AddUInt(_values, 157, value);
    }

    public void SetUnitNatureResistance(uint value) {
        UpdateMaskUtils.AddUInt(_values, 158, value);
    }

    public void SetUnitFrostResistance(uint value) {
        UpdateMaskUtils.AddUInt(_values, 159, value);
    }

    public void SetUnitShadowResistance(uint value) {
        UpdateMaskUtils.AddUInt(_values, 160, value);
    }

    public void SetUnitArcaneResistance(uint value) {
        UpdateMaskUtils.AddUInt(_values, 161, value);
    }

    public void SetUnitBaseMana(uint value) {
        UpdateMaskUtils.AddUInt(_values, 162, value);
    }

    public void SetUnitBaseHealth(uint value) {
        UpdateMaskUtils.AddUInt(_values, 163, value);
    }

    public void SetUnitBytes2(byte facialHair, byte unknown, byte bankBagSlots, byte restedState) {
        UpdateMaskUtils.AddBytes(_values, 164, facialHair, unknown, bankBagSlots, restedState);
    }

    public void SetUnitAttackPower(uint value) {
        UpdateMaskUtils.AddUInt(_values, 165, value);
    }

    public void SetUnitAttackPowerMods(ushort a, ushort b) {
        UpdateMaskUtils.AddTwoShort(_values, 166, a, b);
    }

    public void SetUnitAttackPowerMultiplier(float value) {
        UpdateMaskUtils.AddFloat(_values, 167, value);
    }

    public void SetUnitRangedAttackPower(uint value) {
        UpdateMaskUtils.AddUInt(_values, 168, value);
    }

    public void SetUnitRangedAttackPowerMods(ushort a, ushort b) {
        UpdateMaskUtils.AddTwoShort(_values, 169, a, b);
    }

    public void SetUnitRangedAttackPowerMultiplier(float value) {
        UpdateMaskUtils.AddFloat(_values, 170, value);
    }

    public void SetUnitMinrangeddamage(float value) {
        UpdateMaskUtils.AddFloat(_values, 171, value);
    }

    public void SetUnitMaxrangeddamage(float value) {
        UpdateMaskUtils.AddFloat(_values, 172, value);
    }

    public void SetUnitPowerCostModifier(uint value) {
        UpdateMaskUtils.AddUInt(_values, 173, value);
    }

    public void SetUnitPowerCostMultiplier(float value) {
        UpdateMaskUtils.AddFloat(_values, 180, value);
    }

    public void SetPlayerDuelArbiter(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 188, value);
    }

    public void SetPlayerFlags(uint value) {
        UpdateMaskUtils.AddUInt(_values, 190, value);
    }

    public void SetPlayerGuildid(uint value) {
        UpdateMaskUtils.AddUInt(_values, 191, value);
    }

    public void SetPlayerGuildrank(uint value) {
        UpdateMaskUtils.AddUInt(_values, 192, value);
    }

    public void SetPlayerFeatures(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 193, a, b, c, d);
    }

    public void SetPlayerBytes2(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 194, a, b, c, d);
    }

    public void SetPlayerBytes3(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 195, a, b, c, d);
    }

    public void SetPlayerDuelTeam(uint value) {
        UpdateMaskUtils.AddUInt(_values, 196, value);
    }

    public void SetPlayerGuildTimestamp(uint value) {
        UpdateMaskUtils.AddUInt(_values, 197, value);
    }

    public void SetPlayerQuestLog11(uint value) {
        UpdateMaskUtils.AddUInt(_values, 198, value);
    }

    public void SetPlayerQuestLog12(uint value) {
        UpdateMaskUtils.AddUInt(_values, 199, value);
    }

    public void SetPlayerQuestLog21(uint value) {
        UpdateMaskUtils.AddUInt(_values, 201, value);
    }

    public void SetPlayerQuestLog22(uint value) {
        UpdateMaskUtils.AddUInt(_values, 202, value);
    }

    public void SetPlayerQuestLog31(uint value) {
        UpdateMaskUtils.AddUInt(_values, 204, value);
    }

    public void SetPlayerQuestLog32(uint value) {
        UpdateMaskUtils.AddUInt(_values, 205, value);
    }

    public void SetPlayerQuestLog41(uint value) {
        UpdateMaskUtils.AddUInt(_values, 207, value);
    }

    public void SetPlayerQuestLog42(uint value) {
        UpdateMaskUtils.AddUInt(_values, 208, value);
    }

    public void SetPlayerQuestLog51(uint value) {
        UpdateMaskUtils.AddUInt(_values, 210, value);
    }

    public void SetPlayerQuestLog52(uint value) {
        UpdateMaskUtils.AddUInt(_values, 211, value);
    }

    public void SetPlayerQuestLog61(uint value) {
        UpdateMaskUtils.AddUInt(_values, 213, value);
    }

    public void SetPlayerQuestLog62(uint value) {
        UpdateMaskUtils.AddUInt(_values, 214, value);
    }

    public void SetPlayerQuestLog71(uint value) {
        UpdateMaskUtils.AddUInt(_values, 216, value);
    }

    public void SetPlayerQuestLog72(uint value) {
        UpdateMaskUtils.AddUInt(_values, 217, value);
    }

    public void SetPlayerQuestLog81(uint value) {
        UpdateMaskUtils.AddUInt(_values, 219, value);
    }

    public void SetPlayerQuestLog82(uint value) {
        UpdateMaskUtils.AddUInt(_values, 220, value);
    }

    public void SetPlayerQuestLog91(uint value) {
        UpdateMaskUtils.AddUInt(_values, 222, value);
    }

    public void SetPlayerQuestLog92(uint value) {
        UpdateMaskUtils.AddUInt(_values, 223, value);
    }

    public void SetPlayerQuestLog101(uint value) {
        UpdateMaskUtils.AddUInt(_values, 225, value);
    }

    public void SetPlayerQuestLog102(uint value) {
        UpdateMaskUtils.AddUInt(_values, 226, value);
    }

    public void SetPlayerQuestLog111(uint value) {
        UpdateMaskUtils.AddUInt(_values, 228, value);
    }

    public void SetPlayerQuestLog112(uint value) {
        UpdateMaskUtils.AddUInt(_values, 229, value);
    }

    public void SetPlayerQuestLog121(uint value) {
        UpdateMaskUtils.AddUInt(_values, 231, value);
    }

    public void SetPlayerQuestLog122(uint value) {
        UpdateMaskUtils.AddUInt(_values, 232, value);
    }

    public void SetPlayerQuestLog131(uint value) {
        UpdateMaskUtils.AddUInt(_values, 234, value);
    }

    public void SetPlayerQuestLog132(uint value) {
        UpdateMaskUtils.AddUInt(_values, 235, value);
    }

    public void SetPlayerQuestLog141(uint value) {
        UpdateMaskUtils.AddUInt(_values, 237, value);
    }

    public void SetPlayerQuestLog142(uint value) {
        UpdateMaskUtils.AddUInt(_values, 238, value);
    }

    public void SetPlayerQuestLog151(uint value) {
        UpdateMaskUtils.AddUInt(_values, 240, value);
    }

    public void SetPlayerQuestLog152(uint value) {
        UpdateMaskUtils.AddUInt(_values, 241, value);
    }

    public void SetPlayerQuestLog161(uint value) {
        UpdateMaskUtils.AddUInt(_values, 243, value);
    }

    public void SetPlayerQuestLog162(uint value) {
        UpdateMaskUtils.AddUInt(_values, 244, value);
    }

    public void SetPlayerQuestLog171(uint value) {
        UpdateMaskUtils.AddUInt(_values, 246, value);
    }

    public void SetPlayerQuestLog172(uint value) {
        UpdateMaskUtils.AddUInt(_values, 247, value);
    }

    public void SetPlayerQuestLog181(uint value) {
        UpdateMaskUtils.AddUInt(_values, 249, value);
    }

    public void SetPlayerQuestLog182(uint value) {
        UpdateMaskUtils.AddUInt(_values, 250, value);
    }

    public void SetPlayerQuestLog191(uint value) {
        UpdateMaskUtils.AddUInt(_values, 252, value);
    }

    public void SetPlayerQuestLog192(uint value) {
        UpdateMaskUtils.AddUInt(_values, 253, value);
    }

    public void SetPlayerQuestLog201(uint value) {
        UpdateMaskUtils.AddUInt(_values, 255, value);
    }

    public void SetPlayerQuestLog202(uint value) {
        UpdateMaskUtils.AddUInt(_values, 256, value);
    }

    public void SetPlayerFarsight(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 712, value);
    }

    public void SetPlayerFieldComboTarget(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 714, value);
    }

    public void SetPlayerXp(uint value) {
        UpdateMaskUtils.AddUInt(_values, 716, value);
    }

    public void SetPlayerNextLevelXp(uint value) {
        UpdateMaskUtils.AddUInt(_values, 717, value);
    }

    public void SetPlayerCharacterPoints1(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1102, value);
    }

    public void SetPlayerCharacterPoints2(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1103, value);
    }

    public void SetPlayerTrackCreatures(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1104, value);
    }

    public void SetPlayerTrackResources(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1105, value);
    }

    public void SetPlayerBlockPercentage(float value) {
        UpdateMaskUtils.AddFloat(_values, 1106, value);
    }

    public void SetPlayerDodgePercentage(float value) {
        UpdateMaskUtils.AddFloat(_values, 1107, value);
    }

    public void SetPlayerParryPercentage(float value) {
        UpdateMaskUtils.AddFloat(_values, 1108, value);
    }

    public void SetPlayerCritPercentage(float value) {
        UpdateMaskUtils.AddFloat(_values, 1109, value);
    }

    public void SetPlayerRangedCritPercentage(float value) {
        UpdateMaskUtils.AddFloat(_values, 1110, value);
    }

    public void SetPlayerExploredZones1(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 1111, a, b, c, d);
    }

    public void SetPlayerRestStateExperience(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1175, value);
    }

    public void SetPlayerFieldCoinage(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1176, value);
    }

    public void SetPlayerFieldPosstat0(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1177, value);
    }

    public void SetPlayerFieldPosstat1(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1178, value);
    }

    public void SetPlayerFieldPosstat2(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1179, value);
    }

    public void SetPlayerFieldPosstat3(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1180, value);
    }

    public void SetPlayerFieldPosstat4(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1181, value);
    }

    public void SetPlayerFieldNegstat0(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1182, value);
    }

    public void SetPlayerFieldNegstat1(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1183, value);
    }

    public void SetPlayerFieldNegstat2(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1184, value);
    }

    public void SetPlayerFieldNegstat3(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1185, value);
    }

    public void SetPlayerFieldNegstat4(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1186, value);
    }

    public void SetPlayerFieldResistancebuffmodspositive(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1187, value);
    }

    public void SetPlayerFieldResistancebuffmodsnegative(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1194, value);
    }

    public void SetPlayerFieldModDamageDonePos(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1201, value);
    }

    public void SetPlayerFieldModDamageDoneNeg(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1208, value);
    }

    public void SetPlayerFieldModDamageDonePct(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1215, value);
    }

    public void SetPlayerFieldBytes(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 1222, a, b, c, d);
    }

    public void SetPlayerAmmoId(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1223, value);
    }

    public void SetPlayerSelfResSpell(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1224, value);
    }

    public void SetPlayerFieldPvpMedals(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1225, value);
    }

    public void SetPlayerFieldBuybackPrice1(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1226, value);
    }

    public void SetPlayerFieldBuybackTimestamp1(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1238, value);
    }

    public void SetPlayerFieldSessionKills(ushort a, ushort b) {
        UpdateMaskUtils.AddTwoShort(_values, 1250, a, b);
    }

    public void SetPlayerFieldYesterdayKills(ushort a, ushort b) {
        UpdateMaskUtils.AddTwoShort(_values, 1251, a, b);
    }

    public void SetPlayerFieldLastWeekKills(ushort a, ushort b) {
        UpdateMaskUtils.AddTwoShort(_values, 1252, a, b);
    }

    public void SetPlayerFieldThisWeekKills(ushort a, ushort b) {
        UpdateMaskUtils.AddTwoShort(_values, 1253, a, b);
    }

    public void SetPlayerFieldThisWeekContribution(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1254, value);
    }

    public void SetPlayerFieldLifetimeHonorbaleKills(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1255, value);
    }

    public void SetPlayerFieldLifetimeDishonorbaleKills(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1256, value);
    }

    public void SetPlayerFieldYesterdayContribution(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1257, value);
    }

    public void SetPlayerFieldLastWeekContribution(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1258, value);
    }

    public void SetPlayerFieldLastWeekRank(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1259, value);
    }

    public void SetPlayerFieldBytes2(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 1260, a, b, c, d);
    }

    public void SetPlayerFieldWatchedFactionIndex(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1261, value);
    }

    public void SetPlayerFieldCombatRating1(uint value) {
        UpdateMaskUtils.AddUInt(_values, 1262, value);
    }

    public void SetGameObjectCreatedBy(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 6, value);
    }

    public void SetGameObjectDisplayid(uint value) {
        UpdateMaskUtils.AddUInt(_values, 8, value);
    }

    public void SetGameObjectFlags(uint value) {
        UpdateMaskUtils.AddUInt(_values, 9, value);
    }

    public void SetGameObjectRotation(float value) {
        UpdateMaskUtils.AddFloat(_values, 10, value);
    }

    public void SetGameObjectState(uint value) {
        UpdateMaskUtils.AddUInt(_values, 14, value);
    }

    public void SetGameObjectPosX(float value) {
        UpdateMaskUtils.AddFloat(_values, 15, value);
    }

    public void SetGameObjectPosY(float value) {
        UpdateMaskUtils.AddFloat(_values, 16, value);
    }

    public void SetGameObjectPosZ(float value) {
        UpdateMaskUtils.AddFloat(_values, 17, value);
    }

    public void SetGameObjectFacing(float value) {
        UpdateMaskUtils.AddFloat(_values, 18, value);
    }

    public void SetGameObjectDynFlags(uint value) {
        UpdateMaskUtils.AddUInt(_values, 19, value);
    }

    public void SetGameObjectFaction(uint value) {
        UpdateMaskUtils.AddUInt(_values, 20, value);
    }

    public void SetGameObjectTypeId(uint value) {
        UpdateMaskUtils.AddUInt(_values, 21, value);
    }

    public void SetGameObjectLevel(uint value) {
        UpdateMaskUtils.AddUInt(_values, 22, value);
    }

    public void SetGameObjectArtkit(uint value) {
        UpdateMaskUtils.AddUInt(_values, 23, value);
    }

    public void SetGameObjectAnimprogress(uint value) {
        UpdateMaskUtils.AddUInt(_values, 24, value);
    }

    public void SetDynamicObjectCaster(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 6, value);
    }

    public void SetDynamicObjectBytes(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 8, a, b, c, d);
    }

    public void SetDynamicObjectSpellid(uint value) {
        UpdateMaskUtils.AddUInt(_values, 9, value);
    }

    public void SetDynamicObjectRadius(float value) {
        UpdateMaskUtils.AddFloat(_values, 10, value);
    }

    public void SetDynamicObjectPosX(float value) {
        UpdateMaskUtils.AddFloat(_values, 11, value);
    }

    public void SetDynamicObjectPosY(float value) {
        UpdateMaskUtils.AddFloat(_values, 12, value);
    }

    public void SetDynamicObjectPosZ(float value) {
        UpdateMaskUtils.AddFloat(_values, 13, value);
    }

    public void SetDynamicObjectFacing(float value) {
        UpdateMaskUtils.AddFloat(_values, 14, value);
    }

    public void SetCorpseOwner(ulong value) {
        UpdateMaskUtils.AddGuid(_values, 6, value);
    }

    public void SetCorpseFacing(float value) {
        UpdateMaskUtils.AddFloat(_values, 8, value);
    }

    public void SetCorpsePosX(float value) {
        UpdateMaskUtils.AddFloat(_values, 9, value);
    }

    public void SetCorpsePosY(float value) {
        UpdateMaskUtils.AddFloat(_values, 10, value);
    }

    public void SetCorpsePosZ(float value) {
        UpdateMaskUtils.AddFloat(_values, 11, value);
    }

    public void SetCorpseDisplayId(uint value) {
        UpdateMaskUtils.AddUInt(_values, 12, value);
    }

    public void SetCorpseItem(uint value) {
        UpdateMaskUtils.AddUInt(_values, 13, value);
    }

    public void SetCorpseBytes1(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 32, a, b, c, d);
    }

    public void SetCorpseBytes2(byte a, byte b, byte c, byte d) {
        UpdateMaskUtils.AddBytes(_values, 33, a, b, c, d);
    }

    public void SetCorpseGuild(uint value) {
        UpdateMaskUtils.AddUInt(_values, 34, value);
    }

    public void SetCorpseFlags(uint value) {
        UpdateMaskUtils.AddUInt(_values, 35, value);
    }

    public void SetCorpseDynamicFlags(uint value) {
        UpdateMaskUtils.AddUInt(_values, 36, value);
    }

}
