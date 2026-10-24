# Research: where KSP Wingman goes next

Notes from 2026-10-23, written while building 0.3. Facts are checked against LMP 0.29.2's code (in `LunaMultiplayer/`)
and kRPC 0.7.0 unless they say otherwise. Ideas are the owner's; the "how" is ours.

## Local models (done in 0.3, measured)

- **Ollama on the owner's PC (Radeon RX 9070 XT, 16 GB) runs on Vulkan**, not ROCm: Ollama 0.40's Windows build finds
  the card through Vulkan (`library=Vulkan ... total="15.9 GiB"`). Portable zip, no installer: `D:\Tools\Ollama`.
- **Thinking models are too slow to fly with.** Qwen3 4B "thinks" ~4,000 tokens per reply (25 s), and Ollama's
  OpenAI-compatible endpoint ignores every way to turn that off (`/no_think`, `think: false`, `reasoning_effort`).
  **Qwen3 4B instruct** (no thinking) answers in 0.3-1.5 s and uses 3 GB of the GPU, leaving room for KSP.
- **What small models get wrong, and what the console does about it** (from `tests/ModelEval`):
  - writing the call out as text instead of making it → read as a call;
  - saying "Following you!" without calling `follow` → told which tool it didn't call, and on the second reminder
    offered only that tool (Ollama ignores `tool_choice`; llama.cpp's server honors it);
  - silly arguments (rotate at 1 m/s, a formation slot on top of the leader) → every tool clamps its numbers;
  - stopping after `launch` → tool results say what comes next ("next, take_off").
  - Score: Qwen3 4B instruct **21/21** on the eval's 7 orders × 3 with all of the above (8/21 before them); Qwen3 4B
    (thinking) 5/7 at 25 s each. In the game, with both KSPs on the same GPU: 4 of 5 orders right; it answered "come
    back and land" with `fly` once (first reply 22 s while the model loaded), and with `land` the next time.
- llama.cpp's `llama-server` speaks the same API (`--llm http://127.0.0.1:8080/v1/`); not tried yet.

## Ship takeover (hand a craft to the agent)

The owner flies one craft up to another, then wants the agent to take one of them, so they don't fly both sides of a
docking. LMP already supports a clean hand-off, no protocol change:

- Switching away from a vessel releases your control lock (`VesselLockEvents.OnVesselChange` →
  `ReleasePlayerLocks(LockType.Control)`).
- A player **spectating** that vessel gets the control lock by itself when it's released (`LockReleased`: "if they
  release a control lock and we are spectating, try to get the current vessel control lock").
- So: the agent's KSP switches to your craft (it spectates while you fly it), you switch to your other vessel, and
  LMP gives the agent control. Two vessels close together (docking) are both loaded in the agent's game, so
  switching works with kRPC's `ActiveVessel` setter. Far vessels need a scene change (tracking station); check that
  kRPC can do it before promising it.
- Plan for a tool: `take_over(vessel)` (switch, report "waiting for X to let go"), `hand_back`.

## Mission control (plan → you approve → it flies)

- The console already has the loop. A mission is an order the model turns into **numbered steps** (from the tools it
  has) shown to you; nothing runs until you say "go". Each step reports, and "stop" stops.
- Plain flying is enough for the first missions (patrols, escorts, landing practice). Orbital missions need the
  autopilots below.

## Rescue a crew

- What it needs, in order: launch to orbit (kRPC's autopilot does rockets well), a **rendezvous planner** (Hohmann
  transfer, plane change, phasing: arithmetic like `Formation`, testable without KSP), close approach and docking
  (kRPC has docking ports, RCS and reference frames), then bringing them home (de-orbit and parachutes, or a landing).
- KSP's rescue contracts spawn a stranded Kerbal or a crewed wreck in orbit; kRPC lists vessels with their crew
  and orbits, so "pick a crew or vessel to rescue" is a list the console can show.
- The owner's modpack has **no MechJeb** (kRPC can drive MechJeb's autopilots, but only when it's installed), so
  these autopilots are ours to write.

## The shared "tech tree" (what agents learned), on the LMP server

- The owner's pick: **on the LMP server**. Every agent on a server reads and adds to one knowledge file; export and
  import move it between servers. No online service.
- How it can travel: the LUNA EVO server plugin already runs inside LMP; client mods can talk to plugins through
  LMP's `ModApiSystem.SendModMessage` (the bridge would add two procedures: `KnowledgeRead`, `KnowledgeAdd`). The
  server writes `<server>/LunaEvo/knowledge.json`.
- What goes in: short, checked entries ("Aeris 3A: rotate at 55 m/s, cruise throttle under 0.2", "KSC runway 09
  threshold at -0.0486, -74.7244"), who added them, and whether a flight proved them. Small models get only the
  entries for what they're doing (context is short).
- Guardrails: entries are data, never instructions to the model; size limits; the host can review and delete.

## AI craft generator (without the VAB/SPH)

- `.craft` files are KSP's ConfigNode text: one `PART` block per part (`part = name_id`, position, rotation,
  `link`/`attN` lines for attachments), as the forum notes describe; there's no official spec, so read files saved by
  this KSP. kRPC launches any `.craft` in the save's `Ships/VAB` or `Ships/SPH` folder.
- Realistic path: **templates, not free design.** Start from craft that fly (stock ones), and let the agent change
  what's safe (fuel tanks in a stack, engine choice, number of boosters) and check the result (mass, Δv, TWR) before
  launching. Free-form design by a small model would mostly make things that fall apart.
- With mods (the owner's 149), part names come from GameData; the generator must only use parts installed in that KSP.

## Sources

- Ollama GPU docs: https://docs.ollama.com/windows (Vulkan and ROCm on Windows).
- Small-model tool-calling write-ups: https://github.com/MikeVeerman/tool-calling-benchmark,
  https://www.jdhodges.com/blog/local-llms-on-tool-calling-2026-pt1-local-lm/ (blogs; our own eval is what counts).
- `.craft` format notes: https://forum.kerbalspaceprogram.com/topic/51800-any-doco-on-the-craft-file-format
- LMP 0.29.2: `LmpClient/Systems/VesselLockSys/VesselLockEvents.cs`, `LmpClient/Systems/Lock/LockSystem.cs`.

## Planner eval, measured (2026-10-24)

`tests/PlannerEval`, the rig's six flight records as debriefs plus three free-flight menus, thinking on, Ollama 0.40.9 on
the RX 9070 XT. Lessons accepted by the console's own checks / weakest goal picked / mean and slowest answer:

| Model | Lessons | Weakest picked | Mean | Slowest | Notes |
|---|---|---|---|---|---|
| gemma4:e4b | 6/6 | 3/3 | 3.1 s | 5 s | General but correct advice. Next to two running KSPs Ollama holds only 0.4 GB of it on the card (E4B keeps most weights off the GPU) and it still answered in 4.3 s |
| qwen3:8b | 6/6 | 3/3 | 28.6 s | 200 s | More specific, but mistakes runway "09" for a heading ("Correct heading to 09°") |
| qwen3:4b | 2/6 | 3/3 | 14.4 s | 29 s | Often writes the analysis instead of the answer line |

Before the prompt listed the skill ids (and the debrief matched names to them), the same models scored 3/6, 4/6, 0/6:
they wrote "Landing" or "RNAV Landing". `qwen3:4b-thinking` on Ollama is the same model as `qwen3:4b`.
Next: Gemma 4 E4B as the pilot too (one model), and bigger planners on the owner's cluster with PlannerEval first.

Flight plans (PlannerEval, five starting positions, the tower's real table): gemma4:e4b 5/5 valid, 4.4 fixes a plan,
3.6 s (e.g. far west: "KSC-W12 F09"; north after takeoff: "KSC-DN KSC-N1 KSC-N2 KSC-N3 F09"); qwen3:8b 0/5 (it wrote
an analysis and never the PLAN line), 40.7 s.

## Gemma 4 E4B as one model that plans and flies (to test, 2026-10-23)

Why it's interesting: one small model doing both roles (thinking planner and fast pilot) would keep the console simple
and leave room on the GPU for two KSPs. What the write-ups say (not yet checked here):

- About 4.5B effective parameters (about 8B with embeddings), a 128K-token context, text and images in.
- Native tool calling (its own tool-call tokens); thinking can be switched on or off, and on E4B it can be fully off.
- On Ollama as `gemma4:e4b`. One write-up says Ollama turns thinking on by default; check it. Memory figures conflict
  (6 GB minimum system memory is quoted); measure VRAM next to both rig KSPs.

How we'd test it (with SUPPORT TOOLS): ModelEval with thinking off (as the pilot: right tool, seconds per order) and on
(as the planner: valid plans), then one rig KSP on Gemma 4 E4B as the single model and the other on Qwen3 4B instruct
plus a thinking planner, flying the same tower mission at the same time (RigRun head to head, REPORT.md rows, GIFs). A
vision test: the planner also gets a window capture of the agent's KSP. Pass mark: 24/24 in the eval, a first-try
approach and landing in the rig, both KSPs still smooth.

Sources: https://recipes.vllm.ai/Google/gemma-4-E4B-it, https://registry.ollama.ai/library/gemma4,
https://www.lmstudio.ai/models/google/gemma-4-e4b, https://skills.cat/skills/luckyegg168/gemma-4

### Bigger Gemma variants worth trying (write-ups, not measured here; 16 GB card shared with two KSPs)

- **Gemma 4 26B A4B (MoE)**: only ~3.8B parameters active per token, so it should run near E4B's speed with much
  more knowledge; ~16-19 GB at 4-bit, so on our 16 GB card only with a short context and probably not beside two KSPs.
  Best fit: the planner on the owner's cluster, or on this PC with one KSP. The top candidate for "more power, small cost".
- **Gemma 3 12B (dense)**: ~8 GB at 4-bit, called the sweet spot for 12-16 GB GPUs: a planner here, next to the
  pilot model and two KSPs if the pilot stays small.
- **Gemma 4 31B (dense)**: ~18-20 GB at 4-bit, a 24-32 GB card: cluster only.
- Speed claims for these conflict wildly (11 to 149 tokens/s for the same 26B on the same GPU), so measure with one
  quant, context and backend: EvalMatrix (seconds per order, valid plans) and a rig head to head against E4B.

Sources: https://ai.google.dev/gemma/docs/core, https://www.yottalabs.ai/post/gemma-4-hardware-requirements-gpu-memory-2026,
https://modelfit.io/blog/gemma-4-gpu-requirements/, https://runaihome.com/blog/gemma-4-local-ai-gpu-guide-2026/

## Flight tests in the rig (2026-10-24, 0.11)

Measured with `FlightLab <port> test` / `ground` and the agents' `test_plane` in the rig (stock planes, KSP 1.12.5):

| Plane | Pitch °/s | Roll °/s | Turn °/s at 30° | Slowest m/s | Slows at idle m/s² | Nose wheel °/s | Brakes m/s² |
|---|---|---|---|---|---|---|---|
| Aeris 3A (twice) | 55 | 408 | 2.2 | 54 | 1.2 | 25-27 | 9.0-9.2 |
| Aeris 4A | 37 | 219 | 2.1 | 74 | 1.8 | 19.6-21.6 | 8.7 |

- The first reference, guessed from an old probe note (18 °/s pitch, 160 roll), was 3× off: the tests replaced it.
- From the Aeris 3A's slowest speed the setup gives landing 73 m/s and rotate 54 m/s; the hand-tuned values were 75 and
  55. The Aeris 4A's 74 m/s explains its crashes: the Aeris's 75 m/s landing speed was its stall.
- The nose wheel bites less the faster it rolls (14.9 °/s at 18 m/s, 26.6 at 13), so the ground test holds taxi speed.
- Undamped, take_off's nose-wheel steering swung the Aeris 3A ±10° for 12 s after a pulse; with turn-rate damping it
  settled in 4.6 s.
