"""Tests for the tick-overrun filter (health_filter.py) and its use in train.py and ab_report.py.

Run from outside this folder, isolated:  python -I tools/playerbot-policy/test_health_filter.py
(needs numpy, like train.py). Synthetic files test the filter only: never train a server model on them.
"""

from __future__ import annotations

import argparse
import contextlib
import importlib.util
import io
import json
import os
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))


def _import(name: str):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


import sys  # noqa: E402

sys.path.insert(0, HERE)
health_filter = _import("health_filter")
train = _import("train")
ab_report = _import("ab_report")

T0 = 1_760_000_000_000  # unix ms
FEATURE_COUNT = len(train.FEATURES)


def health(end_s: float, over: int, ms: int = 10_000) -> dict:
    return {"v": 1, "k": "health", "t": int(T0 + end_s * 1000), "ms": ms, "over": over, "budget": 76}


def goal(at_s: float, name: str = "Grind", w: int = 300, arm: str | None = None, bot: str = "A1") -> dict:
    record = {"v": 1, "k": "goal", "t": int(T0 + at_s * 1000), "bot": bot, "map": 0, "zone": 12, "f": [0.5] * FEATURE_COUNT,
              "open": 3, "ladder": "Grind", "pick": name, "src": "agreed", "x": 0, "goal": name, "kind": "Grind", "w": w,
              "o": {"lvl": 0, "lp": 0.01, "xp": 10, "money": 0, "items": 0, "quests": 0, "stalls": 0, "deaths": 0, "anomalies": 0}}
    if arm:
        record["arm"] = arm
    return record


def health_run(start_s: float, end_s: float, overrun_at: dict[float, int] | None = None) -> list[dict]:
    """Contiguous 10 s health intervals covering [start_s, end_s]; overrun_at maps an interval's END second to its overruns."""
    overrun_at = overrun_at or {}
    return [health(end, overrun_at.get(end, 0)) for end in range(int(start_s) + 10, int(end_s) + 1, 10)]


def write(directory: str, name: str, records: list[dict]) -> str:
    path = os.path.join(directory, name)
    with open(path, "w", encoding="utf-8") as handle:
        for record in records:
            handle.write(json.dumps(record) + "\n")
    return path


def args(**overrides) -> argparse.Namespace:
    values = {"max_overruns": 0, "no_health_filter": False, "tick_log": [], "window_slack_s": 10.0, "tick_log_tail_s": 30.0}
    values.update(overrides)
    return argparse.Namespace(**values)


def quiet(function, *a, **k):
    err = io.StringIO()
    with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
        result = function(*a, **k)
    return result, err.getvalue()


