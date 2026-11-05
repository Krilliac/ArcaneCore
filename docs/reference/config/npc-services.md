# NpcServices options

8 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `NpcServices`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `BankBagSlotPricesDbcPath` | string | `(empty)` | text | World | start | Build-5875 BankBagSlotPrices.dbc. |
| `DurabilityCostsDbcPath` | string | `(empty)` | text | World | start | Build-5875 DurabilityCosts.dbc (repair multipliers by item level). |
| `DurabilityQualityDbcPath` | string | `(empty)` | text | World | start | Build-5875 DurabilityQuality.dbc (repair quality factors). |
| `NpcTemplates` | List<NpcTemplateMetadata> | `(empty)` | a list: numbered keys 0, 1, 2, ...; each entry is a group of keys | World | start | Optional per-entry overrides for imported creature_template gossip_menu_id and trainer_*. Without an override, CreatureQuestLookup uses the World v21 imported fields. |
| `SkillLineAbilityDbcPath` | string | `(empty)` | text | World | start | Build-5875 SkillLineAbility.dbc (trainer rank prerequisites, race/class fit). |
| `TaxiNodesDbcPath` | string | `(empty)` | text | World | start | Build-5875 TaxiNodes.dbc; when set its node positions and faction mounts replace the imported table. |
| `TaxiPathDbcPath` | string | `(empty)` | text | World | start | Build-5875 TaxiPath.dbc; when set its routes and costs replace the imported table. |
| `TaxiPathNodeDbcPath` | string | `(empty)` | text | World | start | Build-5875 TaxiPathNode.dbc (flight waypoints). |
