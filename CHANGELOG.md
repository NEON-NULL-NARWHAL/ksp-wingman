# Changelog

## Unreleased (0.2: flying)

### Added
- Flying tools: `launchable_craft`, `launch` (runway or launch pad), `fly` (heading, pitch, throttle on kRPC's
  autopilot; only in the air), `take_off` (rolls straight on the nose wheel, rotates at takeoff speed, climbs out),
  `stage`, `set_controls` (brakes, gear, SAS, lights), `stop_flying`, `recover`. No time warp and no revert, on purpose.
- `status` shows stock vessel names and the right pitch and heading.

### Found in the rig
- The autopilot can't steer a plane on the ground: the first test flight swerved off the runway into the hangar. `fly`
  now refuses on the ground; `take_off` is still to be tried in the game.

## 0.1.0 (2026-10-22)

### Added
- **The chat companion.** The bridge mod (a kRPC service, `Wingman`) and the MCP server with five tools: `status`,
  `players`, `read_chat`, `send_chat` and `wait_for_chat`. Checked with two real KSPs on one LMP server.
