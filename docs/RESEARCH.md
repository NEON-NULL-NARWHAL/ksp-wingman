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
