# Automatic Texture Replacer

Starter project for a Cities: Skylines II C# mod, following the project and publishing layout used by `SmartZoningExtension`.

## Requirements

- .NET SDK
- Cities: Skylines II modding tools installed
- User environment variable `CSII_TOOLPATH` pointing to the game's modding tools directory

## Build

Open `AutomaticTextureReplacer.sln` in Visual Studio, or run:

```powershell
dotnet build .\AutomaticTextureReplacer.sln
```

The top-left toolbar button opens the resizable, draggable **Automatic Texture Replacer** dialog. Its open state, position, and size are stored in the mod settings and restored when the game starts. These values are hidden from the Mods settings UI.

The dialog body is currently a placeholder. The mod entry point is `AutomaticTextureReplacer/Mod.cs`; texture discovery and replacement behavior will be added in later steps.

## Publishing

The project includes VS Code tasks for creating a listing, publishing a new version, and updating the listing. Set the mod's own Paradox Mods ID in `AutomaticTextureReplacer/Properties/PublishConfiguration.xml` before updating an existing listing. The current configuration has no ID and is private by default.
