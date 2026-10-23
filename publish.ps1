# Builds the release zip: dist/KspWingman-<version>.zip with GameData/KspWingman (the bridge), KspWingman.Mcp.exe
# (the MCP server) and KspWingman.exe (the console for local models).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$version = ([xml](Get-Content src/KspWingman.Mcp/KspWingman.Mcp.csproj)).Project.PropertyGroup.Version | Select-Object -First 1
$out = "dist/KspWingman-$version"
Remove-Item $out, "$out.zip" -Recurse -Force -ErrorAction SilentlyContinue
dotnet build src/KspWingman.Bridge -c Release -v q -nologo
dotnet publish src/KspWingman.Mcp -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$out" -v q -nologo
dotnet publish src/KspWingman.Tui -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$out" -v q -nologo
New-Item -ItemType Directory -Force "$out/GameData/KspWingman" | Out-Null
Copy-Item src/KspWingman.Bridge/bin/Release/net472/KspWingman.Bridge.dll "$out/GameData/KspWingman/"
Copy-Item README.md, LICENSE, CHANGELOG.md $out
Get-ChildItem $out -Filter *.pdb | Remove-Item
Compress-Archive -Path "$out/*" -DestinationPath "$out.zip"
"Built $out.zip ($([math]::Round((Get-Item "$out.zip").Length / 1MB, 1)) MB)"
(Get-FileHash "$out.zip" -Algorithm SHA256).Hash
