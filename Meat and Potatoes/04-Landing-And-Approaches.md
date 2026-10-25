# 04. Landing and approaches

## 04.1 Routes and the final

`Landing.Step` is pure arithmetic in the runway's frame (meters along from the touchdown point, 300 m past the
threshold, and right of the centerline). Before the final it flies a route: a published approach (`Approach.Ksc`:
RNAV 09 NORTH, SOUTH, STRAIGHT-IN), the tower's route, or the old visual circuit. Fix speeds are fitted to the plane
(never under its approach speed; no faster than its circuit speed when it's hard to slow down).

On the final it follows the centerline and the glide slope:

- **Aim point**: 3 s ahead up to 90 m/s, growing with the speed cubed above it (1.6 km at 140 m/s): a plane turning
  4-5 km wide snaked ±400 m aiming 3 s ahead. Each centerline crossing on this final stretches it 50% more.
- **Speed**: `PlaneSetup.FinalSpeed`: as fast as it can still slow to its landing speed by 1.5 km out
  (v² = landing² + 2·a·d), at most its approach speed + 20, or the agent's chosen final speed.
- **Glide slope**: 4°, or the agent's choice (3-6°, fitted). Below a steeper slope (the fixes are at 4° heights) it
  flies level until it meets it, never climbing up to it.

## 04.2 Go-around: risk the landing, decide late

Far out only a plane badly off goes around (250 m off the centerline inside 1.5 km, 120 m high inside 1 km, the
ground under 80 m). The **decision point** is 600 m before touchdown: within 60 m of the centerline and 40 m of the
slope, or around it goes. Finals report how they went (`FinalReport`: centerline crossings, height 1 km out) when they
end (flare or go-around).

## 04.3 Flare, rollout, brakes

Flare at 8 m: throttle shut, sink 1 m/s. On the ground (`Landing.Rollout`): the nose wheel steers back onto the
centerline (fading at speed, damped by the yaw rate), the rudder helps while fast. Brakes:

- below 60 m/s with a steady nose (within 8°, under 5 °/s), as always;
- **faster too when the runway left is short** for the plane's measured braking (stopping distance at half the
  measured rate, ×1.3 + 150 m), only while the nose is very steady (6°, 6 °/s; the turn rate is smoothed);
- **touch-and-go** when it can't stop in what's left but is above its rotate speed: full throttle, nose up, and the
  landing goes around from the air.

A bounce after touchdown (KSP says "flying" under 4 m) is still the rollout: once committed below rotate speed, no
go-around. The phases follow the pilot's words too ("Landing, rolling out" is a flare in the air and a rollout on the
ground), or a bounce makes a landing "landed off a runway".

## 04.4 Takeoff and taxi

`take_off` rolls on the nose wheel (damped by the turn rate, scaled by the plane's SteerGain), rotates at the plane's
rotate speed on kRPC's autopilot, and hands over to the pilot at 100 m. `TakeoffCheck.Reject` stops a roll veering
more than 12° or too slow. `Taxi` (the tower's "vacate") turns off right and parks 90 m beside the centerline.
