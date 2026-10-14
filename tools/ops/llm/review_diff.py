"""Job C - reusable LLM diff reviewer.

  py -3 tools/ops/llm/review_diff.py --repo . --base c3102f44 --head HEAD --budget-usd 10
      [--model claude-fable-5-1] [--effort high] [--batch] [--context-file X.md ...] [--chunk-tokens 25000]
      [--out path.json] [--job C-diff-reviewer] [--dry-run]

Sends the branch diff (base...head, chunked by file, new-side line numbers annotated) to the strongest
model in the claude-api skill (claude-fable-5-1) with the repo charter/rules as a cached system prefix,
and writes structured findings (file, line, severity, claim, why) to JSON plus a ranked markdown table.
Every request goes through llmlib's ledger: worst case reserved first, refused if over --budget-usd or the
global cap.  Read-only on the repo (git diff / git show only).
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import subprocess
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import opsenv  # noqa: E402
import llmlib  # noqa: E402

DEFAULT_CONTEXT = ["ARCANECORE_CHARTER.md", "CLAUDE.md", "AGENTS.md", "CONTRIBUTING.md"]
SEV = {"blocker": 0, "major": 1, "minor": 2, "nit": 3}
CONF = {"high": 0, "medium": 1, "low": 2}

SCHEMA = {
    "type": "object", "additionalProperties": False, "required": ["chunk_summary", "findings"],
    "properties": {
        "chunk_summary": {"type": "string"},
        "findings": {"type": "array", "items": {
            "type": "object", "additionalProperties": False,
            "required": ["file", "line", "end_line", "severity", "category", "claim", "why", "suggested_fix", "confidence"],
            "properties": {
                "file": {"type": "string"}, "line": {"type": "integer"}, "end_line": {"type": "integer"},
                "severity": {"type": "string", "enum": list(SEV)},
                "category": {"type": "string", "enum": ["bug", "logic", "concurrency", "resource", "security", "performance",
                                                        "test_gap", "test_validity", "rules_violation", "api_contract", "other"]},
                "claim": {"type": "string"}, "why": {"type": "string"}, "suggested_fix": {"type": "string"},
                "confidence": {"type": "string", "enum": list(CONF)}}}},
    },
}

INSTRUCTIONS = """You are a senior reviewer for the repository described below. You review one chunk of a branch diff at a time and report defects the change introduces or exposes.

Diff format: per file, a header, then hunks. Each line is prefixed with the NEW-file line number and a marker: "  123 + code" (added), "  123   code" (context), "    - - code" (removed; no new-side number). Cite NEW-file line numbers; for a problem in removed code cite the nearest new line.

Report only things a strong human reviewer would block or fix:
- correctness bugs (wrong condition, off-by-one, null/empty handling, wrong units, state not reset, races, lifetime/ownership, exceptions on hot paths), contract breaks between the changed code and code it calls, configuration keys that are parsed but never used (or vice versa), tests that cannot fail or do not exercise what their name claims, and violations of the repository rules below.
- Do not report style, naming, formatting, comment wording, or speculative refactors. Do not report something you cannot point at in the shown code; if a claim depends on code outside this chunk, say so in "why" and lower the confidence.
- "why": the concrete failure scenario (inputs/state -> wrong outcome). "suggested_fix": one or two sentences.
- severity: blocker = wrong behaviour in normal use or data loss/crash; major = wrong in a plausible edge case or a test that gives false assurance; minor = robustness/maintainability with a concrete risk; nit = only if cheap and clearly right.
- Prefer a few correct findings over many speculative ones. An empty findings list is a valid answer.

