# 02. MCP tools and events

## 02.1 The tools

Tools are `[McpServerTool]` methods in `Tools.cs` and `FlightTools.cs`; the README's table lists them all (AuditKit
checks that it does). Rules every tool follows:

- **Only the agent's own vessel** (kRPC's active vessel). No time warp, no revert: they would move the agent's clock
  away from the other players or rewrite the shared universe.
- **Answers are sentences for a small model**: what happened, the numbers, what to do next ("...next, take_off").
  A refusal starts with "Not" and says why.
- **Everything a model asks is clamped**: headings wrapped, speeds 60-400 m/s, rotate 40-90 m/s, finals fitted to the
  plane's limits, explore legs checked for range and fuel.
- Long jobs (landing, tests, explore) **start and return**: the Director flies them; `status` and events tell how it goes.

The console hides some tools from the pilot model (`McpTools.Hidden`): chat (the console handles it), `events`,
`learn` (only the debrief, with its evidence), `procedures` and `file_plan` (free flight's planner).

## 02.2 status

The one tool to start with: player and server, scene, the vessel (localized name), where and how fast, what the pilot
is doing, the plane's **setup** and **limits** (its knowledge card: final approach speeds, touchdown, glide slopes), its
**fuel** (and endurance at this burn), the tower's last message, and how far behind in time the agent is.

## 02.3 Events

`FlightEvents` keeps the last 1,000 events (and logs them all with `WINGMAN_EVENTS_LOG`, up to 20 MB). Kinds:

| Kind | From | Data |
|---|---|---|
| `phase` | FlightWatch | `from`, `to` (Phases.cs: Parked, TakingOff, Climbing, OnRoute, Holding, OnFinal, Flaring, RollingOut, Stopped, Taxiing, Crashed...) |
| `outcome` | FlightWatch | `FlightOutcome`: result, minutes, go-arounds, approach, landing (down the runway, off the centerline, heading), phases, craft, max speed and height, finals, style, fuel used and left, places explored |
| `tower` | TowerClient | the tower's message kind |
| `knowledge`, `lesson` | TowerClient | the flight's grades (flight id, skills), a saved lesson |
| `test` | FlightTools | a ground or flight test's results |
| `place` | FlightTools | an explored place (latitude, longitude, ground, biome) |

## 02.4 Phases and outcomes

`PhaseTracker` (Phases.cs) reads the plane's own numbers (situation, speed, climb, height), not status text, once a
second, with hysteresis so a plane wobbling on the runway at spawn isn't "taking off". A flight ends landed (stopped
after a landing), crashed, splashed, or otherwise (recovered, scene left). The outcome waits up to 5 s for the
landing's numbers (the rollout can finish a moment after the plane stops).

Stock vessels are named by KSP's translation key (`#autoLOC_501174` is the Aeris 3A): names go through
`FlightTools.Localized` (the bridge's `Localize`) and `Director.CraftOf` (the craft launched).
