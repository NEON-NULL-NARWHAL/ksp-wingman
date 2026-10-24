# Changelog

## 0.7.0 (2026-10-24): free flight and learning

### Added
- **Learning, on the server** (LUNA EVO Mission Control 2.13's tower): every finished flight goes to the server's
  knowledge store, which grades the skills it used (take off, fly a route, hold, land visual, land RNAV, land in
  sequence). New tools: `knowledge` (each skill's numbers, yours and everyone's, which are open, the lessons, your last
  flights) and `learn` (a lesson citing one of your flights; the console's debrief uses it).
- **The planner**: `--planner-model` (and `--planner-llm`), a model that thinks (with Ollama, through its own API so
  thinking is on), for decisions and debriefs; it never touches the controls. Default: the pilot's model.
- **The debrief**: after each recorded flight the planner writes one lesson from the record; the console checks it
  (advice for a skill the flight used, not a report) and saves it with the flight as evidence.
- **Free flight**: `/free [minutes]` (or `{"type":"free"}` in `--ipc`): the planner picks a goal from a menu (the
  approaches to practise, with their numbers), code flies it inside a box around KSC (25 km, 3,000 m), stops after two
  crashes in a row, drops a goal that failed twice without a lesson; any order or `/stop-free` stops it.
- `land` with an approach's name asks the tower for it (granted when nobody else is waiting); the tower's short visual
  is flown like any route.

### Fixed (found in the rig)
- A launch with no kerbal free stopped on KSP's "No Control!" question and never returned (every kerbal was in a
  parked plane): `launch` now says so instead (the bridge counts free kerbals).
- After a recovery, KSP's Mission Summary held the next launch up: the bridge closes it before launching.
- Taxiing reached 8.9 m/s on the grass (brakes only above 9): it brakes above 7 now.
- `/status` from a host app is answered at once instead of waiting behind an order.

### Changed
- The tower's fixes are flown from 300 m (was 400), for the short visual's final fix (LUNA EVO's audit 15).

## 0.6.0 (2026-10-23): events, --ipc, taxiing

### Added
- **Flight events**: the MCP server watches the plane (once a second, its own kRPC link) and keeps what happened:
  phases (parked, taking off, climbing, cruising, descending, on a route, holding, on final, flaring, rolling out,
  stopped, taxiing, crashed, splashed), each flight's **outcome** (landed, crashed, splashed, ended in the air, landed
  off a runway; minutes, go-arounds, the approach, and for a landing how far down the runway it stopped, how far off
  the centerline and the heading) and the tower's messages. New tool `events(afterId)`; `WINGMAN_EVENTS_LOG` writes
  them to a file too. The console shows them.
- **`--ipc`** on the console: JSON lines in (orders, status, forget, quit) and out (everything it shows, plus the
  events), for apps. LUNA EVO Mission Control 2.12's Wingman page uses it.
- **Taxiing off the runway**: told to vacate, the agent turns right at walking pace (6 m/s, braking above 9) and
  parks 90 m beside the runway, brakes on, instead of recovering the plane. The tower clears the next plane once it's
  off.

### Found in the rig (two KSPs, two agents, one tower)
- RigPilot landed 1,329 m down the runway, 8 m off the centerline, taxied off and parked 98 m beside it; the tower
  cleared RigViewer 14 s after the landing, and it landed 1,037 m down, 16 m off, and taxied off too.
- Fixed from it: a plane settling after it spawns looked like a takeoff roll (now more than 3 m/s to start rolling,
  under 1 to stop), and the end of a taxi, still braking, looked like a takeoff.

## 0.5.1 (2026-10-23)

### Security
- The agent only flies a tower's route or hold that makes sense (LUNA EVO Mission Control's audit 13, T3): at most 12
  fixes, each within 60 km of KSC, at 400-5,000 m and 60-250 m/s; a hold within 60 km, at 400-5,000 m, on a 1-5 km
  circle. Anything else is refused, and status says so.
