# Playerbot goal policy: training and A/B

Offline tools for the goal policy (`World:Playerbots:Policy`; docs/areas/playbots.md, Goal policy). Both read the JSON-lines
telemetry the world server writes with `World:Playerbots:Policy:Telemetry:Enabled = true` (default folder
`telemetry/playerbot-policy` under the profile folder). Python 3 with numpy; nothing else.

**Data rule.** The only training data is what the server's bots did and what followed: the telemetry records. Never add labels,
rewards or examples produced by Claude or any other LLM (Anthropic's terms prohibit training models on Claude outputs).

## Order of work

1. **Collect.** Run the live server with the policy on, no model, telemetry on, and some exploration so the data holds goals the
   ladder alone never picks:

   ```
   World:Playerbots:Policy:Enabled = true
   World:Playerbots:Policy:ExplorationRate = 0.05
   World:Playerbots:Policy:Telemetry:Enabled = true
   ```

   Each record is one decision with its outcome `OutcomeMinutes` (5) later. The folder is bounded (`MaxFiles` x `MaxFileBytes`,
   16 x 16 MiB by default), so copy it out before it rotates if the run is long.

2. **Train.**

   ```
   python -I tools/playerbot-policy/train.py TELEMETRY_DIR -o policy-model.json
   python -I tools/playerbot-policy/train.py TELEMETRY_DIR -o policy-model.json --model mlp --hidden 32
   ```

   The label is progress per minute over the record's window: `lp` (levels gained, with fractions) plus 0.25 per quest handed in,
   less 0.05 per stall, 0.10 per death and 0.02 per anomaly (`--quest-weight` and the others change these). `linear` (the default)
   is a ridge regression per goal; `mlp` is one shared ReLU layer with an output per goal. Both standardise the features into the
   file's `mean` and `scale`. It prints train and holdout error, refuses to write from fewer than `--min-rows` (1000) usable rows,
   and refuses a fit with a non-finite weight.

3. **Compare.** Put the model on the server for half the bots and run with telemetry on (exploration off, so both arms are clean):

   ```
   World:Playerbots:Policy:ModelPath = policy-model.json
   World:Playerbots:Policy:AbPolicyShare = 0.5
   World:Playerbots:Policy:ExplorationRate = 0
   ```

   then

   ```
   python -I tools/playerbot-policy/ab_report.py TELEMETRY_DIR --json ab-report.json
   ```

   It reports, per arm and per bot-hour, levels, quests, stalls, deaths and anomalies with 95% bootstrap intervals over bots, and
   the policy-minus-ladder difference. It gives no verdict with fewer than `--min-bots` (10) bots in an arm.

4. **Decide.** The report says only which differences are distinguishable from zero. Shipping a model (setting `ModelPath` for
   every bot, `AbPolicyShare` 1) is a person's call made on it.

## Self-test

The scripts can be smoke-tested on synthetic files to check that they run and recover a planted effect. Such files test the
scripts only: never train a model for the server on them.
