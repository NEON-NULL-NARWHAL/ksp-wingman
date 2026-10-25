# 03. The pilot and each plane's setup

## 03.1 The Director and the pilot

`Director` runs one flying mode at a time on its own kRPC connection, 20 times a second: hold (a heading, height and
speed), follow (formation), land, hold at a fix, fly to a point (explore), the flight test and the ground test. Every
second it turns SAS and kRPC's autopilot off (either fights the pilot) and checks for a wreck. Each mode is a function
that sees the plane and returns what to fly (`PlaneTarget`) or, for the test pulses, the stick itself (`PlaneInputs`).

`Pilot.Step` turns a target into inputs, slowest loop outside: altitude → climb rate → pitch → elevator; heading →
bank → ailerons; speed → throttle. Surfaces bite with the square of the speed (`authority`), every axis is damped by
its own rate, and below 150 m going down it levels the wings and climbs ("too low"). The quick part of the elevator
stays within a quarter stick; a slow **trim** (what the plane needs to fly level) may grow to 0.4, fast only while the
quick part is at its limit. (At 0.1 the nose-heavy Aeris 4A sank into the sea after every takeoff.)

## 03.2 Each plane's setup

`PlaneSetup` (Handling.cs) is the pilot's numbers for one plane; the defaults are the Aeris 3A's, the plane everything
was tuned on. `PlaneSetup.From(test, finals)` works it out from the plane's tests and its last five finals:

| Setting | From |
|---|---|
| PitchGain, RollGain | the Aeris 3A's pitch and roll rates over the plane's (a nose twice as quick gets half the elevator), 0.4-2.5 |
| SteerGain, Braking | the ground test: the nose wheel's turn rate, the brakes' deceleration |
| RotateSpeed | its slowest speed (40-90 m/s) |
| LandingSpeed, ApproachSpeed | 1.35× its slowest (60-100 m/s), +15 |
| CircuitSpeed | slower before the final when it hardly slows at idle |
| SlowingRate | how fast it can be planned to slow on the slope: 60% of the idle deceleration less what sinking gives back |
| corrections | each weaving final among the last five softens the ailerons 15%; each high one slows the circuit and allows a harder sink |

`PlaneBook` gets the tests and finals from the tower (shared by every agent) or this session. `launch`, `take_off`
and `test_plane` load the setup; `status` shows it, and where it came from.

### Final approach limits (the knowledge card)

`MinFinalSpeed` (its landing speed), `MaxFinalSpeed` (approach + 20, + 20 more if it slows well), glide slopes 3° up to
`MaxGlideSlope`: 6° with a slowing rate of 0.6 or more, 5° from 0.45, else 4.5° (the Aeris 3A, 0.31, stayed 40 m above
a 5° slope on every try), and never more than `SlopeCap`: a steep final that still arrived high (its `FinalReport`
carries the slope) caps it half a degree lower. The card learns from its flights. `land`'s `finalSpeed` and `glideSlope` are fitted to
them (`Fit`). The tower shows them on the plane's row.

## 03.3 The tests

**Ground test** (`GroundTest`, on the runway before take_off, ~200 m): a roll to 12 m/s with the nose wheel straight
(does it veer?), a steering pulse of 0.2 for 1.5 s (°/s per full lock at 10 m/s: the wheel bites less the faster it
rolls, so the test holds taxi speed), back onto the heading (damped by the turn rate), a brake stop. Stops itself if it
swings 25° off or takes 2 minutes.

**Flight test** (`FlightTest`, ~4 minutes from 1,500 m above the ground): settle level at 150 m/s (the throttle and
trim that hold it), a tenth of a stick of elevator for a second, recover, the same on the ailerons, a 180° turn (back
toward KSC), slow down at idle holding the height until it can't (its slowest speed), full throttle. Stops itself below
400 m above the ground or after 8 minutes.

**References**: the Aeris 3A measured the same way in the rig (FlightLab `test` and `ground`): pitch 55 and roll 408 °/s
per stick, nose wheel 25-27 °/s, brakes 9 m/s², slowest 54 m/s (`PlaneSetup.Reference*`). If the pilot or the test
changes, measure it again; the setups are ratios to it.
