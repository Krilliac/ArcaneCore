"""Checks for tools/ops/deploy_log_lines.py. Run: python -m unittest discover -s tools/ops -p "test_deploy_log_lines.py" """
import unittest

import deploy_log_lines

# Lines in the exact shape LogLineFormatter.Write produces (plain, then colour mode).
SAMPLE = (
    "2026-11-05 10:00:00.001 INFO ArcaneCore.World.Startup: loaded 3 maps, 9 areas\n"
    "2026-11-05 10:00:00.002 WARN ArcaneCore.Maps.Terrain: unknown target map 77\n"
    "2026-11-05 10:00:00.003 FAIL ArcaneCore.Data.Loader[DbError:12]: query failed, FAIL in message text\n"
    "      System.Exception: boom CRIT inside a stack line\n"
    "2026-11-05 10:00:00.004 CRIT ArcaneCore.World.Host: fatal\n"
    "2026-11-05 10:00:00.005 FAIL ArcaneCore.Data.Loader: again\n"
    "\x1b[2m2026-11-05 10:00:00.006\x1b[0m \x1b[31mFAIL\x1b[0m \x1b[2mcat:\x1b[0m coloured\n"
)


class LogCountsTests(unittest.TestCase):
    def test_counts_error_and_critical_levels(self):
        c = deploy_log_lines.log_counts(SAMPLE)
        self.assertEqual(c[" FAIL "], 3)
        self.assertEqual(c[" CRIT "], 1)
        self.assertEqual(c[" WARN "], 1)
        self.assertEqual(c["unknown target map"], 1)
        self.assertEqual(c["not existing zone id"], 0)

    def test_old_level_words_are_not_reported(self):
        c = deploy_log_lines.log_counts("2026-11-05 10:00:00.001 ERROR x: y\n")
        self.assertNotIn(" ERROR ", c)
        self.assertEqual(c[" FAIL "], 0)

    def test_empty_log(self):
        self.assertEqual(sum(deploy_log_lines.log_counts("").values()), 0)


if __name__ == "__main__":
    unittest.main()
