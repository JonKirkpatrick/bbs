#!/usr/bin/env python3

"""Gridworld RL plugin for Build-a-Bot Stadium.

This is a clean-room implementation designed for single-agent RL experiments.
It exposes a deterministic gridworld with optional episodic continuation.
"""

from __future__ import annotations

import json
import random
import sys
from typing import Dict, List, Tuple


# Action names are stable API tokens for bots and replay tooling.
ACTION_DELTAS: Dict[str, Tuple[int, int]] = {
    "up": (0, -1),
    "down": (0, 1),
    "left": (-1, 0),
    "right": (1, 0),
}

ACTION_ALIASES: Dict[str, str] = {
    "0": "up",
    "1": "down",
    "2": "left",
    "3": "right",
    "u": "up",
    "d": "down",
    "l": "left",
    "r": "right",
}


MAP_LIBRARY = {
    "courtyard": {
        "rows": [
            "S...#...G",
            ".##.#.##.",
            "....#....",
            ".##...##.",
            "....L....",
        ],
    },
    "switchback": {
        "rows": [
            "S#......",
            ".#.####.",
            ".#....#.",
            ".####.#.",
            "......#G",
        ],
    },
    "hazard_ring": {
        "rows": [
            "S.......",
            ".LLLLLL.",
            ".L....L.",
            ".L.##.L.",
            ".L....L.",
            ".LLLLLL.",
            ".......G",
        ],
    },
}


def parse_key_value_args(raw_args: object) -> Dict[str, str]:
    values: Dict[str, str] = {}
    if not isinstance(raw_args, list):
        return values

    for item in raw_args:
        if not isinstance(item, str):
            continue
        token = item.strip()
        if not token or "=" not in token:
            continue
        key, value = token.split("=", 1)
        values[key.strip().lower()] = value.strip()
    return values


def parse_bool(raw: str, default: bool) -> bool:
    token = str(raw).strip().lower()
    if token in {"1", "true", "yes", "on"}:
        return True
    if token in {"0", "false", "no", "off"}:
        return False
    return default


def normalize_action(move: object) -> str:
    token = str(move).strip().lower()
    if token in ACTION_DELTAS:
        return token
    if token in ACTION_ALIASES:
        return ACTION_ALIASES[token]
    return ""


