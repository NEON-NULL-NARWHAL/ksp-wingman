# 11. Gotchas

Traps we already fell into, each found in the rig:

- **kRPC's autopilot is for rockets**: it dove a plane into the ground. The Director flies; take_off uses kRPC's
  autopilot only for the rotation and climb-out to 100 m.
- **SAS fights the pilot**: it held 10° of climb and a leader climbed to 5 km. The Director turns SAS and the
  autopilot off every second.
- **Wheel steering's sign is KSP's**: positive steers left; the rudder's positive yaws right.
- **Stock vessel names are translation keys** (`#autoLOC_501174`): localize them, or every flight is a plane with
  that name.
- **A crashed vessel stays active** with no parts, or as a piece without a cockpit, still "flying": `Wrecked`
  checks both.
- **"No Control!"**: with every kerbal aboard a parked or leftover plane, a launch stops on a question nobody can
  answer. Check `AvailableCrew` first; recover planes.
- **Nose wheels bite less the faster they roll**: measure at a steady taxi speed; damp steering by the turn rate.
- **Heavy and fast planes need their own numbers**: the Aeris 4A sank (trim), stalled at the Aeris's speeds, snaked
  (aim point), ran off the runway (brakes), and crashed in a bounce (a hop isn't flight) before it landed.
- **A 180° turn takes longer than you think**: 90 s at 160 m/s; legs shorter than that spiral away.
- **The model writes things after markers** ("| PLANE: Aeris 4A"): a lesson ends at the first "|".
- **A clearance during a climb-out**: hold it (and holds) until 300 m, or the plane turns just off the deck.
