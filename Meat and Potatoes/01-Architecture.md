# 01. Architecture

KSP Wingman lets a local language model fly a plane in Kerbal Space Program, with other players on a Luna
Multiplayer (LMP) server. The model thinks in seconds; code flies in milliseconds. That split runs through everything.

## 01.1 The programs

| Program | What it does | Talks to |
|---|---|---|
| **The console** (`KspWingman.exe`, `src/KspWingman.Tui`) | Takes orders (the terminal, LMP chat from allowed players, or `--ipc` from LUNA EVO Mission Control), runs the pilot model, the planner model, free flight, training and the debrief. | The MCP server (child process, stdio), a model server (Ollama or llama.cpp, OpenAI API) |
| **The MCP server** (`KspWingman.Mcp.exe`, `src/KspWingman.Mcp`) | The tools: status, launch, take off, fly, land, explore, tests... and the flying itself (the Director, 20 times a second). Also usable from any MCP client (Claude Code). | kRPC in the agent's KSP; LUNA EVO's tower (HTTP, localhost) |
| **The bridge** (`GameData/KspWingman`, `src/KspWingman.Bridge`) | A kRPC service inside KSP: LMP's player name, chat, players, free kerbals, dialogs, localized names. | KSP and LMP (in process) |

```
 orders ─▶ console ──stdio/MCP──▶ MCP server ──kRPC──▶ KSP (+ bridge, + LMP) ◀──LMP──▶ other players
             │                        │
             ├─ model server (HTTP)   └─ LUNA EVO tower (HTTP, localhost): clearances, knowledge, tests, places
```

## 01.2 Who decides what

- **The pilot model** (the console's model) turns an order into tool calls: "land at KSC" → `land`.
- **The planner model** (`--planner-model`, e.g. `gemma4:e4b`) chooses: what free flight practises next, a flight plan,
  an explore leg, a riskier final, a lesson after a flight. It never touches the controls.
- **Code** flies and checks: the Director holds a heading, height and speed; the landing flies finals; every model
  choice is checked (a filed plan against the table, an explore leg against range and fuel, a final against the
  plane's limits, a lesson's numbers against what was measured). A model can't fly a plane into the ground by
  answering badly; at worst it picks a dull goal.
- **LUNA EVO's tower** (optional, `WINGMAN_TOWER_URL`) sequences planes, clears approaches, and keeps what every
  agent learned (skills, lessons, plane tests, explored places) on the server.

## 01.3 Data flow of one flight

1. `launch` → KSP puts the craft on the runway; the Director loads the plane's setup (its tests, from the tower).
2. `test_plane` (training on a new plane) → the ground test; `take_off`; `test_plane` again → the flight test.
3. `land` → the tower clears a route (or holds the plane); the Director flies it, the final, the rollout.
4. FlightWatch (once a second) turns the plane's numbers into phases and, at the end, an outcome (with finals, fuel,
   the approach style, places explored). The tower client sends it to the knowledge store, which grades the skills.
5. The console's debrief asks the planner for a lesson from it; code checks it; `learn` saves it on the server.

## 01.4 Settings

The MCP server reads its environment: `WINGMAN_KRPC_HOST` (127.0.0.1), `WINGMAN_KRPC_PORT` (50000),
`WINGMAN_TOWER_URL` (localhost only), `WINGMAN_EVENTS_LOG` (a JSON-lines log of every event). The console takes
`--model`, `--planner-model`, `--llm`, `--krpc-port`, `--allow`, `--ipc`, `--call-sign`, `--mcp`.