class FilterTests(unittest.TestCase):
    def build(self, records, max_overruns=0, slack=0.0):
        h = health_filter.Health()
        for record in records:
            h.add_record(record, "f")
        return h.build(max_overruns, slack)

    def test_windows_before_during_and_after(self):
        gate = self.build(health_run(0, 1000, {500: 3}))
        self.assertEqual("keep", gate.classify(T0 + 100_000, 300))  # ends at 400 s
        self.assertEqual("drop", gate.classify(T0 + 200_000, 300))  # [200, 500] touches the overrun interval's end
        self.assertEqual("drop", gate.classify(T0 + 495_000, 300))  # starts inside it
        self.assertEqual("keep", gate.classify(T0 + 500_000, 300))  # starts where it ends: no overlap
        self.assertEqual("keep", gate.classify(T0 + 190_000, 300))  # ends at 490 s, where it begins: no overlap
        self.assertEqual("keep", gate.classify(T0 + 600_000, 300))

    def test_the_threshold_tolerates_up_to_that_many_overruns(self):
        records = health_run(0, 1000, {500: 3})
        self.assertEqual("drop", self.build(records, 2).classify(T0 + 450_000, 300))
        self.assertEqual("keep", self.build(records, 3).classify(T0 + 450_000, 300))

    def test_a_window_outside_health_coverage_is_unverified_not_dropped(self):
        gate = self.build(health_run(100, 1000))
        self.assertEqual("unverified", gate.classify(T0 + 50_000, 300))  # starts before the first interval
        self.assertEqual("unverified", gate.classify(T0 + 900_000, 300))  # runs past the last
        self.assertEqual("keep", gate.classify(T0 + 200_000, 300))

    def test_a_gap_between_health_runs_is_unverified(self):
        gate = self.build(health_run(0, 300) + health_run(600, 1000))
        self.assertEqual("unverified", gate.classify(T0 + 250_000, 300))
        self.assertEqual("keep", gate.classify(T0 + 650_000, 300))

    def test_health_records_of_version_1_2_and_3_are_accepted(self):
        h = health_filter.Health()
        for version in (1, 2, 3):
            h.add_record({"v": version, "k": "health", "t": T0 + version * 10_000, "ms": 10_000, "over": 3, "budget": 76}, "f")
        self.assertEqual(0, h.bad)
        self.assertEqual(3, len(h.intervals))

    def test_malformed_health_records_are_counted_and_ignored(self):
        h = health_filter.Health()
        for record in ({"v": 1, "k": "health", "t": "x", "ms": 1, "over": 0}, {"v": 4, "k": "health", "t": 1, "ms": 1, "over": 0},
                       {"v": 1, "k": "health", "t": 1, "ms": 1, "over": -1}):
            h.add_record(record, "f")
        self.assertEqual(3, h.bad)
        self.assertEqual([], h.intervals)

    def test_the_outcome_span_is_extended_by_the_slack_for_overlap_and_coverage(self):
        records = health_run(0, 1000, {500: 3})
        # [190, 490] ends where the overrun interval (490, 500] begins; the server may close the outcome up to 10 s late.
        self.assertEqual("keep", self.build(records).classify(T0 + 190_000, 300))
        self.assertEqual("drop", self.build(records, slack=10).classify(T0 + 190_000, 300))
        self.assertEqual("keep", self.build(records, slack=10).classify(T0 + 179_000, 300))
        # Coverage ends at 1000 s: [695, 995] is covered, [695, 1005] is not.
        self.assertEqual("keep", self.build(records).classify(T0 + 695_000, 300))
        self.assertEqual("unverified", self.build(records, slack=10).classify(T0 + 695_000, 300))
        parser = argparse.ArgumentParser()
        health_filter.add_arguments(parser)
        self.assertEqual(10.0, parser.parse_args([]).window_slack_s)
        self.assertEqual(30.0, parser.parse_args([]).tick_log_tail_s)

    def _tick_log(self, d, lines):
        path = os.path.join(d, "world.jsonl")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n")
        h = health_filter.Health()
        h.add_tick_log(path)
        return h

    def test_a_text_format_tick_log_gives_the_same_windows_as_the_json_one(self):
        warn = "2026-10-15 04:26:13.650 WARN ArcaneCore.Kernel.Ops.Watchdog.TickMonitor world tick overran its 100 ms budget 10 time(s) in the last 30 s: frame p50 61 ms"
        info = "2026-10-15 04:30:00.000 INFO hello"
        end = health_filter._unix_ms("2026-10-15T04:26:13.650Z")
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "world.log")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write(warn + "\n" + info + "\n")
            h = health_filter.Health()
            self.assertEqual(1, h.add_tick_log(path))
        self.assertIn((end - 31_000, end, 10), h.intervals)
        self.assertEqual(1, len(h.log_spans))

    def test_the_tail_of_a_tick_log_is_not_covered_and_warning_windows_are_widened(self):
        warn = ('{"ts":"2026-10-15T04:26:13.650Z","level":"Warning","eventId":7101,"eventName":"TickOverrun",'
                '"message":"world tick overran its 100 ms budget 10 time(s) in the last 30 s: frame p50 61 ms"}')
        info = '{"ts":"2026-10-15T04:30:00.000Z","level":"Information","message":"hello"}'
        end = health_filter._unix_ms("2026-10-15T04:26:13.650Z")
        last = health_filter._unix_ms("2026-10-15T04:30:00.000Z")
        with tempfile.TemporaryDirectory() as d:
            h = self._tick_log(d, [warn, info])
        self.assertIn((end - 31_000, end, 10), h.intervals)  # 30 s truncated from milliseconds: widened by 1 s
        trimmed = h.build(0, 0, 30)
        self.assertEqual("keep", trimmed.classify(last - 40_000, 0))
        self.assertEqual("unverified", trimmed.classify(last - 20_000, 0))  # inside the last 30 s: unreported overruns possible
        self.assertEqual("keep", h.build(0, 0, 0).classify(last - 20_000, 0))
        self.assertEqual("drop", trimmed.classify(end - 30_500, 0))  # in the widened part only

    def test_overlapping_health_from_different_files_is_reported_as_mixed(self):
        def run(records_by_file):
            h = health_filter.Health()
            for path, records in records_by_file.items():
                h.note_file(path)
                for record in records:
                    h.add_record(record, path)
            return h

        mixed = run({"a.jsonl": health_run(0, 300), "b.jsonl": health_run(100, 400)})
        self.assertEqual([("a.jsonl", "b.jsonl")], mixed.mixed_files())
        tally = health_filter.Tally()
        err = io.StringIO()
        health_filter.describe(mixed, tally, 0, err)
        self.assertIn("mixed in one directory", err.getvalue())
        self.assertIn("one server per directory", err.getvalue())
        rotated = run({"a.jsonl": health_run(0, 300), "b.jsonl": health_run(300, 600)})  # one server, file rotation
        self.assertEqual([], rotated.mixed_files())
        err = io.StringIO()
        health_filter.describe(rotated, tally, 0, err)
        self.assertNotIn("mixed", err.getvalue())

    def test_a_tick_log_adds_overrun_windows_from_real_log_lines(self):
        first = ('{"ts":"2026-10-15T04:25:43.364Z","level":"Warning","category":"ArcaneCore.Kernel.Ops.Watchdog.TickMonitor","eventId":7101,'
                 '"eventName":"TickOverrun","message":"world tick overran its 100 ms budget 32 time(s): frame p50 56 ms, p99 183 ms"}')
        later = ('{"ts":"2026-10-15T04:26:13.650Z","level":"Warning","category":"ArcaneCore.Kernel.Ops.Watchdog.TickMonitor","eventId":7101,'
                 '"eventName":"TickOverrun","message":"world tick overran its 100 ms budget 10 time(s) in the last 30 s: frame p50 61 ms"}')
        info = '{"ts":"2026-10-15T04:30:00.000Z","level":"Information","category":"x","message":"hello"}'
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "world.jsonl")
            with open(path, "w", encoding="utf-8") as handle:
                handle.write("\n".join([first, later, info]) + "\n")
            h = health_filter.Health()
            self.assertEqual(2, h.add_tick_log(path))
        end = health_filter._unix_ms("2026-10-15T04:26:13.650Z")
        self.assertIn((end - 31_000, end, 10), h.intervals)
        first_end = health_filter._unix_ms("2026-10-15T04:25:43.364Z")
        self.assertIn((first_end - 31_000, first_end, 32), h.intervals)  # no "in the last": the default 30 s
        gate = h.build(0, 0)
        self.assertEqual("drop", gate.classify(end - 10_000, 60))
        # The log spans 04:25:43 to 04:30:00; a quiet stretch inside it is verified clean, one past it is not.
        self.assertEqual("keep", gate.classify(end + 5_000, 60))
        self.assertEqual("unverified", gate.classify(end + 600_000, 60))


