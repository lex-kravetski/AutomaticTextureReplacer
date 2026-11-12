# Replacement Solutions

A Cities: Skylines II mod for replacing building, hand-drawn, and specialized-industry surfaces. Rules can run on existing areas through the dialog buttons or automatically when new buildings and industry areas are created. A transparent surface is available for hiding unwanted surface artwork.

## Build

The project requires the .NET SDK, Cities: Skylines II modding tools, and the `CSII_TOOLPATH` user environment variable pointing to the modding tools directory.

```powershell
dotnet build .\ReplacementSolutions.sln -c Release
```

The Release build deploys the mod to the game's local Mods directory. Settings are stored in `ReplacementSolutions.coc` in the Cities: Skylines II user data directory. An existing `AutomaticTextureReplacer.coc` is copied on first launch; rules are cleared because the transparent surface has a new internal ID.

## Publishing

VS Code tasks are available for the first Paradox Mods publication, a new version, and listing updates. Set the mod ID in `ReplacementSolutions/Properties/PublishConfiguration.xml` before updating an existing listing.
