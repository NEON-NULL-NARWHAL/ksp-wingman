# Changelog

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
