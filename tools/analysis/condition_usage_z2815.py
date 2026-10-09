#!/usr/bin/env python3
"""Count ClassicDB condition rows and nonzero references without copying dump data.

Usage: py -3 tools/analysis/condition_usage_z2815.py D:/refs/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz
Only condition_id and quest_template.RequiredCondition name the conditions table.
combat_condition.*ConditionID and creature_spell_targeting.UnitCondition name other tables.
"""

import collections
import gzip
import json
import re
import sys


ROW = re.compile(r"\((\d+),(-?\d+),(\d+),(\d+),(\d+),(\d+),(\d+),")


def values(text):
    """Yield INSERT tuple fields; quoted SQL strings may contain commas and parentheses."""
    fields, field, quoted, escaped, in_row = [], [], False, False, False
    for ch in text:
        if escaped:
            field.append(ch)
            escaped = False
        elif quoted and ch == "\\":
            escaped = True
        elif ch == "'":
            quoted = not quoted
        elif not quoted and ch == "(":
            fields, field, in_row = [], [], True
        elif not quoted and ch in ",)":
            if in_row:
                fields.append("".join(field))
                field = []
            if ch == ")" and in_row:
                yield fields
                in_row = False
        elif in_row:
            field.append(ch)


def main(path):
    columns = {}
    rows = {}
    refs = collections.Counter()
    ids_by_column = collections.defaultdict(set)
    creating = None
    fields = []
    with gzip.open(path, "rt", encoding="utf-8", errors="replace") as stream:
        for line in stream:
            match = re.match(r"CREATE TABLE `([^`]+)`", line)
            if match:
                creating, fields = match[1], []
                continue
            if creating:
                match = re.match(r"  `([^`]+)`", line)
                if match:
                    fields.append(match[1])
                if line.startswith(") ENGINE"):
                    columns[creating] = fields
                    creating = None
                continue
            match = re.match(r"INSERT INTO `([^`]+)` VALUES ", line)
            if not match:
                continue
            table = match[1]
            if table == "conditions":
                for raw in ROW.findall(line):
                    entry, kind, value1, value2, value3, value4, flags = map(int, raw)
                    rows[entry] = (kind, value1, value2, value3, value4, flags)
            named = [(i, name) for i, name in enumerate(columns.get(table, []))
                     if name.lower() in ("condition_id", "requiredcondition")]
            if not named:
                continue
            for record in values(line[match.end():]):
                for i, name in named:
                    if i < len(record) and record[i].isdigit() and int(record[i]) > 0:
                        key = table + "." + name
                        entry = int(record[i])
                        refs[key] += 1
                        ids_by_column[key].add(entry)

    all_refs = set().union(*ids_by_column.values()) if ids_by_column else set()
    type_counts = collections.Counter(row[0] for row in rows.values())
    composite = collections.Counter()
    composite_ids = set()
    for kind, value1, value2, value3, value4, _ in rows.values():
        if kind in (-3, -2, -1):
            for name, value in zip(("value1", "value2", "value3", "value4"),
                                   (value1, value2, value3, value4)):
                if value and (kind != -3 or name == "value1"):
                    composite[name] += 1
                    composite_ids.add(value)

    # Snapshot of leaf implementations on this lane's base. A leaf counts only when its
    # collaborator is present; this is the maximum code-path coverage, not a live-world claim.
    before = set((-3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13,
                  14, 15, 17, 18, 19, 22, 23, 26, 29, 30, 35))
    after = before | {31, 33, 36, 37, 39, 40, 42}

    def covered(leaves):
        cache = {}

        def visit(entry):
            if entry in cache:
                return cache[entry]
            row = rows.get(entry)
            if row is None:
                return False
            kind, value1, value2, value3, value4, _ = row
            cache[entry] = False  # cycles cannot be valid; fail closed if one appears
            if kind == -3:
                result = visit(value1)
            elif kind in (-2, -1):
                result = all(visit(v) for v in (value1, value2, value3, value4) if v)
            else:
                result = kind in leaves
            cache[entry] = result
            return result

        return {entry for entry in rows if visit(entry)}

    supported_before, supported_after = covered(before), covered(after)
    result = {
        "conditionRows": len(rows),
        "typeCounts": dict(sorted(type_counts.items())),
        "compositeReferences": dict(sorted(composite.items())),
        "compositeReferencedDistinctIds": len(composite_ids),
        "codePathCoverageBefore": len(supported_before),
        "codePathCoverageAfter": len(supported_after),
        "externalReferencedSupportedBefore": len(all_refs & supported_before),
        "externalReferencedSupportedAfter": len(all_refs & supported_after),
        "references": {key: {"uses": refs[key], "distinctIds": len(ids_by_column[key])}
                       for key in sorted(refs)},
        "referencedDistinctIds": len(all_refs),
        "referencedMissingIds": sorted(all_refs - rows.keys()),
    }
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("give one .sql.gz dump path")
    main(sys.argv[1])
