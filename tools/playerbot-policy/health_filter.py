"""Drop decisions recorded while the world tick was overrunning (shared by train.py and ab_report.py).

The server writes a "k":"health" record about every 10 s next to the goal records
(docs/areas/playbots.md, Tick health):

    {"v":1,"k":"health","t":<unix ms at the interval's end>,"ms":<interval length>,"over":<frames over the tick budget>,"budget":<ms>}

so the interval it describes is [t - ms, t]. A goal record's decision ran over [t, t + w*1000] (t unix ms of the
decision, w the outcome window in seconds). A decision overlapping a health interval with more than --max-overruns
overruns happened in, or was measured over, a lagging world, and is dropped.

A decision whose window is not entirely inside health coverage (a file from a build that writes no health records,
the start of a run, a restart gap) cannot be checked: it is kept and counted as unverified, with a warning.

--tick-log adds overrun windows from the world's JSON-lines log (logs/*.jsonl): each "TickOverrun" warning (event
7101) says "overran its N ms budget K time(s) in the last S s", so [ts - S s, ts] held K overruns (the first warning
after a start has no "in the last" part: 30 s, the default WarnIntervalSeconds, is assumed). Those windows are coarser
than the health records (the warning is rate limited) and only ever add drops. They also count the log's own time span
as covered: a stretch the log spans without a warning had none.
"""

from __future__ import annotations

import bisect
import json
import re
import sys
from collections import defaultdict
from datetime import datetime, timezone

HEALTH_VERSION = 1
# Health intervals are contiguous by construction; a few ms of rounding must not read as a gap.
GAP_SLACK_MS = 1000
DEFAULT_LOG_WINDOW_S = 30

_TS = re.compile(r'^\{"ts":"([^"]+)"')
_OVERRUN = re.compile(r"overran its \d+ ms budget (\d+) time\(s\)(?: in the last (\d+) s)?")


def add_arguments(parser) -> None:
    parser.add_argument("--max-overruns", type=int, default=0,
                        help="drop a decision whose window overlaps a health interval with MORE than this many tick overruns (default 0)")
    parser.add_argument("--no-health-filter", action="store_true",
                        help="keep every decision, whatever the tick was doing")
    parser.add_argument("--tick-log", action="append", default=[], metavar="WORLD.jsonl",
                        help="also derive overrun windows from this world JSON-lines log (repeatable); needed for telemetry from builds without health records")


def _unix_ms(text: str) -> int | None:
    try:
        stamp = datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError:
        return None
    if stamp.tzinfo is None:
        stamp = stamp.replace(tzinfo=timezone.utc)  # the server logs UTC ("Z") or an explicit offset
    return int(stamp.timestamp() * 1000)


def _merge(spans: list[tuple[int, int]], slack: int = 0) -> list[tuple[int, int]]:
    merged: list[tuple[int, int]] = []
    for start, end in sorted(spans):
        if merged and start <= merged[-1][1] + slack:
            if end > merged[-1][1]:
                merged[-1] = (merged[-1][0], end)
        else:
            merged.append((start, end))
    return merged


class Health:
    """The health intervals and tick-log windows collected while reading the telemetry."""

    def __init__(self) -> None:
        self.intervals: list[tuple[int, int, int]] = []  # (start ms, end ms, overruns)
        self.log_spans: list[tuple[int, int]] = []
        self.files: set[str] = set()
        self.files_with_health: set[str] = set()
        self.bad = 0

    def note_file(self, path: str) -> None:
        self.files.add(path)

    def add_record(self, record: dict, path: str) -> None:
        """A "k":"health" record; counts it as unusable when malformed."""
        t, ms, over = record.get("t"), record.get("ms"), record.get("over")
        if record.get("v") != HEALTH_VERSION or not all(isinstance(v, int) and not isinstance(v, bool) for v in (t, ms, over)) or ms < 0 or over < 0:
            self.bad += 1
            return
        self.files_with_health.add(path)
        self.intervals.append((t - ms, t, over))

    def add_tick_log(self, path: str) -> int:
        """Read overrun windows from a world JSON-lines log; returns how many it found."""
        found = 0
        first = last = None
        with open(path, encoding="utf-8", errors="replace") as handle:
            for line in handle:
                m = _TS.match(line)
                if not m:
                    continue
                if '"TickOverrun"' not in line:
                    # Only the span of the log is needed from the other lines: the first and the last stamp.
                    if first is None:
                        first = m.group(1)
                    last = m.group(1)
                    continue
                if first is None:
                    first = m.group(1)
                last = m.group(1)
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if record.get("eventName") != "TickOverrun" and record.get("eventId") != 7101:
                    continue
                end = _unix_ms(record.get("ts", ""))
                found_match = _OVERRUN.search(record.get("message", ""))
                if end is None or found_match is None:
                    self.bad += 1
                    continue
                window_s = int(found_match.group(2)) if found_match.group(2) else DEFAULT_LOG_WINDOW_S
                self.intervals.append((end - window_s * 1000, end, int(found_match.group(1))))
                found += 1
        if first is not None and last is not None:
            a, b = _unix_ms(first), _unix_ms(last)
            if a is not None and b is not None and b >= a:
                self.log_spans.append((a, b))
        return found

    def build(self, max_overruns: int) -> "Filter":
        bad = _merge([(s, e) for s, e, over in self.intervals if over > max_overruns])
        covered = _merge([(s, e) for s, e, _ in self.intervals if e > s] + self.log_spans, GAP_SLACK_MS)
        return Filter(bad, covered)

    def files_without_health(self) -> list[str]:
        return sorted(self.files - self.files_with_health)


