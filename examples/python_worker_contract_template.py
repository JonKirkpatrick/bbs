#!/usr/bin/env python3
"""Python worker template for BBS Agent Contract v0.1.

This script does not talk to the BBS server directly.
It talks to a future bbs-agent process over stdin/stdout JSON lines.

Bot authors should mainly customize choose_move().
"""

from __future__ import annotations

import json
import random
import sys
from typing import Any, Dict, Optional

CONTRACT_VERSION = "0.1"


def send_message(msg_type: str, payload: Dict[str, Any], msg_id: Optional[str] = None) -> None:
    msg: Dict[str, Any] = {
        "v": CONTRACT_VERSION,
        "type": msg_type,
        "payload": payload,
    }
    if msg_id:
        msg["id"] = msg_id

    sys.stdout.write(json.dumps(msg, ensure_ascii=True) + "\n")
    sys.stdout.flush()


def log(level: str, message: str) -> None:
    send_message("log", {"level": level, "message": message})


class WorkerState:
    def __init__(self) -> None:
        self.session_id: Optional[int] = None
        self.arena_id: Optional[int] = None
        self.player_id: Optional[int] = None
        self.game: str = ""


def normalized_game_name(name: str) -> str:
    return name.strip().lower()


def is_our_turn(state_payload: Dict[str, Any], worker_state: WorkerState) -> bool:
    """Best-effort turn inference from generic payload.

    The v0.1 contract intentionally keeps raw_state game-agnostic,
    so this helper checks common keys if the agent provides them.
    """

    if "your_turn" in state_payload:
        return bool(state_payload.get("your_turn"))

    turn_player = state_payload.get("turn_player")
    return worker_state.player_id is not None and turn_player == worker_state.player_id


def choose_move(state_payload: Dict[str, Any], worker_state: WorkerState) -> Optional[str]:
    """Customize this function with your actual bot logic.

    Default behavior:
    - for connect4, derive legal columns from the top board row in state_obj
    - fallback to `legal_moves` if present
    - otherwise return None (no move emitted)
    """

    if normalized_game_name(worker_state.game) == "connect4" or looks_like_connect4_board(state_payload):
        legal_cols = connect4_legal_columns_from_state(state_payload)
        if legal_cols:
            return random.choice(legal_cols)

    legal_moves = state_payload.get("legal_moves")
    if isinstance(legal_moves, list) and legal_moves:
        picked = random.choice([str(m) for m in legal_moves])
        return picked

    return None


def connect4_legal_columns_from_state(state_payload: Dict[str, Any]) -> list[str]:
    state_obj = state_payload.get("state_obj")
    if not isinstance(state_obj, dict):
        return []

    board = state_obj.get("board")
    if not isinstance(board, list) or not board:
        return []

    top_row = board[0]
    if not isinstance(top_row, list) or not top_row:
        return []

    legal: list[str] = []
    for col, cell in enumerate(top_row):
        if is_empty_cell(cell):
            legal.append(str(col))

    return legal


def looks_like_connect4_board(state_payload: Dict[str, Any]) -> bool:
    state_obj = state_payload.get("state_obj")
    if not isinstance(state_obj, dict):
        return False

    board = state_obj.get("board")
    if not isinstance(board, list) or not board:
        return False

    first_row = board[0]
    if not isinstance(first_row, list):
        return False

    cols = len(first_row)
    if cols == 0:
        return False

    # Typical Connect4 shape is 6x7, but we allow compatible variants.
    if len(board) < 4 or cols < 4:
        return False

    for row in board:
        if not isinstance(row, list) or len(row) != cols:
            return False

    return True


def is_empty_cell(cell: Any) -> bool:
    if isinstance(cell, bool):
        return False
    if isinstance(cell, (int, float)):
        return int(cell) == 0
    if isinstance(cell, str):
        trimmed = cell.strip()
        return trimmed in {"", "0"}
    return False


def handle_registered(payload: Dict[str, Any], worker_state: WorkerState) -> None:
    worker_state.session_id = payload.get("session_id")
    log("info", f"registered session_id={worker_state.session_id}")


def handle_manifest(payload: Dict[str, Any], worker_state: WorkerState) -> None:
    worker_state.arena_id = payload.get("arena_id")
    worker_state.player_id = payload.get("player_id")
    worker_state.game = str(payload.get("game") or "")
    log(
        "info",
        f"manifest arena_id={worker_state.arena_id} player_id={worker_state.player_id} game={worker_state.game}",
    )


def handle_state(payload: Dict[str, Any], worker_state: WorkerState) -> None:
    if not is_our_turn(payload, worker_state):
        return

    move = choose_move(payload, worker_state)
    if move is None:
        return

    send_message("move", {"move": move})
    log("info", f"submitted move={move}")


def handle_event(payload: Dict[str, Any]) -> None:
    name = payload.get("name")
    log("info", f"event name={name}")


def main() -> int:
    worker_state = WorkerState()

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line:
            continue

        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            log("error", f"invalid JSON from agent: {line[:120]}")
            continue

        if not isinstance(msg, dict):
            log("error", "invalid envelope: expected object")
            continue

        version = str(msg.get("v", ""))
        msg_type = str(msg.get("type", ""))
        payload = msg.get("payload")

        if version != CONTRACT_VERSION:
            log("error", f"unsupported contract version: {version}")
            continue

        if not isinstance(payload, dict):
            payload = {}

        if msg_type == "hello":
            send_message(
                "hello_ack",
                {
                    "worker_name": "python_worker_template",
                    "worker_version": "0.1.0",
                    "language": "python",
                },
                msg.get("id"),
            )
            continue

        if msg_type == "registered":
            handle_registered(payload, worker_state)
            continue

        if msg_type == "manifest":
            handle_manifest(payload, worker_state)
            continue

        if msg_type == "state":
            handle_state(payload, worker_state)
            continue

        if msg_type == "event":
            handle_event(payload)
            continue

        if msg_type == "error":
            log("error", f"agent error: {payload.get('message')}")
            continue

        if msg_type == "shutdown":
            log("info", "shutdown requested")
            return 0

        log("debug", f"ignored message type={msg_type}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
