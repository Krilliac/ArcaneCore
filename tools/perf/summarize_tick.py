#!/usr/bin/env python3
"""Summarize WorldTickLoadTests CSVs; timing is evidence, never a CI pass threshold.
Usage: python tools/perf/summarize_tick.py before.csv after.csv [--warmup 100]
"""
import argparse
import csv
import json
import math
import statistics


def summarize(path, warmup):
    with open(path, encoding="utf-8-sig", newline="") as stream:
        rows = [{key: int(value) for key, value in row.items()} for row in csv.DictReader(stream)]
    measured = [row for row in rows if row["tick"] >= warmup]
    if not measured:
        raise ValueError(f"{path}: no measured ticks after warmup {warmup}")
    phases = {}
    for key in measured[0]:
        if key.endswith("_us") or key == "bytes":
            values = sorted(row[key] for row in measured)
            phases[key] = {"mean": round(statistics.mean(values), 2),
                           "p95": values[math.ceil(.95 * len(values)) - 1],
                           "p99": values[math.ceil(.99 * len(values)) - 1], "max": values[-1]}
    return {"file": str(path), "samples": len(measured), "warmup": warmup,
            "phases": phases, "collections": {key: sum(row[key] for row in measured) for key in ("gen0", "gen1", "gen2")},
            "cold_first_tick": rows[0], "slowest_ticks": sorted(measured, key=lambda row: row["tick_us"], reverse=True)[:5]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("csv", nargs="+")
    parser.add_argument("--warmup", type=int, default=100)
    args = parser.parse_args()
    for path in args.csv:
        print(json.dumps(summarize(path, args.warmup)))


if __name__ == "__main__":
    main()
