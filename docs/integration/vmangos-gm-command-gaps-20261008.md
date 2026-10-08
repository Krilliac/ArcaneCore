# vmangos GM command paths absent from ArcaneCore

Exact path comparison of `D:/refs/vmangos/src/game/Chat/Chat.cpp`'s `ChatHandler::getCommandTable` against `docs/reference/gm-commands.md` on this worktree. Parent groups are included; an equivalent operation under another path is still listed. vmangos bot, debug, console and later-era extensions are included because they are in its table. This is a name inventory, not a claim that all commands suit a 1.12.1 public realm.

vmangos named paths: 816; current ArcaneCore paths: 200; exact paths absent: 654.

| Missing path | vmangos table line |
|---|---:|
| `.account` | 1187 |
| `.account characters` | 147 |
| `.account cleardata` | 148 |
| `.account create` | 149 |
| `.account delete` | 150 |
| `.account onlinelist` | 151 |
| `.account lock` | 152 |
| `.account set` | 153 |
| `.account set addon` | 138 |
| `.account set gmlevel` | 139 |
| `.account set password` | 140 |
| `.account set locked` | 141 |
| `.account password` | 154 |
| `.auction` | 1188 |
| `.auction alliance` | 161 |
| `.auction goblin` | 162 |
| `.auction horde` | 163 |
| `.cast back` | 197 |
| `.cast dist` | 198 |
| `.cast self` | 199 |
| `.cast target` | 200 |
| `.character aiinfo` | 240 |
| `.character deleted` | 241 |
| `.character deleted delete` | 214 |
| `.character deleted list` | 215 |
| `.character deleted list account` | 207 |
| `.character deleted list name` | 208 |
| `.character deleted restore` | 216 |
| `.character deleted old` | 217 |
| `.character erase` | 242 |
| `.character level` | 243 |
| `.character hasitem` | 246 |
| `.character race` | 247 |
| `.character skin` | 248 |
| `.character fillflys` | 249 |
| `.character premade` | 250 |
| `.character premade gear` | 231 |
| `.character premade spec` | 232 |
| `.character premade savegear` | 233 |
| `.character premade savespec` | 234 |
| `.character clean` | 251 |
| `.character clean todelete` | 224 |
| `.character clean items` | 225 |
| `.character citytitle` | 252 |
| `.charge` | 1191 |
| `.cheat` | 1192 |
| `.cheat fly` | 258 |
| `.cheat fixedz` | 259 |
| `.cheat beastmaster` | 260 |
| `.cheat god` | 261 |
| `.cheat cooldown` | 262 |
| `.cheat casttime` | 263 |
| `.cheat powercost` | 264 |
| `.cheat debuffs` | 265 |
| `.cheat criticals` | 266 |
| `.cheat castchecks` | 267 |
| `.cheat procs` | 268 |
| `.cheat triggerpass` | 269 |
| `.cheat ignoretriggers` | 270 |
| `.cheat immunepc` | 271 |
| `.cheat immunenpc` | 272 |
| `.cheat untargetable` | 273 |
| `.cheat waterwalk` | 274 |
| `.cheat wallclimb` | 275 |
| `.cheat debugtargetinfo` | 276 |
| `.cheat status` | 277 |
| `.debug` | 1193 |
| `.debug anim` | 329 |
| `.debug bg` | 330 |
| `.debug bytes1` | 331 |
| `.debug bytes2` | 332 |
| `.debug condition` | 333 |
| `.debug getitemstate` | 334 |
| `.debug lrecipient` | 335 |
| `.debug getitemvalue` | 336 |
| `.debug getvaluebyindex` | 337 |
| `.debug getvaluebyname` | 338 |
| `.debug getprevplaytime` | 339 |
| `.debug moditemvalue` | 340 |
| `.debug modvalue` | 341 |
| `.debug play` | 342 |
| `.debug play cinematic` | 291 |
| `.debug play sound` | 292 |
| `.debug play text` | 293 |
| `.debug play music` | 294 |
| `.debug send` | 343 |
| `.debug send buyerror` | 307 |
| `.debug send channelnotify` | 308 |
| `.debug send chatmmessage` | 309 |
| `.debug send equiperror` | 310 |
| `.debug send opcode` | 311 |
| `.debug send poi` | 312 |
| `.debug send qpartymsg` | 313 |
| `.debug send qinvalidmsg` | 314 |
| `.debug send mailerror` | 315 |
| `.debug send sellerror` | 316 |
| `.debug send spellfail` | 317 |
| `.debug send visual` | 318 |
| `.debug send chanvisual` | 319 |
| `.debug send chanvisualnext` | 320 |
| `.debug send impact` | 321 |
| `.debug send openbag` | 322 |
| `.debug send worldstate` | 323 |
| `.debug setaurastate` | 344 |
| `.debug setitemvalue` | 345 |
| `.debug setvaluebyindex` | 346 |
| `.debug setvaluebyname` | 347 |
| `.debug setprevplaytime` | 348 |
| `.debug spellcheck` | 349 |
| `.debug spellcoefs` | 350 |
| `.debug spellmods` | 351 |
| `.debug forceupdate` | 352 |
| `.debug los` | 353 |
| `.debug los check` | 300 |
| `.debug los allow` | 301 |
| `.debug moveto` | 354 |
| `.debug movedistance` | 355 |
| `.debug faceme` | 356 |
| `.debug assert` | 357 |
| `.debug pvpcredit` | 358 |
| `.debug unitstate` | 359 |
| `.debug control` | 360 |
| `.debug monster` | 361 |
| `.debug time` | 362 |
| `.debug moveflags` | 363 |
| `.debug movespline` | 364 |
| `.debug movemotion` | 365 |
| `.debug factionchange_items` | 366 |
| `.debug loottable` | 367 |
| `.debug utf8overflow` | 368 |
| `.debug chatfreeze` | 369 |
| `.gm fly` | 387 |
| `.gm visible` | 390 |
| `.gm setview` | 391 |
| `.go graveyard` | 399 |
| `.go grid` | 400 |
| `.go target` | 402 |
| `.go taxinode` | 403 |
| `.go trigger` | 404 |
| `.go zonexy` | 405 |
| `.go xy` | 406 |
| `.go xyzo` | 408 |
| `.go forward` | 410 |
| `.go up` | 411 |
| `.go relative` | 412 |
| `.go warsong` | 414 |
| `.go arathi` | 415 |
| `.go alterac` | 416 |
| `.gobject tmpadd` | 424 |
| `.gobject target` | 428 |
| `.gobject ufinfo` | 431 |
| `.gobject select` | 432 |
| `.gobject despawn` | 433 |
| `.gobject toggle` | 434 |
| `.gobject reset` | 435 |
| `.gobject respawn` | 436 |
| `.gobject use` | 437 |
| `.gobject setgostate` | 438 |
| `.gobject setlootstate` | 439 |
| `.gobject customanim` | 440 |
| `.gobject spawnanim` | 441 |
| `.gobject despawnanim` | 442 |
| `.guild rename` | 453 |
| `.guild showlog` | 454 |
| `.instance continents` | 479 |
| `.instance getdata` | 480 |
| `.instance setdata` | 481 |
| `.instance groupunbind` | 484 |
| `.instance savedata` | 486 |
| `.instance switch` | 487 |
| `.instance perfinfos` | 488 |
| `.instance smartrebind` | 489 |
| `.learn all` | 495 |
| `.learn all_gm` | 496 |
| `.learn all_crafts` | 497 |
| `.learn all_default` | 498 |
| `.learn all_lang` | 499 |
| `.learn all_myclass` | 500 |
| `.learn all_myspells` | 501 |
| `.learn all_mytalents` | 502 |
| `.learn all_mytaxis` | 503 |
| `.learn all_recipes` | 504 |
| `.learn all_trainer` | 505 |
| `.learn all_items` | 506 |
| `.list clicktomove` | 524 |
| `.list exploredareas` | 525 |
| `.list item` | 526 |
| `.list talents` | 528 |
| `.list maps` | 529 |
| `.list movegens` | 530 |
| `.list hostilerefs` | 531 |
| `.list threat` | 532 |
| `.list visibleguids` | 533 |
| `.lookup account` | 559 |
| `.lookup account email` | 539 |
| `.lookup account ip` | 540 |
| `.lookup account iponline` | 541 |
| `.lookup account name` | 542 |
| `.lookup creaturemodel` | 562 |
| `.lookup itemset` | 566 |
| `.lookup player` | 569 |
| `.lookup player account` | 549 |
| `.lookup player email` | 550 |
| `.lookup player ip` | 551 |
| `.lookup player name` | 552 |
| `.lookup player character` | 553 |
| `.lookup pool` | 570 |
| `.lookup sound` | 573 |
| `.lookup guild` | 576 |
| `.modify swim` | 588 |
| `.modify bwalk` | 590 |
| `.modify fly` | 591 |
| `.modify aspeed` | 592 |
| `.modify faction` | 593 |
| `.modify tp` | 594 |
| `.modify mount` | 595 |
| `.modify drunk` | 598 |
| `.modify exhaustion` | 599 |
| `.modify emotestate` | 600 |
| `.modify morph` | 601 |
| `.modify gender` | 602 |
| `.modify strength` | 603 |
| `.modify agility` | 604 |
| `.modify stamina` | 605 |
| `.modify intellect` | 606 |
| `.modify spirit` | 607 |
| `.modify armor` | 608 |
| `.modify holy` | 609 |
| `.modify fire` | 610 |
| `.modify nature` | 611 |
| `.modify frost` | 612 |
| `.modify shadow` | 613 |
| `.modify arcane` | 614 |
| `.modify ap` | 615 |
| `.modify rangeap` | 616 |
| `.modify spellpower` | 617 |
| `.modify crit` | 618 |
| `.modify rangecrit` | 619 |
| `.modify spellcrit` | 620 |
| `.modify mainspeed` | 621 |
| `.modify offspeed` | 622 |
| `.modify rangespeed` | 623 |
| `.modify castspeed` | 624 |
| `.modify block` | 625 |
| `.modify dodge` | 626 |
| `.modify parry` | 627 |
| `.modify combreach` | 628 |
| `.modify boundrad` | 629 |
| `.modify xprate` | 630 |
| `.modify hairstyle` | 631 |
| `.modify haircolor` | 632 |
| `.modify skincolor` | 633 |
| `.modify accessories` | 634 |
| `.npc additem` | 691 |
| `.npc addweapon` | 692 |
| `.npc aiinfo` | 693 |
| `.npc allowmove` | 694 |
| `.npc allowattack` | 695 |
| `.npc despawn` | 696 |
| `.npc delitem` | 697 |
| `.npc evade` | 698 |
| `.npc follow` | 699 |
| `.npc unfollow` | 700 |
| `.npc move` | 702 |
| `.npc summon` | 705 |
| `.npc tame` | 709 |
| `.npc spawn` | 710 |
| `.npc spawn add` | 664 |
| `.npc spawn addentry` | 665 |
| `.npc spawn delete` | 666 |
| `.npc spawn info` | 667 |
| `.npc spawn set` | 668 |
| `.npc spawn set entry` | 649 |
| `.npc spawn set displayid` | 650 |
| `.npc spawn set emotestate` | 651 |
| `.npc spawn set standstate` | 652 |
| `.npc spawn set sheathstate` | 653 |
| `.npc spawn set movetype` | 654 |
| `.npc spawn set wanderdistance` | 655 |
| `.npc spawn set respawntime` | 656 |
| `.npc spawn set deathstate` | 657 |
| `.npc spawn set auras` | 658 |
| `.npc spawn move` | 669 |
| `.npc spawn load` | 670 |
| `.npc spawn unload` | 671 |
| `.npc set entry` | 677 |
| `.npc set level` | 678 |
| `.npc set faction` | 679 |
| `.npc set displayid` | 681 |
| `.npc set movetype` | 682 |
| `.npc set wanderdistance` | 683 |
| `.npc set respawntime` | 684 |
| `.npc set reactstate` | 685 |
| `.npc group` | 712 |
| `.npc group add` | 640 |
| `.npc group addrel` | 641 |
| `.npc group del` | 642 |
| `.npc group link` | 643 |
| `.unit` | 1208 |
| `.unit aiinfo` | 739 |
| `.unit info` | 740 |
| `.unit moveinfo` | 741 |
| `.unit speedinfo` | 742 |
| `.unit statinfo` | 743 |
| `.unit ufinfo` | 744 |
| `.unit factioninfo` | 745 |
| `.unit show` | 746 |
| `.unit show race` | 718 |
| `.unit show class` | 719 |
| `.unit show gender` | 720 |
| `.unit show powertype` | 721 |
| `.unit show form` | 722 |
| `.unit show visflags` | 723 |
| `.unit show miscflags` | 724 |
| `.unit show emotestate` | 725 |
| `.unit show standstate` | 726 |
| `.unit show sheathstate` | 727 |
| `.unit show unitstate` | 728 |
| `.unit show unitflags` | 729 |
| `.unit show npcflags` | 730 |
| `.unit show moveflags` | 731 |
| `.unit show createspell` | 732 |
| `.unit show combattimer` | 733 |
| `.pool` | 1209 |
| `.pool list` | 766 |
| `.pool update` | 767 |
| `.pool spawns` | 768 |
| `.pdump` | 1210 |
| `.pdump load` | 759 |
| `.pdump write` | 760 |
| `.reload` | 1212 |
| `.reload all` | 796 |
| `.reload all_area` | 797 |
| `.reload all_gossips` | 798 |
| `.reload all_item` | 799 |
| `.reload all_locales` | 800 |
| `.reload all_loot` | 801 |
| `.reload all_npc` | 802 |
| `.reload all_quest` | 803 |
| `.reload all_scripts` | 804 |
| `.reload all_spell` | 805 |
| `.reload anticheat` | 807 |
| `.reload config` | 808 |
| `.reload account_banned` | 810 |
| `.reload areatrigger_involvedrelation` | 811 |
| `.reload areatrigger_tavern` | 812 |
| `.reload areatrigger_teleport` | 813 |
| `.reload autobroadcast` | 814 |
| `.reload character_pet` | 815 |
| `.reload cinematic_waypoints` | 816 |
| `.reload command` | 817 |
| `.reload conditions` | 818 |
| `.reload creature` | 819 |
| `.reload creature_ai_events` | 820 |
| `.reload creature_battleground` | 821 |
| `.reload creature_display_info_addon` | 822 |
| `.reload creature_groups` | 823 |
| `.reload creature_involvedrelation` | 824 |
| `.reload creature_loot_template` | 825 |
| `.reload creature_onkill_reputation` | 826 |
| `.reload creature_questrelation` | 827 |
| `.reload creature_spells` | 828 |
| `.reload creature_spells_scripts` | 829 |
| `.reload creature_template` | 830 |
| `.reload disenchant_loot_template` | 831 |
| `.reload event_scripts` | 832 |
| `.reload exploration_basexp` | 833 |
| `.reload fishing_loot_template` | 834 |
| `.reload game_graveyard_zone` | 835 |
| `.reload game_tele` | 836 |
| `.reload game_weather` | 837 |
| `.reload gameobject` | 838 |
| `.reload gameobject_battleground` | 839 |
| `.reload gameobject_involvedrelation` | 840 |
| `.reload gameobject_loot_template` | 841 |
| `.reload gameobject_questrelation` | 842 |
| `.reload gameobject_requirement` | 843 |
| `.reload gameobject_scripts` | 844 |
| `.reload gameobject_template` | 845 |
| `.reload generic_scripts` | 846 |
| `.reload gossip_menu` | 847 |
| `.reload gossip_menu_option` | 848 |
| `.reload gossip_scripts` | 849 |
| `.reload instance_buff_removal` | 850 |
| `.reload ip_banned` | 851 |
| `.reload item_enchantment_template` | 852 |
| `.reload item_loot_template` | 853 |
| `.reload item_required_target` | 854 |
| `.reload item_template` | 855 |
| `.reload locales_creature` | 856 |
| `.reload locales_gameobject` | 857 |
| `.reload locales_gossip_menu_option` | 858 |
| `.reload locales_item` | 859 |
| `.reload locales_page_text` | 860 |
| `.reload locales_points_of_interest` | 861 |
| `.reload locales_quest` | 862 |
| `.reload mail_loot_template` | 863 |
| `.reload mangos_string` | 864 |
| `.reload map_loot_disabled` | 865 |
| `.reload map_template` | 866 |
| `.reload npc_gossip` | 867 |
| `.reload npc_text` | 868 |
| `.reload npc_trainer` | 869 |
| `.reload npc_vendor` | 870 |
| `.reload page_text` | 871 |
| `.reload pet_name_generation` | 872 |
| `.reload petitions` | 873 |
| `.reload pickpocketing_loot_template` | 874 |
| `.reload player_factionchange_items` | 875 |
| `.reload player_factionchange_mounts` | 876 |
| `.reload player_factionchange_quests` | 877 |
| `.reload player_factionchange_reputations` | 878 |
| `.reload player_factionchange_spells` | 879 |
| `.reload points_of_interest` | 880 |
| `.reload quest_end_scripts` | 881 |
| `.reload quest_greeting` | 882 |
| `.reload quest_start_scripts` | 883 |
| `.reload quest_template` | 884 |
| `.reload reference_loot_template` | 885 |
| `.reload reputation_reward_rate` | 886 |
| `.reload reputation_spillover_template` | 887 |
| `.reload reserved_name` | 888 |
| `.reload skill_fishing_base_level` | 889 |
| `.reload skinning_loot_template` | 890 |
| `.reload spell_area` | 891 |
| `.reload spell_chain` | 892 |
| `.reload spell_disabled` | 893 |
| `.reload spell_elixir` | 894 |
| `.reload spell_group` | 895 |
| `.reload spell_group_stack_rules` | 896 |
| `.reload spell_learn_spell` | 897 |
| `.reload spell_mod` | 898 |
| `.reload spell_pet_auras` | 899 |
| `.reload spell_proc_event` | 900 |
| `.reload spell_proc_item_enchant` | 901 |
| `.reload spell_script_target` | 902 |
| `.reload spell_scripts` | 903 |
| `.reload spell_target_position` | 904 |
| `.reload spell_template` | 905 |
| `.reload spell_threats` | 906 |
| `.reload taxi_path_transitions` | 907 |
| `.reload trainer_greeting` | 908 |
| `.reload variables` | 909 |
| `.reset honor` | 915 |
| `.reset level` | 916 |
| `.reset spells` | 917 |
| `.reset stats` | 918 |
| `.reset items` | 920 |
| `.reset all` | 921 |
| `.server corpses` | 987 |
| `.server exit` | 988 |
| `.server log` | 992 |
| `.server log filter` | 974 |
| `.server log level` | 975 |
| `.server plimit` | 994 |
| `.server resetallraids` | 995 |
| `.tele add` | 1004 |
| `.tele del` | 1005 |
| `.tele group` | 1007 |
| `.trigger` | 1216 |
| `.trigger active` | 1014 |
| `.trigger near` | 1015 |
| `.wp` | 1217 |
| `.wp show` | 1030 |
| `.wp add` | 1031 |
| `.wp modify` | 1032 |
| `.wp export` | 1033 |
| `.service` | 1218 |
| `.service del_characters` | 1148 |
| `.bot` | 1219 |
| `.bot add` | 83 |
| `.bot add_all` | 84 |
| `.bot delete` | 85 |
| `.bot info` | 86 |
| `.bot reload` | 87 |
| `.bot stop` | 88 |
| `.bot start` | 89 |
| `.bot ranadd` | 90 |
| `.ahbot` | 1220 |
| `.ahbot reload` | 76 |
| `.ahbot update` | 77 |
| `.partybot` | 1221 |
| `.partybot add` | 96 |
| `.partybot clone` | 97 |
| `.partybot load` | 98 |
| `.partybot setrole` | 99 |
| `.partybot attackstart` | 100 |
| `.partybot attackstop` | 101 |
| `.partybot pull` | 102 |
| `.partybot aoe` | 103 |
| `.partybot caststart` | 104 |
| `.partybot caststop` | 105 |
| `.partybot ccmark` | 106 |
| `.partybot focusmark` | 107 |
| `.partybot clearmarks` | 108 |
| `.partybot cometome` | 109 |
| `.partybot usegobject` | 110 |
| `.partybot pause` | 111 |
| `.partybot unpause` | 112 |
| `.partybot unequip` | 113 |
| `.partybot remove` | 114 |
| `.battlebot` | 1222 |
| `.battlebot add` | 128 |
| `.battlebot add alterac` | 120 |
| `.battlebot add arathi` | 121 |
| `.battlebot add warsong` | 122 |
| `.battlebot remove` | 129 |
| `.battlebot removeall` | 130 |
| `.battlebot showpath` | 131 |
| `.battlebot showallpaths` | 132 |
| `.world` | 1223 |
| `.world update` | 1039 |
| `.world cansee` | 1040 |
| `.world detail` | 1041 |
| `.possess` | 1224 |
| `.cinematic` | 1225 |
| `.cinematic addwp` | 1047 |
| `.cinematic gotime` | 1048 |
| `.cinematic listwp` | 1049 |
| `.escort` | 1226 |
| `.escort create` | 1055 |
| `.escort addwp` | 1056 |
| `.escort modwp` | 1057 |
| `.escort clearwp` | 1058 |
| `.escort showwp` | 1059 |
| `.escort hidewp` | 1060 |
| `.bg` | 1227 |
| `.bg status` | 1066 |
| `.bg start` | 1067 |
| `.bg stop` | 1068 |
| `.spell` | 1228 |
| `.spell effects` | 1075 |
| `.spell info` | 1076 |
| `.spell search` | 1077 |
| `.spell iconfix` | 1078 |
| `.pvp` | 1229 |
| `.variable` | 1230 |
| `.nameaura` | 1232 |
| `.group` | 1238 |
| `.group additem` | 460 |
| `.group revive` | 461 |
| `.group replenish` | 462 |
| `.group summon` | 463 |
| `.groupgo` | 1239 |
| `.demorph` | 1242 |
| `.namedie` | 1243 |
| `.fear` | 1245 |
| `.knockback` | 1246 |
| `.mount` | 1248 |
| `.dismount` | 1249 |
| `.itemmove` | 1253 |
| `.cooldown list` | 283 |
| `.cooldown clear` | 284 |
| `.cooldown clearclientside` | 285 |
| `.unlearn all_gm` | 513 |
| `.unlearn all_crafts` | 514 |
| `.unlearn all_recipes` | 515 |
| `.removeriding` | 1256 |
| `.ban note` | 174 |
| `.ban warn` | 175 |
| `.start` | 1267 |
| `.unstuck` | 1268 |
| `.taxicheat` | 1269 |
| `.linkgrave` | 1270 |
| `.hover` | 1273 |
| `.bank` | 1280 |
| `.ticket assign` | 1121 |
| `.ticket closedlist` | 1123 |
| `.ticket counter` | 1124 |
| `.ticket comment` | 1125 |
| `.ticket complete` | 1126 |
| `.ticket escalate` | 1128 |
| `.ticket escalatedlist` | 1129 |
| `.ticket next` | 1131 |
| `.ticket notify` | 1132 |
| `.ticket previous` | 1134 |
| `.ticket reload` | 1135 |
| `.ticket reset` | 1136 |
| `.ticket response` | 1137 |
| `.ticket response reset` | 1113 |
| `.ticket response append` | 1114 |
| `.ticket response appendln` | 1115 |
| `.ticket togglesystem` | 1138 |
| `.ticket unassign` | 1139 |
| `.ticket viewid` | 1140 |
| `.ticket viewname` | 1141 |
| `.wr` | 1286 |
| `.groupinfo` | 1288 |
| `.pbcast` | 1289 |
| `.pbcast stats` | 752 |
| `.pbcast setthreads` | 753 |
| `.send` | 1291 |
| `.send mass` | 935 |
| `.send mass items` | 927 |
| `.send mass mail` | 928 |
| `.send mass money` | 929 |
| `.send items` | 937 |
| `.send mail` | 938 |
| `.send message` | 939 |
| `.send money` | 940 |
| `.movegens` | 1294 |
| `.cometome` | 1295 |
| `.aoedamage` | 1296 |
| `.combatstop` | 1298 |
| `.stable` | 1300 |
| `.quit` | 1301 |
| `.mmap` | 1302 |
| `.mmap path` | 1084 |
| `.mmap loc` | 1085 |
| `.mmap loadedtiles` | 1086 |
| `.mmap stats` | 1087 |
| `.mmap testarea` | 1088 |
| `.mmap connect` | 1089 |
| `.mmap reload` | 1090 |
| `.mmap unload` | 1091 |
| `.video` | 1303 |
| `.video expendables` | 1098 |
| `.video turn` | 1099 |
| `.freeze` | 1304 |
| `.unfreeze` | 1305 |
| `.anticheat` | 1306 |
| `.groupspell` | 1307 |
| `.groupspell add` | 69 |
| `.groupspell rule` | 70 |
| `.pet` | 1308 |
| `.pet learnspell` | 784 |
| `.pet unlearnspell` | 785 |
| `.pet list` | 786 |
| `.pet rename` | 787 |
| `.pet delete` | 788 |
| `.pet loyalty` | 789 |
| `.pet info` | 790 |
| `.channel` | 1309 |
| `.channel join` | 1106 |
| `.channel leave` | 1107 |
| `.log` | 1310 |
| `.sniff` | 1311 |
| `.spamer` | 1312 |
| `.spamer mute` | 1154 |
| `.spamer unmute` | 1155 |
| `.spamer list` | 1156 |
| `.antispam` | 1313 |
| `.antispam add` | 1162 |
| `.antispam remove` | 1163 |
| `.antispam replace` | 1164 |
| `.antispam removereplace` | 1165 |
| `.gold` | 1314 |
| `.gold remove` | 1171 |
| `.wareffort` | 1315 |
| `.wareffort info` | 1177 |
| `.wareffort setgongtime` | 1178 |
| `.wareffort setstage` | 1179 |
| `.wareffort getresource` | 1180 |
| `.wareffort setresource` | 1181 |
