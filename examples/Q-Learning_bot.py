#!/usr/bin/env python3
"""Q-learning bot example for the gridworld_rl plugin via bbs-agent.

This bot talks to the local JSONL bridge (`bbs-agent --listen`) and learns
online with epsilon-greedy exploration.
"""

from __future__ import annotations

import argparse
import json
import random
import socket
import sys
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Tuple

CONTRACT_VERSION = "0.2"
DEFAULT_ACTIONS = ["up", "down", "left", "right"]


def send_message(writer, msg_type: str, payload: Dict[str, Any]) -> None:
    writer.write(json.dumps({"v": CONTRACT_VERSION, "type": msg_type, "payload": payload}, ensure_ascii=True) + "\n")
    writer.flush()


def log(writer, level: str, message: str) -> None:
    send_message(writer, "log", {"level": level, "message": message})


def normalize_socket_path(raw: str) -> str:
    value = raw.strip()
    if value.startswith("unix://"):
        return value[len("unix://") :]
    return value


def as_int(value: Any, default: int = 0) -> int:
    if isinstance(value, bool):
        return default
    if isinstance(value, (int, float)):
        return int(value)
    if isinstance(value, str):
        token = value.strip()
        if not token:
            return default
        try:
            return int(token)
        except ValueError:
            return default
    return default


def as_float(value: Any, default: float = 0.0) -> float:
    if isinstance(value, bool):
        return default
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str):
        token = value.strip()
        if not token:
            return default
        try:
            return float(token)
        except ValueError:
            return default
    return default


def parse_capabilities(raw: str) -> List[str]:
    out: List[str] = []
    for part in raw.split(","):
        token = part.strip()
        if token:
            out.append(token)
    return out


@dataclass
class Learner:
    alpha: float
    gamma: float
    epsilon: float
    epsilon_min: float
    epsilon_decay: float
    q: Dict[str, Dict[str, float]] = field(default_factory=dict)

    def _ensure_state(self, state_key: str, actions: List[str]) -> None:
        action_map = self.q.setdefault(state_key, {})
        for action in actions:
            action_map.setdefault(action, 0.0)

    def greedy_action(self, state_key: str, actions: List[str]) -> str:
        self._ensure_state(state_key, actions)
        action_map = self.q[state_key]
        best = max(action_map[a] for a in actions)
        ties = [a for a in actions if abs(action_map[a] - best) < 1e-12]
        return random.choice(ties)

    def choose_action(self, state_key: str, actions: List[str]) -> str:
        self._ensure_state(state_key, actions)
        if random.random() < self.epsilon:
            return random.choice(actions)
        return self.greedy_action(state_key, actions)

    def update(self, prev_state: str, prev_action: str, reward: float, next_state: str, next_actions: List[str], done: bool) -> None:
        self._ensure_state(prev_state, [prev_action])
        self._ensure_state(next_state, next_actions)

        q_prev = self.q[prev_state][prev_action]
        if done:
            target = reward
        else:
            next_best = max(self.q[next_state][a] for a in next_actions)
            target = reward + self.gamma * next_best

        self.q[prev_state][prev_action] = q_prev + self.alpha * (target - q_prev)

    def decay(self) -> None:
        self.epsilon = max(self.epsilon_min, self.epsilon * self.epsilon_decay)


@dataclass
class BotState:
    player_id: Optional[int] = None
    env: str = ""
    prev_state_key: Optional[str] = None
    prev_action: Optional[str] = None
    episodes_completed: int = 0
    cumulative_reward: float = 0.0


def parse_turn(payload: Dict[str, Any]) -> Tuple[Dict[str, Any], float, bool]:
    reward = as_float(payload.get("reward"), 0.0)
    done = bool(payload.get("done"))

    obs = payload.get("obs")
    if not isinstance(obs, dict):
        return {}, reward, done

    state_obj = obs.get("state_obj")
    if not isinstance(state_obj, dict):
        raw_state = obs.get("raw_state")
        if isinstance(raw_state, str) and raw_state.strip():
            try:
                parsed = json.loads(raw_state)
                if isinstance(parsed, dict):
                    state_obj = parsed
            except json.JSONDecodeError:
                state_obj = {}

    if not isinstance(state_obj, dict):
        state_obj = {}

    if "reward" in state_obj:
        reward = as_float(state_obj.get("reward"), reward)
    if "done" in state_obj:
        done = bool(state_obj.get("done"))

    return state_obj, reward, done


