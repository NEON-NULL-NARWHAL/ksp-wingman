# Changelog

## 0.11.0 (2026-10-24): flight tests and each plane's own setup

### Added
- **`test_plane`** (the owner's idea: learn each plane from real tests): on the runway, a ground test (a roll with the
  nose wheel straight, a steering pulse, a brake stop; about 200 m); in the air, a flight test (an elevator and an
  aileron pulse, a 180° turn, slowing at idle until it can't hold its height, full throttle; about four minutes from
  1,500 m above the ground). The numbers are compared with the Aeris 3A's, measured the same way in the rig.
- **Each plane's own setup**, worked out from its tests: elevator, aileron and nose wheel gains, rotate, approach and
  landing speeds, how slow to fly before the final; corrected by its last finals (weaving, arriving high). `launch` and
  `take_off` load it; `status` shows it. With LUNA EVO's tower (Mission Control 2.17) the tests are shared by every agent.
- **A faster final** (the owner's idea): planes join the final faster and slow down along it, as fast as their measured
  slowing allows, to be at landing speed 1.5 km out.
- **Risk the landing, then go around** (the owner's idea): far out only a plane that's badly off (250 m from the
  centerline, 120 m high) goes around; the decision is made 600 m before touchdown (within 60 m of the centerline, at
  most 40 m high).
- **Fast planes stop snaking on final**: the pilot aimed at the centerline 3 s ahead, fine at the Aeris 3A's 90 m/s
  but far too close for a plane turning 4-5 km wide at 140 m/s (the rig's Aeris 4A snaked ±400 m on every final and
  went around three times). The aim point now grows with the speed cubed above 90 m/s (1.6 km at 140 m/s), and each
  centerline crossing on a final stretches it further; a laggy simulated plane that crossed three times now crosses
  at most once and is lined up at the decision point.
- **Brakes by the runway left, and a touch-and-go when it can't stop**: the brakes come on at any speed when the runway
  left is short for this plane's measured braking (they waited for 60 m/s, and the rig's Aeris 4A, touching down at
  93 m/s, ran off the end); too fast to stop and still above its rotate speed, it goes around from the runway. Above
  60 m/s the brakes hold only while the nose is very steady (within 4°, turning under 3 °/s), like anti-skid.
- **A bounce is still the rollout**: after touchdown, "flying" under 4 m is treated as rolling out; the rig's Aeris
  4A hopped at 58 m/s, below its stall, and the landing sent it around into the ground.
- **Training on new planes** tests each one (ground, then air) before landing it, and the debrief writes a note about
  how it differs from the Aeris 3A; a note quoting numbers the tests didn't measure isn't saved.
- Reports tell the tower the plane's approach speed (fast planes get the long approaches); "Offline" when the agent's
  KSP isn't in a server; "leaving" when it stops.
- FlightLab `ground`, `test` and `steps` modes (the reference numbers, and turns with given gains).

### Fixed
- **The pilot couldn't lift a nose-heavy plane's nose**: at most 0.1 of trim, the Aeris 4A flew level at 150 m/s with
  its nose 2° up, sinking into the sea after every takeoff. The trim may now grow to 0.4 (fast only while the quick
  corrections are at their limit, so the Aeris 3A flies as before).
- Takeoff steering is damped by the turn rate (undamped, the Aeris 3A swung ±10° for 12 s after a pulse at 14 m/s).
- A repeated go-around report (every 2-4 s until 300 m) and a new plane put in the hold from the last plane's request.
- The fly-high drill flies a square over KSC (its back-and-forth legs spiralled 48 km away).
- Stock planes were named "#autoLOC_501174" in flight records.

### In the test rig
- The Aeris 3A, tested twice the same way: pitch 55 and roll 408 °/s per stick, slowest 54 m/s, nose wheel 25-27 °/s,
  brakes 9 m/s². Its setup from that (landing 73 m/s, rotate 54) matches the hand-tuned one (75, 55).
- The Aeris 4A had never landed: it sank into the sea after every takeoff (not enough trim), then stalled at the Aeris's
  speeds (its slowest is 71-74 m/s), snaked on every final, ran off the end of the runway, and crashed in a bounce.
  Fixed one by one, it landed 7 m off the centerline, 1,817 m down the runway, and taxied off.

## 0.10.0 (2026-10-24): rejected takeoffs and go-arounds

### Added
- **Rejected takeoff** (the owner's idea): a takeoff roll veering more than 12° off the runway, under half its rotate
  speed after 30 s, or not up to it after 45 s is stopped (throttle off, brakes on), and take_off says why; free flight
  counts it as a failed attempt and tries again (two in a row stop it).
- **Go-arounds reported**: going around, the agent tells LUNA EVO's tower (Mission Control 2.16), which resequences
  it; a clearance that arrives during the climb-out is flown from 300 m above the ground.

- **Notes about planes**: the debrief can mark a lesson as being about the flight's plane only (`| PLANE`): how it
  handles, its speeds, how it differs; `learn` takes `aboutPlane`, and `knowledge` shows each plane's profile and what's
  still to learn about it (LUNA EVO's tower keeps them).

### Fixed (found in the rig)
- Training on new planes with no plane left in the save's SPH spun without pause: it stops with the reason (and says
  to put planes in the save's Ships/SPH); free flight stops after five rounds without a flight.

## 0.9.0 (2026-10-24): training and flight plans

### Added
- **Training on request** (the owner's idea): `/train fast|high|landings|visual|rnav|planes|plans [minutes]`, the
  `train` tool for the pilot model ("go practise flying high"), `{"type":"train"}` in `--ipc`: free flight on one
  focus until learned (3 good in a row). New drills: fly fast (220 m/s legs), fly high (6,000 m), different planes.
  Flight records carry the top speed and height.
- **Flight plans of its own**: tools `procedures` (LUNA EVO's tower's table) and `file_plan`; in free flight the
  planner builds a plan from the table (FlightPlan.cs: the table as text, the tower's rules checked before filing).
  PlannerEval scores plans too (gemma4:e4b 5/5 valid, 4.4 fixes; qwen3:8b 0/5, it wrote essays).

### Fixed (found in the rig)
- A filed plan's or a direct's route wasn't recognized as one ("Flying FILED ...", "Flying DIRECT ...").
- Training on one goal still asked the planner to pick it.
- On a flight where every grade was good the debrief wrote trivial lessons: it's asked for NONE then.

## 0.8.0 (2026-10-24): runway in sight

### Added
- **"Runway in sight"** (the owner's idea): on final, within 10 km of the touchdown point and 300 m of the centerline,
  the agent calls the runway in sight to LUNA EVO's tower (Mission Control 2.14), which clears it to land.

### Changed
- **gemma4:e4b as the pilot**: ModelEval 24/24 in 56 s (qwen3:4b-instruct: 24/24). With it as the planner too, two rig
  agents flew free flight on that one model (0.4 GB of the graphics card next to two KSPs): short visuals 12, 8 and 6 m
  off the centerline, every landing cleared after "runway in sight".

## 0.7.1 (2026-10-24)

### Changed
- Steers back to the centerline twice as hard on the rollout (1° per 5 m off, up to 6°): with LUNA EVO's longer short
  visual, landings in the rig stopped 10-11 m off the centerline (were 16-23).
- The debrief accepts a skill written as a name ("RNAV Landing", "Landing"), matched to that flight's own skills, and
  the prompt lists the skill ids: in the planner eval most lessons were lost to that.
- A landed flight's record waits up to 5 s for the landing's numbers (a 10 m landing was once recorded without them).

### Added
- **PlannerEval** (`tests/PlannerEval`): compares planner models on real flight records and free-flight menus through
  the console's own code, no KSP. First results (2026-10-24, thinking on): gemma4:e4b 6/6 lessons, 3/3 weakest picks,
  3.1 s mean; qwen3:8b 6/6, 3/3, 28.6 s; qwen3:4b 2/6, 3/3, 14.4 s. gemma4:e4b is the suggested planner.

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
