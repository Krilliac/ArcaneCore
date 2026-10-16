#!/usr/bin/env python3
"""Compare the policy arm with the ladder arm of a playerbot A/B run.

With World:Playerbots:Policy:AbPolicyShare between 0 and 1, each bot is in one arm for good
(by a hash of its guid): the policy arm tries the policy's choice first, the ladder arm only
records what the policy would have chosen. Every telemetry record carries its "arm".

For each arm this reports, per bot-hour: levels gained (lp, with fractions), quests handed in,
stalls, deaths and anomaly reports, as the mean over bots of each bot's mean per-minute rate
(records of one bot overlap and correlate, so the bot is the unit), with a 95% bootstrap
interval, and the policy-minus-ladder difference with its interval.

The verdict line only says whether the difference is distinguishable from zero; whether that
is enough to ship is a person's call, made on this report.

Decisions made while the world tick was overrunning are left out of both arms (health_filter.py:
--max-overruns, --no-health-filter, --tick-log), as train.py does.

Usage:
  python -I tools/playerbot-policy/ab_report.py TELEMETRY_DIR [--json report.json]
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import random
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # python -I leaves the script's folder off the path
import health_filter  # noqa: E402

METRICS = [("levels", "lp"), ("quests", "quests"), ("stalls", "stalls"), ("deaths", "deaths"), ("anomalies", "anomalies")]
# Higher is better for the first two, lower for the rest.
BETTER_HIGH = {"levels", "quests"}


def load(directory: str, args=None):
    """Per arm and bot, the decisions' per-minute outcomes; with args, minus the decisions made while the tick overran."""
    per_arm = {"policy": defaultdict(list), "ladder": defaultdict(list)}
    skipped = 0
    health = health_filter.Health()
    decisions = []
    for path in sorted(glob.glob(os.path.join(directory, "policy-*.jsonl"))):
        health.note_file(path)
        with open(path, encoding="utf-8") as handle:
            for line in handle:
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    skipped += 1
                    continue
                if record.get("k") == "health":
                    health.add_record(record, path)
                    continue
                if record.get("k", "goal") != "goal":
                    continue  # combat records (fight, skip) are not goal decisions
                arm = record.get("arm")
                if arm not in per_arm or record.get("w", 0) <= 0:
                    skipped += 1
                    continue
                decisions.append((arm, record))
    gate = None
    tally = health_filter.Tally()
    if args is not None and not args.no_health_filter:
        health_filter.read_tick_logs(health, args.tick_log)
        gate = health.build(args.max_overruns, args.window_slack_s, args.tick_log_tail_s)
    arm_counts = {"policy": [0, 0], "ladder": [0, 0]}  # [dropped, seen]
    for arm, record in decisions:
        if gate is not None:
            verdict = gate.classify(record.get("t", 0), record["w"])
            tally.add(record.get("goal") or "(none)", verdict)
            arm_counts[arm][1] += 1
            if verdict == "drop":
                arm_counts[arm][0] += 1
                continue
        minutes = record["w"] / 60.0
        per_arm[arm][record["bot"]].append({name: record["o"][key] / minutes for name, key in METRICS})
    if gate is not None:
        health_filter.describe(health, tally, args.max_overruns)
        for arm, (dropped, seen) in arm_counts.items():
            print(f"  arm {arm}: dropped {dropped} of {seen}", file=sys.stderr)
    elif args is not None:
        print("health filter off (--no-health-filter): every decision kept", file=sys.stderr)
    return per_arm, skipped


def bot_rates(bots: dict) -> dict[str, list[float]]:
    rates = {name: [] for name, _ in METRICS}
    for windows in bots.values():
        for name, _ in METRICS:
            rates[name].append(60.0 * sum(w[name] for w in windows) / len(windows))  # per hour
    return rates


def mean(values):
    return sum(values) / len(values) if values else float("nan")


def bootstrap(values, rounds, rng):
    if len(values) < 2:
        return (float("nan"), float("nan"))
    means = sorted(mean([rng.choice(values) for _ in values]) for _ in range(rounds))
    return (means[int(0.025 * rounds)], means[int(0.975 * rounds) - 1])


def bootstrap_difference(a, b, rounds, rng):
    if len(a) < 2 or len(b) < 2:
        return (float("nan"), float("nan"))
    diffs = sorted(mean([rng.choice(a) for _ in a]) - mean([rng.choice(b) for _ in b]) for _ in range(rounds))
    return (diffs[int(0.025 * rounds)], diffs[int(0.975 * rounds) - 1])


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("telemetry")
    parser.add_argument("--json", help="also write the report as JSON")
    parser.add_argument("--rounds", type=int, default=2000)
    parser.add_argument("--min-bots", type=int, default=10, help="per arm, below which no verdict is given")
    parser.add_argument("--seed", type=int, default=11)
    health_filter.add_arguments(parser)
    args = parser.parse_args(argv)

    per_arm, skipped = load(args.telemetry, args)
    rng = random.Random(args.seed)
    rates = {arm: bot_rates(bots) for arm, bots in per_arm.items()}
    bots = {arm: len(b) for arm, b in per_arm.items()}
    windows = {arm: sum(len(w) for w in b.values()) for arm, b in per_arm.items()}
    print(f"bots: policy {bots['policy']}, ladder {bots['ladder']}; windows: policy {windows['policy']}, "
          f"ladder {windows['ladder']}; {skipped} records without an arm or window skipped")

    report = {"bots": bots, "windows": windows, "metrics": {}}
    print(f"{'per bot-hour':<12} {'policy':>22} {'ladder':>22} {'policy - ladder':>26}")
    for name, _ in METRICS:
        p, l = rates["policy"][name], rates["ladder"][name]
        p_ci, l_ci = bootstrap(p, args.rounds, rng), bootstrap(l, args.rounds, rng)
        d_ci = bootstrap_difference(p, l, args.rounds, rng)
        diff = mean(p) - mean(l)
        distinguishable = d_ci[0] > 0 or d_ci[1] < 0
        better = distinguishable and ((diff > 0) == (name in BETTER_HIGH))
        report["metrics"][name] = {"policy": mean(p), "policy_ci": p_ci, "ladder": mean(l), "ladder_ci": l_ci,
                                   "difference": diff, "difference_ci": d_ci, "distinguishable": distinguishable,
                                   "policy_better": better}
        mark = ("better" if better else "worse") if distinguishable else "no difference"
        print(f"{name:<12} {mean(p):>8.3f} [{p_ci[0]:.3f},{p_ci[1]:.3f}] {mean(l):>8.3f} [{l_ci[0]:.3f},{l_ci[1]:.3f}] "
              f"{diff:>+8.3f} [{d_ci[0]:+.3f},{d_ci[1]:+.3f}] {mark}")

    if min(bots.values()) < args.min_bots:
        print(f"verdict: not enough bots per arm (< {args.min_bots}) for a verdict")
        report["verdict"] = "insufficient"
    else:
        worse = [n for n, m in report["metrics"].items() if m["distinguishable"] and not m["policy_better"]]
        better = [n for n, m in report["metrics"].items() if m["policy_better"]]
        report["verdict"] = {"better": better, "worse": worse}
        print(f"verdict: policy distinguishably better on {better or 'nothing'}, worse on {worse or 'nothing'}")
    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(report, handle, indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
