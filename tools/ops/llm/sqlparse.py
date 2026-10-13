"""Minimal MySQL-dump INSERT parser (enough for ClassicDB tables)."""
from __future__ import annotations

import gzip
import re


def iter_insert_rows(dump_path: str, table: str):
    prefix = f"INSERT INTO `{table}` VALUES "
    opener = gzip.open if dump_path.endswith(".gz") else open
    with opener(dump_path, "rt", encoding="utf-8", errors="replace") as f:
        for line in f:
            if line.startswith(prefix):
                yield from _parse_tuples(line[len(prefix):])


def _parse_tuples(s: str):
    i, n = 0, len(s)
    while i < n:
        while i < n and s[i] != "(":
            i += 1
        if i >= n:
            return
        i += 1
        row, cur, inq = [], [], False
        while i < n:
            ch = s[i]
            if inq:
                if ch == "\\" and i + 1 < n:
                    nxt = s[i + 1]
                    cur.append({"n": "\n", "r": "\r", "t": "\t", "0": "\0"}.get(nxt, nxt))
                    i += 2
                    continue
                if ch == "'":
                    inq = False
                else:
                    cur.append(ch)
                i += 1
                continue
            if ch == "'":
                inq = True
                cur.append("\x01")  # marks quoted
            elif ch == ",":
                row.append(_val(cur))
                cur = []
            elif ch == ")":
                row.append(_val(cur))
                i += 1
                yield row
                break
            else:
                cur.append(ch)
            i += 1


def _val(chars):
    s = "".join(chars)
    if s.startswith("\x01"):
        return s[1:]
    s = s.strip()
    if s == "NULL":
        return None
    try:
        return int(s)
    except ValueError:
        try:
            return float(s)
        except ValueError:
            return s


if __name__ == "__main__":
    import argparse

    ap = argparse.ArgumentParser(description="Minimal MySQL-dump INSERT parser helper module.")
    ap.parse_args()