class GridworldRLGame:
    def __init__(self) -> None:
        self.map_name = "courtyard"
        self.rows: List[str] = []
        self.width = 0
        self.height = 0

        self.start = (0, 0)
        self.position = (0, 0)

        self.max_steps = 80
        self.random_start = False
        self.episodic = True

        self.step_penalty = -1.0
        self.wall_penalty = -1.5
        self.goal_reward = 15.0
        self.lava_penalty = -8.0

        self.episode = 0
        self.steps = 0
        self.last_reward = 0.0
        self.episode_return = 0.0
        self.done = False
        self.terminal_reason = ""

    def init(self, args: object) -> Dict[str, object]:
        parsed = parse_key_value_args(args)

        map_name = parsed.get("map", "courtyard").strip().lower()
        if map_name not in MAP_LIBRARY:
            raise ValueError(f"unknown map '{map_name}'")

        self.map_name = map_name
        self._load_map(MAP_LIBRARY[map_name]["rows"])

        self.max_steps = max(1, int(parsed.get("max_steps", "80")))
        self.random_start = parse_bool(parsed.get("random_start", "false"), False)
        self.episodic = parse_bool(parsed.get("episodic", "true"), True)

        self.step_penalty = float(parsed.get("step_penalty", "-1.0"))
        self.wall_penalty = float(parsed.get("wall_penalty", "-1.5"))
        self.goal_reward = float(parsed.get("goal_reward", "15.0"))
        self.lava_penalty = float(parsed.get("lava_penalty", "-8.0"))

        self._start_episode(initial=True)

        return {
            "name": "gridworld_rl",
            "required_players": 1,
            "supports_move_clock": False,
            "supports_handicap": False,
            "supports_episodic": self.episodic,
        }

    def _load_map(self, rows: List[str]) -> None:
        if not rows:
            raise ValueError("map rows cannot be empty")

        width = len(rows[0])
        if width == 0:
            raise ValueError("map width must be > 0")

        start = None
        cleaned_rows: List[str] = []

        for y, row in enumerate(rows):
            if len(row) != width:
                raise ValueError("all map rows must have identical width")

            clean_chars = list(row)
            for x, char in enumerate(clean_chars):
                if char == "S":
                    start = (x, y)
                    clean_chars[x] = "."
                elif char not in {".", "#", "G", "L"}:
                    raise ValueError(f"unsupported map symbol '{char}'")

            cleaned_rows.append("".join(clean_chars))

        if start is None:
            raise ValueError("map must include exactly one start tile 'S'")

        self.rows = cleaned_rows
        self.height = len(cleaned_rows)
        self.width = width
        self.start = start

    def _start_episode(self, initial: bool = False) -> None:
        if initial:
            self.episode = 1
        else:
            self.episode += 1

        self.steps = 0
        self.last_reward = 0.0
        self.episode_return = 0.0
        self.done = False
        self.terminal_reason = ""

        if self.random_start:
            self.position = self._sample_open_position()
        else:
            self.position = self.start

    def _sample_open_position(self) -> Tuple[int, int]:
        open_cells: List[Tuple[int, int]] = []
        for y, row in enumerate(self.rows):
            for x, symbol in enumerate(row):
                if symbol == ".":
                    open_cells.append((x, y))

        if not open_cells:
            return self.start

        return random.choice(open_cells)

    def _in_bounds(self, x: int, y: int) -> bool:
        return 0 <= x < self.width and 0 <= y < self.height

    def _cell_symbol(self, x: int, y: int) -> str:
        return self.rows[y][x]

    def _legal_moves(self, x: int, y: int) -> List[str]:
        legal: List[str] = []
        for action, delta in ACTION_DELTAS.items():
            tx = x + delta[0]
            ty = y + delta[1]
            if not self._in_bounds(tx, ty):
                continue
            if self._cell_symbol(tx, ty) == "#":
                continue
            legal.append(action)

        if not legal:
            # Rare but possible in pathological maps; keep one no-op-like action.
            return ["up"]
        return legal

    def get_state(self) -> Dict[str, str]:
        x, y = self.position
        legal_moves = self._legal_moves(x, y)

        state = {
            "game": "gridworld_rl",
            "map": self.map_name,
            "episode": self.episode,
            "step": self.steps,
            "max_steps": self.max_steps,
            "position": {"x": x, "y": y},
            "start": {"x": self.start[0], "y": self.start[1]},
            "grid_size": {"width": self.width, "height": self.height},
            "map_rows": self.rows,
            "legend": {
                ".": "open",
                "#": "wall",
                "G": "goal",
                "L": "lava",
            },
            "reward": self.last_reward,
            "episode_return": self.episode_return,
            "done": self.done,
            "terminal_reason": self.terminal_reason,
            "turn_player": 1,
            "legal_moves": legal_moves,
            "all_actions": list(ACTION_DELTAS.keys()),
            "viewer": {
                "title": "Gridworld RL",
                "subtitle": f"Episode {self.episode}",
            },
        }

        return {"state": json.dumps(state)}

    def validate_move(self, player_id: int, move: object) -> None:
        if player_id != 1:
            raise ValueError("gridworld_rl expects player_id=1")
        if self.done:
            raise ValueError("episode is complete")

        action = normalize_action(move)
        if not action:
            allowed = ", ".join(list(ACTION_DELTAS.keys()) + list(ACTION_ALIASES.keys()))
            raise ValueError(f"unsupported action; expected one of: {allowed}")

    def apply_move(self, player_id: int, move: object) -> Dict[str, object]:
        self.validate_move(player_id, move)

        action = normalize_action(move)
        dx, dy = ACTION_DELTAS[action]
        x, y = self.position
        tx = x + dx
        ty = y + dy

        reward = self.step_penalty
        reason = ""

        if not self._in_bounds(tx, ty):
            tx, ty = x, y
            reward = self.wall_penalty
            reason = "boundary"
        elif self._cell_symbol(tx, ty) == "#":
            tx, ty = x, y
            reward = self.wall_penalty
            reason = "wall"

        self.position = (tx, ty)
        self.steps += 1

        symbol = self._cell_symbol(tx, ty)
        if symbol == "G":
            reward = self.goal_reward
            self.done = True
            self.terminal_reason = "goal"
        elif symbol == "L":
            reward = self.lava_penalty
            self.done = True
            self.terminal_reason = "lava"

        if not self.done and self.steps >= self.max_steps:
            self.done = True
            self.terminal_reason = "max_steps"

        self.last_reward = reward
        self.episode_return += reward

        return {
            "applied_action": action,
            "position": {"x": tx, "y": ty},
            "reward": reward,
            "done": self.done,
            "transition_note": reason,
        }

    def is_game_over(self) -> Dict[str, object]:
        winner = "player_1" if self.done and self.terminal_reason == "goal" else ""
        return {
            "is_game_over": self.done,
            "winner": winner,
        }

    def advance_episode(self) -> Dict[str, object]:
        if not self.episodic:
            raise RuntimeError("game does not support advance_episode")
        if not self.done:
            raise RuntimeError("episode is not complete")

        summary = {
            "episode": self.episode,
            "episode_return": self.episode_return,
            "terminal_reason": self.terminal_reason,
            "reward": self.last_reward,
        }

        self._start_episode(initial=False)

        return {
            "continued": True,
            "payload": summary,
        }


