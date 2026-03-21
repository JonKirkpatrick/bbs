#!/usr/bin/env python3
"""Python socket bot template for bbs-agent local bridge mode.

This template connects to a Unix socket exposed by `bbs-agent --listen` and
speaks the local JSONL protocol documented in docs/reference/BBS_AGENT_CONTRACT.md.
"""

from __future__ import annotations

import argparse
import json
import random
import socket
import sys
from dataclasses import dataclass
from typing import Any, Dict, Optional

CONTRACT_VERSION = "0.2"


def send_message(writer, msg_type: str, payload: Dict[str, Any]) -> None:
    msg = {"v": CONTRACT_VERSION, "type": msg_type, "payload": payload}
    writer.write(json.dumps(msg, ensure_ascii=True) + "\n")
    writer.flush()


def log(writer, level: str, message: str) -> None:
    send_message(writer, "log", {"level": level, "message": message})


@dataclass
class BotState:
    player_id: Optional[int] = None
    env: str = ""


def as_int(value: Any, default: int = 0) -> int:
    if isinstance(value, bool):
        return default
    if isinstance(value, (int, float)):
        return int(value)
    if isinstance(value, str):
        text = value.strip()
        if not text:
            return default
        try:
            return int(text)
        except ValueError:
            return default
    return default


def is_empty_cell(cell: Any) -> bool:
    if isinstance(cell, bool):
        return False
    if isinstance(cell, (int, float)):
        return int(cell) == 0
    if isinstance(cell, str):
        return cell.strip() in {"", "0"}
    return False


def connect4_legal_columns(obs: Dict[str, Any]) -> list[str]:
    state_obj = obs.get("state_obj")
    if not isinstance(state_obj, dict):
        return []

    board = state_obj.get("board")
    if not isinstance(board, list) or not board:
        return []

    top_row = board[0]
    if not isinstance(top_row, list):
        return []

    legal: list[str] = []
    for col, cell in enumerate(top_row):
        if is_empty_cell(cell):
            legal.append(str(col))
    return legal


def is_our_turn(obs: Dict[str, Any], state: BotState) -> bool:
    if "your_turn" in obs:
        return bool(obs.get("your_turn"))
    turn_player = as_int(obs.get("turn_player"), default=0)
    return state.player_id is not None and turn_player == state.player_id


def choose_action(obs: Dict[str, Any], state: BotState) -> Optional[str]:
    if state.env.strip().lower() == "connect4":
        legal = connect4_legal_columns(obs)
        if legal:
            return random.choice(legal)

    legal_moves = obs.get("legal_moves")
    if isinstance(legal_moves, list) and legal_moves:
        return str(random.choice(legal_moves))

    return None


def parse_capabilities(raw: str) -> list[str]:
    out: list[str] = []
    for part in raw.split(","):
        token = part.strip()
        if token:
            out.append(token)
    return out


def normalize_socket_path(raw: str) -> str:
    value = raw.strip()
    if value.startswith("unix://"):
        return value[len("unix://") :]
    return value


def main() -> int:
    parser = argparse.ArgumentParser(description="Socket bot template for bbs-agent local bridge")
    parser.add_argument("--socket", default="/tmp/bbs-agent.sock", help="unix socket path")
    parser.add_argument("--name", default="socket_python_bot", help="bot name for register")
    parser.add_argument("--owner-token", default="", help="owner token from dashboard")
    parser.add_argument("--capabilities", default="connect4", help="comma-separated capabilities")
    parser.add_argument("--bot-id", default="", help="existing bot identity id")
    parser.add_argument("--bot-secret", default="", help="existing bot identity secret")
    args = parser.parse_args()

    sock_path = normalize_socket_path(args.socket)
    if not sock_path:
        print("socket path is empty", file=sys.stderr)
        return 1

    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    sock.connect(sock_path)

    reader = sock.makefile("r", encoding="utf-8", newline="\n")
    writer = sock.makefile("w", encoding="utf-8", newline="\n")

    send_message(
        writer,
        "hello",
        {
            "name": args.name,
            "owner_token": args.owner_token,
            "capabilities": parse_capabilities(args.capabilities),
            "bot_id": args.bot_id,
            "bot_secret": args.bot_secret,
        },
    )

    state = BotState()

    for raw_line in reader:
        line = raw_line.strip()
        if not line:
            continue

        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            log(writer, "error", f"invalid JSON from agent: {line[:120]}")
            continue

        if not isinstance(msg, dict):
            log(writer, "error", "invalid message envelope")
            continue

        if str(msg.get("v", "")) != CONTRACT_VERSION:
            log(writer, "error", f"unsupported contract version: {msg.get('v')}")
            continue

        msg_type = str(msg.get("type", "")).strip().lower()
        payload = msg.get("payload")
        if not isinstance(payload, dict):
            payload = {}

        if msg_type == "welcome":
            state.player_id = as_int(payload.get("player_id"), default=0)
            state.env = str(payload.get("env") or "")
            log(writer, "info", f"joined session={payload.get('session_id')} arena={payload.get('arena_id')}")
            continue

        if msg_type == "turn":
            if bool(payload.get("done")):
                continue

            obs = payload.get("obs")
            if not isinstance(obs, dict):
                continue

            if not is_our_turn(obs, state):
                continue

            action = choose_action(obs, state)
            if action is None:
                continue

            send_message(writer, "action", {"action": action})
            continue

        if msg_type == "shutdown":
            return 0

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
