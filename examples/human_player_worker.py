#!/usr/bin/env python3
"""Human GUI worker for BBS Agent Contract v0.2.

Renders a Connect4 board and lets a human click to submit moves.
No move validation — whatever column you click is sent directly to the agent.

Note:
    This script targets the legacy stdin/stdout worker runtime.
    For new integrations, use the local socket bridge model described in
    `BBS_AGENT_CONTRACT.md`.
"""

from __future__ import annotations

import json
import queue
import sys
import threading
import time
import tkinter as tk
from typing import Any, Dict, List, Optional

CONTRACT_VERSION = "0.2"

# ---------------------------------------------------------------------------
# Layout / style
# ---------------------------------------------------------------------------
CELL_PX   = 72    # pixels per grid cell
PAD       = 10    # canvas inner padding

COLOR_BG        = "#f3efe5"
COLOR_TOPBAR    = "#14213d"
COLOR_BOARD_BG  = "#14213d"
COLOR_EMPTY     = "#374968"
COLOR_P1        = "#f5c518"   # player 1 — yellow
COLOR_P2        = "#d63031"   # player 2 — red
COLOR_CELL_RING = "#2a3a55"
COLOR_BTN       = "#2d4a6a"
COLOR_BTN_HOV   = "#0b7285"
COLOR_STATUS_BG = "#e8e0d0"
COLOR_INK       = "#14213d"
COLOR_TIMER_OK  = "#2b9348"
COLOR_TIMER_WRN = "#bc6c25"
COLOR_TIMER_BAD = "#b7094c"

FONT_TITLE  = ("Courier New", 10)
FONT_BTN    = ("Courier New", 11, "bold")
FONT_STATUS = ("Courier New", 10)
FONT_TIMER  = ("Courier New", 13, "bold")

POLL_MS = 50
TICK_MS = 80


# ---------------------------------------------------------------------------
# Protocol helpers  (stdout → agent)
# ---------------------------------------------------------------------------

def _send(msg_type: str, payload: Dict[str, Any]) -> None:
    line = json.dumps({"v": CONTRACT_VERSION, "type": msg_type, "payload": payload},
                      ensure_ascii=True)
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def send_action(action: str) -> None:
    _send("action", {"action": action})


# ---------------------------------------------------------------------------
# GUI application
# ---------------------------------------------------------------------------

class HumanPlayerApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("BBS Human Player")
        self.root.configure(bg=COLOR_BG)
        self.root.resizable(False, False)

        # inbox filled by the stdin-reader thread
        self.inbox: queue.Queue = queue.Queue()

        # set by welcome
        self.player_id:    Optional[int] = None
        self.session_id:   int = 0
        self.arena_id:     int = 0
        self.env:          str = ""
        self.deadline_ms:  int = 5000

        # updated per turn
        self.board:         List[List[int]] = []
        self.rows:          int = 6
        self.cols:          int = 7
        self.your_turn:     bool = False
        self.done:          bool = False
        self.turn_recv_at:  float = 0.0
        self.action_sent:   bool = False

        # widget refs (created after welcome)
        self.canvas:       Optional[tk.Canvas] = None
        self.col_buttons:  List[tk.Button] = []
        self.timer_label:  Optional[tk.Label] = None

        self.status_var = tk.StringVar(value="Connecting to arena…")
        self.timer_var  = tk.StringVar(value="")

        self._build_lobby()
        self.root.after(POLL_MS, self._poll_inbox)
        self.root.after(TICK_MS, self._tick_timer)

    # -----------------------------------------------------------------------
    # Lobby (shown before welcome arrives)
    # -----------------------------------------------------------------------

    def _build_lobby(self) -> None:
        self._lobby = tk.Frame(self.root, bg=COLOR_BG, padx=40, pady=40)
        self._lobby.pack()
        tk.Label(self._lobby, text="Build-a-Bot Stadium",
                 font=("Courier New", 16, "bold"), bg=COLOR_BG, fg=COLOR_INK).pack()
        tk.Label(self._lobby, text="Human Player",
                 font=FONT_TITLE, bg=COLOR_BG, fg="#557").pack(pady=(6, 14))
        tk.Label(self._lobby, textvariable=self.status_var,
                 font=FONT_TITLE, bg=COLOR_BG, fg="#334").pack()

    # -----------------------------------------------------------------------
    # Full board UI  (built once after welcome)
    # -----------------------------------------------------------------------

    def _build_board_ui(self) -> None:
        if hasattr(self, "_lobby"):
            self._lobby.destroy()
            del self._lobby

        cw = self.cols * CELL_PX
        ch = self.rows * CELL_PX

        # top bar
        topbar = tk.Frame(self.root, bg=COLOR_TOPBAR, padx=10, pady=6)
        topbar.pack(fill=tk.X)
        color_name = "Yellow" if self.player_id == 1 else "Red"
        tk.Label(topbar,
                 text=(f"Session #{self.session_id}  ·  Arena #{self.arena_id}"
                       f"  ·  Player {self.player_id} ({color_name})"),
                 font=FONT_TITLE, bg=COLOR_TOPBAR, fg="white").pack(side=tk.LEFT)

        # board canvas — wrap in a frame so padding shows as board background
        board_wrap = tk.Frame(self.root, bg=COLOR_BOARD_BG, padx=PAD, pady=PAD)
        board_wrap.pack()
        self.canvas = tk.Canvas(board_wrap, width=cw, height=ch,
                                bg=COLOR_BOARD_BG, highlightthickness=0)
        self.canvas.pack()

        # --- column buttons row  -------------------------------------------
        # We want each of the `cols` buttons to span exactly CELL_PX pixels,
        # matching the board columns above.  Use a fixed-width frame + grid.
        btn_frame = tk.Frame(self.root, bg=COLOR_BG, pady=6, width=cw)
        btn_frame.pack_propagate(False)   # freeze at cw px
        btn_frame.pack()

        for col_idx in range(self.cols):
            btn_frame.columnconfigure(col_idx, minsize=CELL_PX)

        self.col_buttons = []
        for c in range(self.cols):
            btn = tk.Button(
                btn_frame, text=str(c),
                font=FONT_BTN,
                bg=COLOR_BTN, fg="white",
                activebackground=COLOR_BTN_HOV, activeforeground="white",
                disabledforeground="#667",
                relief=tk.FLAT, bd=0,
                command=lambda col=c: self._submit(str(col)),
            )
            btn.grid(row=0, column=c, sticky="ew", padx=1)
            btn.config(state=tk.DISABLED)
            self.col_buttons.append(btn)

        # status bar
        status_bar = tk.Frame(self.root, bg=COLOR_STATUS_BG, pady=5)
        status_bar.pack(fill=tk.X)
        tk.Label(status_bar, textvariable=self.status_var,
                 font=FONT_STATUS, bg=COLOR_STATUS_BG, fg=COLOR_INK,
                 anchor=tk.W, padx=10).pack(side=tk.LEFT, expand=True, fill=tk.X)
        self.timer_label = tk.Label(status_bar, textvariable=self.timer_var,
                                    font=FONT_TIMER, bg=COLOR_STATUS_BG,
                                    fg=COLOR_TIMER_OK, width=7, padx=8)
        self.timer_label.pack(side=tk.RIGHT)

        self._draw_board()

    # -----------------------------------------------------------------------
    # Board rendering
    # -----------------------------------------------------------------------

    def _draw_board(self) -> None:
        if not self.canvas:
            return
        self.canvas.delete("all")
        cell_colors = {0: COLOR_EMPTY, 1: COLOR_P1, 2: COLOR_P2}
        for r in range(self.rows):
            for c in range(self.cols):
                x0 = c * CELL_PX + 4
                y0 = r * CELL_PX + 4
                x1 = x0 + CELL_PX - 8
                y1 = y0 + CELL_PX - 8
                val = 0
                if self.board and r < len(self.board) and c < len(self.board[r]):
                    val = self.board[r][c]
                fill = cell_colors.get(val, COLOR_EMPTY)
                self.canvas.create_oval(x0, y0, x1, y1,
                                        fill=fill, outline=COLOR_CELL_RING, width=2)

    # -----------------------------------------------------------------------
    # Button state
    # -----------------------------------------------------------------------

    def _set_buttons(self, enabled: bool) -> None:
        state = tk.NORMAL if enabled else tk.DISABLED
        for btn in self.col_buttons:
            btn.config(state=state)

    # -----------------------------------------------------------------------
    # Action submission
    # -----------------------------------------------------------------------

    def _submit(self, action: str) -> None:
        if self.action_sent or self.done:
            return
        self.action_sent = True
        self._set_buttons(False)
        self.timer_var.set("")
        self.status_var.set(f"Sent: column {action} — waiting for result…")
        send_action(action)

    def _terminal_result(self, payload: Dict[str, Any]) -> str:
        reward = float(payload.get("reward") or 0.0)
        if reward > 0:
            return "You won!"
        if reward < 0:
            return "You lost."

        # Fallback for older agents that don't project terminal reward.
        response = payload.get("response") or {}
        response_payload = response.get("payload") or {}
        if isinstance(response_payload, dict):
            if bool(response_payload.get("is_draw")):
                return "Draw."
            winner_player_id = int(response_payload.get("winner_player_id") or 0)
            if winner_player_id > 0 and self.player_id is not None:
                if winner_player_id == self.player_id:
                    return "You won!"
                return "You lost."

        return "Draw."

    # -----------------------------------------------------------------------
    # Turn countdown timer
    # -----------------------------------------------------------------------

    def _tick_timer(self) -> None:
        if (self.your_turn and not self.action_sent
                and not self.done and self.deadline_ms > 0):
            elapsed_ms = (time.monotonic() - self.turn_recv_at) * 1000
            remaining_ms = max(0.0, self.deadline_ms - elapsed_ms)
            secs = remaining_ms / 1000.0
            self.timer_var.set(f"{secs:.1f}s")
            if self.timer_label:
                frac = remaining_ms / max(self.deadline_ms, 1)
                if frac > 0.5:
                    self.timer_label.config(fg=COLOR_TIMER_OK)
                elif frac > 0.2:
                    self.timer_label.config(fg=COLOR_TIMER_WRN)
                else:
                    self.timer_label.config(fg=COLOR_TIMER_BAD)
        self.root.after(TICK_MS, self._tick_timer)

    # -----------------------------------------------------------------------
    # Inbox polling  (stdin thread → GUI thread via queue)
    # -----------------------------------------------------------------------

    def _poll_inbox(self) -> None:
        try:
            while True:
                self._handle(self.inbox.get_nowait())
        except queue.Empty:
            pass
        self.root.after(POLL_MS, self._poll_inbox)

    def _handle(self, msg: Dict[str, Any]) -> None:
        t = msg.get("type", "")
        p = msg.get("payload") or {}
        if t == "welcome":
            self._on_welcome(p)
        elif t == "turn":
            self._on_turn(p)
        elif t == "shutdown":
            self.status_var.set(f"Agent shutdown: {p.get('reason', '')}")
            self._set_buttons(False)
            self.root.after(3000, self.root.destroy)

    # -----------------------------------------------------------------------
    # welcome handler
    # -----------------------------------------------------------------------

    def _on_welcome(self, p: Dict[str, Any]) -> None:
        self.player_id   = int(p.get("player_id")  or 1)
        self.session_id  = int(p.get("session_id") or 0)
        self.arena_id    = int(p.get("arena_id")   or 0)
        self.env         = str(p.get("env")        or "")
        self.deadline_ms = int(
            p.get("effective_time_limit_ms") or p.get("time_limit_ms") or 5000
        )
        self._build_board_ui()
        self.status_var.set("Joined arena — waiting for your first turn…")

    # -----------------------------------------------------------------------
    # turn handler
    # -----------------------------------------------------------------------

    def _on_turn(self, p: Dict[str, Any]) -> None:
        obs   = p.get("obs") or {}
        state = obs.get("state_obj") or {}
        board = state.get("board")
        if isinstance(board, list) and board:
            self.board = board
            self.rows  = len(board)
            self.cols  = len(board[0]) if board[0] else self.cols

        self.done        = bool(p.get("done") or p.get("truncated"))
        self.deadline_ms = int(p.get("deadline_ms") or self.deadline_ms)

        self._draw_board()

        # --- terminal turn -------------------------------------------------
        if self.done:
            result = self._terminal_result(p)
            self.status_var.set(f"Game over — {result}")
            self.timer_var.set("")
            self._set_buttons(False)
            return

        # --- active turn ---------------------------------------------------
        self.action_sent  = False
        self.your_turn    = True
        self.turn_recv_at = time.monotonic()

        # surface any error from the previous action attempt
        resp        = p.get("response") or {}
        resp_status = str(resp.get("status") or "").lower()
        resp_body   = resp.get("payload") or ""

        color_name = "Yellow" if self.player_id == 1 else "Red"
        if resp_status == "err":
            self.status_var.set(f"Invalid: {resp_body} — pick a different column")
        else:
            self.status_var.set(
                f"Your turn  (Player {self.player_id} · {color_name})  — click a column"
            )

        self._set_buttons(True)


# ---------------------------------------------------------------------------
# Stdin reader thread  (agent → GUI)
# ---------------------------------------------------------------------------

def stdin_reader(inbox: queue.Queue) -> None:
    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue
        try:
            inbox.put(json.loads(raw))
        except json.JSONDecodeError:
            pass


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main() -> None:
    root = tk.Tk()
    app = HumanPlayerApp(root)
    threading.Thread(target=stdin_reader, args=(app.inbox,), daemon=True).start()
    root.mainloop()


if __name__ == "__main__":
    main()
