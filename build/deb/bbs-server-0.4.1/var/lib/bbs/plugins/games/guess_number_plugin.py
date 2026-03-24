#!/usr/bin/env python3

import json
import random
import sys


def parse_key_value_args(raw_args):
    values = {}
    if not isinstance(raw_args, list):
        return values
    for item in raw_args:
        if not isinstance(item, str):
            continue
        token = item.strip()
        if not token:
            continue
        if "=" in token:
            key, value = token.split("=", 1)
            values[key.strip().lower()] = value.strip()
    return values


class GuessNumberGame:
    def __init__(self):
        self.target = 0
        self.max_range = 100
        self.attempts = 0
        self.last_guess = None
        self.last_feedback = "Start guessing!"
        self.game_over = False

    def init(self, args):
        parsed = parse_key_value_args(args)
        max_range_raw = parsed.get("max_range", "100")
        try:
            max_range = int(max_range_raw)
        except ValueError as exc:
            raise ValueError("max_range must be an integer") from exc

        if max_range < 2:
            raise ValueError("max_range must be >= 2")

        self.max_range = max_range
        self.target = random.randint(1, self.max_range)
        self.attempts = 0
        self.last_guess = None
        self.last_feedback = f"Guess a number between 1 and {self.max_range}"
        self.game_over = False

        return {
            "name": "guess_number",
            "required_players": 1,
            "supports_move_clock": False,
            "supports_handicap": False,
            "supports_episodic": False,
        }

    def get_state(self):
        progress_max = max(8, min(24, self.max_range // 4))
        outcome = "won" if self.game_over else "in_progress"

        return {
            "state": json.dumps(
                {
                    "game": "guess_number",
                    "max_range": self.max_range,
                    "attempts": self.attempts,
                    "last_guess": self.last_guess,
                    "feedback": self.last_feedback,
                    "done": self.game_over,
                    "outcome": outcome,
                    "viewer": {
                        "mode": "client",
                        "hint": f"Enter an integer move from 1 to {self.max_range}",
                        "progress": {
                            "label": "Attempt pressure",
                            "value": self.attempts,
                            "max": progress_max,
                        },
                    },
                }
            )
        }

    def validate_move(self, player_id, move):
        if player_id != 1:
            raise ValueError("guess_number expects player_id=1")
        if self.game_over:
            raise ValueError("game is already over")
        try:
            guess = int(move)
        except ValueError as exc:
            raise ValueError("move must be an integer") from exc
        if guess < 1 or guess > self.max_range:
            raise ValueError(f"guess must be between 1 and {self.max_range}")

    def apply_move(self, player_id, move):
        self.validate_move(player_id, move)
        guess = int(move)
        self.attempts += 1
        self.last_guess = guess
        if guess < self.target:
            self.last_feedback = "Higher"
        elif guess > self.target:
            self.last_feedback = "Lower"
        else:
            self.last_feedback = f"Correct! It was {self.target}."
            self.game_over = True
        return {}

    def is_game_over(self):
        return {
            "is_game_over": self.game_over,
            "winner": "player_1" if self.game_over else "",
        }


def encode_response(req_id, result=None, error_code=None, error_message=None):
    payload = {"id": req_id}
    if error_code:
        payload["error"] = {
            "code": error_code,
            "message": error_message or error_code,
        }
    else:
        payload["result"] = result if result is not None else {}
    return payload


def decode_params(req):
    params = req.get("params")
    if isinstance(params, dict):
        return params
    return {}


def main():
    game = GuessNumberGame()
    initialized = False

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            print(
                json.dumps(
                    encode_response(
                        0,
                        error_code="bad_request",
                        error_message="failed to decode request",
                    )
                )
            )
            sys.stdout.flush()
            continue

        req_id = req.get("id", 0)
        method = req.get("method", "")
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
                    resp = encode_response(req_id, result={"name": "guess_number"})
                elif method == "get_state":
                    resp = encode_response(req_id, result=game.get_state())
                elif method == "validate_move":
                    game.validate_move(params.get("player_id", 0), params.get("move", ""))
                    resp = encode_response(req_id, result={})
                elif method == "apply_move":
                    result = game.apply_move(params.get("player_id", 0), params.get("move", ""))
                    resp = encode_response(req_id, result=result)
                elif method == "is_game_over":
                    resp = encode_response(req_id, result=game.is_game_over())
                elif method == "advance_episode":
                    resp = encode_response(
                        req_id,
                        error_code="unsupported",
                        error_message="game does not support advance_episode",
                    )
                else:
                    resp = encode_response(
                        req_id,
                        error_code="unknown_method",
                        error_message=f"unsupported method '{method}'",
                    )
        except ValueError as exc:
            resp = encode_response(req_id, error_code="validation_failed", error_message=str(exc))
        except RuntimeError as exc:
            resp = encode_response(req_id, error_code="not_initialized", error_message=str(exc))
        except Exception as exc:  # Defensive plugin boundary
            resp = encode_response(req_id, error_code="internal", error_message=str(exc))

        print(json.dumps(resp))
        sys.stdout.flush()


if __name__ == "__main__":
    main()
