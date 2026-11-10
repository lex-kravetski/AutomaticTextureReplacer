using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.Json;
using Colossal.UI.Binding;
using Game.Areas;
using Game.Modding;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI;
using Unity.Collections;
using Unity.Entities;

namespace AutomaticTextureReplacer
{
    public sealed partial class AutomaticTextureReplacerUISystem : UISystemBase
    {
        private ValueBinding<bool> m_DialogOpen;
        private ValueBinding<int> m_DialogX;
        private ValueBinding<int> m_DialogY;
        private ValueBinding<int> m_DialogWidth;
        private ValueBinding<int> m_DialogHeight;
        private ValueBinding<int> m_DialogFontSize;
        private ValueBinding<int> m_SurfacePickerX;
        private ValueBinding<int> m_SurfacePickerY;
        private ValueBinding<int> m_SurfacePickerWidth;
        private ValueBinding<int> m_SurfacePickerHeight;
        private ValueBinding<bool> m_SurfacePickerOpen;
        private ValueBinding<string> m_SurfaceCatalog;
        private EntityQuery m_SurfaceQuery;
        private int m_LastDialogFontSize = -1;
        private bool m_SurfaceCatalogLoaded;

        protected override void OnCreate()
        {
            base.OnCreate();

            AutomaticTextureReplacerSettings settings = Mod.Settings;
            AddBinding(m_DialogOpen = new ValueBinding<bool>("automatictexturereplacer", "dialogOpen", settings?.DialogOpen ?? true));
            AddBinding(m_DialogX = new ValueBinding<int>("automatictexturereplacer", "dialogX", settings?.DialogX ?? -1));
            AddBinding(m_DialogY = new ValueBinding<int>("automatictexturereplacer", "dialogY", settings?.DialogY ?? -1));
            AddBinding(m_DialogWidth = new ValueBinding<int>("automatictexturereplacer", "dialogWidth", settings?.DialogWidth ?? 440));
            AddBinding(m_DialogHeight = new ValueBinding<int>("automatictexturereplacer", "dialogHeight", settings?.DialogHeight ?? 300));
            AddBinding(m_DialogFontSize = new ValueBinding<int>("automatictexturereplacer", "dialogFontSize", settings?.DialogFontSize ?? 100));
            AddBinding(m_SurfacePickerX = new ValueBinding<int>("automatictexturereplacer", "surfacePickerX", settings?.SurfacePickerX ?? -1));
            AddBinding(m_SurfacePickerY = new ValueBinding<int>("automatictexturereplacer", "surfacePickerY", settings?.SurfacePickerY ?? -1));
            AddBinding(m_SurfacePickerWidth = new ValueBinding<int>("automatictexturereplacer", "surfacePickerWidth", settings?.SurfacePickerWidth ?? 920));
            AddBinding(m_SurfacePickerHeight = new ValueBinding<int>("automatictexturereplacer", "surfacePickerHeight", settings?.SurfacePickerHeight ?? 680));
            AddBinding(m_SurfacePickerOpen = new ValueBinding<bool>("automatictexturereplacer", "surfacePickerOpen", settings?.SurfacePickerOpen ?? false));
            AddBinding(m_SurfaceCatalog = new ValueBinding<string>("automatictexturereplacer", "surfaceCatalog", "[]"));

            m_SurfaceQuery = GetEntityQuery(
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<SurfaceData>(),
                    },
                },
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<AreaData>(),
                    },
                });

            AddBinding(new TriggerBinding("automatictexturereplacer", "toggleDialog", () =>
                SetDialogOpen(Mod.Settings != null && !Mod.Settings.DialogOpen)));
            AddBinding(new TriggerBinding("automatictexturereplacer", "closeDialog", () => SetDialogOpen(false)));
            AddBinding(new TriggerBinding<string>("automatictexturereplacer", "setDialogPosition", SetDialogPosition));
            AddBinding(new TriggerBinding<string>("automatictexturereplacer", "setDialogSize", SetDialogSize));
            AddBinding(new TriggerBinding("automatictexturereplacer", "openSurfacePicker", OpenSurfacePicker));
            AddBinding(new TriggerBinding("automatictexturereplacer", "closeSurfacePicker", CloseSurfacePicker));
            AddBinding(new TriggerBinding<string>("automatictexturereplacer", "setSurfacePickerPosition", SetSurfacePickerPosition));
            AddBinding(new TriggerBinding<string>("automatictexturereplacer", "setSurfacePickerSize", SetSurfacePickerSize));
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            if (!m_SurfaceCatalogLoaded && Mod.Settings?.SurfacePickerOpen == true)
            {
                RefreshSurfaceCatalog();
            }

            int fontSize = Mod.Settings?.DialogFontSize ?? 100;
            if (fontSize != m_LastDialogFontSize)
            {
                m_LastDialogFontSize = fontSize;
                m_DialogFontSize.Update(fontSize);
            }
        }

        private void SetDialogOpen(bool isOpen)
        {
            if (Mod.Settings == null)
            {
                return;
            }

            Mod.Settings.DialogOpen = isOpen;
            Mod.Settings.ApplyAndSave();
            m_DialogOpen.Update(isOpen);
        }

        private void SetDialogPosition(string value)
        {
            string[] parts = value?.Split(',');
            if (parts == null || parts.Length != 2 ||
                !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y) ||
                Mod.Settings == null)
            {
                return;
            }

            Mod.Settings.DialogX = x;
            Mod.Settings.DialogY = y;
            Mod.Settings.ApplyAndSave();
            m_DialogX.Update(x);
            m_DialogY.Update(y);
        }

        private void SetDialogSize(string value)
        {
            string[] parts = value?.Split(',');
            if (parts == null || parts.Length != 2 ||
                !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height) ||
                Mod.Settings == null)
            {
                return;
            }

            width = System.Math.Max(320, System.Math.Min(width, 1600));
            height = System.Math.Max(200, System.Math.Min(height, 1200));
            Mod.Settings.DialogWidth = width;
            Mod.Settings.DialogHeight = height;
            Mod.Settings.ApplyAndSave();
            m_DialogWidth.Update(width);
            m_DialogHeight.Update(height);
        }

        private void OpenSurfacePicker()
        {
            RefreshSurfaceCatalog();
            if (Mod.Settings != null)
            {
                Mod.Settings.SurfacePickerOpen = true;
                Mod.Settings.ApplyAndSave();
            }
            m_SurfacePickerOpen.Update(true);
        }

        private void CloseSurfacePicker()
        {
            if (Mod.Settings != null)
            {
                Mod.Settings.SurfacePickerOpen = false;
                Mod.Settings.ApplyAndSave();
            }
            m_SurfacePickerOpen.Update(false);
        }

        private void RefreshSurfaceCatalog()
        {
            PrefabSystem prefabSystem = World.GetExistingSystemManaged<PrefabSystem>();
            if (prefabSystem == null || m_SurfaceQuery.IsEmptyIgnoreFilter)
            {
                m_SurfaceCatalog.Update("[]");
                return;
            }

            List<Dictionary<string, string>> catalog = new List<Dictionary<string, string>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            using (NativeArray<Entity> entities = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (!prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab prefab) || prefab == null)
                    {
                        continue;
                    }

                    string id = prefab.GetPrefabID().ToString();
                    string name = prefab.name ?? id;
                    if (IsExcludedSurfaceName(name))
                    {
                        continue;
                    }

                    if (!seen.Add(id))
                    {
                        continue;
                    }

                    string group = GetSurfaceGroup(prefab);
                    string menuPath = GetSurfaceMenuPath(prefab);
                    string modName = GetModDisplayName(prefab);
                    string modId = GetModId(prefab);
                    Mod.Log.InfoFormat(
                        "Surface catalog: asset='{0}', group='{1}', menuPath='{2}', modName='{3}', modId='{4}'",
                        name,
                        group,
                        menuPath,
                        modName ?? "",
                        modId ?? "");

                    catalog.Add(new Dictionary<string, string>
                    {
                        ["id"] = id,
                        ["name"] = name,
                        ["thumbnail"] = GetSurfaceThumbnail(prefab),
                        ["tooltipThumbnail"] = GetLargeThumbnail(prefab),
                        ["group"] = group,
                        ["menuPath"] = menuPath,
                        ["isMod"] = (!prefab.isBuiltin).ToString(),
                    });
                }
            }

            catalog = catalog.OrderBy(item => item["name"], StringComparer.CurrentCultureIgnoreCase).ToList();
            m_SurfaceCatalog.Update(JSON.Dump(catalog));
            m_SurfaceCatalogLoaded = true;
        }

        private static bool IsExcludedSurfaceName(string name)
        {
            return name.EndsWith("Placeholder", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Surface Area", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSurfaceGroup(SurfacePrefab prefab)
        {
            if (!prefab.isBuiltin)
            {
                return GetModDisplayName(prefab) ?? GetSurfaceMenuPath(prefab);
            }

            if (prefab.isBuiltin)
            {
                string name = prefab.name ?? string.Empty;
                string menuPath = GetSurfaceMenuPath(prefab);
                if (ContainsSpecialIndustry(name) || ContainsSpecialIndustry(menuPath))
                {
                    return "Special Industries";
                }

                return "In-game";
            }

            return GetSurfaceMenuPath(prefab);
        }

        private static bool ContainsSpecialIndustry(string value)
        {
            return GetSpecialIndustryName(value) != null;
        }

        private static string GetSpecialIndustryName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            string[] segments = value.Split(new[] { '/', '\\', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                if (segment.Equals("Agriculture", StringComparison.OrdinalIgnoreCase)) return "Agriculture";
                if (segment.Equals("Forestry", StringComparison.OrdinalIgnoreCase)) return "Forestry";
                if (segment.Equals("Oil", StringComparison.OrdinalIgnoreCase)) return "Oil";
                if (segment.Equals("Ore", StringComparison.OrdinalIgnoreCase)) return "Ore";
                if (segment.Equals("Fish", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("Fishery", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("Fisheries", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("Fishing", StringComparison.OrdinalIgnoreCase)) return "Fishing";
            }

            return value.IndexOf("Special Industries", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Special Industries"
                : null;
        }

        private static string GetSurfaceMenuPath(SurfacePrefab prefab)
        {
            if (prefab.TryGet<UIObject>(out UIObject uiObject) && uiObject?.m_Group != null)
            {
                if (uiObject.m_Group is UIAssetCategoryPrefab category && category.m_Menu != null)
                {
                    return category.m_Menu.name + " / " + category.name;
                }

                return uiObject.m_Group.name;
            }

            return "In-game asset";
        }

        private static string GetLargeThumbnail(SurfacePrefab prefab)
        {
            string icon = GetAssetIcon(prefab);
            if (!string.IsNullOrWhiteSpace(icon))
            {
                return icon;
            }

            return ImageSystem.GetThumbnail(prefab) is string thumbnail &&
                   !string.IsNullOrWhiteSpace(thumbnail) &&
                   !thumbnail.StartsWith("thumbnail://", StringComparison.OrdinalIgnoreCase)
                ? thumbnail
                : prefab.thumbnailUrl + "?width=512&height=512";
        }

        private static string GetSurfaceThumbnail(SurfacePrefab prefab)
        {
            return GetAssetIcon(prefab) ?? ImageSystem.GetThumbnail(prefab) ?? "Media/Placeholder.svg";
        }

        private static string GetAssetIcon(SurfacePrefab prefab)
        {
            // Asset Icon Library registers its surface icons under this host. Use its flat
            // surface artwork ahead of a vanilla isometric prefab icon.
            string iconName = typeof(SurfacePrefab).Name + "." + prefab.name + ".svg";
            string userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Colossal Order", "Cities Skylines II", "ModsData",
                "AssetIconLibrary", ".Thumbnails", iconName);
            if (File.Exists(userData))
            {
                return "coui://ail/" + iconName;
            }

            string icon = ImageSystem.GetIcon(prefab);
            return string.IsNullOrWhiteSpace(icon) ? null : icon;
        }

        private static string GetModDisplayName(SurfacePrefab prefab)
        {
            ModManager.ModInfo owner = FindOwningMod(prefab);
            if (owner != null)
            {
                string ownerDisplayName = owner.asset.GetMeta().displayName;
                if (!string.IsNullOrWhiteSpace(ownerDisplayName))
                {
                    return ownerDisplayName;
                }

                if (!string.IsNullOrWhiteSpace(owner.asset.name))
                {
                    return owner.asset.name;
                }
            }

            if (prefab.asset != null)
            {
                string displayName = prefab.asset.GetMeta().displayName;
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    string assetSuffix = GetModAssetNameSuffix(prefab);
                    if (!string.IsNullOrEmpty(assetSuffix) &&
                        displayName.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        displayName = displayName.Substring(0, displayName.Length - assetSuffix.Length).TrimEnd();
                    }

                    return displayName;
                }
            }

            string modFolder = GetModFolderName(prefab.asset?.path);
            if (modFolder != null && GameManager.instance?.modManager != null)
            {
                foreach (ModManager.ModInfo mod in GameManager.instance.modManager)
                {
                    if (string.Equals(GetModFolderName(mod.asset?.path), modFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        string displayName = mod.asset.GetMeta().displayName;
                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            return displayName;
                        }
                    }
                }
            }

            return null;
        }

        private static string GetModAssetNameSuffix(SurfacePrefab prefab)
        {
            if (prefab.TryGet<UIObject>(out UIObject uiObject) &&
                !string.IsNullOrWhiteSpace(uiObject?.m_Group?.name))
            {
                return " " + uiObject.m_Group.name + " " + prefab.name;
            }

            return " " + prefab.name;
        }

        private static string GetModFolderName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string directory = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(directory))
            {
                string name = Path.GetFileName(directory);
                int separator = name.IndexOf('_');
                if (separator > 0 && int.TryParse(name.Substring(0, separator), out _))
                {
                    return name.Substring(0, separator);
                }

                directory = Path.GetDirectoryName(directory);
            }

            return null;
        }

        private static string GetModId(SurfacePrefab prefab)
        {
            if (prefab.asset != null)
            {
                string platformId = prefab.asset.GetMeta().platformID;
                if (!string.IsNullOrWhiteSpace(platformId))
                {
                    return platformId;
                }
            }

            ModManager.ModInfo owner = FindOwningMod(prefab);
            if (owner?.asset != null)
            {
                string ownerPlatformId = owner.asset.GetMeta().platformID;
                if (!string.IsNullOrWhiteSpace(ownerPlatformId))
                {
                    return ownerPlatformId;
                }

                string ownerFolderId = GetModFolderName(owner.asset.path);
                if (!string.IsNullOrWhiteSpace(ownerFolderId))
                {
                    return ownerFolderId;
                }
            }

            return GetModFolderName(prefab.asset?.path);
        }

        private static ModManager.ModInfo FindOwningMod(SurfacePrefab prefab)
        {
            string sourceName = prefab.asset?.GetMeta().displayName;
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                sourceName = prefab.name;
            }

            ModManager modManager = GameManager.instance?.modManager;
            if (modManager == null || string.IsNullOrWhiteSpace(sourceName))
            {
                return null;
            }

            ModManager.ModInfo bestMatch = null;
            int bestMatchLength = 0;
            foreach (ModManager.ModInfo mod in modManager)
            {
                if (mod?.asset == null)
                {
                    continue;
                }

                string[] possibleNames =
                {
                    mod.asset.name,
                    mod.asset.GetMeta().displayName,
                };
                foreach (string possibleName in possibleNames)
                {
                    if (string.IsNullOrWhiteSpace(possibleName) || possibleName.Length <= bestMatchLength ||
                        !sourceName.StartsWith(possibleName, StringComparison.OrdinalIgnoreCase) ||
                        (sourceName.Length > possibleName.Length && !char.IsWhiteSpace(sourceName[possibleName.Length])))
                    {
                        continue;
                    }

                    bestMatch = mod;
                    bestMatchLength = possibleName.Length;
                }
            }

            return bestMatch;
        }

        private void SetSurfacePickerPosition(string value)
        {
            string[] parts = value?.Split(',');
            if (parts == null || parts.Length != 2 ||
                !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y) ||
                Mod.Settings == null)
            {
                return;
            }

            Mod.Settings.SurfacePickerX = x;
            Mod.Settings.SurfacePickerY = y;
            Mod.Settings.ApplyAndSave();
            m_SurfacePickerX.Update(x);
            m_SurfacePickerY.Update(y);
        }

        private void SetSurfacePickerSize(string value)
        {
            string[] parts = value?.Split(',');
            if (parts == null || parts.Length != 2 ||
                !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height) ||
                Mod.Settings == null)
            {
                return;
            }

            width = Math.Max(520, Math.Min(width, 1800));
            height = Math.Max(360, Math.Min(height, 1300));
            Mod.Settings.SurfacePickerWidth = width;
            Mod.Settings.SurfacePickerHeight = height;
            Mod.Settings.ApplyAndSave();
            m_SurfacePickerWidth.Update(width);
            m_SurfacePickerHeight.Update(height);
        }
    }
}
