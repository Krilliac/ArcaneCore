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
   file's `mean` and `scale`. It prints train and holdout error, refuses to write from fewer than `--min-rows` (1000) usable rows
   or with any goal under `--min-goal-rows` (50) rows (a goal with no rows would score a guess for every state, and an untrained
   QuestTravel won 89% of the states it was open in on real telemetry; `0` lifts the check), and refuses a fit with a non-finite weight.

   **Tick-overrun filter.** A decision made while the world tick was overrunning (a concurrent engine build, a stall) describes a
   lagging world, not what the policy chose, so both `train.py` and `ab_report.py` drop it. The server writes a `"k":"health"`
   record about every 10 s (`t` the unix ms at the end, `ms` the length, `over` the frames over the tick budget; docs/areas/playbots.md,
   Tick health). A decision is dropped when its window `[t, t + w]` overlaps a health interval with more than `--max-overruns`
   (default 0) overruns. The tools print how many decisions were dropped per goal.

   - `--no-health-filter` keeps everything.
   - A decision whose window is not entirely inside health coverage (the start of a run, a restart, or a file from a build that writes
     no health records, such as the Node1 fleet build `ea054aaa`) cannot be checked: it is kept and counted as *unverified*, with a
     warning. With no health records at all the tools say loudly that the filter could not apply.
   - `--tick-log WORLD_LOG` (repeatable) adds windows from the world's log in either format (`Logging:ArcaneCore:Format` = `Json` or `Text`; `logs/*.jsonl` or `logs/*.log`; the format is detected per line, and each rolled or fallback segment is its own file to pass), so old telemetry can still
     be filtered: each `TickOverrun` warning gives `[ts - S s, ts]` ("in the last S s"; the first warning after a start has no such
     part and 30 s, the default `WarnIntervalSeconds`, is assumed). These windows are coarser than health records, because the
     warning is rate limited, and the log's own time span counts as covered. The log and the telemetry must come from the same run
     and the same clock (the log's `ts` is UTC or carries an offset; telemetry `t` is unix ms).

   - The checked span is `[t, t + w + slack]`: the server closes an outcome on the first think after the window has elapsed, so under
     lag the real span runs longer than `w`. `--window-slack-s` (default 10, one health interval) sets the slack, and the coverage test
     uses the same extended span.
   - The last `--tick-log-tail-s` (default 30, `WarnIntervalSeconds`) of a tick log does not count as covered, because an overrun
     there is reported only after the log ends. Each warning window is widened by 1 s (its "in the last S s" is truncated).
   - **One server per directory.** Coverage is global across the files of a directory (one server's health and goal records are split
     across files), so a directory must hold a single server's run. The tools warn when health intervals of different files overlap
     in time, which means two servers or runs are mixed.

   Test the filter with `python -I tools/playerbot-policy/test_health_filter.py` (run from outside the folder; stdlib `unittest`
   plus numpy).

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
