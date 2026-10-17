#!/usr/bin/env python3
"""Regenerates docs/ops/exception-audit.md.

Usage: py -3 scripts/exception-audit/audit.py <repo-root> <base-ref>

Scans every `catch` in src/** of the working tree and of <base-ref> (the commit before the exception-visibility change; exported with
`git archive`), classifies each site from its own text, and writes the table. The classification is a HEURISTIC over the catch body
(regular expressions, not a compiler); it is a map for review, not a proof. See the header the script writes.
"""
import collections
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
if len(sys.argv) != 3:
    sys.exit('usage: audit.py <repo-root> <base-ref>')
repo = os.path.abspath(sys.argv[1])
base_ref = sys.argv[2]


def resolve_tree(ref):
    """The tree id <ref> names. A ref starting with '-' would reach `git archive` as an option (--output=<file> writes anywhere), so it
    is refused; the archive is then made from the verified hex id, never from the caller's text."""
    if not ref or ref.startswith('-'):
        sys.exit(f'audit.py: base ref {ref!r} starts with "-" (or is empty); give a commit, branch or tag')
    r = subprocess.run(['git', '-C', repo, 'rev-parse', '--verify', '--quiet', '--end-of-options', ref + '^{tree}'],
                       capture_output=True, text=True, check=False)
    tree = r.stdout.strip()
    if r.returncode != 0 or not re.fullmatch(r'[0-9a-f]{40,64}', tree):
        sys.exit(f'audit.py: base ref {ref!r} does not name a commit or tree in {repo}')
    return tree


def scan_both(base_tree):
    """Runs scan.py over the working tree and over <base_tree>'s src/, in a temporary directory that is always removed."""
    work = tempfile.mkdtemp(prefix='exception-audit-')
    try:
        base_dir = os.path.join(work, 'base')
        os.makedirs(base_dir)
        archive = subprocess.run(['git', '-C', repo, 'archive', base_tree, 'src'], check=True, capture_output=True).stdout
        subprocess.run(['tar', '-x', '-C', base_dir], input=archive, check=True)
        for tree, name in ((repo, 'final.json'), (base_dir, 'base.json')):
            subprocess.run([sys.executable, os.path.join(HERE, 'scan.py'), tree, os.path.join(work, name)], check=True, capture_output=True)
        with open(os.path.join(work, 'final.json'), encoding='utf-8') as f:
            final_rows = json.load(f)
        with open(os.path.join(work, 'base.json'), encoding='utf-8') as f:
            base_rows = json.load(f)
        return final_rows, base_rows
    finally:
        shutil.rmtree(work, ignore_errors=True)


final, base = scan_both(resolve_tree(base_ref))

# a log call that receives the exception object (directly, or through a helper that does)
LOG_CALL = re.compile(r'\bLog(Trace|Debug|Information|Warning|Error|Critical)\b|\.Log\(|LogFault|ReportCommandFault|CommandFaultLogging\.Log|LogHookFault|\.Failed\(')
LEDGER = re.compile(r'FaultLedger\.Shared\.Record\(')
CANCEL_TYPES = {'OperationCanceledException', 'TaskCanceledException', 'ObjectDisposedException'}


def norm(text):
    return re.sub(r'\s+', ' ', text)


def bare(var, body):
    return bool(var) and re.search(r'(?<![\w.])' + re.escape(var) + r'(?![\w.(])', body) is not None


def first_comment(body):
    m = re.search(r'//\s*(.+)', body) or re.search(r'/\*\s*(.+?)\s*\*/', body)
    return re.sub(r'\s+', ' ', m.group(1)).strip() if m else None


def classify(x):
    body, var, typ, decl = x['body'], x['var'], x['typ'].split('.')[-1], x['decl']
    comment = first_comment(body)
    logs = LOG_CALL.search(body) is not None
    if logs and (bare(var, body) or re.search(r'LogHookFault|\.Failed\(|ReportCommandFault|CommandFaultLogging\.Log|LogFault', body)):
        return 'logged', 'logs the exception object'
    if LEDGER.search(body) and not logs:
        return 'ledger-only', 'counted in the FaultLedger (.server faults) but not written to the log; the failure text is returned to a caller that logs it'
    if re.search(r'\bthrow\b', body):
        return 'propagated', 'rethrows' if re.search(r'throw\s*;', body) else 'wraps and throws (inner exception kept unless the site says otherwise)'
    if typ == 'RegexMatchTimeoutException':
        return 'by-design', 'regex timeout: treated as a match / rejection (fails closed)' + (f'; "{comment}"' if comment else '')
    if typ in CANCEL_TYPES or re.search(r'IsCancellationRequested|IsStopped|_stopping', decl):
        return 'by-design', 'cancellation / shutdown / disposed: expected control flow' + (f'; "{comment}"' if comment else '')
    if var and re.search(r'\b' + re.escape(var) + r'\b', body) and re.search(r'(\.Add\(|=\s*' + re.escape(var) + r'\b|FromException|SetException|return\s+' + re.escape(var) + r'\b|\?\?=\s*' + re.escape(var) + r'\b)', body):
        return 'propagated', 'carried to a caller / observer that logs or rethrows it (not checked per site)'
    if var and re.search(re.escape(var) + r'\.GetType\(\)\.Name', body) and not bare(var, body):
        return 'by-design', 'reported as the exception type only (a message may hold a secret or client data; some tests enforce it)'
    if logs:
        return 'message-only', 'logs a message without the exception: an expected, recoverable or client-caused condition (review each if the policy changes)'
    if var and re.search(r'\b' + re.escape(var) + r'\b', body):
        return 'by-design', 'reported to the operator / returned as a failure result using the exception message'
    if comment:
        return 'by-design', f'documented in place: "{comment}"'
    if typ == 'InvalidOperationException':
        return 'by-design', 'optional service not attached (inferred from the type; no comment)'
    return 'by-design', 'inferred from the exception type; no comment at the site'


