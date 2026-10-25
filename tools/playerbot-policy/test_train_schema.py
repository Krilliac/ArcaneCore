"""Tests for train.py's record and goal schema (the server's goal list and record version).

Run from outside this folder, isolated:  python -I tools/playerbot-policy/test_train_schema.py
(needs numpy, like train.py). Synthetic files test the schema handling only: never train a server model on them.
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)


def _import(name: str):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


train = _import("train")

T0 = 1_760_000_000_000


def goal_record(version: int, goal: str, open_bits: int) -> dict:
    return {"v": version, "k": "goal", "t": T0, "bot": "A1", "map": 0, "zone": 12, "f": [0.5] * len(train.FEATURES), "open": open_bits,
            "ladder": "Grind", "pick": goal, "src": "agreed", "x": 0, "goal": goal, "kind": "Grind", "w": 300,
            "o": {"lvl": 0, "lp": 0.01, "xp": 10, "money": 0, "items": 0, "quests": 0, "stalls": 0, "deaths": 0, "anomalies": 0}}


def load_lines(records: list[dict]) -> list[dict]:
    with tempfile.TemporaryDirectory() as directory:
        with open(os.path.join(directory, "policy-00000001.jsonl"), "w", encoding="utf-8") as handle:
            for record in records:
                handle.write(json.dumps(record) + "\n")
        with contextlib.redirect_stderr(io.StringIO()):
            rows, _ = train.load(directory)
    return rows


class GoalListTests(unittest.TestCase):
    def test_the_goal_list_is_the_servers_with_opportunity_between_town_and_train(self):
        self.assertEqual(["Quest", "Town", "Opportunity", "Train", "Mount", "QuestTravel", "Grind", "Explore"], train.GOALS)

    def test_the_trainer_reads_the_server_record_version(self):
        self.assertEqual(3, train.RECORD_VERSION)

    def test_the_weights_file_names_every_goal_in_order(self):
        # The server refuses a model whose goal names differ (RequireNames): the model train.py writes lists GOALS as they are.
        self.assertEqual(len(train.GOALS), len(set(train.GOALS)))
        self.assertEqual(8, len(train.GOALS))


class RemapTests(unittest.TestCase):
    def test_version_1_open_bits_move_to_the_version_2_layout(self):
        quest, town, train_bit, explore = 1 << 0, 1 << 1, 1 << 2, 1 << 6  # version 1: Quest, Town, Train ... Explore
        self.assertEqual(1 << 0, train.remap_open_v1(quest))
        self.assertEqual(1 << 1, train.remap_open_v1(town))
        self.assertEqual(1 << 3, train.remap_open_v1(train_bit))  # Train moved from bit 2 to 3
        self.assertEqual(1 << 7, train.remap_open_v1(explore))  # Explore moved from bit 6 to 7
        self.assertEqual((1 << 0) | (1 << 3) | (1 << 7), train.remap_open_v1(quest | train_bit | explore))
        self.assertEqual(0, train.remap_open_v1(0))

    def test_no_version_1_bit_lands_on_the_opportunity_bit(self):
        for bits in range(1 << 7):
            self.assertEqual(0, train.remap_open_v1(bits) & (1 << train.GOALS.index("Opportunity")))

    def test_load_keeps_version_1_records_and_remaps_their_open_bits(self):
        rows = load_lines([goal_record(1, "Grind", (1 << 5) | (1 << 6)), goal_record(2, "Opportunity", (1 << 2) | (1 << 7))])
        self.assertEqual(2, len(rows))
        v1 = next(row for row in rows if row["goal"] == "Grind")
        self.assertEqual(3, v1["v"])
        self.assertEqual((1 << 6) | (1 << 7), v1["open"])  # Grind 5 -> 6, Explore 6 -> 7
        v2 = next(row for row in rows if row["goal"] == "Opportunity")
        self.assertEqual((1 << 2) | (1 << 7), v2["open"])  # a version 2 record keeps its bits
        self.assertEqual(3, v2["v"])  # and is labelled with the current version, like version 1

    def test_upgrade_record_labels_version_2_as_current_and_leaves_the_input_alone(self):
        original = goal_record(2, "Grind", 1 << 6)
        upgraded = train.upgrade_record(original)
        self.assertEqual(train.RECORD_VERSION, upgraded["v"])
        self.assertEqual(1 << 6, upgraded["open"])
        self.assertEqual(2, original["v"])

    def test_load_refuses_a_version_it_does_not_know(self):
        rows = load_lines([goal_record(4, "Grind", 1), goal_record(2, "Grind", 1), goal_record(3, "Grind", 1)])
        self.assertEqual(2, len(rows))

    def test_a_version_2_opportunity_record_is_a_training_row(self):
        class Args:
            quest_weight, stall_weight, death_weight, anomaly_weight = 0.25, 0.05, 0.10, 0.02

        x, goal, y, _ = train.dataset(load_lines([goal_record(2, "Opportunity", 4)]), Args)
        self.assertEqual(1, len(y))
        self.assertEqual(train.GOALS.index("Opportunity"), int(goal[0]))

    def test_combat_and_opportunity_records_are_left_out_of_the_goal_rows(self):
        burst_fight = {"v": 3, "k": "fight", "t": T0, "retreat": {"retreat": 1, "f": [0.5] * 8, "reason": "losing-burst-2"}}
        rows = load_lines([{"v": 2, "k": "opportunity", "t": T0}, {"v": 2, "k": "fight", "t": T0}, burst_fight, goal_record(2, "Grind", 1)])
        self.assertEqual(1, len(rows))


class ThinGoalTests(unittest.TestCase):
    """A goal with no rows got the overall mean as its score, which beat the trained goals in most states: on the live telemetry of
    2026-10-16 the untrained QuestTravel was the model's best open goal in 6754 of the 7605 decisions where it was open."""

    def run_main(self, records: list[dict], *extra: str) -> tuple[int, bool]:
        with tempfile.TemporaryDirectory() as directory:
            with open(os.path.join(directory, "policy-00000001.jsonl"), "w", encoding="utf-8") as handle:
                for i, record in enumerate(records):
                    record = dict(record, t=T0 + i * 1000, f=[(i % 7) / 7.0] + [0.5] * (len(train.FEATURES) - 1))
                    handle.write(json.dumps(record) + "\n")
            out = os.path.join(directory, "model.json")
            with contextlib.redirect_stderr(io.StringIO()):
                code = train.main([directory, "-o", out, "--min-rows", "10", "--no-health-filter", *extra])
            return code, os.path.exists(out)

    def every_goal(self, per_goal: int, skip: str | None = None) -> list[dict]:
        return [goal_record(2, goal, 1 << train.GOALS.index(goal)) for goal in train.GOALS if goal != skip for _ in range(per_goal)]

    def test_a_goal_without_rows_writes_no_model(self):
        code, written = self.run_main(self.every_goal(60, skip="QuestTravel"))
        self.assertNotEqual(0, code)
        self.assertFalse(written)

    def test_a_goal_with_too_few_rows_writes_no_model(self):
        code, written = self.run_main(self.every_goal(60, skip="Mount") + [goal_record(2, "Mount", 1 << 4)] * 3)
        self.assertNotEqual(0, code)
        self.assertFalse(written)

    def test_every_goal_with_rows_writes_a_model(self):
        code, written = self.run_main(self.every_goal(60))
        self.assertEqual(0, code)
        self.assertTrue(written)

    def test_the_bound_can_be_lowered_on_purpose(self):
        code, written = self.run_main(self.every_goal(60, skip="QuestTravel"), "--min-goal-rows", "0")
        self.assertEqual(0, code)
        self.assertTrue(written)


if __name__ == "__main__":
    unittest.main()
