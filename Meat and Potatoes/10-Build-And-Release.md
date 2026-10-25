# 10. Build and release

- **Build**: `dotnet build KspWingman.slnx` (.NET 10; the bridge targets net472).
- **Version**: the same `<Version>` in `src/KspWingman.Bridge`, `src/KspWingman.Mcp` and `src/KspWingman.Tui`
  csprojs, and a `## <version>` section at the top of `CHANGELOG.md`.
- **Package**: `publish.ps1` → `dist/KspWingman-<version>.zip`: `KspWingman.exe` and `KspWingman.Mcp.exe` (single-file,
  self-contained, win-x64), `GameData/KspWingman/KspWingman.Bridge.dll`, README, LICENSE, CHANGELOG. It prints the
  zip's SHA-256.
- **Release**: `gh release create v<version> dist/KspWingman-<version>.zip --notes-file <the CHANGELOG section>`.
  LUNA EVO Mission Control installs from the latest release and checks the zip against GitHub's SHA-256 digest, so
  the asset name must stay `KspWingman-<version>.zip`.
- **After pushing**: check CI (`gh run list`).
- The repo is public: no player names, LAN addresses, user paths or secrets in code, docs, tests or screenshots.
