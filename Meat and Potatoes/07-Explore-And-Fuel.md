# 07. Explore and fuel

The owner's idea: a freer free flight, where the agent decides where to go and learns the map, and the groundwork for
longer flights. The planner decides; code checks.

## 07.1 Explore

Each explore flight is up to four legs (`Learner.ExploreAsync`, prompts in `Drills.cs`):

1. The planner sees where the plane is, its fuel, and what the server's agents explored (the `knowledge` tool's
   "Explored" section: places, biomes, the highest ground, the last few), and answers `GO <bearing> <km> <altitude>`
   or `HOME`.
2. The `explore` tool checks the leg (`Exploring.Check`): 1-30 km, the end within 60 km of KSC, and enough fuel to get
   there and back to KSC keeping 25% (with the burn not known yet, the first minutes, within 30 km). A refused leg goes
   back to the planner with the reason; two refusals in a row and it heads home.
   Then **the ground on the leg** (0.12.1): kRPC's `SurfaceHeight` every 1 km and 2 km past the end. The leg's altitude
   is raised to clear the highest point by 500 m (`SafeAltitude`; the answer says "raised from..."), and a leg whose
   ground rises faster than the plane climbs is refused (`TooSteep`: counted at 5 m per 100 m, `ClimbGradient`).
3. The Director flies there at 150 m/s (`FlyTo`), never under 500 m above the ground below it **or up to 12 km ahead**
   (`AheadKm`, looked up every 2 s along the way to the target and, in a turn, where the nose points). Ground ahead too
   steep to climb: it turns for KSC and says why (an `explore` event; the console hands it to the planner with the next
   leg). On arrival it notes the place: its biome (kRPC's `Vessel.Biome`) and the ground's height. The `place` event
   goes to the tower (`/v1/place`), which merges places within 1 km.

   Why (rig, 2026-10-25, Flight Lab's first run): looking only at the ground below, a leg at 1,500 m flew into a
   1,717 m peak (the ground rose 22 m/s, the plane climbed 10); then, with the leg raised, a turn at 210° into higher
   mountains asked for 4,500 m from 2,800 and hit a face 2 km on. The pilot settles near 9 m/s of climb at 150 m/s (its
   climb loop is proportional only), so legs are planned for what it can climb, not what it's asked to. Checked after
   in the rig: raised a leg to 1,839 m, turned back from a 2,496 m ridge 12 km ahead, and landed 1 m off the centerline.

Then it lands. The tower grades **explore** (3 places and back) and, for flights of 20 minutes or more, **long flight**
(landing with a quarter of the fuel or more).

## 07.2 Approach variety

The planner picks a riskier final (`Drills.Styles`: steep 6°, shallow 3°, fast, slow, long), seeing the plane's limits;
`land` gets the matching `glideSlope`, `finalSpeed` or the 12 km straight-in, fitted to the plane. The flight record
carries the style; the tower grades **approaches** when a chosen final lands well.

## 07.3 Fuel

`FuelGauge` (FlightWatch feeds it once a second): the tanks' liquid fuel as a fraction of full, the burn over the last
two minutes, the endurance at that burn. `status` shows it; explore heads home below 35%; outcomes carry the fuel used
and left, and the tower works each plane's endurance on full tanks out of its flights. Next steps: oxidizer and
electric charge for rockets and other planes, refuelling, planning a flight's length from the endurance.
