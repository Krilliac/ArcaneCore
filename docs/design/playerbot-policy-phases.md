# Playerbot policy: phases and gates

The playerbot goal policy (docs/areas/playbots.md, Goal policy) is built in phases. Each later phase has gates that must all hold
before it starts. Nothing in a later phase is merged early. **The rule ladder and the risk model stay the default and the fallback in
every phase.** A policy only re-ranks options the rules already allow, and late, missing or non-finite scores leave the rules in charge.

**Data rule (every phase).** Models train only on the server's own telemetry: what the bots did and what followed. No label, reward or
example may come from Claude or any other LLM (Anthropic's terms prohibit training models on Claude outputs).

## Phase 1: one CPU goal policy engine (done)

`IPlayerbotPolicy` takes features in and gives goal scores out. The ladder is `RuleBasedPolicy`. A built-in SIMD dense model reads its
weights from a file. Scoring runs per map in batches on a worker task, within a per-tick budget, and its results are applied on a later
think, bounded by staleness. The Ollama planner and the ONNX pass are removed. Chat is separate.

## Phase 2: decision telemetry (done)

Telemetry is off by default. It writes goal records (`"k":"goal"`), an optional epsilon exploration among the ladder's runner-ups, and
the combat-decision records phase 4 will need (`"k":"fight"`, `"k":"skip"`; docs/areas/playbots.md, Combat decisions). Output is
bounded and rotating, and is written off the world thread.

## Phase 3: train the goal policy and A/B it (staged, gated)

Branch `claude/bot-policy-phase3`, stacked on phases 1 and 2. It adds `AbPolicyShare` (the bot's arm, by a hash of its guid), the
`arm` telemetry key, `tools/playerbot-policy/train.py` and `ab_report.py`.

Gates, all required before training starts:

1. main has the H1 hardening.
2. main has the playerbot defer pass.
3. main has the memoization/tabulation pass (branch `claude/memo-tabulation`).
4. The live server runs with the policy on, no model, telemetry on (and some exploration), long enough for `train.py`'s
   `--min-rows` and for every goal to have rows.

The order of work is collect, train, A/B with `AbPolicyShare` 0.5, report, and then a person decides on the report. The README in
`tools/playerbot-policy/` has the commands.

## Phase 4: combat-adjacent decisions (planned, gated)

Four decisions, each scored with the same `IPlayerbotPolicy` pattern and the same fallback. The rule each one replaces stays in charge
whenever scores are late, missing or non-finite, and a policy only chooses among options the rule already allows:

| Decision | Rule today | Hook | Options a policy may choose |
|---|---|---|---|
| Engage or skip a pull | `PlayerbotRisk.ChooseTarget` / `PlayerbotRiskModel.Assess` (risk against reward, adds, elites) | after `Assess`, before the candidate is returned | engage, or skip a candidate the rules would engage. Never engage one the rules avoid as lethal. |
| Retreat threshold | `PlayerbotRiskModel.Judge` (`RetreatHealthPct` 35, time to die against time to kill) | `PlayerbotRisk.ObserveFight` | the health threshold within a bounded range around the option. The retreat-feasibility refusal stays a rule. |
| Rest, eat, drink threshold | `PlayerbotBrain.Rest` (45/35), `RecoverHealthPct` 80 | the out-of-combat rest check | the health and mana thresholds within bounded ranges. |
| Target among valid candidates | `FirstDefensiveCandidate` (reachable, current, nearest) | the defensive target pick | which of the valid attackers to hit. |

Never in scope:

- Spell rotations. They are per global cooldown, on the world thread, and outside the batch-and-apply-later design.
- Pathing.
- Group, dungeon and battleground AI.

Gates, all required before phase 4 starts:

1. The phase 3 goal model has beaten the ladder in its A/B (the report, and a person's decision to ship it).
2. Each combat decision gets its own A/B (`AbPolicyShare` per decision, or one decision on at a time), compared on deaths, time to
   kill, downtime and damage taken from the `fight` records, plus XP/h and stalls from the goal records.
3. The combat telemetry from phase 2 has run on the live server long enough to train each decision.

**Training data per decision**, from phase 2:

- **Engage or skip.** `fight.engage` (features, `decision`, `reason`) with the fight's outcome, and `skip` records with the wait until
  the next fight.
- **Retreat.** `fight.retreat` (the verdict that ran, or the stay closest to running) with `died`, `damage_pct` and `ttk`.
- **Rest.** `fight.rest` (health and mana at the fight's end, `rested`) with `downtime` and the next fight's outcome.
- **Target choice.** `fight.targets` (each in-fight switch) with `ttk`, `damage_pct` and `died`.