- A tower address of 127.0.0.1 is used as localhost (the tower's listener only answers to that name).

## 0.5.0 (2026-10-23): a real tower

### Added
- **LUNA EVO Mission Control's KSC tower** (2.11): with `WINGMAN_TOWER_URL` (localhost only), the agent reports to it
  every 3 s, answers "say intentions" from what its pilot is doing, and flies what it's told: a cleared route (the
  tower sends the fixes), a hold (right-hand circles around a point at an altitude), and vacating the runway after
  landing (it recovers the plane: an agent can't taxi). `land` asks the tower when there is one; status shows the
  tower's last message.

### Found in the rig (two KSPs, two agents, one tower)
- RigPilot was cleared for RNAV 09 SOUTH, RigViewer held as number 2 at KSC-HS for six minutes, RigPilot was cleared
  to land, landed and vacated, and RigViewer was cleared the same second.

## 0.4.0 (2026-10-23): the tower

### Added
- **Published approaches and a tower** (the owner's idea): KSC runway 09 has three RNAV approaches (straight-in, north
  and south traffic patterns: downwind, base, the extended centerline, the final fix). `land` asks the tower, which
  picks one for where the plane is and gives a clearance ("KSC tower: Wingman, cleared RNAV 09 NORTH. Proceed to
  KSC-N1 at 900 m, 110 m/s, then KSC-N2, KSC-N3, F09"); status shows it. `land(approach)` picks one yourself, or
  `"visual"` for the old circuit. A new tool, `approaches`, lists them.
- The route follower flies the fixes: it stays on the line between them, turns early by the turn's radius (fly-by
  waypoints), and meets each fix's height and speed. After a go-around the tower clears a new approach from wherever
  the plane is.

### Changed
- Rollout: the nose wheel's steering fades at high speed and at walking speed, every axis is damped by the yaw rate,
  the rudder helps while it's fast, and the brakes wait for a steady nose below 60 m/s.
- Final: up to 30° of bank far out (20° made the turn onto final too wide), 20° near the ground; touchdown aimed 300 m
  past the threshold (was 400) and the flare starts at 8 m (was 12, which floated for 600 m).
- The landed message says which way the plane points when it stops.

### Found in the rig (four tower landings, with RigRun and RigWatch)
- Every tower approach lined up the first time (on final 9 km out about 240 m off the centerline, 3 m off at the flare).
- Rollout, in order: brakes only within 4° of the runway heading ran the plane 475 m off the end; brakes from 65 m/s
  without damping ground-looped it (111° → 63° → 197° at 57 m/s, seen in the recording); with damping it stopped
  straight but 77 m from the end; with the earlier touchdown: 1,153 m down the runway, 11 m off the centerline,
  3° off the heading.
- The eval has a new order ("land at KSC using the north approach"): Qwen3 4B instruct 24/24.

## 0.3.0 (2026-10-23): local models

### Added
- **The Wingman console** (`KspWingman.exe`, `src/KspWingman.Tui`): a model on your own PC flies the agent's KSP.
  Any OpenAI-compatible server: Ollama, llama.cpp's `llama-server`, others. It starts `KspWingman.Mcp.exe` and uses
  the same tools. Orders from the console, or from LMP's chat by players on `--allow` who start with the call sign
  ("Wingman, land"); the answer goes back the same way.
- **`land`**: KSC's runway from anywhere in the air (final approach 8 km west of KSC, 4° glide slope, flare,
  rollout on the centerline, brakes; goes around by itself when it isn't lined up).
- `take_off` takes the altitude and speed to fly on at afterwards (one call for "take off and fly east at 1000 m").
- `tests/ModelEval`: tries a model on seven orders with the real tool list and scripted game answers, no KSP.
- `docs/RESEARCH.md`: ship takeover, mission control, crew rescue, the shared knowledge store, a craft generator.

### Changed (for small models, from the eval)
- Tool calls written out as text are read as calls; an answer that claims an action whose tool wasn't called gets a
  reminder, and the second time only that tool is offered (Ollama ignores `tool_choice`).
- Tools clamp silly numbers: `take_off` rotates at 40-90 m/s, a `follow` slot can't sit on top of the leader.
- `launch` says what comes next (`take_off`, or `stage` on the pad).
- Chat answers are one line without emoji (KSP's font has none); answers say what's happening, not that it's done.

### Found in the rig (and fixed)
- Flown straight at the start of the final, the plane got there heading the wrong way and its 180° turn ended 4 km
  off the centerline: `land` now flies a circuit (out past the start and to the side, then back in), latched so it
  doesn't turn back and forth across the line, and joins the centerline at up to 45° far out.
- West of KSC is land, not sea: on final, a hill closer than 80 m under the plane far out means go around.
- A small model asked `take_off` for heading 0 from the east-west runway: it now always rolls and climbs out along the
  runway, and turns to the asked heading once airborne.
- Quitting the console mid-landing left the plane on SAS, which held it in its descent into the hills: `/quit` now
  warns once while the pilot is flying.
- Joining the final only at its exact start made the plane miss the point and circle back to chase it: out past the
  start and roughly facing the runway, it joins where it is.
- Measured: `land` from 8 km out ended 4-5 m off the centerline at touchdown and stopped 1,161 m down the runway.

## 0.2.0 (2026-10-23): the wingman

### Added
- **Flying tools:** `launchable_craft`, `launch` (runway or launch pad; also right after a crash), `take_off` (rolls
  straight on the nose wheel, rotates at takeoff speed, gear up, climbs out), `fly` (heading, altitude, speed),
  `follow` (formation on another player, found through LMP's control lock), `stop_following`, `stage`, `set_controls`
  (brakes, gear, SAS, lights), `stop_flying`, `recover`. No time warp and no revert, on purpose.
- **Our own plane pilot**, in the MCP server, 20 times a second between the agent's calls: altitude → climb rate →
  pitch, heading → bank, speed → throttle, each axis damped by its own rate.
- `status` shows stock vessel names, the right pitch and heading, what the pilot is doing, and a destroyed vessel as
  destroyed. The bridge has a new procedure, `PlayerPosition`.

### Found in the rig (and fixed)
- kRPC's autopilot can't steer a plane on the ground (the first flight swerved into the hangar) and dove one into the
  ground in the air, so the pilot is our own, and `fly` refuses on the ground.
- SAS left on by one flying mode fought the next (the leader climbed to 5 km): the pilot keeps SAS and the autopilot off.
- At twice its tuned speed the pilot porpoised a plane into the sea: the controls now soften with the square of the
  speed, and below 150 m and sinking it levels the wings and climbs.
- Catching up, the agent reached 390 m/s; it now flies at most 250 m/s, slows to 160 m/s for big turns (at 270 m/s
  a turn is 13 km wide), closes no faster than it can slow down again, and averages the leader's speed over 2 s (LMP
  moves other players in jumps). The throttle has a direct share as well as its slow trim, so it no longer overshoots.
- From far away or head-on, `follow` flies straight at the slot first.
- A crashed vessel stays "Flying" in kRPC, frozen: status, `fly` and the pilot now see it as destroyed.
- Closing the MCP server mid-flight left the plane with nobody on the stick: it now turns SAS on as it goes.

### Known limits
- In the rig the agent kept within about 10-210 m of its slot, drifting forward and back: a slick jet slows down
  slowly with nothing but the throttle. Air brakes would help; the pilot doesn't use them yet.
- Tuned on the stock Aeris 3A. Other planes fly, but may wander more.

## 0.1.0 (2026-10-22)

### Added
- **The chat companion.** The bridge mod (a kRPC service, `Wingman`) and the MCP server with five tools: `status`,
  `players`, `read_chat`, `send_chat` and `wait_for_chat`. Checked with two real KSPs on one LMP server.
