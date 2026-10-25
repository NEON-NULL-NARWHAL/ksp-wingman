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
3. The Director flies there at 150 m/s (`FlyTo`), never under 500 m above the ground below it, and on arrival notes the
   place: its biome (kRPC's `Vessel.Biome`) and the ground's height. The `place` event goes to the tower (`/v1/place`),
   which merges places within 1 km.

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