def encode_response(req_id: int, result=None, error_code: str = "", error_message: str = "") -> Dict[str, object]:
    payload: Dict[str, object] = {"id": req_id}
    if error_code:
        payload["error"] = {
            "code": error_code,
            "message": error_message or error_code,
        }
    else:
        payload["result"] = result if result is not None else {}
    return payload


def decode_params(req: Dict[str, object]) -> Dict[str, object]:
    params = req.get("params")
    return params if isinstance(params, dict) else {}


def main() -> None:
    game = GridworldRLGame()
    initialized = False

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            print(json.dumps(encode_response(0, error_code="bad_request", error_message="failed to decode request")))
            sys.stdout.flush()
            continue

        req_id = int(req.get("id", 0))
        method = str(req.get("method", "")).strip()
        params = decode_params(req)

        try:
            if method == "init":
                result = game.init(params.get("args", []))
                initialized = True
                resp = encode_response(req_id, result=result)
            elif method == "shutdown":
                resp = encode_response(req_id, result={})
                print(json.dumps(resp))
                sys.stdout.flush()
                break
            else:
                if not initialized:
                    raise RuntimeError("init must be called before this method")

                if method == "get_name":
                    resp = encode_response(req_id, result={"name": "gridworld_rl"})
                elif method == "get_state":
                    resp = encode_response(req_id, result=game.get_state())
                elif method == "validate_move":
                    game.validate_move(int(params.get("player_id", 0)), params.get("move", ""))
                    resp = encode_response(req_id, result={})
                elif method == "apply_move":
                    result = game.apply_move(int(params.get("player_id", 0)), params.get("move", ""))
                    resp = encode_response(req_id, result=result)
                elif method == "is_game_over":
                    resp = encode_response(req_id, result=game.is_game_over())
                elif method == "advance_episode":
                    result = game.advance_episode()
                    resp = encode_response(req_id, result=result)
                else:
                    resp = encode_response(
                        req_id,
                        error_code="unknown_method",
                        error_message=f"unsupported method '{method}'",
                    )
        except ValueError as exc:
            resp = encode_response(req_id, error_code="validation_failed", error_message=str(exc))
        except RuntimeError as exc:
            code = "not_initialized" if not initialized else "unsupported"
            resp = encode_response(req_id, error_code=code, error_message=str(exc))
        except Exception as exc:
            resp = encode_response(req_id, error_code="internal", error_message=str(exc))

        print(json.dumps(resp))
        sys.stdout.flush()


if __name__ == "__main__":
    main()
