# 09. Tests and tools

## 09.1 Unit tests

`dotnet test KspWingman.slnx` (CI runs it on every push). Everything that decides how to fly is pure and tested
without KSP:

| Tests | What |
|---|---|
| `FlyingTests`, `HandlingTests` | the pilot on toy planes (a simulated plane with lag and a stall: the flight test must measure what the simulation is; a nose-heavy one must be trimmed up; a laggy fast one must settle onto the centerline), the setup from tests and finals, the ground test |
| `LandingTests`, `ApproachTests` | finals, go-arounds, the decision point, the rollout and brakes, touch-and-go, routes, holds, taxi, the takeoff check |
| `DrillTests` | explore's legs (range, fuel), the fuel gauge, the planner's answers, final approach limits, a steep final |
| `LearningTests` | the debrief's checks, free flight and training with fake tools and a fake planner |
| `PhaseTests`, `IpcTests`, `ChatTests`, `ConsoleTests`, `TowerClientTests` | phases and outcomes, `--ipc`, chat orders, the console, the tower's answers |

A toy plane is cheaper than a rig run: when the rig shows a problem, reproduce it in a test first (the snaking 4A was
reproduced as "a laggy simulated plane at 140 m/s crosses three times") and fix it there.

## 09.2 FlightLab

`tests/FlightLab`: flies a real KSP through kRPC, logging what the plane does. `probe` (which way the inputs move it),
`hold` (the pilot on legs), `steps <rollGain> <pitchGain>` (30° heading steps, with `FLIGHTLAB_CRAFT` for the plane),
`test` and `ground` (the test cards on the Aeris 3A: the reference numbers), `camera`, `show`, `wreck`. It needs a
free kerbal and leaves planes in the universe: clean up after.

## 09.3 ModelEval and PlannerEval

`tests/ModelEval`: which pilot models call the right tools for orders (24 cases). `tests/PlannerEval`: which planner
models pick goals, write lessons and file valid plans from recorded flights (`flights.json`, `procedures.json`),
without KSP. Results are in [docs/RESEARCH.md](../docs/RESEARCH.md); gemma4:e4b is the recommended model for both.

## 09.4 The test rig

Two stock KSP 1.12.5 copies with kRPC and LMP on a private test server, driven by LUNA EVO Mission Control with two
agents. Our own rig tools (not shipped) start and stop it, clean leftover planes, and summarize a session's logs
(flights, tests, trainings, finals, what looked wrong) in a few lines. Never run it against anyone's real save.
