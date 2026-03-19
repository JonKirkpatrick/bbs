import socket
import json
import random
import argparse

class SarsaBot:
    def __init__(self, alpha=0.1, gamma=0.9, epsilon=0.1):
        self.q_table = {}  # (row, col): [q_up, q_right, q_down, q_left]
        self.alpha = alpha
        self.gamma = gamma
        self.epsilon = epsilon
        self.actions = ["up", "right", "down", "left"]
        
        # Track SARSA state
        self.last_state = None
        self.last_action_idx = None

    def get_q(self, state):
        if state not in self.q_table:
            self.q_table[state] = [0.0] * 4
        return self.q_table[state]

    def choose_action(self, state):
        if random.random() < self.epsilon:
            return random.randint(0, 3)
        qs = self.get_q(state)
        max_q = max(qs)
        # Random choice among ties
        return random.choice([i for i, q in enumerate(qs) if q == max_q])

    def update(self, s, a_idx, r, s_next, a_next_idx, done):
        current_q = self.get_q(s)[a_idx]
        if done:
            target = r
        else:
            target = r + self.gamma * self.get_q(s_next)[a_next_idx]
        
        self.q_table[s][a_idx] += self.alpha * (target - current_q)

def run_bot(socket_path, name, max_actions):
    bot = SarsaBot()
    client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    client.connect(socket_path)
    reader = client.makefile("r", encoding="utf-8", newline="\n")

    # 1. Local Hello to bbs-agent
    hello = {
        "v": "0.2",
        "type": "hello",
        "payload": {
            "name": name,
            "capabilities": ["gridworld"]
        }
    }
    client.sendall((json.dumps(hello) + "\n").encode())

    print(f"[*] SARSA Bot '{name}' connected to bridge.")

    # Enforce strict action pacing: one outbound action per inbound turn/result cycle.
    awaiting_action_result = False
    total_actions_sent = 0
    episode_return = 0.0
    last_terminal_signature = None

    while True:
        try:
            msg_str = reader.readline()
            if not msg_str:
                print("[*] Connection closed by server")
                break

            msg = json.loads(msg_str.strip())
            m_type = msg.get("type")

            if m_type != "turn":
                continue

            payload = msg.get("payload", {})
            obs = payload.get("obs", {})

            # bbs-agent includes previous MOVE status in turn.payload.response.
            response = payload.get("response")
            if awaiting_action_result and isinstance(response, dict):
                response_type = str(response.get("type", "")).lower()
                if response_type in {"move", "error", "timeout", "ejected", "episode_end", "gameover"}:
                    awaiting_action_result = False

            # Extract state from GridWorld statePayload.
            grid_data = obs.get("state_obj", {}) if isinstance(obs, dict) else {}
            agent_pos = grid_data.get("agent", {}) if isinstance(grid_data, dict) else {}
            curr_s = (int(agent_pos.get("row", 0)), int(agent_pos.get("col", 0)))
            reward = float(payload.get("reward", 0.0) or 0.0)
            done = bool(payload.get("done", False))
            step = payload.get("step", 0)
            episode_index = int(grid_data.get("episode", 0)) if isinstance(grid_data, dict) else 0
            env_episode_step = int(grid_data.get("step", 0)) if isinstance(grid_data, dict) else 0
            env_episode_return = float(grid_data.get("episode_reward", 0.0) or 0.0) if isinstance(grid_data, dict) else 0.0

            # Track per-episode return from the stream the learner receives.
            episode_return += reward

            # SARSA update from previous transition.
            curr_a_idx = bot.choose_action(curr_s)
            if bot.last_state is not None:
                bot.update(bot.last_state, bot.last_action_idx, reward, curr_s, curr_a_idx, done)

            if done:
                bot.last_state = None
                bot.last_action_idx = None
                awaiting_action_result = False
                terminal = grid_data.get("terminal", "unknown") if isinstance(grid_data, dict) else "unknown"

                terminal_signature = (episode_index, env_episode_step, terminal)
                if terminal_signature != last_terminal_signature:
                    print(
                        "[!] Episode terminated"
                        f" global_step={step}"
                        f" episode={episode_index}"
                        f" episode_step={env_episode_step}"
                        f" terminal={terminal}"
                        f" step_reward={reward:.3f}"
                        f" return(stream)={episode_return:.3f}"
                        f" return(env)={env_episode_return:.3f}"
                    )
                    last_terminal_signature = terminal_signature

                # Reset accumulation for the next episode.
                episode_return = 0.0
                continue

            if awaiting_action_result:
                # Wait until the current move is acknowledged in payload.response.
                continue

            if max_actions > 0 and total_actions_sent >= max_actions:
                print(f"[!] Reached action safety cap ({max_actions}); exiting.")
                break

            bot.last_state = curr_s
            bot.last_action_idx = curr_a_idx

            action_msg = {
                "v": "0.2",
                "type": "action",
                "payload": {"action": bot.actions[curr_a_idx]}
            }
            client.sendall((json.dumps(action_msg) + "\n").encode())
            awaiting_action_result = True
            total_actions_sent += 1
        except Exception as e:
            print(f"[!] Error: {e}")
            break

    reader.close()
    client.close()
    print("[*] Bot disconnected")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--socket", default="/tmp/bbs-agent.sock")
    parser.add_argument("--name", default="sarsa_explorer")
    parser.add_argument("--max-actions", type=int, default=50000)
    args = parser.parse_args()
    run_bot(args.socket, args.name, args.max_actions)
