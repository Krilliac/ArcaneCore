# Per-world hunter pet-name options

Pet validation now has a named `Pets:Names` configuration section with vmangos
defaults: `MinPetName=2`, `StrictPetNames=0`, and `RealmZone=1`. Minimum length
is clamped to 2..12 and the pet maximum is the build's 12-character limit.
Strict bit behavior and realm-script selection use the existing
`CharacterNameRules.IsValidString` implementation with `Create=false`, matching
vmangos `ObjectMgr::CheckPetName` (`ObjectMgr.cpp:9618-9645`) and
`GetRealmLanguageType`/`isValidString` (`ObjectMgr.cpp:9515-9578`) semantics.

The options are bound into the world-local `PetNameRules` instance. Injected
rules and external reserved/profanity vetoes remain per-world; no static policy
state or player-creation configuration is reused. Real NamesProfanity,
NamesReserved, and `reserved_name` catalogs remain pending because this tree has
no source data loader for them.

World tests cover defaults, minimum clamping, strict realm scripts, and
independence between differently configured rule instances.
