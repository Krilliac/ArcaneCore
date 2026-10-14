"""Shared Anthropic client + hard spend ledger for the ArcaneCore LLM jobs.

Rules enforced here (not by convention in the callers):
  * Every request is priced at its WORST case before it is sent: counted input tokens
    (count_tokens API) all billed at the 5-minute cache-WRITE rate, plus max_tokens of
    output.  Batch requests get the Batches API 50% discount on both sides.
  * The worst case is RESERVED in ledger.json against the job budget and the global cap
    before submission; submission is refused (BudgetError) if either would be exceeded.
  * When results arrive the reservation is replaced by the ACTUAL cost computed from the
    response usage (input, cache write, cache read, output).
  * The API key is read from the environment or HKCU\\Environment and never printed,
    logged, or passed on a command line.

Prices: claude-api skill model table (cached 2026-09-25).  Cache write = 1.25x base input
(5-minute TTL); cache read per the skill: Sonnet 5.5 $0.20, Opus 5.5 $0.20, Fable 5.1 $0.25.
"""
from __future__ import annotations

import json
import os
import pathlib
import time
import uuid
from contextlib import contextmanager
from datetime import datetime, timezone

ROOT = pathlib.Path(__file__).resolve().parent
LEDGER_PATH = ROOT / "ledger.json"
LOCK_PATH = ROOT / "ledger.lock"
GLOBAL_CAP_USD = 90.0
BATCH_DISCOUNT = 0.5

# $ per million tokens
PRICES = {
    "claude-sonnet-5-5": {"in": 2.00, "out": 10.00, "cache_write": 2.50, "cache_read": 0.20},
    "claude-opus-5-5": {"in": 4.00, "out": 20.00, "cache_write": 5.00, "cache_read": 0.20},
    "claude-fable-5-1": {"in": 10.00, "out": 50.00, "cache_write": 12.50, "cache_read": 0.25},
}

SONNET = "claude-sonnet-5-5"
STRONGEST = "claude-fable-5-1"


class BudgetError(RuntimeError):
    pass


# ---------------------------------------------------------------- key / client
def _api_key() -> str:
    key = os.environ.get("ANTHROPIC_API_KEY")
    if not key:
        import winreg

        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as h:
            key, _ = winreg.QueryValueEx(h, "ANTHROPIC_API_KEY")
    if not key:
        raise RuntimeError("ANTHROPIC_API_KEY not found in env or HKCU\\Environment")
    return key


_client = None


def client():
    global _client
    if _client is None:
        import anthropic

        _client = anthropic.Anthropic(api_key=_api_key(), max_retries=4, timeout=900)
    return _client


# ---------------------------------------------------------------- pricing
def _price(model: str) -> dict:
    for k, v in PRICES.items():
        if model == k or model.startswith(k):
            return v
    raise BudgetError(f"no price for model {model!r}; refusing to spend on an unpriced model")


def usage_dict(usage) -> dict:
    return {
        "input_tokens": getattr(usage, "input_tokens", 0) or 0,
        "cache_creation_input_tokens": getattr(usage, "cache_creation_input_tokens", 0) or 0,
        "cache_read_input_tokens": getattr(usage, "cache_read_input_tokens", 0) or 0,
        "output_tokens": getattr(usage, "output_tokens", 0) or 0,
    }


def cost_usd(model: str, u: dict, batch: bool) -> float:
    p = _price(model)
    c = (
        u["input_tokens"] * p["in"]
        + u["cache_creation_input_tokens"] * p["cache_write"]
        + u["cache_read_input_tokens"] * p["cache_read"]
        + u["output_tokens"] * p["out"]
    ) / 1e6
    return c * (BATCH_DISCOUNT if batch else 1.0)


def worst_case_usd(model: str, input_tokens: int, max_tokens: int, batch: bool) -> float:
    p = _price(model)
    c = (input_tokens * p["cache_write"] + max_tokens * p["out"]) / 1e6
    return c * (BATCH_DISCOUNT if batch else 1.0)


