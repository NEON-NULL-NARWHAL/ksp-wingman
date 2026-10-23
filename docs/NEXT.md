# Working on next

Short list for the next sessions, after 0.3.0 (2026-10-23). Ideas are the owner's unless marked; thoughts and plans
are ours. Nothing here is built yet.

## Fix first

- **Rollout swerve.** Both rig landings stopped pointing well off the runway heading (75° and 37°, 880-1,161 m down
  the runway): the nose-wheel steering and brakes over-correct at walking speed. Plan: scale steering down with
  speed (none below ~5 m/s), brakes gently below 20 m/s, and stop steering once stopped. Test in `LandingTests`
  (steer command vs speed) and in the rig.
- **Lining up on the first try.** The second agent landing went around once (450 m off the centerline at 2 km). The
  circuit's side point and the 45° intercept work, but the turn onto final at 20° of bank is wide. Plan: allow 30° of
  bank on final far out (20° only below ~300 m), and start the turn earlier.

## The owner's idea: an AI control tower with RNAV routes

> Give the AI options of circuits, set up fake RNAV routes for planes like in real life so they're easier to follow,
> and a list of approaches. The agent asks the "approach" program for the best approach for its direction and
> conditions; the tower answers and sends the route (waypoints, speeds and heights to meet), and the agent flies it.
> It would be cool to see the tower's output in game.

**Our thoughts: yes, and it fits what we found.** Today's landing trouble was all *route-finding* (where to turn,
when to join final), not flying. Pilots don't work that out in the air either: they're handed published procedures.
Splitting it the same way makes the agent's job simpler (a small model picks "RNAV 09 from the north" instead of
doing geometry) and the flying more predictable.

How it could look:

- **Procedures as data**, not code: a file of runways, waypoints (name, lat/lon) and approaches, each a list of legs
  with altitude and speed constraints ("KSC-N1 at or above 1,500 m, 140 m/s → KSC-F09 at 560 m, 90 m/s → runway").
  Start with KSC 09/27 (both directions), the Island Airfield, and the Desert Airfield; anyone can add more.
- **A route follower** in the pilot: fly to each waypoint, meet its altitude and speed, turn early by the turn radius
  (fly-by waypoints), then hand over to today's final/flare/rollout. This replaces the hand-made circuit in
  `Landing.cs` and is easy to test without KSP, like `Formation`.
- **The tower** picks an approach from where the plane is, its heading, the wind (KSP has none without mods) and
  traffic: another plane already on approach gets sequenced (one on final at a time, the next holds at a fix). A
  first version is plain rules, not AI; a model can add the talking.
- **Tools for the agent:** `approaches(airport)` (the list), `request_approach(airport)` (the tower's pick, with the
  full route), `fly_approach(name)`. A small model handles these well: short choices with names.
- **In game:** the tower speaks in LMP's chat as its own call sign ("Wingman, KSC tower: cleared RNAV 09 north,
  descend to 1,500 m"), and waypoints could be drawn on the map later (LUNA EVO Mission Control's map already shows
  orbits; it could show the routes too). Real players could ask the tower too.
- **With several agents** (or agents and players), the tower is where sequencing lives, and it fits the shared
  knowledge store: approaches that worked go there.

Plan, smallest first: (1) the procedures file and route follower for KSC 09 with two approaches, tests;
(2) `approaches`/`fly_approach` tools, eval scenarios; (3) the tower's choice and chat lines; (4) more airports and
sequencing.

## Using a local model to save tokens (ours)

Most of today's tokens went to *watching*: reading status lines every 10 s, checking logs, waiting for planes. A
local model on the same PC can do the watching and only report what matters.

- **A rig watcher:** a small script that tails the test logs and asks the local model (Qwen3 4B instruct, ~1 s)
  "anything wrong or worth reporting?" with strict rules, printing one line only on events (crash, go-around,
  landed, stuck). It replaces most of the Monitor notifications. The script's own checks (regexes for "destroyed",
  "Stopped", numbers out of range) come first; the model is for summaries, not for deciding what's safe.
- **Summaries of long output:** test logs, LMP server logs, eval runs → a few lines.
- **Longer rig runs** with the Wingman console as the "player" (it already takes orders): scripted order lists
  ("take off, fly to 1,500 m, land") run by the console, with the watcher summarizing at the end.
- **What it shouldn't do:** decide fixes, write code, or judge correctness on its own: the 4B model claims things it
  didn't do (see `docs/RESEARCH.md`). Its output is a hint to check, not a result.

Plan: (1) `tests/RigWatch` (a log tailer with rules plus an optional local-model summary line), (2) order scripts
for the console (`--script orders.txt`), (3) use both in the next landing and tower tests.
