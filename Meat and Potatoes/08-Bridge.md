# 08. The bridge (the KSP mod)

`GameData/KspWingman/KspWingman.Bridge.dll` (.NET Framework 4.7.2, KSP's runtime) adds a kRPC service, `Wingman`,
for what kRPC itself doesn't know: LMP and a few game things. The MCP server calls it through `KrpcLink.Wingman<T>`;
an older bridge without a procedure answers with an error, and the caller falls back.

| Procedure | What |
|---|---|
| `PlayerName` | LMP's player name (the agent's call sign) |
| `InGame` | in an LMP server (connected and running) |
| `Players` | who's on the server, and what they fly |
| `PlayerPosition(player)` | where a player's vessel is (through LMP's control lock), for `follow` |
| `Chat(after)` / `SendChat(text)` | LMP's chat lines, numbered (`ChatLog`), and sending one |
| `SecondsBehind` | how far behind the player furthest ahead in time (other players may not see the agent) |
| `AvailableCrew` | free kerbals: with none, a launch would stop on KSP's "No Control" question nobody can answer |
| `CloseDialogs` | closes KSP's Mission Summary and similar windows that would hold up the next launch |
| `Localize(text)` | a stock name as KSP shows it: `#autoLOC_501174` → "Aeris 3A" |

It builds without KSP against `src/Stubs` (stand-ins with the names and shapes of the few KSP, Unity and LMP types
it uses; the real assemblies are used in the game). Adding a procedure: a `[KRPCProcedure] public static` method in
`WingmanService.cs`, a stub for any new KSP type, and a fallback in the caller for older bridges.

Mission Control installs the bridge into a KSP's GameData (and checks it's the same by SHA-256); a running KSP locks
the DLL, so updates happen with KSP closed.
