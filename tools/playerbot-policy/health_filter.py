"""Drop decisions recorded while the world tick was overrunning (shared by train.py and ab_report.py).

The server writes a "k":"health" record about every 10 s next to the goal records
(docs/areas/playbots.md, Tick health; "v" is the server record version: 3 now, 2 before the burst retreat, 1 before the Opportunity goal, all read):

    {"v":3,"k":"health","t":<unix ms at the interval's end>,"ms":<interval length>,"over":<frames over the tick budget>,"budget":<ms>}

so the interval it describes is [t - ms, t]. A goal record's decision ran over [t, t + w*1000 + slack] (t unix ms of the
decision, w the outcome window in seconds). The server closes an outcome on the first think after the window has
elapsed (PlayerbotPolicyTelemetry), so under lag the real span is longer than w; --window-slack-s (default 10 s, one
health interval) extends the checked span, and the coverage test uses the same extended span. A decision overlapping a health interval with more than --max-overruns
overruns happened in, or was measured over, a lagging world, and is dropped.

A decision whose window is not entirely inside health coverage (a file from a build that writes no health records,
the start of a run, a restart gap) cannot be checked: it is kept and counted as unverified, with a warning.

--tick-log adds overrun windows from the world's log (logs/*.jsonl or logs/*.log; JSON lines or text, per Logging:ArcaneCore:Format): each "TickOverrun" warning (event
7101) says "overran its N ms budget K time(s) in the last S s", so [ts - S s, ts] held K overruns (the first warning
after a start has no "in the last" part: 30 s, the default WarnIntervalSeconds, is assumed). Those windows are coarser
than the health records (the warning is rate limited) and only ever add drops. They also count the log's own time span
as covered: a stretch the log spans without a warning had none, except the last --tick-log-tail-s (default 30, the
WarnIntervalSeconds) of the log, because an overrun there is reported only after the log ends. A warning's "in the last
S s" is truncated from milliseconds, so each warning window is widened by 1 s.

Coverage is global across files (one server's health and goal records are split across files), so a directory must hold
one server's run only; health intervals of different files that overlap in time are reported as mixed sources.
"""

from __future__ import annotations

import bisect
import json
import re
import sys
from collections import defaultdict
from datetime import datetime, timezone

# The server's record version covers every record kind; version 2 (the Opportunity goal changed the `open` bits) left the health record's
# keys as they were, and so did version 3 (the burst retreat reason and `round_damage` live in fight records).
HEALTH_VERSIONS = (1, 2, 3)
# Health intervals are contiguous by construction; a few ms of rounding must not read as a gap.
GAP_SLACK_MS = 1000
DEFAULT_LOG_WINDOW_S = 30
DEFAULT_WINDOW_SLACK_S = 10.0  # one health interval: the outcome closes on the first think after the window elapsed
DEFAULT_LOG_TAIL_S = 30.0  # WarnIntervalSeconds: overruns in the last stretch of a log are reported only after it ends
LOG_WINDOW_WIDEN_S = 1  # "in the last S s" is sinceMs / 1000 truncated

_TS = re.compile(r'^\{"ts":"([^"]+)"')
# The text format ("Logging:ArcaneCore:Format" = "Text"): "yyyy-MM-dd HH:mm:ss.fff LEVEL ...", UTC like the JSON "ts".
_TEXT_TS = re.compile(r"^(\d{4}-\d\d-\d\d) (\d\d:\d\d:\d\d\.\d{3}) (\w+) ")
_OVERRUN = re.compile(r"overran its \d+ ms budget (\d+) time\(s\)(?: in the last (\d+) s)?")


