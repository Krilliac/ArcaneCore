"""Compare the vmangos Chat.cpp command table with ArcaneCore's generated reference.

Usage: python tools/gm-command-gap-report.py D:/refs/vmangos/src/game/Chat/Chat.cpp
The script reads the reference and prints Markdown to stdout; it never edits it.
"""

from pathlib import Path
from contextlib import redirect_stdout
import re
import sys


def tables(source: str) -> dict[str, list[tuple[str, str | None, int]]]:
    found = {}
    pattern = re.compile(r"static ChatCommand\s+(\w+)\[\]\s*=\s*\{(.*?)\n\s*\};", re.S)
    for table in pattern.finditer(source):
        rows = []
        for entry in re.finditer(r'\{\s*"([^"]+)"\s*,(.*?)\}', table.group(2), re.S):
            name, body = entry.groups()
            child = re.search(r',\s*(\w+CommandTable)\s*$', body)
            line = source.count("\n", 0, table.start(2) + entry.start()) + 1
            rows.append((name, child.group(1) if child else None, line))
        found[table.group(1)] = rows
    return found


def flatten(all_tables: dict[str, list[tuple[str, str | None, int]]]):
    results = []

    def walk(table: str, prefix: str = "", seen: tuple[str, ...] = ()):
        if table in seen:
            raise ValueError(f"cycle in command tables: {table}")
        for name, child, line in all_tables[table]:
            path = (prefix + " " + name).strip()
            results.append((path, line))
            if child:
                if child not in all_tables:
                    raise ValueError(f"missing command table {child} from {path}")
                walk(child, path, (*seen, table))

    walk("commandTable")
    return results


def main() -> None:
    source = Path(sys.argv[1]).read_text(encoding="utf-8")
    vmangos = flatten(tables(source))
    page = Path("docs/reference/gm-commands.md").read_text(encoding="utf-8")
    ours = set(re.findall(r"^\| `\.([^`]+)`(?: \.\.\.)? \|", page, re.M))
    # The generated page gives groups with `...` and leaves without it.
    absent = [(path, line) for path, line in vmangos if path not in ours]
    print("# vmangos GM command paths absent from ArcaneCore")
    print()
    print("Exact path comparison of `D:/refs/vmangos/src/game/Chat/Chat.cpp`'s `ChatHandler::getCommandTable` "
          "against `docs/reference/gm-commands.md` on this worktree. Parent groups are included; "
          "an equivalent operation under another path is still listed. vmangos bot, debug, "
          "console and later-era extensions are included because they are in its table. "
          "This is a name inventory, not a claim that all commands suit a 1.12.1 public realm.")
    print()
    print(f"vmangos named paths: {len(vmangos)}; current ArcaneCore paths: {len(ours)}; exact paths absent: {len(absent)}.")
    print()
    print("| Missing path | vmangos table line |")
    print("|---|---:|")
    for path, line in absent:
        print(f"| `.{path}` | {line} |")


if __name__ == "__main__":
    if len(sys.argv) == 3:
        with Path(sys.argv[2]).open("w", encoding="utf-8", newline="\n") as output, redirect_stdout(output):
            main()
    else:
        main()