Repository rules and charter follow.
"""


def git(repo: str, *args: str) -> str:
    return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True, check=True,
                          encoding="utf-8", errors="replace").stdout


def split_files(diff: str) -> list[tuple[str, str]]:
    parts = re.split(r"(?m)^diff --git ", diff)
    out = []
    for p in parts[1:]:
        m = re.search(r"(?m)^\+\+\+ (?:b/)?(.+)$", p)
        path = m.group(1).strip() if m else p.split()[1][2:]
        if path == "/dev/null":
            path = re.search(r"(?m)^--- (?:a/)?(.+)$", p).group(1).strip() + " (deleted)"
        out.append((path, "diff --git " + p))
    return out


def annotate(file_diff: str) -> list[str]:
    """Return annotated lines; hunk boundaries kept as separate lines so chunks can split on them."""
    lines, new = [], 0
    for raw in file_diff.splitlines():
        if raw.startswith("@@"):
            m = re.match(r"@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@(.*)", raw)
            new = int(m.group(1)) if m else new
            lines.append(f"@@ hunk at new line {new}{m.group(2) if m else ''}")
        elif raw.startswith(("diff --git", "index ", "--- ", "+++ ", "new file", "deleted file", "similarity", "rename ")):
            lines.append(raw)
        elif raw.startswith("+"):
            lines.append(f"{new:6d} + {raw[1:]}")
            new += 1
        elif raw.startswith("-"):
            lines.append(f"     - - {raw[1:]}")
        elif raw.startswith("\\"):
            continue
        else:
            lines.append(f"{new:6d}   {raw[1:] if raw.startswith(' ') else raw}")
            new += 1
    return lines


def chunk(files: list[tuple[str, list[str]]], cap_chars: int) -> list[list[tuple[str, str]]]:
    chunks, cur, size = [], [], 0
    for path, lines in files:
        text = "\n".join(lines)
        if len(text) > cap_chars:  # split a big file on hunk boundaries
            pieces, buf = [], []
            for ln in lines:
                if ln.startswith("@@") and sum(len(x) + 1 for x in buf) > cap_chars * 0.8:
                    pieces.append(buf)
                    buf = []
                buf.append(ln)
            pieces.append(buf)
            for i, pc in enumerate(pieces, 1):
                if cur:
                    chunks.append(cur)
                    cur, size = [], 0
                chunks.append([(f"{path} (part {i}/{len(pieces)})", "\n".join(pc))])
            continue
        if size + len(text) > cap_chars and cur:
            chunks.append(cur)
            cur, size = [], 0
        cur.append((path, text))
        size += len(text)
    if cur:
        chunks.append(cur)
    return chunks


def build_params(args, system_text: str, all_files: list[str], ch: list[tuple[str, str]], idx: int, n: int) -> dict:
    body = "\n\n".join(f"===== FILE {p} =====\n{t}" for p, t in ch)
    user = (f"Branch diff {args.base}..{args.head} of {args.repo}. Files changed in the whole branch: "
            f"{', '.join(all_files)}.\n\nThis is chunk {idx}/{n}. Review the files in this chunk:\n\n{body}")
    p = {
        "model": args.model, "max_tokens": args.max_tokens,
        "system": [{"type": "text", "text": system_text, "cache_control": {"type": "ephemeral"}}],
        "messages": [{"role": "user", "content": user}],
        "output_config": {"effort": args.effort, "format": {"type": "json_schema", "schema": SCHEMA}},
    }
    return p


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--repo", required=True)
    ap.add_argument("--base", required=True)
    ap.add_argument("--head", required=True)
    ap.add_argument("--budget-usd", type=float, required=True)
    ap.add_argument("--model", default=llmlib.STRONGEST)
    ap.add_argument("--effort", default="high", choices=["low", "medium", "high", "xhigh", "max"])
    ap.add_argument("--max-tokens", type=int, default=32000)
    ap.add_argument("--chunk-tokens", type=int, default=25000)
    ap.add_argument("--context-file", action="append", default=None)
    ap.add_argument("--unified", type=int, default=10, help="diff context lines")
    ap.add_argument("--batch", action="store_true", help="use the Message Batches API (50%% off, no refusal fallback)")
    ap.add_argument("--no-fallback", action="store_true")
    ap.add_argument("--job", default="C-diff-reviewer")
    ap.add_argument("--out")
    ap.add_argument("--dry-run", action="store_true", help="build chunks and print the worst-case estimate only")
    args = ap.parse_args()

    repo = args.repo
    base = git(repo, "rev-parse", "--short", args.base).strip()
    head = git(repo, "rev-parse", "--short", args.head).strip()
    diff = git(repo, "diff", f"-U{args.unified}", "--no-color", "--no-ext-diff", f"{args.base}..{args.head}")
    files = [(p, annotate(d)) for p, d in split_files(diff)]
    all_files = [p for p, _ in files]
    ctx_names = args.context_file or DEFAULT_CONTEXT
    ctx = []
    for name in ctx_names:
        try:
            ctx.append(f"----- {name} (at {head}) -----\n" + git(repo, "show", f"{args.head}:{name}"))
        except subprocess.CalledProcessError:
            if args.context_file:
                sys.exit(f"context file {name} not found at {args.head}")
    system_text = INSTRUCTIONS + "\n".join(ctx)
    chunks = chunk(files, int(args.chunk_tokens * 3.2))
    print(f"{len(files)} files, {len(diff)} diff chars -> {len(chunks)} chunks; context: "
          f"{[c.splitlines()[0] for c in ctx]}; model {args.model}")
    params = [build_params(args, system_text, all_files, ch, i, len(chunks)) for i, ch in enumerate(chunks, 1)]
    if args.dry_run:
        tot = 0.0
        for p in params:
            n = llmlib.count_input_tokens(p)
            w = llmlib.worst_case_usd(p["model"], n, p["max_tokens"], args.batch)
            tot += w
            print(f"  chunk input {n} tok, worst case ${w:.3f}")
        print(f"worst case total ${tot:.3f}")
        return

    out = pathlib.Path(args.out) if args.out else llmlib.ROOT / "out" / "review" / f"{pathlib.Path(repo).name}-{base}-{head}.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    results = []
    if args.batch:
        state = out.with_suffix(".batch.json")
        llmlib.submit_batch(args.job, args.budget_usd, [(f"chunk-{i}", p) for i, p in enumerate(params, 1)], state,
                            f"review/{pathlib.Path(repo).name}")
        res = llmlib.collect_batch(state)
        for i in range(1, len(params) + 1):
            r = res.get(f"chunk-{i}", {})
            results.append((i, r.get("stop_reason"), r.get("text", ""), r.get("error")))
    else:
        for i, p in enumerate(params, 1):
            beta = False
            if not args.no_fallback and args.model == llmlib.STRONGEST:
                p = dict(p, betas=["server-side-fallback-2026-07-01"], fallbacks="default")
                beta = True
            print(f"  chunk {i}/{len(params)} ...", flush=True)
            msg = llmlib.call_sync(args.job, args.budget_usd, p, f"review/{pathlib.Path(repo).name}/chunk-{i}", beta=beta)
            results.append((i, msg.stop_reason, llmlib.first_text(msg), None))
            print(f"    stop={msg.stop_reason} model={msg.model} usage={llmlib.usage_dict(msg.usage)}")

    findings, summaries, failures = [], [], []
    for i, stop, text, err in results:
        if err or stop != "end_turn":
            failures.append({"chunk": i, "stop_reason": stop, "error": err})
            continue
        d = json.loads(text)
        summaries.append({"chunk": i, "files": [p for p, _ in chunks[i - 1]], "summary": d["chunk_summary"]})
        for f in d["findings"]:
            f["file"] = re.sub(r" \(part \d+/\d+\)$", "", f["file"])
            f["chunk"] = i
            findings.append(f)
    findings.sort(key=lambda f: (SEV[f["severity"]], CONF[f["confidence"]], f["file"], f["line"]))
    for n, f in enumerate(findings, 1):
        f["rank"] = n
    doc = {"repo": repo, "base": base, "head": head, "model": args.model, "effort": args.effort,
           "chunks": len(chunks), "failures": failures, "summaries": summaries, "findings": findings,
           "ledger": llmlib.summary()["jobs"].get(args.job)}
    out.write_text(json.dumps(doc, indent=1), encoding="utf-8")
    md = [f"# Diff review {pathlib.Path(repo).name} {base}..{head} ({args.model}, effort {args.effort})", "",
          "| # | Sev | Conf | Location | Claim | Why |", "|---|---|---|---|---|---|"]
    for f in findings:
        md.append(f"| {f['rank']} | {f['severity']} | {f['confidence']} | `{f['file']}:{f['line']}` | "
                  f"{f['claim'].replace('|', '/')} | {f['why'].replace('|', '/')} |")
    out.with_suffix(".md").write_text("\n".join(md) + "\n", encoding="utf-8")
    print(f"{len(findings)} findings ({', '.join(f'{k} {sum(1 for f in findings if f['severity'] == k)}' for k in SEV)}), "
          f"{len(failures)} failed chunks -> {out} (+ .md)")


if __name__ == "__main__":
    main()
