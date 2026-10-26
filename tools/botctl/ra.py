#!/usr/bin/env python3
"""ArcaneCore remote-admin (RA) client for scripts and AI agents (docs/ops/remote-admin.md, docs/ops/botctl.md).

Logs in (token or account), runs commands in automation mode and prints one JSON line per command:
  {"command": ".botctl state Alice --json", "ok": true, "reason": null, "lines": [...], "json": {...}, "events": [...]}
Exit code: 0 when every command succeeded, 1 when one failed, 2 on a connection or login problem.

Examples:
  ra.py --token-file ra-token.txt ".botctl list" ".botctl take Alice"
  ra.py --token-file ... --script steps.botctl --stop-on-error     # one command per line, '#' comments
  ra.py --token-file ... --stream Alice --seconds 60                # print the bot's #event lines
  ra.py --user ADMIN --password secret ".server info"
"""
from __future__ import annotations

import argparse
import json
import os
import socket
import sys
import time
from dataclasses import dataclass, field
from typing import Any, Iterable, Optional


@dataclass
class Result:
    command: str
    ok: bool
    reason: Optional[str]
    lines: list[str] = field(default_factory=list)
    events: list[str] = field(default_factory=list)

    @property
    def json(self) -> Any:
        """The command's JSON reply (a .botctl --json line), or None."""
        for line in self.lines:
            s = line.strip()
            if s.startswith("{"):
                try:
                    return json.loads(s)
                except ValueError:
                    pass
        return None

    def to_dict(self) -> dict:
        return {"command": self.command, "ok": self.ok, "reason": self.reason, "lines": self.lines, "json": self.json, "events": self.events}


class RaError(Exception):
    pass


class RaClient:
    """One RA connection. Commands run one at a time; '#event' lines are collected separately."""

    def __init__(self, host: str = "127.0.0.1", port: int = 3443, token: Optional[str] = None,
                 user: Optional[str] = None, password: Optional[str] = None, timeout: float = 1900.0):
        self._sock = socket.create_connection((host, port), timeout=30)
        self._sock.settimeout(timeout)
        self._buf = b""
        self._seq = 0
        self.events: list[str] = []
        self._login(token, user, password)

    # -- wire -------------------------------------------------------------------------------------------------
    def _send(self, line: str) -> None:
        self._sock.sendall(line.encode("utf-8") + b"\r\n")

    def _read_line(self) -> str:
        while b"\n" not in self._buf:
            chunk = self._sock.recv(65536)
            if not chunk:
                raise RaError("connection closed")
            self._buf += chunk
        raw, self._buf = self._buf.split(b"\n", 1)
        line = raw.decode("utf-8", "replace").rstrip("\r")
        while line.startswith("AC> "):
            line = line[4:]
        return line

    def _login(self, token: Optional[str], user: Optional[str], password: Optional[str]) -> None:
        while self._read_line() != "Username:":
            pass
        if token:
            self._send("@token " + token.strip())
        elif user and password is not None:
            self._send(f"@login {user} {password}")
        else:
            raise RaError("need a token or a user and password")
        answer = self._read_line()
        if not answer.startswith("+"):
            raise RaError("login failed: " + answer)

    # -- API --------------------------------------------------------------------------------------------------
    def run(self, command: str) -> Result:
        """Run one command and wait for its '#done' line."""
        self._seq += 1
        tag = f"c{self._seq}"
        self._send(f"@id {tag} {command}")
        lines: list[str] = []
        events: list[str] = []
        prefix = f"#done {tag} "
        while True:
            line = self._read_line()
            if line.startswith(prefix):
                status = line[len(prefix):]
                ok = status == "ok"
                reason = None if ok else status[len("error "):] if status.startswith("error ") else status
                self.events.extend(events)
                return Result(command, ok, reason, lines, events)
            if line.startswith("#event"):
                events.append(line)
            else:
                lines.append(line)

    def read_events(self, seconds: float) -> Iterable[str]:
        """Yield pushed '#event' lines for up to `seconds` (after '.botctl stream <bots> on')."""
        deadline = time.monotonic() + seconds
        old = self._sock.gettimeout()
        try:
            while (left := deadline - time.monotonic()) > 0:
                self._sock.settimeout(left)
                try:
                    line = self._read_line()
                except socket.timeout:
                    return
                if line.startswith("#event"):
                    yield line
        finally:
            self._sock.settimeout(old)

    def close(self) -> None:
        try:
            self._send("quit")
        except OSError:
            pass
        self._sock.close()

    def __enter__(self) -> "RaClient":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()


def _commands(args: argparse.Namespace) -> list[str]:
    commands = list(args.commands)
    if args.script:
        with open(args.script, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if line and not line.startswith("#"):
                    commands.append(line)
    return commands


def main(argv: Optional[list[str]] = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("commands", nargs="*", help="commands to run in order")
    p.add_argument("--host", default=os.environ.get("ARCANECORE_RA_HOST", "127.0.0.1"))
    p.add_argument("--port", type=int, default=int(os.environ.get("ARCANECORE_RA_PORT", "3443")))
    p.add_argument("--token", default=os.environ.get("ARCANECORE_RA_TOKEN"))
    p.add_argument("--token-file", default=os.environ.get("ARCANECORE_RA_TOKEN_FILE"))
    p.add_argument("--user")
    p.add_argument("--password", default=os.environ.get("ARCANECORE_RA_PASSWORD"))
    p.add_argument("--script", help="a file with one command per line ('#' comments)")
    p.add_argument("--stop-on-error", action="store_true")
    p.add_argument("--stream", metavar="BOTS", help="turn on .botctl stream for BOTS and print #event lines")
    p.add_argument("--seconds", type=float, default=30.0, help="how long --stream listens")
    p.add_argument("--text", action="store_true", help="print the reply lines as text instead of JSON lines")
    args = p.parse_args(argv)

    token = args.token
    if not token and args.token_file:
        with open(args.token_file, encoding="ascii") as fh:
            token = fh.read().strip()
    try:
        client = RaClient(args.host, args.port, token=token, user=args.user, password=args.password)
    except (OSError, RaError) as error:
        print(json.dumps({"ok": False, "reason": "connect", "error": str(error)}))
        return 2

    failed = False
    with client:
        for command in _commands(args):
            result = client.run(command)
            if args.text:
                print("\n".join(result.lines + result.events + [("#done ok" if result.ok else "#done error " + str(result.reason))]))
            else:
                print(json.dumps(result.to_dict()), flush=True)
            if not result.ok:
                failed = True
                if args.stop_on_error:
                    break
        if args.stream:
            on = client.run(f".botctl stream {args.stream} on")
            if not on.ok:
                print(json.dumps(on.to_dict()))
                return 1
            for event in client.read_events(args.seconds):
                print(event, flush=True)
            client.run(f".botctl stream {args.stream} off")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())