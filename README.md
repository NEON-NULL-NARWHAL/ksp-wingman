# KSP Wingman

**An AI agent that joins your [Luna Multiplayer](https://github.com/LunaMultiplayer/LunaMultiplayer) (LMP) server in
Kerbal Space Program as a player, and plays with you.**

A local agent (Claude Code, or any agent that speaks [MCP](https://modelcontextprotocol.io)) drives its own KSP
through [kRPC](https://krpc.github.io/krpc/). It's a real player on your server: its own vessel, held by LMP's normal
locks, so it can only ever fly its own craft. It talks with you in LMP's chat.

A **LUNA Evolved** project, from the makers of [LUNA EVO Mission Control](https://github.com/NEON-NULL-NARWHAL/luna-evo-mission-control).

> [!NOTE]
> **Version 0.1: the chat companion.** The agent joins, sees who's online and what they're flying, and chats in LMP's
> chat. **Flying is next** (0.2: take off and fly formation on you; then rescues, tankers, docking help).

## What you need

- **KSP 1.12.5** with **LMP 0.29.2** (the server and the players).
- **A second KSP for the agent**, on this PC or another one. It's your own copy, running twice. A second PC is
  better: a second KSP takes 3-5 GB of memory and CPU, and a KSP that slows down falls out of step with the others.
- **[kRPC 0.7.0](https://github.com/krpc/krpc/releases)** in the agent's KSP.
- **An agent that speaks MCP**, for example [Claude Code](https://claude.com/claude-code).
- Recommended: the **[LUNA EVO patches](https://github.com/NEON-NULL-NARWHAL/luna-evo-mission-control)** in every
  KSP, so players (and the agent) stay in step in time and see each other.

## Install

1. Download `KspWingman-<version>.zip` from the [latest release](https://github.com/NEON-NULL-NARWHAL/ksp-wingman/releases/latest).
2. Into the **agent's** KSP: copy the zip's `GameData/KspWingman` folder into KSP's `GameData`, next to `kRPC` and
   `LunaMultiplayer`.
3. Put `KspWingman.Mcp.exe` anywhere (one file, nothing to install).
4. Start the agent's KSP, join your server with LMP (pick a player name for it, e.g. "Wingman"), and let it reach the
   Space Center. kRPC's server should be running (its window: Start server; default port 50000).
5. Add the MCP server to your agent. In Claude Code:

   ```
   claude mcp add ksp-wingman --env WINGMAN_KRPC_PORT=50000 -- "C:\path\to\KspWingman.Mcp.exe"
   ```

   For an agent's KSP on another PC, add `--env WINGMAN_KRPC_HOST=<its address>` (and allow kRPC's ports there).
6. Ask your agent to say hello in the chat.

## The tools

| Tool | What it does |
|---|---|
| `status` | The agent's player name, whether it's in a server, the scene, and in flight its vessel, altitude, speed, heading and position. Also says if it's behind the others in time. |
| `players` | The other players: name, the vessel they fly, LMP's status line ("Flying above Kerbin"). |
| `read_chat` | LMP's chat lines after a line number, oldest first. |
| `send_chat` | Says one line in LMP's chat, as the agent's player. |
| `wait_for_chat` | Waits until someone else says something (up to 2 minutes), then returns the new lines. |

**Guardrails, built into the bridge:** chat lines are one line, at most 300 characters, at most one a second. The agent
only ever controls its own KSP; LMP's locks keep it off everyone else's vessels.

## How it works

- **The bridge** (`src/KspWingman.Bridge`, a KSP mod) adds a kRPC service called `Wingman` for what kRPC can't see by
  itself: LMP's chat, players and time. It runs on KSP's main thread, like LMP's own windows.
- **The MCP server** (`src/KspWingman.Mcp`, .NET 10) connects to kRPC in the agent's KSP and turns the game into tools.
- Nothing new goes over LMP's network: the agent is a normal LMP player.

## Building it

.NET 10 SDK, then `dotnet build KspWingman.slnx` and `dotnet test KspWingman.slnx`. No KSP needed: kRPC and LMP's
DLLs the bridge builds against are in `lib/` (see [`lib/THIRD-PARTY.md`](lib/THIRD-PARTY.md)), and KSP's and Unity's
names come from LUNA EVO's own stand-ins in `src/Stubs` (no Squad or Unity code).

`tests/mcp_smoke.py` talks MCP to the server like an agent's host would, against a real KSP.

## How it was made

Written by Claude (Anthropic's AI model), directed and reviewed by the project owner, like LUNA EVO Mission Control.
Version 0.1 was checked with two real KSPs on one LMP server: the agent's chat reached the other player, and the
other player's reply reached the agent.

## License

GPL-3.0 ([LICENSE](LICENSE)). kRPC's `KRPC.Core.dll` (LGPL-3.0) and LMP's client DLLs (MIT) are in `lib/` only to
build against; see [`lib/THIRD-PARTY.md`](lib/THIRD-PARTY.md).

Kerbal Space Program is a trademark of Take-Two Interactive. This project isn't affiliated with them, the LMP team or
the kRPC team.
