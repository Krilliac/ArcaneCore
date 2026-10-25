#!/usr/bin/env python3
"""Train the playerbot goal policy from the server's own decision telemetry.

Reads the JSON-lines files the world server writes with World:Playerbots:Policy:Telemetry
(docs/areas/playbots.md, Goal policy), fits progress-per-minute for each (state, goal), and
writes the weights file PlayerbotModelPolicy loads (format arcanecore.playerbot-policy v1).

Data rule: the only training data is what the server's bots did and what followed (the
telemetry records). Never add labels, rewards or examples produced by Claude or any other
LLM: Anthropic's terms prohibit training models on Claude outputs.

The label of a record is its outcome over its window, per minute:

    progress = lp + quest_weight * quests - stall_weight * stalls
               - death_weight * deaths - anomaly_weight * anomalies
    label    = progress / (w / 60)

where lp is levels gained with their fractions (comparable across levels). The goal a record
trains is the one the think ended on ("goal"); records whose goal no policy chooses (null)
are skipped. Each goal's score is the predicted label for that goal from the state.

Models (both one dense format the server evaluates with SIMD on the CPU):
  linear  one ridge regression per goal (closed form); the default.
  mlp     a shared ReLU hidden layer and one output per goal, trained with Adam on the
          squared error of the taken goal's output only.

Decisions made while the world tick was overrunning (a concurrent build, a stall) describe a
lagging world and are dropped before training: see health_filter.py, --max-overruns,
--no-health-filter and --tick-log.

Usage:
  python -I tools/playerbot-policy/train.py TELEMETRY_DIR -o policy-model.json [--model mlp]
"""

from __future__ import annotations

import argparse
import glob
import json
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # python -I leaves the script's folder off the path
import health_filter  # noqa: E402

try:
    import numpy as np
except ImportError:  # pragma: no cover - environment check
    sys.exit("train.py needs numpy (pip install numpy)")

FORMAT = "arcanecore.playerbot-policy"
VERSION = 1
# The server's vocabulary (PlayerbotPolicySchema); the server refuses a file whose names differ.
FEATURES = ["level", "money", "free_bag_slots", "quest_log", "quests_complete", "set_asides", "stalls",
            "zone", "map", "nearby_targets", "health", "in_home"]
GOALS = ["Quest", "Town", "Opportunity", "Train", "Mount", "QuestTravel", "Grind", "Explore"]
# Record version 2 added the Opportunity goal: the `open` bitmask is `1 << goal` in GOALS order, so every bit from Train up moved one
# place. Version 1 records (written by a server without the goal) are read by remapping their bits through the old 7-goal order.
# Record version 3 added the `losing-burst-N` retreat reason and the `round_damage` retreat feature (fight records only); a goal record's
# layout is the same as in version 2, so version 2 records are read as written.
RECORD_VERSION = 3
GOALS_V1 = ["Quest", "Town", "Train", "Mount", "QuestTravel", "Grind", "Explore"]


def remap_open_v1(bits: int) -> int:
    """A version 1 `open` bitmask in the version 2 layout (each old goal's bit moved to its place in GOALS)."""
    out = 0
    for old, name in enumerate(GOALS_V1):
        if bits & (1 << old):
            out |= 1 << GOALS.index(name)
    return out


def upgrade_record(record: dict) -> dict | None:
    """The record in the current layout, or None for a version this trainer does not read. Version 1 keeps its data."""
    version = record.get("v")
    if version == RECORD_VERSION or version == 2:
        return record
    if version == 1:
        upgraded = dict(record)
        upgraded["v"] = RECORD_VERSION
        if isinstance(record.get("open"), int) and not isinstance(record.get("open"), bool):
            upgraded["open"] = remap_open_v1(record["open"])
        return upgraded
    return None