bybase = collections.defaultdict(list)
for x in sorted(base, key=lambda r: (r['file'], r['line'])):
    bybase[(x['file'], x['typ'])].append(x)
seen = collections.Counter()
rows = []
counts = collections.Counter()
for x in sorted(final, key=lambda r: (r['file'], r['line'])):
    k = (x['file'], x['typ'])
    index = seen[k]
    seen[k] += 1
    verdict, why = classify(x)
    before = bybase[k][index] if index < len(bybase[k]) else None
    if before is not None and norm(before['body']) != norm(x['body']):
        old_verdict, old_why = classify(before)
        if verdict == 'logged' and old_verdict != 'logged':
            verdict, why = 'FIXED', f'was {old_verdict} ({old_why}); now logs the exception object'
        elif verdict != 'logged':
            why += ' (changed by this lane: comment or counting only)'
    counts[verdict] += 1
    rows.append((x['file'], x['line'], 'all exceptions' if x['typ'] == '(all)' else x['typ'], verdict, why))

out = ['# Exception audit', '']
out.append(f'Every `catch` in `src/**` ({len(rows)} sites) after the exception-visibility change.')
out.append('')
out.append('**Method and limits.** The table is generated by `scripts/exception-audit/audit.py` (rerun: `py -3 scripts/exception-audit/audit.py . <base-ref>`). '
           'It reads each catch body with regular expressions and classifies it from that text alone. The verdicts are heuristic and **unverified**: '
           'no compiler or test checked them, and "propagated" or "by-design" means the body looks that way, not that the caller was followed. '
           'A reason in quotation marks is the comment found at the site. FIXED is a site whose body changed in this lane and now logs the exception object.')
out.append('')
out.append('Policy: an unexpected exception is logged with its object at Error (the console shows type, message, stack trace and inner exceptions) and counted '
           'in the `FaultLedger` (`.server faults`); Warning is for expected, recoverable conditions; shutdown and cancellation and documented parse fallbacks are '
           'swallowed. Text that may hold a secret (bot chat, GitHub publishing) is deliberately logged as the exception type only.')
out.append('')
out.append('## Counts')
out.append('')
out.append('| Verdict | Sites |')
out.append('|---|---|')
for name in ('logged', 'FIXED', 'ledger-only', 'propagated', 'message-only', 'by-design'):
    out.append(f'| {name} | {counts[name]} |')
out.append(f'| total | {len(rows)} |')
out.append('')
out.append('## Fire-and-forget and unobserved tasks')
out.append('')
out.append('About 115 sites (`_ = Task.Run`, `_ = XAsync()`, `ContinueWith`, background consumers) were listed by pattern; the queue consumers catch and log inside '
           'their loop, so they appear in the table. A fault that still goes unobserved reaches `TaskScheduler.UnobservedTaskException`, which `CrashHandler` logs at '
           'Critical with the full exception. Fixed here: the `.playerbot` command lambdas (`PlayerbotCommands`, `PlayerbotFleetCommands`, `PlayerbotScenarioCommands`), '
           '`.reload`, `.hotcode refresh` and the hot module commands answered the GM with a type or message and lost the exception; they now log it '
           '(`ManagedPlayerbotFeature.ReportCommandFault`, `CommandFaultLogging`). There is no `async void` in `src/**`. The per-site follow-through of each task was not audited.')
out.append('')
out.append('## Sites')
out.append('')
out.append('| file:line | catches | verdict | reason / change |')
out.append('|---|---|---|---|')
for f, line, typ, verdict, why in rows:
    out.append('| `%s:%d` | %s | %s | %s |' % (f, line, typ.replace('|', '/'), verdict, why.replace('|', '/')))
target = os.path.join(repo, 'docs', 'ops', 'exception-audit.md')
with open(target, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(out) + '\n')
print(dict(counts), len(rows))