class TrainTests(unittest.TestCase):
    def load(self, d):
        return quiet(train.load, d)[0]

    def test_it_drops_decisions_whose_window_overlaps_overruns_and_counts_them_per_goal(self):
        records = health_run(0, 2000, {700: 5}) + [
            goal(100, "Grind"),  # [100, 400]: clean
            goal(500, "Grind"),  # [500, 800]: overlaps the overrun at (690, 700]
            goal(450, "Quest"),  # [450, 750]: overlaps
            goal(800, "Quest"),  # [800, 1100]: clean
            goal(1000, "Explore"),
        ]
        with tempfile.TemporaryDirectory() as d:
            write(d, "policy-00000001.jsonl", records)
            rows, h = self.load(d)
            self.assertEqual(5, len(rows))  # health records are not goal rows
            kept, err = quiet(train.drop_overrun_decisions, rows, h, args())
        self.assertEqual([100, 800, 1000], [(r["t"] - T0) // 1000 for r in kept])
        self.assertIn("dropped 2 decisions", err)
        self.assertIn("Grind: dropped 1 of 2", err)
        self.assertIn("Quest: dropped 1 of 2", err)
        self.assertIn("Explore: dropped 0 of 1", err)
        self.assertNotIn("WARNING", err)

    def test_a_goal_record_without_t_is_unverified_not_an_error(self):
        records = health_run(0, 2000, {700: 5}) + [goal(100)]
        no_t = goal(100)
        del no_t["t"]
        with tempfile.TemporaryDirectory() as d:
            write(d, "policy-00000001.jsonl", records + [no_t])
            rows, h = self.load(d)
            kept, err = quiet(train.drop_overrun_decisions, rows, h, args())
        self.assertEqual(2, len(kept))
        self.assertIn("1 unverified", err)

    def test_max_overruns_and_the_off_switch(self):
        records = health_run(0, 2000, {700: 5}) + [goal(500)]
        with tempfile.TemporaryDirectory() as d:
            write(d, "policy-00000001.jsonl", records)
            rows, h = self.load(d)
            self.assertEqual(1, len(quiet(train.drop_overrun_decisions, rows, h, args(max_overruns=5))[0]))
            self.assertEqual(1, len(quiet(train.drop_overrun_decisions, rows, h, args(no_health_filter=True))[0]))
            self.assertEqual(0, len(quiet(train.drop_overrun_decisions, rows, h, args(max_overruns=4))[0]))

    def test_files_with_no_health_records_keep_their_rows_and_warn(self):
        with tempfile.TemporaryDirectory() as d:
            write(d, "policy-00000001.jsonl", [goal(100), goal(500)])  # an older build: no health records at all
            rows, h = self.load(d)
            kept, err = quiet(train.drop_overrun_decisions, rows, h, args())
        self.assertEqual(2, len(kept))
        self.assertIn("could NOT apply", err)

    def test_a_file_without_health_among_files_with_it_is_named(self):
        with tempfile.TemporaryDirectory() as d:
            write(d, "policy-00000001.jsonl", [goal(100)])
            write(d, "policy-00000002.jsonl", health_run(0, 1000, {500: 1}) + [goal(450)])
            rows, h = self.load(d)
            kept, err = quiet(train.drop_overrun_decisions, rows, h, args())
        self.assertEqual([100], [(r["t"] - T0) // 1000 for r in kept])  # the older file's row is covered by the other file's health
        self.assertIn("policy-00000001.jsonl", err)
        self.assertIn("1 telemetry file(s) hold no health records", err)

    def test_main_trains_on_what_the_filter_leaves(self):
        rng_records = []
        for i in range(60):
            r = goal(100 + i, "Grind" if i % 2 else "Quest", bot=f"B{i}")
            r["f"] = [(i % 7) / 7.0] * FEATURE_COUNT
            rng_records.append(r)
        clean = health_run(0, 3000)
        with tempfile.TemporaryDirectory() as d:
            write(d, "policy-00000001.jsonl", clean + rng_records)
            out = os.path.join(d, "model.json")
            # Two goals only: this tests the filter, not the per-goal bound (test_train_schema.ThinGoalTests).
            code, err = quiet(train.main, [d, "-o", out, "--min-rows", "10", "--holdout", "0.2", "--min-goal-rows", "0"])
            self.assertEqual(0, code)
            self.assertIn("dropped 0 decisions", err)
            self.assertTrue(os.path.exists(out))
        with tempfile.TemporaryDirectory() as d:
            write(d, "policy-00000001.jsonl", health_run(0, 3000, {300: 9}) + rng_records)  # every window overlaps the overrun
            err = io.StringIO()
            with contextlib.redirect_stderr(err), self.assertRaises(SystemExit) as stop:
                train.main([d, "-o", os.path.join(d, "m.json"), "--min-rows", "10"])
            self.assertIn("dropped 60 decisions", err.getvalue())
            self.assertIn("nothing to train on", str(stop.exception))
            self.assertFalse(os.path.exists(os.path.join(d, "m.json")))


class AbReportTests(unittest.TestCase):
    def test_the_arms_leave_out_decisions_made_while_the_tick_overran(self):
        records = health_run(0, 2000, {700: 5}) + [
            goal(100, arm="policy", bot="P1"), goal(500, arm="policy", bot="P1"),
            goal(100, arm="ladder", bot="L1"), goal(450, arm="ladder", bot="L2"),
        ]
        with tempfile.TemporaryDirectory() as d:
            write(d, "policy-00000001.jsonl", records)
            (per_arm, skipped), err = quiet(ab_report.load, d, args())
            self.assertEqual(1, len(per_arm["policy"]["P1"]))
            self.assertEqual({"L1"}, set(per_arm["ladder"]))
            self.assertIn("dropped 2 decisions", err)
            self.assertIn("arm policy: dropped 1 of 2", err)
            self.assertIn("arm ladder: dropped 1 of 2", err)
            (per_arm, _), _ = quiet(ab_report.load, d, args(no_health_filter=True))
            self.assertEqual(2, len(per_arm["policy"]["P1"]))
            self.assertEqual({"L1", "L2"}, set(per_arm["ladder"]))


if __name__ == "__main__":
    unittest.main()