def load(directory: str):
    """The goal records, and the tick-health intervals found beside them (a health_filter.Health)."""
    rows = []
    health = health_filter.Health()
    files = sorted(glob.glob(os.path.join(directory, "policy-*.jsonl")))
    if not files:
        sys.exit(f"no policy-*.jsonl telemetry files in {directory}")
    bad = other = 0
    for path in files:
        health.note_file(path)
        with open(path, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    bad += 1  # a line cut by a crash mid-write
                    continue
                if record.get("k") == "health":
                    health.add_record(record, path)
                    continue
                if record.get("k", "goal") != "goal":
                    other += 1  # a combat record (fight, skip): another decision's data
                    continue
                record = upgrade_record(record)
                if record is None or len(record.get("f", [])) != len(FEATURES):
                    bad += 1
                    continue
                rows.append(record)
    print(f"read {len(rows)} goal records from {len(files)} files ({other} combat records left out, {bad} unusable lines)", file=sys.stderr)
    return rows, health


def drop_overrun_decisions(rows: list[dict], health, args) -> list[dict]:
    """The rows whose [t, t + w] did not overlap a tick-overrun window (all of them with --no-health-filter)."""
    if args.no_health_filter:
        print("health filter off (--no-health-filter): every decision kept", file=sys.stderr)
        return rows
    health_filter.read_tick_logs(health, args.tick_log)
    gate = health.build(args.max_overruns, args.window_slack_s, args.tick_log_tail_s)
    tally = health_filter.Tally()
    kept = []
    for record in rows:
        t = record.get("t")
        if isinstance(t, int) and not isinstance(t, bool):
            verdict = gate.classify(t, record.get("w", 0))
        else:
            verdict = "unverified"  # no usable decision time: it cannot be checked, so it is kept and counted
        tally.add(record.get("goal") or "(none)", verdict)
        if verdict != "drop":
            kept.append(record)
    health_filter.describe(health, tally, args.max_overruns)
    return kept


def label(record: dict, args) -> float:
    o = record["o"]
    minutes = max(record["w"], 1) / 60.0
    progress = (o["lp"] + args.quest_weight * o["quests"] - args.stall_weight * o["stalls"]
                - args.death_weight * o["deaths"] - args.anomaly_weight * o["anomalies"])
    return progress / minutes


def dataset(rows: list[dict], args):
    x, goal, y, explored = [], [], [], []
    for record in rows:
        if record.get("goal") not in GOALS:
            continue
        x.append(record["f"])
        goal.append(GOALS.index(record["goal"]))
        y.append(label(record, args))
        explored.append(record.get("x", 0) == 1)
    if not x:
        sys.exit("no record has a policy goal: nothing to train on")
    return (np.asarray(x, dtype=np.float64), np.asarray(goal), np.asarray(y, dtype=np.float64),
            np.asarray(explored, dtype=bool))


def split(n: int, holdout: float, seed: int):
    order = list(range(n))
    random.Random(seed).shuffle(order)
    cut = int(n * (1 - holdout))
    return np.asarray(order[:cut], dtype=int), np.asarray(order[cut:], dtype=int)


def fit_linear(x, goal, y, ridge: float):
    g, f = len(GOALS), x.shape[1]
    weights = np.zeros((g, f))
    bias = np.zeros(g)
    overall = float(np.mean(y)) if len(y) else 0.0
    for k in range(g):
        mask = goal == k
        if mask.sum() < 2:
            # No evidence for this goal (only with --min-goal-rows below 2): the mean. That is no neutral score: it beats every trained
            # goal predicted below the mean, so such a model promotes the goal it knows nothing about (main refuses this by default).
            bias[k] = overall
            continue
        xk = np.hstack([x[mask], np.ones((mask.sum(), 1))])
        penalty = ridge * np.eye(f + 1)
        penalty[f, f] = 0.0  # the intercept is not shrunk
        solution = np.linalg.solve(xk.T @ xk + penalty, xk.T @ y[mask])
        weights[k], bias[k] = solution[:f], solution[f]
    return [{"inputs": f, "outputs": g, "activation": "linear",
             "weights": weights.reshape(-1).tolist(), "bias": bias.tolist()}]


def fit_mlp(x, goal, y, hidden: int, epochs: int, lr: float, l2: float, seed: int):
    rng = np.random.default_rng(seed)
    n, f = x.shape
    g = len(GOALS)
    w1 = rng.normal(0, math.sqrt(2.0 / f), (hidden, f))
    b1 = np.zeros(hidden)
    w2 = rng.normal(0, math.sqrt(1.0 / hidden), (g, hidden))
    b2 = np.full(g, float(np.mean(y)))
    params = [w1, b1, w2, b2]
    m = [np.zeros_like(p) for p in params]
    v = [np.zeros_like(p) for p in params]
    step = 0
    batch = min(256, n)
    for _ in range(epochs):
        order = rng.permutation(n)
        for start in range(0, n, batch):
            idx = order[start:start + batch]
            xb, gb, yb = x[idx], goal[idx], y[idx]
            h_pre = xb @ w1.T + b1
            h = np.maximum(h_pre, 0)
            out = h @ w2.T + b2
            taken = out[np.arange(len(idx)), gb]
            err = (taken - yb) / len(idx)
            d_out = np.zeros_like(out)
            d_out[np.arange(len(idx)), gb] = err
            gw2 = d_out.T @ h + l2 * w2
            gb2 = d_out.sum(axis=0)
            d_h = (d_out @ w2) * (h_pre > 0)
            gw1 = d_h.T @ xb + l2 * w1
            gb1 = d_h.sum(axis=0)
            step += 1
            for i, grad in enumerate([gw1, gb1, gw2, gb2]):
                m[i] = 0.9 * m[i] + 0.1 * grad
                v[i] = 0.999 * v[i] + 0.001 * grad * grad
                m_hat = m[i] / (1 - 0.9 ** step)
                v_hat = v[i] / (1 - 0.999 ** step)
                params[i] -= lr * m_hat / (np.sqrt(v_hat) + 1e-8)
    return [{"inputs": f, "outputs": hidden, "activation": "relu", "weights": w1.reshape(-1).tolist(), "bias": b1.tolist()},
            {"inputs": hidden, "outputs": g, "activation": "linear", "weights": w2.reshape(-1).tolist(), "bias": b2.tolist()}]


def predict(layers, x):
    a = x
    for layer in layers:
        w = np.asarray(layer["weights"]).reshape(layer["outputs"], layer["inputs"])
        a = a @ w.T + np.asarray(layer["bias"])
        if layer["activation"] == "relu":
            a = np.maximum(a, 0)
    return a


def report(name, layers, x, goal, y):
    if len(y) == 0:
        return
    taken = predict(layers, x)[np.arange(len(y)), goal]
    mse = float(np.mean((taken - y) ** 2))
    var = float(np.var(y))
    r2 = 1 - mse / var if var > 0 else float("nan")
    print(f"{name}: {len(y)} rows, mse {mse:.5f}, r2 {r2:.3f}", file=sys.stderr)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("telemetry", help="the telemetry folder (World:Playerbots:Policy:Telemetry:Directory)")
    parser.add_argument("-o", "--output", required=True, help="the weights file to write (World:Playerbots:Policy:ModelPath)")
    parser.add_argument("--model", choices=["linear", "mlp"], default="linear")
    parser.add_argument("--hidden", type=int, default=32, help="mlp hidden units (1..256)")
    parser.add_argument("--epochs", type=int, default=30)
    parser.add_argument("--lr", type=float, default=0.003)
    parser.add_argument("--ridge", type=float, default=1.0, help="linear L2 penalty (on standardised features)")
    parser.add_argument("--l2", type=float, default=1e-4, help="mlp weight decay")
    parser.add_argument("--quest-weight", type=float, default=0.25, help="progress per quest handed in, in levels")
    parser.add_argument("--stall-weight", type=float, default=0.05)
    parser.add_argument("--death-weight", type=float, default=0.10)
    parser.add_argument("--anomaly-weight", type=float, default=0.02)
    parser.add_argument("--holdout", type=float, default=0.2)
    parser.add_argument("--min-rows", type=int, default=1000, help="refuse to write a model trained on fewer rows")
    parser.add_argument("--min-goal-rows", type=int, default=50,
                        help="refuse to write a model with a goal trained on fewer rows (0 allows it: such a goal's score is a guess)")
    parser.add_argument("--seed", type=int, default=7)
    health_filter.add_arguments(parser)
    args = parser.parse_args(argv)
    if not 1 <= args.hidden <= 256:
        parser.error("--hidden must be 1..256")

    rows, health = load(args.telemetry)
    x, goal, y, explored = dataset(drop_overrun_decisions(rows, health, args), args)
    counts = {GOALS[k]: int((goal == k).sum()) for k in range(len(GOALS))}
    print(f"rows per goal {counts}; explored {int(explored.sum())}", file=sys.stderr)
    if len(y) < args.min_rows:
        print(f"only {len(y)} usable rows (< --min-rows {args.min_rows}): no model written", file=sys.stderr)
        return 2
    # Every goal is scored for every state where it is open, so a goal the data never shows gets a score from nothing, and an untrained
    # score wins as often as not (the live telemetry of 2026-10-16: the untrained QuestTravel was best in 6754 of 7605 states where open).
    thin = {name: count for name, count in counts.items() if count < args.min_goal_rows}
    if thin:
        print(f"goals with fewer than --min-goal-rows {args.min_goal_rows} rows: {thin}: no model written "
              "(collect until every goal has rows; docs/design/playerbot-policy-phases.md, Phase 3 gate 4)", file=sys.stderr)
        return 4

    mean = x.mean(axis=0)
    scale = 1.0 / np.where(x.std(axis=0) > 1e-6, x.std(axis=0), 1.0)
    z = (x - mean) * scale
    train, test = split(len(y), args.holdout, args.seed)
    if args.model == "linear":
        layers = fit_linear(z[train], goal[train], y[train], args.ridge)
    else:
        # Labels are small (levels per minute): train on standardised labels, then fold the scale into the output layer.
        y_mean, y_std = float(np.mean(y[train])), float(np.std(y[train])) or 1.0
        layers = fit_mlp(z[train], goal[train], (y[train] - y_mean) / y_std, args.hidden, args.epochs, args.lr, args.l2, args.seed)
        out = layers[-1]
        out["weights"] = [w * y_std for w in out["weights"]]
        out["bias"] = [b * y_std + y_mean for b in out["bias"]]
    report("train", layers, z[train], goal[train], y[train])
    report("holdout", layers, z[test], goal[test], y[test])

    model = {"format": FORMAT, "version": VERSION, "features": FEATURES, "goals": GOALS,
             "mean": mean.tolist(), "scale": scale.tolist(), "layers": layers,
             "trained": {"rows": int(len(y)), "model": args.model, "label": "levels-equivalent progress per minute"}}
    for layer in layers:
        if not all(math.isfinite(v) for v in layer["weights"] + layer["bias"]):
            print("the fit produced a non-finite weight: no model written", file=sys.stderr)
            return 3
    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump(model, handle, separators=(",", ":"))
    print(f"wrote {args.output} ({os.path.getsize(args.output)} bytes)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
