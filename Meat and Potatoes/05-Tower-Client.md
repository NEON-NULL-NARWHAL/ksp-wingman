# 05. The tower client

With `WINGMAN_TOWER_URL` (LUNA EVO Mission Control's KSC tower, localhost only), `TowerClient` reports every 3 s and
acts on what the tower says. The tower's side is in Mission Control's Meat and Potatoes, chapters 28 (the tower) and
30 (the knowledge store).

## 05.1 Reports

`/v1/report`: position, heading, speed, situation, the pilot's doing, and the plane's **approach speed** (fast planes
get the long approaches). Situations that take the plane out of the sequence: `SpaceCenter` (not in flight),
`Destroyed` (a wreck), `Splashed`, `Offline` (the agent's KSP isn't in an LMP server). A plane that's gone, or a new one
on the runway, forgets any request to land. When the MCP server stops it says `leaving`; Mission Control also tells
its tower when an agent's process ends (it's killed with the console).

## 05.2 Orders from the tower

| Message | What the client does |
|---|---|
| `cleared-departure` / `hold-departure` | the answer to `departure`, asked by `launch` before a runway launch: on hold, `launch` says "Not launched yet" and training tries again |
| `ask-intent` | answers "land at KSC" (it asked to land, or is landing or holding at a fix) or "local flight" |
| `clearance` (with a route) | flies it; during a go-around's climb-out it's held until 300 m |
| `hold` (with a fix) | holds there; also held until 300 m during a climb-out |
| `cleared-to-land` | nothing to do (the pilot flies on); it says "runway in sight" itself when lined up |
| `vacate` | taxis off when stopped on the runway |

Going around, it tells the tower once (`go-around`); the flag stays set until the next clearance is flown (resetting
it on receipt made it repeat every 2-4 s).

## 05.3 Knowledge

| Call | When |
|---|---|
| `/v1/flight` | each outcome: the tower grades its skills (the answer becomes a `knowledge` event) |
| `/v1/lesson` | `learn`, citing a flight |
| `/v1/knowledge` | the `knowledge` tool: skills, lessons, planes, the explored map |
| `/v1/procedures`, `/v1/plan` | a flight plan of the agent's own |
| `/v1/test` | a finished test, with the plane's final approach limits |
| `/v1/craft` | a plane's latest test and last finals, for its setup |
| `/v1/place` | an explored place |

Every number the tower sends back is checked again (finite, in range) before it sets up a plane.
