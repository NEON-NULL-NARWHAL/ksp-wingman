# 🥩🥔 Meat and Potatoes

**The maintainer's guide to KSP Wingman.** The main [README](../README.md) says *what* Wingman does; this folder says
*how*, *where* and *why*, so you can change it with confidence. It mirrors
[LUNA EVO Mission Control's](https://github.com/NEON-NULL-NARWHAL/luna-evo-mission-control/tree/main/Meat%20and%20Potatoes)
guide, which covers the other side of the link (the KSC tower, the knowledge store, the Wingman page).

Every source file starts with a short header pointing back here:

```csharp
// KSP Wingman · src/KspWingman.Mcp/Landing.cs
// Landing a plane on a runway: to a point on the extended centerline, down a 4° glide slope, flare, roll out...
// Guide:   Meat and Potatoes/04-Landing-And-Approaches.md
```

## 🗺️ Chapters

| # | Chapter | Read it when you want to... |
|---|---|---|
| 01 | [Architecture](01-Architecture.md) | see the big picture: the three programs, the KSP mod, kRPC, LMP and the tower, and what talks to what |
| 02 | [MCP tools and events](02-MCP-Tools.md) | add or change a tool, or what an agent hears back (status, events, outcomes) |
| 03 | [The pilot and each plane's setup](03-Pilot-And-Handling.md) | change how planes are flown, the flight and ground tests, or the per-plane setup and its limits |
| 04 | [Landing and approaches](04-Landing-And-Approaches.md) | change finals, go-arounds, the decision point, the rollout, brakes, touch-and-go, taxiing |
| 05 | [The tower client](05-Tower-Client.md) | change what Wingman tells LUNA EVO's tower, or how it acts on clearances and holds |
| 06 | [The console: orders, learning, free flight](06-Console-And-Learning.md) | change the model loop, the debrief, free flight, training, the drills, or the `--ipc` mode |
| 07 | [Explore and fuel](07-Explore-And-Fuel.md) | change exploring, its safety checks, or fuel management |
| 08 | [The bridge (the KSP mod)](08-Bridge.md) | add a kRPC procedure, or change chat, players or dialogs in the game |
| 09 | [Tests and tools](09-Tests-And-Tools.md) | run, read or write tests; FlightLab, ModelEval, PlannerEval; the test rig |
| 10 | [Build and release](10-Build-And-Release.md) | build the zip, bump the version, ship a release |
| 11 | [Gotchas](11-Gotchas.md) | avoid the traps we already fell into. **Read before touching the pilot, landing or the bridge.** |

## 📁 File map

Symbols: 🧑‍✈️ flies the plane · 🛬 landing · 📡 talks to the tower · 🧠 learning and planning · 🔌 kRPC and the game ·
🧪 tests and tools · 📦 build

| | File | What it is | Chapter |
|---|---|---|---|
| 🔌 | `src/KspWingman.Mcp/Program.cs` | The MCP server's start: settings from the environment, the singletons | [01](01-Architecture.md) |
| 🔌 | `src/KspWingman.Mcp/KrpcLink.cs` | The kRPC connection, reopened after KSP restarts; the bridge's procedures | [01](01-Architecture.md) |
| 🔌 | `src/KspWingman.Mcp/Tools.cs` | status, events, knowledge, procedures, file_plan, learn, players and chat tools | [02](02-MCP-Tools.md) |
| 🧑‍✈️ | `src/KspWingman.Mcp/FlightTools.cs` | launch, take_off, fly, follow, land, explore, test_plane, recover, stage, controls | [02](02-MCP-Tools.md) |
| 🧑‍✈️ | `src/KspWingman.Mcp/Director.cs` | The flight loop (20 Hz): hold, follow, land, hold at a fix, fly to a point, the tests | [03](03-Pilot-And-Handling.md) |
| 🧑‍✈️ | `src/KspWingman.Mcp/Pilot.cs` | Heading, altitude and speed to stick and throttle, with the plane's gains | [03](03-Pilot-And-Handling.md) |
| 🧑‍✈️ | `src/KspWingman.Mcp/Handling.cs` | PlaneTest, PlaneSetup (gains, speeds, limits), ApproachStyle, FinalReport | [03](03-Pilot-And-Handling.md) |
| 🧑‍✈️ | `src/KspWingman.Mcp/FlightTest.cs` | The test card in the air: pulses, a turn, slowest speed, full throttle | [03](03-Pilot-And-Handling.md) |
| 🧑‍✈️ | `src/KspWingman.Mcp/GroundTest.cs` | The ground test: a straight roll, a steering pulse, a brake stop | [03](03-Pilot-And-Handling.md) |
| 📡 | `src/KspWingman.Mcp/PlaneBook.cs` | Where a plane's tests and finals come from: the tower, else this session | [03](03-Pilot-And-Handling.md) |
| 🛬 | `src/KspWingman.Mcp/Landing.cs` | The final, glide slope, decision point, flare, rollout, brakes, touch-and-go | [04](04-Landing-And-Approaches.md) |
| 🛬 | `src/KspWingman.Mcp/Approaches.cs` | Published approaches, routes, the tower's pick, holds, taxi, the takeoff check | [04](04-Landing-And-Approaches.md) |
| 🧑‍✈️ | `src/KspWingman.Mcp/Formation.cs` | Formation flying, and the geometry (offsets, Geo) the rest uses | [03](03-Pilot-And-Handling.md) |
| 📡 | `src/KspWingman.Mcp/TowerClient.cs` | Reports, intentions, clearances, holds, go-arounds, flights, tests, places | [05](05-Tower-Client.md) |
| 🧠 | `src/KspWingman.Mcp/Phases.cs` | Flight phases and outcomes from the plane's own numbers | [02](02-MCP-Tools.md) |
| 🧠 | `src/KspWingman.Mcp/FlightEvents.cs` | The events list and log; FlightWatch (phases, outcomes, fuel) | [02](02-MCP-Tools.md) |
| 🧑‍✈️ | `src/KspWingman.Mcp/Exploring.cs` | Explore's geometry and checks (range, leg length, fuel to get home) | [07](07-Explore-And-Fuel.md) |
| 🧑‍✈️ | `src/KspWingman.Mcp/Fuel.cs` | The fuel gauge: tanks, burn, endurance, the reserve | [07](07-Explore-And-Fuel.md) |
| 🧠 | `src/KspWingman.Tui/Program.cs` | The console: orders from the terminal, LMP chat or `--ipc`; free flight; training | [06](06-Console-And-Learning.md) |
| 🧠 | `src/KspWingman.Tui/Agent.cs` | The model loop: an order in, tool calls, an answer out | [06](06-Console-And-Learning.md) |
| 🧠 | `src/KspWingman.Tui/Llm.cs` | The OpenAI-compatible chat API (Ollama, llama.cpp), and the thinking planner | [06](06-Console-And-Learning.md) |
| 🧠 | `src/KspWingman.Tui/McpTools.cs` | The MCP server as a child process; which tools the pilot model gets | [06](06-Console-And-Learning.md) |
| 🧠 | `src/KspWingman.Tui/Learning.cs` | The debrief, free flight's goals and menu, the planner's answers | [06](06-Console-And-Learning.md) |
| 🧠 | `src/KspWingman.Tui/Learner.cs` | Free flight and training run: launch, test, fly the goal, wait, debrief | [06](06-Console-And-Learning.md) |
| 🧠 | `src/KspWingman.Tui/Drills.cs` | The free-will drills: Explore's legs and Approach variety's finals | [07](07-Explore-And-Fuel.md) |
| 🧠 | `src/KspWingman.Tui/FlightPlan.cs` | Flight plans from the tower's table: the planner writes, code checks | [06](06-Console-And-Learning.md) |
| 🧠 | `src/KspWingman.Tui/TrainTool.cs` | The pilot model's `train` tool | [06](06-Console-And-Learning.md) |
| 🧠 | `src/KspWingman.Tui/Ipc.cs` | `--ipc`: one JSON object a line, for LUNA EVO's Wingman page | [06](06-Console-And-Learning.md) |
| 🧠 | `src/KspWingman.Tui/Orders.cs` | Orders from LMP chat: allow list, call sign | [06](06-Console-And-Learning.md) |
| 🔌 | `src/KspWingman.Bridge/WingmanService.cs` | The KSP mod's kRPC service: player, chat, crew, dialogs, names | [08](08-Bridge.md) |
| 🔌 | `src/KspWingman.Bridge/ChatLog.cs` | LMP's chat lines, numbered | [08](08-Bridge.md) |
| 🔌 | `src/Stubs/*` | Stand-ins for KSP and Unity types, so the bridge builds without KSP | [08](08-Bridge.md) |
| 🧪 | `tests/KspWingman.Tests/*` | Unit tests: the pilot on toy planes, landing, tests, drills, learning, IPC | [09](09-Tests-And-Tools.md) |
| 🧪 | `tests/FlightLab/Program.cs` | Flies a real KSP straight through kRPC: probes, holds, the test cards | [09](09-Tests-And-Tools.md) |
| 🧪 | `tests/ModelEval`, `tests/PlannerEval` | Which models fly and plan well, without KSP | [09](09-Tests-And-Tools.md) |
| 📦 | `publish.ps1` | The release zip: console, MCP server, the mod | [10](10-Build-And-Release.md) |
