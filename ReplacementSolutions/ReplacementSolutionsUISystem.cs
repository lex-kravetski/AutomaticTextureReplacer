using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Colossal.Json;
using Colossal.UI.Binding;
using Game.Areas;
using Game.Common;
using Game.Modding;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace AutomaticTextureReplacer
{
    public sealed partial class ReplacementSolutionsUISystem : UISystemBase
    {
        private ValueBinding<bool> m_DialogOpen;
        private ValueBinding<int> m_DialogX;
        private ValueBinding<int> m_DialogY;
        private ValueBinding<int> m_DialogWidth;
        private ValueBinding<int> m_DialogHeight;
        private ValueBinding<int> m_DialogFontSize;
        private ValueBinding<string> m_ReplacementScope;
        private ValueBinding<string> m_ActiveMainTab;
        private ValueBinding<bool> m_AlsoNewBuildings;
        private ValueBinding<bool> m_ShowInGameSurfacesAsTargets;
        private ValueBinding<bool> m_ShowSpecialIndustrySurfacesAsTargets;
        private ValueBinding<bool> m_ShowModSurfacesAsSources;
        private ValueBinding<bool> m_ShowRevertAllButton;
        private ValueBinding<int> m_SurfacePickerX;
        private ValueBinding<int> m_SurfacePickerY;
        private ValueBinding<int> m_SurfacePickerWidth;
        private ValueBinding<int> m_SurfacePickerHeight;
        private ValueBinding<bool> m_SurfacePickerOpen;
        private ValueBinding<string> m_SurfacePickerContext;
        private ValueBinding<string> m_SurfacePickerCollapsedGroups;
        private ValueBinding<string> m_SurfaceCatalog;
        private ValueBinding<string> m_AreaCatalog;
        private ValueBinding<string> m_RulesJson;
        private ValueBinding<string> m_AreaRulesJson;
        private EntityQuery m_SurfaceQuery;
        private int m_LastDialogFontSize = -1;
        private bool? m_LastShowInGameSurfacesAsTargets;
        private bool? m_LastShowSpecialIndustrySurfacesAsTargets;
        private bool? m_LastShowModSurfacesAsSources;
        private bool? m_LastShowRevertAllButton;
        private bool m_SurfaceCatalogBuildStarted;
        private Entity m_TransparentSurfaceEntity;
        private bool m_TransparentSurfaceInitializationAttempted;
        private AreaBatchSystem m_AreaBatchSystem;
        private Task<string> m_SurfaceCatalogBuildTask;
        private Task<string> m_AreaCatalogBuildTask;
        private int m_SurfaceCatalogSurfaceCount;
        private int m_SurfaceCatalogModSurfaceCount;
        private int m_AreaCatalogIndustryCount;

        protected override void OnCreate()
        {
            base.OnCreate();

            ReplacementSolutionsSettings settings = Mod.Settings;
            AddBinding(m_DialogOpen = new ValueBinding<bool>("replacementsolutions", "dialogOpen", settings?.DialogOpen ?? false));
            AddBinding(m_DialogX = new ValueBinding<int>("replacementsolutions", "dialogX", settings?.DialogX ?? -1));
            AddBinding(m_DialogY = new ValueBinding<int>("replacementsolutions", "dialogY", settings?.DialogY ?? -1));
            AddBinding(m_DialogWidth = new ValueBinding<int>("replacementsolutions", "dialogWidth", settings?.DialogWidth ?? -1));
            AddBinding(m_DialogHeight = new ValueBinding<int>("replacementsolutions", "dialogHeight", settings?.DialogHeight ?? -1));
            AddBinding(m_DialogFontSize = new ValueBinding<int>("replacementsolutions", "dialogFontSize", settings?.DialogFontSize ?? 100));
            AddBinding(m_ReplacementScope = new ValueBinding<string>(
                "replacementsolutions",
                "replacementScope",
                settings?.ReplacementScope ?? "assets"));
            AddBinding(m_ActiveMainTab = new ValueBinding<string>(
                "replacementsolutions",
                "activeMainTab",
                settings?.ActiveMainTab ?? "surfaces"));
            AddBinding(m_AlsoNewBuildings = new ValueBinding<bool>(
                "replacementsolutions",
                "alsoNewBuildings",
                settings?.AlsoNewBuildings ?? false));
            AddBinding(m_ShowInGameSurfacesAsTargets = new ValueBinding<bool>(
                "replacementsolutions",
                "showInGameSurfacesAsTargets",
                settings?.ShowInGameSurfacesAsTargets ?? false));
            AddBinding(m_ShowSpecialIndustrySurfacesAsTargets = new ValueBinding<bool>(
                "replacementsolutions",
                "showSpecialIndustrySurfacesAsTargets",
                settings?.ShowSpecialIndustrySurfacesAsTargets ?? false));
            AddBinding(m_ShowModSurfacesAsSources = new ValueBinding<bool>(
                "replacementsolutions",
                "showModSurfacesAsSources",
                settings?.ShowModSurfacesAsSources ?? false));
            AddBinding(m_ShowRevertAllButton = new ValueBinding<bool>(
                "replacementsolutions",
                "showRevertAllButton",
                settings?.ShowRevertAllButton ?? false));
            AddBinding(m_SurfacePickerX = new ValueBinding<int>("replacementsolutions", "surfacePickerX", settings?.SurfacePickerX ?? -1));
            AddBinding(m_SurfacePickerY = new ValueBinding<int>("replacementsolutions", "surfacePickerY", settings?.SurfacePickerY ?? -1));
            AddBinding(m_SurfacePickerWidth = new ValueBinding<int>("replacementsolutions", "surfacePickerWidth", settings?.SurfacePickerWidth ?? -1));
            AddBinding(m_SurfacePickerHeight = new ValueBinding<int>("replacementsolutions", "surfacePickerHeight", settings?.SurfacePickerHeight ?? -1));
            AddBinding(m_SurfacePickerOpen = new ValueBinding<bool>("replacementsolutions", "surfacePickerOpen", settings?.SurfacePickerOpen ?? false));
            AddBinding(m_SurfacePickerContext = new ValueBinding<string>("replacementsolutions", "surfacePickerContext", settings?.SurfacePickerContextJson ?? ""));
            AddBinding(m_SurfacePickerCollapsedGroups = new ValueBinding<string>(
                "replacementsolutions",
                "surfacePickerCollapsedGroups",
                settings?.SurfacePickerCollapsedGroupsJson ?? "{}"));
            AddBinding(m_SurfaceCatalog = new ValueBinding<string>("replacementsolutions", "surfaceCatalog", "[]"));
            AddBinding(m_AreaCatalog = new ValueBinding<string>("replacementsolutions", "areaCatalog", "[]"));
            AddBinding(m_RulesJson = new ValueBinding<string>("replacementsolutions", "rulesJson", settings?.RulesJson ?? "[]"));
            AddBinding(m_AreaRulesJson = new ValueBinding<string>("replacementsolutions", "areaRulesJson", settings?.AreaRulesJson ?? "[]"));

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
            AddBinding(new TriggerBinding("replacementsolutions", "toggleDialog", () =>
                SetDialogOpen(Mod.Settings != null && !Mod.Settings.DialogOpen)));
            AddBinding(new TriggerBinding("replacementsolutions", "closeDialog", () => SetDialogOpen(false)));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setDialogPosition", SetDialogPosition));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setDialogSize", SetDialogSize));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "openSurfacePicker", OpenSurfacePicker));
            AddBinding(new TriggerBinding("replacementsolutions", "closeSurfacePicker", CloseSurfacePicker));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "saveRules", SetRulesJson));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "saveAreaRules", SetAreaRulesJson));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "replaceSurfaces", ReplaceSurfaces));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "replaceAreas", ReplaceAreas));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setReplacementScope", SetReplacementScope));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setActiveMainTab", SetActiveMainTab));
            AddBinding(new TriggerBinding<bool>("replacementsolutions", "setAlsoNewBuildings", SetAlsoNewBuildings));
            AddBinding(new TriggerBinding("replacementsolutions", "revertAllSurfaces", RevertAllSurfaces));
            AddBinding(new TriggerBinding("replacementsolutions", "revertAllAreas", () => SurfaceReplacementSystem.QueueAreaRevertAll()));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setSurfacePickerPosition", SetSurfacePickerPosition));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setSurfacePickerSize", SetSurfacePickerSize));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "saveSurfacePickerCollapsedGroups", SetSurfacePickerCollapsedGroups));
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            // Wait until a map has finished loading so all enabled mods have registered
            // their prefabs. Capture game-owned data here, then sort the detached snapshot
            // on a worker thread.
            GameManager gameManager = GameManager.instance;
            bool mapReady = gameManager != null &&
                !gameManager.isGameLoading &&
                gameManager.shouldUpdateWorld &&
                (string.Equals(gameManager.gameMode.ToString(), "Game", StringComparison.Ordinal) ||
                 string.Equals(gameManager.gameMode.ToString(), "Editor", StringComparison.Ordinal));
            if (!m_SurfaceCatalogBuildStarted && mapReady && !m_SurfaceQuery.IsEmptyIgnoreFilter)
            {
                StartSurfaceCatalogBuild();
            }

            if (m_SurfaceCatalogBuildTask != null && m_SurfaceCatalogBuildTask.IsCompleted)
            {
                if (m_SurfaceCatalogBuildTask.Status == TaskStatus.RanToCompletion)
                {
                    m_SurfaceCatalog.Update(m_SurfaceCatalogBuildTask.Result);
                    Mod.Log.InfoFormat(
                        "Surface catalog cached after map load: surfaces={0}, modSurfaces={1}",
                        m_SurfaceCatalogSurfaceCount,
                        m_SurfaceCatalogModSurfaceCount);
                }
                else if (m_SurfaceCatalogBuildTask.IsFaulted)
                {
                    Mod.Log.Error(m_SurfaceCatalogBuildTask.Exception, "Surface catalog background processing failed");
                }

                m_SurfaceCatalogBuildTask = null;
            }

            if (m_AreaCatalogBuildTask != null && m_AreaCatalogBuildTask.IsCompleted)
            {
                if (m_AreaCatalogBuildTask.Status == TaskStatus.RanToCompletion)
                {
                    m_AreaCatalog.Update(m_AreaCatalogBuildTask.Result);
                    Mod.Log.InfoFormat("Special industry menu catalog cached after map load: entries={0}", m_AreaCatalogIndustryCount);
                }
                else if (m_AreaCatalogBuildTask.IsFaulted)
                {
                    Mod.Log.Error(m_AreaCatalogBuildTask.Exception, "Special industry menu catalog background processing failed");
                }

                m_AreaCatalogBuildTask = null;
            }

            int fontSize = Mod.Settings?.DialogFontSize ?? 100;
            if (fontSize != m_LastDialogFontSize)
            {
                m_LastDialogFontSize = fontSize;
                m_DialogFontSize.Update(fontSize);
            }

            bool showInGameTargets = Mod.Settings?.ShowInGameSurfacesAsTargets ?? false;
            if (showInGameTargets != m_LastShowInGameSurfacesAsTargets)
            {
                m_LastShowInGameSurfacesAsTargets = showInGameTargets;
                m_ShowInGameSurfacesAsTargets.Update(showInGameTargets);
            }

            bool showSpecialIndustryTargets = Mod.Settings?.ShowSpecialIndustrySurfacesAsTargets ?? false;
            if (showSpecialIndustryTargets != m_LastShowSpecialIndustrySurfacesAsTargets)
            {
                m_LastShowSpecialIndustrySurfacesAsTargets = showSpecialIndustryTargets;
                m_ShowSpecialIndustrySurfacesAsTargets.Update(showSpecialIndustryTargets);
            }

            bool showModSources = Mod.Settings?.ShowModSurfacesAsSources ?? false;
            if (showModSources != m_LastShowModSurfacesAsSources)
            {
                m_LastShowModSurfacesAsSources = showModSources;
                m_ShowModSurfacesAsSources.Update(showModSources);
            }

            bool showRevertAllButton = Mod.Settings?.ShowRevertAllButton ?? false;
            if (showRevertAllButton != m_LastShowRevertAllButton)
            {
                m_LastShowRevertAllButton = showRevertAllButton;
                m_ShowRevertAllButton.Update(showRevertAllButton);
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

            int fontSize = System.Math.Max(100, System.Math.Min(Mod.Settings.DialogFontSize, 200));
            int minimumWidth = (int)System.Math.Ceiling(760d * fontSize / 100d);
            width = System.Math.Max(minimumWidth, System.Math.Min(width, 1600));
            height = System.Math.Max(200, System.Math.Min(height, 1200));
            Mod.Settings.DialogWidth = width;
            Mod.Settings.DialogHeight = height;
            Mod.Settings.ApplyAndSave();
            m_DialogWidth.Update(width);
            m_DialogHeight.Update(height);
        }

        private void OpenSurfacePicker(string contextJson)
        {
            if (Mod.Settings != null)
            {
                Mod.Settings.SurfacePickerContextJson = contextJson ?? "";
                Mod.Settings.SurfacePickerOpen = true;
                Mod.Settings.ApplyAndSave();
            }
            m_SurfacePickerContext.Update(contextJson ?? "");
            m_SurfacePickerOpen.Update(true);
        }

        private void SetRulesJson(string value)
        {
            if (Mod.Settings == null)
            {
                return;
            }

            string json = string.IsNullOrWhiteSpace(value) ? "[]" : value;
            Mod.Settings.RulesJson = json;
            Mod.Settings.ApplyAndSave();
            m_RulesJson.Update(json);
        }

        private void SetAreaRulesJson(string value)
        {
            if (Mod.Settings == null)
            {
                return;
            }

            string json = string.IsNullOrWhiteSpace(value) ? "[]" : value;
            Mod.Settings.AreaRulesJson = json;
            Mod.Settings.ApplyAndSave();
            m_AreaRulesJson.Update(json);
        }

        private void ReplaceSurfaces(string requestedRuleId)
        {
            if (Mod.Settings == null)
            {
                return;
            }

            SurfaceReplacementSystem.QueueReplacement(requestedRuleId, Mod.Settings.RulesJson);
        }

        private void ReplaceAreas(string requestedRuleId)
        {
            if (Mod.Settings == null)
            {
                return;
            }

            SurfaceReplacementSystem.QueueAreaReplacement(requestedRuleId, Mod.Settings.AreaRulesJson);
        }

        private void SetReplacementScope(string value)
        {
            if (Mod.Settings == null ||
                (value != "assets" && value != "hand-drawn-surfaces" && value != "all"))
            {
                return;
            }

            Mod.Settings.ReplacementScope = value;
            Mod.Settings.ApplyAndSave();
            m_ReplacementScope.Update(value);
        }

        private void SetActiveMainTab(string value)
        {
            if (Mod.Settings == null || (value != "surfaces" && value != "areas"))
            {
                return;
            }

            Mod.Settings.ActiveMainTab = value;
            Mod.Settings.ApplyAndSave();
            m_ActiveMainTab.Update(value);
        }

        private void SetAlsoNewBuildings(bool value)
        {
            if (Mod.Settings == null)
            {
                return;
            }

            Mod.Settings.AlsoNewBuildings = value;
            Mod.Settings.ApplyAndSave();
            m_AlsoNewBuildings.Update(value);
        }

        private void RevertAllSurfaces()
        {
            SurfaceReplacementSystem.QueueRevertAll();
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

        private void StartSurfaceCatalogBuild()
        {
            PrefabSystem prefabSystem = World.GetExistingSystemManaged<PrefabSystem>();
            if (prefabSystem == null || m_SurfaceQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            EnsureTransparentSurfacePrefab(prefabSystem);
            m_SurfaceCatalogBuildStarted = true;
            StartAreaCatalogBuild(prefabSystem);
            List<Dictionary<string, string>> catalog = new List<Dictionary<string, string>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            int modSurfaceCount = 0;
            bool anarchyEnabled = IsAnarchyEnabled();
            bool shouldSuppressTrees = !anarchyEnabled;
            int surfacesWithTreeSuppression = 0;
            using (NativeArray<Entity> entities = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (!prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab prefab) || prefab == null)
                    {
                        continue;
                    }

                    if (shouldSuppressTrees && EntityManager.HasComponent<AreaGeometryData>(entity))
                    {
                        AreaGeometryData geometry = EntityManager.GetComponentData<AreaGeometryData>(entity);
                        if ((geometry.m_Flags & GeometryFlags.ClearArea) == 0)
                        {
                            geometry.m_Flags |= GeometryFlags.ClearArea;
                            EntityManager.SetComponentData(entity, geometry);
                            surfacesWithTreeSuppression++;
                        }
                    }

                    string id = prefab.GetPrefabID().ToString();
                    string name = prefab.name ?? id;
                    bool isTransparentSurface = ReplacementRuleCatalog.IsTransparentSurface(name);
                    if (IsExcludedSurfaceName(name) && !isTransparentSurface)
                    {
                        continue;
                    }

                    if (!seen.Add(id))
                    {
                        continue;
                    }

                    if (isTransparentSurface)
                    {
                        name = ReplacementRuleCatalog.TransparentSurfaceDisplayName;
                    }
                    string group = isTransparentSurface ? "In-game" : GetSurfaceGroup(prefab);
                    string menuPath = isTransparentSurface ? "In-game / Transparent Surface" : GetSurfaceMenuPath(prefab);
                    bool isMod = !prefab.isBuiltin && IsOwnedByMod(prefab);
                    if (isMod)
                    {
                        modSurfaceCount++;
                    }

                    if (ContainsSpecialIndustry(name) || ContainsSpecialIndustry(menuPath))
                    {
                        Mod.Log.InfoFormat(
                            "Special-industry surface catalog candidate: asset='{0}', group='{1}', menuPath='{2}', isBuiltin={3}, isMod={4}",
                            name,
                            group,
                            menuPath,
                            prefab.isBuiltin,
                            isMod);
                    }

                    catalog.Add(new Dictionary<string, string>
                    {
                        ["id"] = id,
                        ["name"] = name,
                        ["thumbnail"] = isTransparentSurface
                            ? "coui://replacementsolutions/images/TransparentSurface.svg"
                            : GetSurfaceThumbnail(prefab),
                        ["tooltipThumbnail"] = isTransparentSurface
                            ? "coui://replacementsolutions/images/TransparentSurface.svg"
                            : GetLargeThumbnail(prefab),
                        ["group"] = group,
                        ["menuPath"] = menuPath,
                        ["isMod"] = isMod.ToString(),
                    });
                }
            }

            Mod.Log.InfoFormat(
                "Surface catalog snapshot captured after map load: surfaces={0}, modSurfaces={1}, queriedPrefabs={2}, treeSuppressionFlagsAdded={3}, anarchyEnabled={4}",
                catalog.Count,
                modSurfaceCount,
                m_SurfaceQuery.CalculateEntityCount(),
                surfacesWithTreeSuppression,
                !shouldSuppressTrees);            m_SurfaceCatalogSurfaceCount = catalog.Count;
            m_SurfaceCatalogModSurfaceCount = modSurfaceCount;
            m_SurfaceCatalogBuildTask = Task.Run(() => catalog
                .OrderBy(item => item["name"], StringComparer.CurrentCultureIgnoreCase)
                .ToList())
                .ContinueWith(task => JSON.Dump(task.GetAwaiter().GetResult()), TaskScheduler.Default);
        }

        private void EnsureTransparentSurfacePrefab(PrefabSystem prefabSystem)
        {
            if (m_TransparentSurfaceInitializationAttempted)
            {
                return;
            }

            m_TransparentSurfaceInitializationAttempted = true;
            m_AreaBatchSystem = World.GetOrCreateSystemManaged<AreaBatchSystem>();
            try
            {
                using (NativeArray<Entity> entities = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity entity in entities)
                    {
                        if (!prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab existing) ||
                            existing == null ||
                            !ReplacementRuleCatalog.IsTransparentSurface(existing.name))
                        {
                            continue;
                        }

                        m_TransparentSurfaceEntity = entity;
                        return;
                    }

                    SurfacePrefab template = null;
                    Entity templateEntity = Entity.Null;
                    foreach (Entity entity in entities)
                    {
                        if (!prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab candidate) || candidate == null ||
                            !string.Equals(candidate.name, ReplacementRuleCatalog.TransparentSurfaceTemplateName, StringComparison.OrdinalIgnoreCase) ||
                            !candidate.TryGet<RenderedArea>(out RenderedArea rendered) || rendered == null)
                        {
                            continue;
                        }

                        template = candidate;
                        templateEntity = entity;
                        break;
                    }

                    // Missing Area is the intended structural template. Keep a
                    // fallback for game versions/mod combinations where it is not
                    // exposed through the prefab query.
                    if (template == null)
                    foreach (Entity entity in entities)
                    {
                        if (!prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab candidate) || candidate == null ||
                            candidate.TryGet<RenderedArea>(out RenderedArea rendered) == false || rendered == null)
                        {
                            continue;
                        }

                        template = candidate;
                        templateEntity = entity;
                        break;
                    }

                    if (template == null)
                    {
                        Mod.Log.Warn("Transparent surface initialization skipped: no RenderedArea surface template found");
                        return;
                    }

                    SurfacePrefab transparent = (SurfacePrefab)template.Clone(ReplacementRuleCatalog.TransparentSurfacePrefabName);
                    transparent.Remove<UIObject>();
                    if (!transparent.TryGet<RenderedArea>(out RenderedArea transparentRendered) || transparentRendered == null)
                    {
                        Mod.Log.Warn("Transparent surface initialization skipped: cloned template has no RenderedArea");
                        return;
                    }

                    transparentRendered.m_Version = RenderedArea.Version.OverrideMaterial;
                    transparentRendered.m_BaseColor = Color.clear;
                    transparent.m_Color = Color.clear;
                    transparent.m_EdgeColor = Color.clear;
                    transparent.m_SelectionColor = Color.clear;
                    transparent.m_SelectionEdgeColor = Color.clear;

                    Material sourceMaterial = transparentRendered.m_Material;
                    if (sourceMaterial == null && EntityManager.HasComponent<RenderedAreaData>(templateEntity))
                    {
                        int batchIndex = EntityManager.GetComponentData<RenderedAreaData>(templateEntity).m_BatchIndex;
                        if (m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out Material batchMaterial, out _, out _, out _) &&
                            batchMaterial != null)
                        {
                            sourceMaterial = batchMaterial;
                        }
                    }

                    // Some built-in surface prefabs do not have RenderedAreaData
                    // yet when the catalog is built. In that case the prefab's
                    // material is null and its batch index is not usable, but an
                    // already-created area batch still provides a compatible
                    // area shader/material template.
                    if (sourceMaterial == null && m_AreaBatchSystem != null)
                    {
                        for (int batchIndex = 0; batchIndex < m_AreaBatchSystem.GetBatchCount(); batchIndex++)
                        {
                            if (!m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out Material batchMaterial, out _, out _, out _) ||
                                batchMaterial == null)
                            {
                                continue;
                            }

                            sourceMaterial = batchMaterial;
                            break;
                        }
                    }

                    if (sourceMaterial != null)
                    {
                        // Do not clone the Missing Area material itself. A clone
                        // would inherit its textures and lighting properties and
                        // could remain visible even with a clear base color.
                        // Reuse only the compatible area shader and initialize a
                        // fresh material with explicitly transparent properties.
                        Material transparentMaterial = new Material(sourceMaterial.shader)
                        {
                            name = "ATR Transparent Surface Material",
                            renderQueue = 3000,
                        };
                        transparentMaterial.SetColor("_BaseColor", Color.clear);
                        if (transparentMaterial.HasProperty("_Color"))
                        {
                            transparentMaterial.SetColor("_Color", Color.clear);
                        }
                        if (transparentMaterial.HasProperty("_EmissionColor"))
                        {
                            transparentMaterial.SetColor("_EmissionColor", Color.clear);
                        }
                        if (transparentMaterial.HasProperty("_SpecColor"))
                        {
                            transparentMaterial.SetColor("_SpecColor", Color.clear);
                        }
                        if (transparentMaterial.HasProperty("_Metallic"))
                        {
                            transparentMaterial.SetFloat("_Metallic", 0f);
                        }
                        if (transparentMaterial.HasProperty("_Smoothness"))
                        {
                            transparentMaterial.SetFloat("_Smoothness", 0f);
                        }
                        foreach (string textureProperty in new[] { "_BaseMap", "_MainTex", "_MaskMap", "_NormalMap", "_BumpMap" })
                        {
                            if (transparentMaterial.HasProperty(textureProperty))
                            {
                                transparentMaterial.SetTexture(textureProperty, null);
                            }
                        }
                        transparentMaterial.SetFloat("_SurfaceType", 1f);
                        transparentMaterial.SetFloat("_AlphaCutoffEnable", 0f);
                        transparentMaterial.SetFloat("_BlendMode", 0f);
                        transparentMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                        transparentMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        transparentMaterial.SetInt("_ZWrite", 0);
                        transparentMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        transparentRendered.m_Material = transparentMaterial;
                    }
                    else
                    {
                        Mod.Log.Warn("Transparent surface initialization: source area material was unavailable; using transparent base color only");
                    }

                    if (!prefabSystem.AddPrefab(transparent))
                    {
                        Mod.Log.Warn("Transparent surface initialization failed: PrefabSystem.AddPrefab returned false");
                        return;
                    }

                    m_TransparentSurfaceEntity = prefabSystem.GetEntity(transparent);
                    // AddPrefab creates the prefab entity, while UpdatePrefab queues
                    // the normal prefab-update path that initializes dependent
                    // RenderedAreaData/AreaBatch state for the new surface.
                    prefabSystem.UpdatePrefab(transparent);
                    Mod.Log.InfoFormat(
                        "Transparent surface registered: entity={0}, prefabId='{1}'",
                        m_TransparentSurfaceEntity,
                        transparent.GetPrefabID());
                }
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Transparent surface initialization failed");
            }
        }

        private void StartAreaCatalogBuild(PrefabSystem prefabSystem)
        {
            List<Dictionary<string, string>> catalog = new List<Dictionary<string, string>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            EntityManager entityManager = prefabSystem.World.EntityManager;
            using (EntityQuery query = entityManager.CreateEntityQuery(
                       ComponentType.ReadOnly<PrefabData>(),
                       ComponentType.ReadOnly<UIAssetMenuData>(),
                       ComponentType.ReadOnly<UIGroupElement>()))
            using (NativeArray<Entity> menuEntities = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity menuEntity in menuEntities)
                {
                    if (!prefabSystem.TryGetPrefab<UIAssetMenuPrefab>(menuEntity, out UIAssetMenuPrefab menu) || menu == null)
                    {
                        continue;
                    }

                    DynamicBuffer<UIGroupElement> categories = entityManager.GetBuffer<UIGroupElement>(menuEntity, true);
                    using NativeList<UIObjectInfo> sortedCategories = UIObjectInfo.GetSortedObjects(entityManager, categories, Allocator.Temp);
                    sortedCategories.Sort();
                    foreach (UIObjectInfo categoryEntry in sortedCategories)
                    {
                        if (!prefabSystem.TryGetPrefab<UIAssetCategoryPrefab>(categoryEntry.entity, out UIAssetCategoryPrefab category) ||
                            category == null)
                        {
                            continue;
                        }

                        string menuName = menu.name ?? string.Empty;
                        string categoryName = category.name ?? string.Empty;
                        if (!IsIndustryMenuEntry(menuName, categoryName))
                        {
                            continue;
                        }

                        DynamicBuffer<UIGroupElement> menuEntries = entityManager.GetBuffer<UIGroupElement>(categoryEntry.entity, true);
                        Mod.Log.InfoFormat(
                            "Reading special-industry menu category: menu='{0}', category='{1}', entries={2}",
                            menuName,
                            categoryName,
                            menuEntries.Length);
                        int resolvedEntries = 0;
                        using NativeList<UIObjectInfo> sortedEntries = UIObjectInfo.GetSortedObjects(entityManager, menuEntries, Allocator.Temp);
                        sortedEntries.Sort();
                        foreach (UIObjectInfo menuEntry in sortedEntries)
                        {
                            if (!prefabSystem.TryGetPrefab<PrefabBase>(menuEntry.entity, out PrefabBase prefab) ||
                                prefab == null)
                            {
                                Mod.Log.WarnFormat(
                                    "Special-industry menu entry could not resolve to a prefab: category='{0}', entity={1}, hasPrefabData={2}",
                                    categoryName,
                                    menuEntry.entity,
                                    entityManager.HasComponent<PrefabData>(menuEntry.entity));
                                continue;
                            }

                            resolvedEntries++;
                            string displayName = GetIndustryMenuDisplayName(prefab);
                            bool excluded = IsExcludedIndustryEntry(prefab.name);
                            Mod.Log.InfoFormat(
                                "Special-industry menu entry: category='{0}', asset='{1}', displayName='{2}', prefabType='{3}', excluded={4}",
                                categoryName,
                                prefab.name,
                                displayName,
                                prefab.GetType().Name,
                                excluded);
                            if (excluded)
                            {
                                continue;
                            }

                            string id = prefab.GetPrefabID().ToString();
                            if (!seen.Add(id))
                            {
                                continue;
                            }

                            string icon = ImageSystem.GetIcon(prefab);
                            if (string.IsNullOrWhiteSpace(icon))
                            {
                                icon = ImageSystem.GetThumbnail(prefab);
                            }

                            catalog.Add(new Dictionary<string, string>
                            {
                                ["id"] = id,
                                ["name"] = displayName ?? id,
                                ["thumbnail"] = icon ?? "Media/Placeholder.svg",
                                ["tooltipThumbnail"] = icon ?? "Media/Placeholder.svg",
                                ["group"] = categoryName,
                                ["menuPath"] = string.IsNullOrEmpty(menuName) ? categoryName : menuName + " / " + categoryName,
                                ["isMod"] = (!prefab.isBuiltin).ToString(),
                            });
                        }
                        Mod.Log.InfoFormat(
                            "Special-industry menu category processed: category='{0}', resolvedEntries={1}, catalogEntries={2}",
                            categoryName,
                            resolvedEntries,
                            catalog.Count);
                    }
                }
            }

            m_AreaCatalogIndustryCount = catalog.Count;
            m_AreaCatalogBuildTask = Task.Run(() => JSON.Dump(catalog));
            Mod.Log.InfoFormat("Special industry menu catalog captured: entries={0}", m_AreaCatalogIndustryCount);
        }

        private static bool IsIndustryMenuEntry(string menuName, string categoryName)
        {
            // The menu is enumerated through its own UIGroupElement buffer. The
            // category name identifies the specialized-industry section; don't
            // depend on the internal menu prefab's localized/display name.
            bool isSpecialIndustryCategory =
                categoryName.IndexOf("special", StringComparison.OrdinalIgnoreCase) >= 0 &&
                categoryName.IndexOf("industry", StringComparison.OrdinalIgnoreCase) >= 0;
            return isSpecialIndustryCategory ||
                   (categoryName.IndexOf("extractor", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    menuName.IndexOf("zone", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string GetIndustryMenuDisplayName(PrefabBase prefab)
        {
            if (prefab == null)
            {
                return null;
            }

            string localeId = "Assets.NAME[" + prefab.name + "]";
            GameManager gameManager = GameManager.instance;
            string displayName = null;
            if (gameManager?.localizationManager?.activeDictionary != null &&
                gameManager.localizationManager.activeDictionary.TryGetValue(localeId, out string localizedName) &&
                !string.IsNullOrWhiteSpace(localizedName) &&
                !string.Equals(localizedName, localeId, StringComparison.Ordinal))
            {
                displayName = localizedName;
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = prefab.name;
            }

            return displayName.Replace("Placeholder", string.Empty).Trim();
        }

        private static bool IsExcludedIndustryEntry(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return true;
            }

            string normalizedName = new string(name.Where(character => !char.IsWhiteSpace(character)).ToArray());
            return string.Equals(normalizedName, "AquacultureAreaPlaceholder-Water", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedName, "OffshoreOilIndustryPlaceholder", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExcludedSurfaceName(string name)
        {
            return name.EndsWith("Placeholder", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Surface Area", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Missing Area", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSurfaceGroup(SurfacePrefab prefab)
        {
            string name = prefab.name ?? string.Empty;
            string menuPath = GetSurfaceMenuPath(prefab);
            if (ContainsSpecialIndustry(name) || ContainsSpecialIndustry(menuPath))
            {
                return "Special Industries";
            }

            if (IsOwnedByMod(prefab))
            {
                return GetModDisplayName(prefab) ?? menuPath;
            }

            return "In-game";
        }

        private static bool IsOwnedByMod(SurfacePrefab prefab)
        {
            return prefab != null && (FindOwningMod(prefab) != null || GetModFolderName(prefab.asset?.path) != null);
        }

        private bool IsAnarchyEnabled()
        {
            foreach (ComponentSystemBase system in World.Systems)
            {
                Type type = system.GetType();
                if (!string.Equals(type.FullName, "Anarchy.Systems.Common.AnarchyUISystem", StringComparison.Ordinal))
                {
                    continue;
                }

                PropertyInfo property = type.GetProperty("AnarchyEnabled", BindingFlags.Instance | BindingFlags.Public);
                return property != null && property.PropertyType == typeof(bool) &&
                       property.GetValue(system, null) is bool enabled && enabled;
            }

            return false;
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

        private void SetSurfacePickerCollapsedGroups(string value)
        {
            if (Mod.Settings == null)
            {
                return;
            }

            string json = string.IsNullOrWhiteSpace(value) ? "{}" : value;
            Mod.Settings.SurfacePickerCollapsedGroupsJson = json;
            Mod.Settings.ApplyAndSave();
            m_SurfacePickerCollapsedGroups.Update(json);
        }

    }
}