def state_key_from_state_obj(state_obj: Dict[str, Any]) -> str:
    pos = state_obj.get("position")
    if isinstance(pos, dict):
        x = as_int(pos.get("x"), 0)
        y = as_int(pos.get("y"), 0)
    else:
        x = as_int(state_obj.get("x"), 0)
        y = as_int(state_obj.get("y"), 0)

    map_name = str(state_obj.get("map") or "grid")
    return f"{map_name}:{x},{y}"


def actions_from_state_obj(state_obj: Dict[str, Any]) -> List[str]:
    legal = state_obj.get("legal_moves")
    if isinstance(legal, list):
        moves = [str(item).strip().lower() for item in legal if str(item).strip()]
        if moves:
            return moves

    all_actions = state_obj.get("all_actions")
    if isinstance(all_actions, list):
        moves = [str(item).strip().lower() for item in all_actions if str(item).strip()]
        if moves:
            return moves

    return list(DEFAULT_ACTIONS)


def is_our_turn(payload: Dict[str, Any], state_obj: Dict[str, Any], player_id: Optional[int]) -> bool:
    obs = payload.get("obs")
    if isinstance(obs, dict) and "your_turn" in obs:
        return bool(obs.get("your_turn"))

    turn_player = as_int(state_obj.get("turn_player"), 0)
    if turn_player == 0 and isinstance(obs, dict):
        turn_player = as_int(obs.get("turn_player"), 0)

    return player_id is not None and turn_player == player_id


def main() -> int:
    parser = argparse.ArgumentParser(description="Q-learning bot for gridworld_rl via bbs-agent")
    parser.add_argument("--socket", default="/tmp/bbs-agent.sock", help="unix socket path")
    parser.add_argument("--name", default="gridworld_q_bot", help="bot name used in hello")
    parser.add_argument("--owner-token", default="", help="optional owner token")
    parser.add_argument("--capabilities", default="gridworld_rl", help="comma-separated capabilities")
    parser.add_argument("--bot-id", default="", help="optional existing bot identity id")
    parser.add_argument("--bot-secret", default="", help="optional existing bot identity secret")

    parser.add_argument("--alpha", type=float, default=0.2, help="Q-learning step size")
    parser.add_argument("--gamma", type=float, default=0.95, help="discount factor")
    parser.add_argument("--epsilon", type=float, default=0.25, help="initial exploration rate")
    parser.add_argument("--epsilon-min", type=float, default=0.03, help="minimum exploration rate")
    parser.add_argument("--epsilon-decay", type=float, default=0.9995, help="exploration decay per completed episode")
    args = parser.parse_args()

    sock_path = normalize_socket_path(args.socket)
    if not sock_path:
        print("socket path is empty", file=sys.stderr)
        return 1

    learner = Learner(
        alpha=args.alpha,
        gamma=args.gamma,
        epsilon=args.epsilon,
        epsilon_min=args.epsilon_min,
        epsilon_decay=args.epsilon_decay,
    )
    state = BotState()

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
            state.player_id = as_int(payload.get("player_id"), 0)
            state.env = str(payload.get("env") or "")
            log(
                writer,
                "info",
                (
                    f"ready env={state.env} player={state.player_id} "
                    f"arena={payload.get('arena_id')} epsilon={learner.epsilon:.3f}"
                ),
            )
            continue

        if msg_type == "turn":
            state_obj, reward, done = parse_turn(payload)
            if not state_obj:
                continue

            cur_state_key = state_key_from_state_obj(state_obj)
            cur_actions = actions_from_state_obj(state_obj)
            state.cumulative_reward += reward

            if state.prev_state_key and state.prev_action:
                learner.update(
                    prev_state=state.prev_state_key,
                    prev_action=state.prev_action,
                    reward=reward,
                    next_state=cur_state_key,
                    next_actions=cur_actions,
                    done=done,
                )

            if done:
                state.episodes_completed += 1
                learner.decay()
                if state.episodes_completed % 10 == 0:
                    avg = state.cumulative_reward / max(1, state.episodes_completed)
                    log(
                        writer,
                        "info",
                        (
                            f"episodes={state.episodes_completed} "
                            f"avg_reward={avg:.3f} epsilon={learner.epsilon:.3f}"
                        ),
                    )
                state.prev_state_key = None
                state.prev_action = None
                continue

            if not is_our_turn(payload, state_obj, state.player_id):
                continue

            action = learner.choose_action(cur_state_key, cur_actions)
            send_message(writer, "action", {"action": action})
            state.prev_state_key = cur_state_key
            state.prev_action = action
            continue

        if msg_type == "shutdown":
            return 0

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
