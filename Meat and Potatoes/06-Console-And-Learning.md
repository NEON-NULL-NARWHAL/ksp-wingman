# 06. The console: orders, learning, free flight

## 06.1 Orders and the model loop

Orders come from the terminal (you're always in charge), from LMP chat (players on `--allow`, calling the agent by its
call sign; `Orders.cs`) or, with `--ipc`, from a host app. `Agent.RunAsync` sends the order and the tools to the pilot
model and runs its tool calls until it answers (small models need short tool lists and plain answers). Console
commands: `/status`, `/free [minutes]`, `/train <what> [minutes]`, `/stop-free`, `/forget`, `/debrief`, `/quit`.

## 06.2 --ipc (for LUNA EVO's Wingman page)

One JSON object a line each way. In: `{"type":"order","text":...}`, `status`, `free`, `stop-free`, `train` (with
`focus`), `forget`, `quit`. Out: `{"type":"event","kind":...,"text":...,"data":...}` with the MCP server's events plus
the console's own (`wingman`, `order`, `call`, `result`, `answer`, `status`, `free`, `debrief`, `error`). A `status`
request is answered at once, not queued (it would stop free flight). Any order stops free flight.

## 06.3 Learning

After each recorded flight the debrief (`Learner.DebriefAsync`) asks the planner for **one lesson** for the skill that
went worst: advice starting with a verb, 10-200 characters, citing the flight. Code refuses reports ("Landed 25 m
off..."), unknown skills, and anything after a "|" (the answer's markers). A lesson marked `| PLANE` is about the
plane only; its numbers must be ones the flight or the plane's tests measured (`Debrief.Grounded`). After a plane's
tests the planner also writes one note on how it differs from the Aeris 3A.

## 06.4 Free flight and training

`Learner.FreeFlyAsync` loops: the planner picks a goal from the menu (`FreeFlight.Goals`: four landings, a flight plan,
fly fast, fly high, a new plane, a riskier final, explore), weakest skills first; code launches, takes off, flies the
goal, waits for the outcome (sending the plane back if it leaves the box: 25 km and 3,000 m, wider for explore and fly
high) and debriefs. Training is free flight on one focus until 3 good flights in a row. It stops for an order, at the
time limit, after two crashes in a row, after five rounds without a flight, or when every goal failed twice.

Switching to "a new plane" in the air lands this one first. A plane the server already tested isn't tested again.
