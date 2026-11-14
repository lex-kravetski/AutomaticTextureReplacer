# Project map

This document describes the source layout of Replacement Solutions. The two runtime systems are split across `partial` class files: each set of files still compiles into one system, and private members remain shared between its parts.

## Runtime code

| File | Responsibility |
| --- | --- |
| `ReplacementSolutions/Mod.cs` | Mod entry point. Clears the session log, loads settings, registers systems and UI assets, and handles legacy settings migration. |
| `ReplacementSolutions/ReplacementSolutionsSettings.cs` | In-game mod settings and persisted dialog preferences. |
| `ReplacementSolutions/ReplacementRuleCatalog.cs` | Shared rule parsing and mapping from source surface/industry IDs to target surface IDs. |
| `ReplacementSolutions/ReplacementSolutionsUISystem.cs` | Main UI-system lifecycle, bindings, dialog state, rules and user actions. |
| `ReplacementSolutions/ReplacementSolutionsUISystem.SurfaceParameters.cs` | Surface-parameter editor: scale, edge settings, base color, persistence and application to prefabs/render batches. |
| `ReplacementSolutions/ReplacementSolutionsUISystem.SurfaceCatalog.cs` | Surface and industry catalogs, custom-texture scanning and registration, preview generation, grouping, sorting, and picker state. |
| `ReplacementSolutions/SurfaceReplacementSystem.cs` | Replacement-system state and ECS lifecycle, request queue, queries, and per-frame orchestration. |
| `ReplacementSolutions/SurfaceReplacementSystem.TemporaryAreas.cs` | Temporary and preview area replacement, cleanup and lifetime handling, deferred industry-root work. |
| `ReplacementSolutions/SurfaceReplacementSystem.Construction.cs` | Automatic replacement work for newly constructed buildings and special-industry areas. |
| `ReplacementSolutions/SurfaceReplacementSystem.Replacement.cs` | User-triggered replacement and revert requests, surface-prefab swaps, default-surface matching and geometry helpers. |
| `ReplacementSolutions/SurfaceReplacementSystem.IndustryAreas.cs` | Special-industry area routes, transparent prefab clones, overlays, and restoration of shared industry-prefab state. |
| `ReplacementSolutions/SurfaceReplacementSystem.Diagnostics.cs` | Batch and special-industry diagnostics used to inspect replacement and rendering state. |
| `ReplacementSolutions/NewBuildingSurfaceReplacementSystem.cs` | Tracks newly created buildings/industry areas and applies enabled automatic rules. |
| `ReplacementSolutions/TemporaryAreaBatchReleaseSystem.cs` | Queue used to coordinate same-update release and cleanup of temporary area batches. |

## UI assets

| File | Responsibility |
| --- | --- |
| `ReplacementSolutions/ui/ReplacementSolutions.mjs` | Game UI implementation: dialogs, tabs, rules table, surface picker and surface-parameter controls. |
| `ReplacementSolutions/ui/ReplacementSolutions.css` | Styling for the mod UI and custom controls. |
| `ReplacementSolutions/ui/images/ReplacementSolutions_Icon.svg` | Toolbar icon. |
| `ReplacementSolutions/ui/images/TransparentSurface.svg` | Icon for the pseudo-delete transparent surface. |

## Build and publishing

| File | Responsibility |
| --- | --- |
| `ReplacementSolutions.sln` | Visual Studio solution. |
| `ReplacementSolutions/ReplacementSolutions.csproj` | C# project, game references, and deployment of UI assets after the WIP deploy target. |
| `ReplacementSolutions/Properties/PublishConfiguration.xml` | Paradox Mods publishing metadata, including the listing ID. |
| `ReplacementSolutions/Properties/PublishProfiles/PublishNewMod.pubxml` | First publication profile. |
| `ReplacementSolutions/Properties/PublishProfiles/PublishNewVersion.pubxml` | Existing-listing version publication profile. |
| `ReplacementSolutions/Properties/PublishProfiles/UpdatePublishedConfiguration.pubxml` | Published-listing metadata update profile. |
| `README.md` | Short project overview, build prerequisites, settings location, and publishing notes. |

Build a Release version from the repository root with:

```powershell
dotnet build .\ReplacementSolutions.sln -c Release
```

The project uses SDK-style default source inclusion, so new `.cs` partial files are included automatically.