# ---------------------------------------------------------------- ledger
@contextmanager
def _locked():
    deadline = time.time() + 60
    while True:
        try:
            fd = os.open(str(LOCK_PATH), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            break
        except FileExistsError:
            if time.time() > deadline:
                raise RuntimeError(f"ledger lock {LOCK_PATH} held for >60s; remove it if stale")
            time.sleep(0.2)
    try:
        yield
    finally:
        os.close(fd)
        os.remove(str(LOCK_PATH))


def _load() -> dict:
    if LEDGER_PATH.exists():
        return json.loads(LEDGER_PATH.read_text(encoding="utf-8"))
    return {"global_cap_usd": GLOBAL_CAP_USD, "jobs": {}}


def _save(led: dict) -> None:
    tmp = LEDGER_PATH.with_suffix(".tmp")
    tmp.write_text(json.dumps(led, indent=1), encoding="utf-8")
    os.replace(tmp, LEDGER_PATH)


def _job(led: dict, job: str, budget: float | None) -> dict:
    j = led["jobs"].setdefault(job, {"budget_usd": budget or 0.0, "spent_usd": 0.0, "reserved": {}, "calls": []})
    if budget is not None:
        j["budget_usd"] = budget
    return j


def _totals(led: dict) -> tuple[float, float]:
    spent = sum(j["spent_usd"] for j in led["jobs"].values())
    reserved = sum(sum(j["reserved"].values()) for j in led["jobs"].values())
    return spent, reserved


def reserve(job: str, budget_usd: float, amount: float, label: str) -> str:
    with _locked():
        led = _load()
        j = _job(led, job, budget_usd)
        jr = sum(j["reserved"].values())
        if j["spent_usd"] + jr + amount > budget_usd + 1e-9:
            raise BudgetError(
                f"[{job}] worst case ${amount:.4f} + spent ${j['spent_usd']:.4f} + reserved ${jr:.4f} "
                f"exceeds job budget ${budget_usd:.2f}"
            )
        gs, gr = _totals(led)
        cap = led.get("global_cap_usd", GLOBAL_CAP_USD)
        if gs + gr + amount > cap + 1e-9:
            raise BudgetError(
                f"worst case ${amount:.4f} + global spent ${gs:.4f} + reserved ${gr:.4f} exceeds global cap ${cap:.2f}"
            )
        rid = f"{label}:{uuid.uuid4().hex[:8]}"
        j["reserved"][rid] = round(amount, 6)
        _save(led)
        return rid


def settle(job: str, rid: str | None, records: list[dict]) -> float:
    """Replace a reservation by actual call records. Each record: model, usage, batch, label."""
    with _locked():
        led = _load()
        j = _job(led, job, None)
        total = 0.0
        for r in records:
            usd = cost_usd(r["model"], r["usage"], r["batch"])
            r = dict(r, usd=round(usd, 6), ts=datetime.now(timezone.utc).isoformat(timespec="seconds"))
            j["calls"].append(r)
            total += usd
        j["spent_usd"] = round(j["spent_usd"] + total, 6)
        if rid:
            j["reserved"].pop(rid, None)
        _save(led)
        return total


def release(job: str, rid: str) -> None:
    with _locked():
        led = _load()
        _job(led, job, None)["reserved"].pop(rid, None)
        _save(led)


def summary() -> dict:
    led = _load()
    gs, gr = _totals(led)
    out = {"global_cap_usd": led.get("global_cap_usd", GLOBAL_CAP_USD), "global_spent_usd": round(gs, 4),
           "global_reserved_usd": round(gr, 4), "global_remaining_usd": round(led.get("global_cap_usd", GLOBAL_CAP_USD) - gs - gr, 4),
           "jobs": {}}
    for name, j in led["jobs"].items():
        u = {"input_tokens": 0, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0, "output_tokens": 0}
        for c in j["calls"]:
            for k in u:
                u[k] += c["usage"].get(k, 0)
        out["jobs"][name] = {"budget_usd": j["budget_usd"], "spent_usd": round(j["spent_usd"], 4),
                             "reserved_usd": round(sum(j["reserved"].values()), 4), "calls": len(j["calls"]), "tokens": u}
    return out


# ---------------------------------------------------------------- requests
def count_input_tokens(params: dict) -> int:
    keys = ("model", "messages", "system", "tools", "output_config", "thinking", "cache_control")
    p = {k: params[k] for k in keys if k in params}
    return client().messages.count_tokens(**p).input_tokens


def first_text(msg) -> str:
    return next((b.text for b in msg.content if getattr(b, "type", None) == "text"), "")


def call_sync(job: str, budget_usd: float, params: dict, label: str, beta: bool = False):
    """One streamed request with reserve -> send -> settle. Returns the final Message."""
    import anthropic

    n = count_input_tokens(params)
    wc = worst_case_usd(params["model"], n, params["max_tokens"], batch=False)
    rid = reserve(job, budget_usd, wc, label)
    try:
        api = client().beta.messages if beta else client().messages
        with api.stream(**params) as s:
            msg = s.get_final_message()
    except anthropic.BadRequestError:
        release(job, rid)  # rejected before generation: nothing billed
        raise
    except Exception:
        # Possibly billed mid-stream: leave the worst-case reservation standing (conservative).
        raise
    settle(job, rid, [{"model": params["model"], "usage": usage_dict(msg.usage), "batch": False, "label": label,
                       "stop_reason": msg.stop_reason}])
    return msg


def submit_batch(job: str, budget_usd: float, requests: list[tuple[str, dict]], state_path: pathlib.Path,
                 label: str) -> dict:
    """requests: [(custom_id, params)]. Reserves the summed worst case, then creates the batch.
    Writes state_path immediately after creation so collection is resumable."""
    total_wc = 0.0
    per = {}
    for cid, params in requests:
        n = count_input_tokens(params)
        w = worst_case_usd(params["model"], n, params["max_tokens"], batch=True)
        per[cid] = {"input_tokens_counted": n, "worst_case_usd": round(w, 6)}
        total_wc += w
    print(f"[{job}] {len(requests)} requests, counted input {sum(v['input_tokens_counted'] for v in per.values())} tok, "
          f"worst case ${total_wc:.4f} (batch-discounted)")
    rid = reserve(job, budget_usd, total_wc, label)
    try:
        from anthropic.types.messages.batch_create_params import Request

        batch = client().messages.batches.create(
            requests=[Request(custom_id=cid, params=params) for cid, params in requests])
    except Exception:
        release(job, rid)
        raise
    state = {"job": job, "batch_id": batch.id, "reservation": rid, "label": label,
             "model_by_id": {cid: p["model"] for cid, p in requests}, "estimates": per,
             "created": datetime.now(timezone.utc).isoformat(timespec="seconds"), "collected": False}
    state_path.parent.mkdir(parents=True, exist_ok=True)
    state_path.write_text(json.dumps(state, indent=1), encoding="utf-8")
    print(f"[{job}] batch {batch.id} submitted; state -> {state_path}")
    return state


def collect_batch(state_path: pathlib.Path, poll_s: int = 30) -> dict:
    """Poll until ended, settle actual usage into the ledger, return {custom_id: result-dict}."""
    state = json.loads(state_path.read_text(encoding="utf-8"))
    c = client()
    while True:
        b = c.messages.batches.retrieve(state["batch_id"])
        if b.processing_status == "ended":
            break
        rc = b.request_counts
        print(f"  {state['batch_id']}: {b.processing_status} processing={rc.processing} succeeded={rc.succeeded} "
              f"errored={rc.errored}", flush=True)
        time.sleep(poll_s)
    out, records = {}, []
    for r in c.messages.batches.results(state["batch_id"]):
        t = r.result.type
        if t == "succeeded":
            m = r.result.message
            u = usage_dict(m.usage)
            records.append({"model": m.model, "usage": u, "batch": True, "label": f"{state['label']}/{r.custom_id}",
                            "stop_reason": m.stop_reason})
            out[r.custom_id] = {"type": t, "stop_reason": m.stop_reason, "text": first_text(m), "usage": u}
        else:
            err = getattr(r.result, "error", None)
            out[r.custom_id] = {"type": t, "error": str(err) if err else t}
    if not state.get("collected"):
        total = settle(state["job"], state["reservation"], records)
        state["collected"] = True
        state["actual_usd"] = round(total, 6)
        state_path.write_text(json.dumps(state, indent=1), encoding="utf-8")
        print(f"[{state['job']}] settled actual ${total:.4f}")
    return out


def numbered(text: str, start: int = 1) -> str:
    return "\n".join(f"{i:5d}| {line}" for i, line in enumerate(text.splitlines(), start))


if __name__ == "__main__":
    import argparse

    ap = argparse.ArgumentParser(description="Print the ArcaneCore LLM spend ledger summary.")
    ap.parse_args()
    print(json.dumps(summary(), indent=1))