class Filter:
    """Disjoint sorted spans: where the tick overran, and where health is known at all."""

    def __init__(self, bad: list[tuple[int, int]], covered: list[tuple[int, int]]) -> None:
        self._bad = bad
        self._bad_ends = [e for _, e in bad]
        self._covered = covered
        self._cov_ends = [e for _, e in covered]

    def overruns_in(self, start: int, end: int) -> bool:
        i = bisect.bisect_right(self._bad_ends, start)  # first span ending after the start
        return i < len(self._bad) and self._bad[i][0] < end

    def covers(self, start: int, end: int) -> bool:
        i = bisect.bisect_left(self._cov_ends, end)  # first span reaching the end
        return i < len(self._covered) and self._covered[i][0] <= start

    def classify(self, t_ms: int, window_s: float) -> str:
        """"drop" (overlaps an overrun), "unverified" (not entirely inside health coverage) or "keep"."""
        end = t_ms + int(max(window_s, 0) * 1000)
        if self.overruns_in(t_ms, end):
            return "drop"
        return "keep" if self.covers(t_ms, end) else "unverified"


class Tally:
    """Per-goal counts of what the filter did."""

    def __init__(self) -> None:
        self.counts: dict[str, dict[str, int]] = defaultdict(lambda: {"keep": 0, "drop": 0, "unverified": 0})

    def add(self, goal: str, verdict: str) -> None:
        self.counts[goal][verdict] += 1

    def total(self, verdict: str) -> int:
        return sum(c[verdict] for c in self.counts.values())


def read_tick_logs(health: Health, paths: list[str]) -> None:
    for path in paths:
        try:
            found = health.add_tick_log(path)
        except OSError as error:
            sys.exit(f"cannot read --tick-log {path}: {error}")
        print(f"tick log {path}: {found} overrun warnings", file=sys.stderr)


def describe(health: Health, tally: Tally, max_overruns: int, out=None) -> None:
    """Print what the filter dropped per goal, and a warning wherever it could not apply."""
    out = out or sys.stderr
    dropped = tally.total("drop")
    print(f"health filter (tick overruns > {max_overruns} in the window): dropped {dropped} decisions, "
          f"kept {tally.total('keep')} verified, {tally.total('unverified')} unverified", file=out)
    for goal in sorted(tally.counts):
        c = tally.counts[goal]
        print(f"  {goal}: dropped {c['drop']} of {sum(c.values())}", file=out)
    if not health.intervals:
        print("WARNING: no health records and no --tick-log windows: the overrun filter could NOT apply; "
              "every decision was kept, including any made while the world lagged "
              "(the server build writes no \"k\":\"health\" records; pass --tick-log WORLD.jsonl, or --no-health-filter to silence this)", file=out)
        return
    missing = health.files_without_health()
    if missing:
        names = ", ".join(missing[:5]) + (f" and {len(missing) - 5} more" if len(missing) > 5 else "")
        print(f"WARNING: {len(missing)} telemetry file(s) hold no health records (older build?): {names}", file=out)
    if tally.total("unverified"):
        print(f"WARNING: {tally.total('unverified')} decision(s) have a window not entirely covered by health records "
              f"(start of the run, a restart, or an older build); they were kept unfiltered", file=out)
    if health.bad:
        print(f"WARNING: {health.bad} unusable health/tick-log lines ignored", file=out)