def add_arguments(parser) -> None:
    parser.add_argument("--max-overruns", type=int, default=0,
                        help="drop a decision whose window overlaps a health interval with MORE than this many tick overruns (default 0)")
    parser.add_argument("--no-health-filter", action="store_true",
                        help="keep every decision, whatever the tick was doing")
    parser.add_argument("--window-slack-s", type=float, default=DEFAULT_WINDOW_SLACK_S,
                        help="extend each decision's checked span past its outcome window by this many seconds, because the server closes an outcome on the first think after the window elapsed (default 10)")
    parser.add_argument("--tick-log-tail-s", type=float, default=DEFAULT_LOG_TAIL_S,
                        help="do not count the last this-many seconds of a --tick-log as covered (WarnIntervalSeconds; default 30)")
    parser.add_argument("--tick-log", action="append", default=[], metavar="WORLD_LOG",
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
        self.file_intervals: dict[str, list[tuple[int, int]]] = defaultdict(list)  # health records only, per telemetry file
        self.files: set[str] = set()
        self.files_with_health: set[str] = set()
        self.bad = 0

    def note_file(self, path: str) -> None:
        self.files.add(path)

    def add_record(self, record: dict, path: str) -> None:
        """A "k":"health" record; counts it as unusable when malformed."""
        t, ms, over = record.get("t"), record.get("ms"), record.get("over")
        if record.get("v") not in HEALTH_VERSIONS or not all(isinstance(v, int) and not isinstance(v, bool) for v in (t, ms, over)) or ms < 0 or over < 0:
            self.bad += 1
            return
        self.files_with_health.add(path)
        self.intervals.append((t - ms, t, over))
        self.file_intervals[path].append((t - ms, t))

    def add_tick_log(self, path: str) -> int:
        """Read overrun windows from a world log in either format (JSON lines or text, decided per line); returns how many it found.

        Rolled segments (name-yyyyMMdd-NNN.ext) and fallback names (stem.process.ext) are separate files: pass each one."""
        found = 0
        first = last = None
        with open(path, encoding="utf-8", errors="replace") as handle:
            for line in handle:
                text_match = None
                m = _TS.match(line)
                if not m:
                    text_match = _TEXT_TS.match(line)
                    if not text_match:
                        continue
                stamp = m.group(1) if m else f"{text_match.group(1)}T{text_match.group(2)}Z"
                if first is None:
                    first = stamp
                last = stamp
                if text_match is not None:
                    if text_match.group(3).upper() not in ("WARN", "WARNING"):
                        continue
                    found_match = _OVERRUN.search(line)
                    if found_match is None:
                        continue
                    end = _unix_ms(stamp)
                    message_ok = end is not None
                elif '"TickOverrun"' not in line:
                    continue
                else:
                    try:
                        record = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if record.get("eventName") != "TickOverrun" and record.get("eventId") != 7101:
                        continue
                    end = _unix_ms(record.get("ts", ""))
                    found_match = _OVERRUN.search(record.get("message", ""))
                    message_ok = end is not None and found_match is not None
                if not message_ok:
                    self.bad += 1
                    continue
                window_s = int(found_match.group(2)) if found_match.group(2) else DEFAULT_LOG_WINDOW_S
                self.intervals.append((end - (window_s + LOG_WINDOW_WIDEN_S) * 1000, end, int(found_match.group(1))))
                found += 1
        if first is not None and last is not None:
            a, b = _unix_ms(first), _unix_ms(last)
            if a is not None and b is not None and b >= a:
                self.log_spans.append((a, b))
        return found

    def build(self, max_overruns: int, window_slack_s: float = DEFAULT_WINDOW_SLACK_S,
              log_tail_s: float = DEFAULT_LOG_TAIL_S) -> "Filter":
        bad = _merge([(s, e) for s, e, over in self.intervals if over > max_overruns])
        tail = int(max(log_tail_s, 0) * 1000)
        log_spans = [(a, b - tail) for a, b in self.log_spans if b - tail > a]
        covered = _merge([(s, e) for s, e, _ in self.intervals if e > s] + log_spans, GAP_SLACK_MS)
        return Filter(bad, covered, window_slack_s)

    def mixed_files(self) -> list[tuple[str, str]]:
        """Pairs of telemetry files whose health intervals overlap in time (two servers or runs in one directory)."""
        spans = sorted((s, e, path) for path, ivs in self.file_intervals.items() for s, e in _merge(ivs))
        pairs: set[tuple[str, str]] = set()
        reach_end, reach_path = None, None
        for s, e, path in spans:
            if reach_end is not None and path != reach_path and s < reach_end - GAP_SLACK_MS:
                pairs.add(tuple(sorted((path, reach_path))))
            if reach_end is None or e > reach_end:
                reach_end, reach_path = e, path
        return sorted(pairs)

    def files_without_health(self) -> list[str]:
        return sorted(self.files - self.files_with_health)


class Filter:
    """Disjoint sorted spans: where the tick overran, and where health is known at all."""

    def __init__(self, bad: list[tuple[int, int]], covered: list[tuple[int, int]],
                 window_slack_s: float = DEFAULT_WINDOW_SLACK_S) -> None:
        self._slack_ms = int(max(window_slack_s, 0) * 1000)
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
        end = t_ms + int(max(window_s, 0) * 1000) + self._slack_ms
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
              "(the server build writes no \"k\":\"health\" records; pass --tick-log WORLD_LOG, or --no-health-filter to silence this)", file=out)
        return
    mixed = health.mixed_files()
    if mixed:
        names = "; ".join(f"{a} / {b}" for a, b in mixed[:3]) + (f" and {len(mixed) - 3} more pair(s)" if len(mixed) > 3 else "")
        print(f"WARNING: health intervals of different files overlap in time ({names}): two servers or runs are mixed in one "
              f"directory, so coverage and the overrun filter may be wrong; use one server per directory", file=out)
    missing = health.files_without_health()
    if missing:
        names = ", ".join(missing[:5]) + (f" and {len(missing) - 5} more" if len(missing) > 5 else "")
        print(f"WARNING: {len(missing)} telemetry file(s) hold no health records (older build?): {names}", file=out)
    if tally.total("unverified"):
        print(f"WARNING: {tally.total('unverified')} decision(s) have a window not entirely covered by health records "
              f"(start of the run, a restart, or an older build); they were kept unfiltered", file=out)
    if health.bad:
        print(f"WARNING: {health.bad} unusable health/tick-log lines ignored", file=out)
