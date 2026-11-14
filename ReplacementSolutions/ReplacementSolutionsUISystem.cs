using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Colossal.Json;
using Colossal.IO.AssetDatabase;
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
using Unity.Mathematics;
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
        private ValueBinding<bool> m_ShowSurfaceBaseColorSettings;
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
        private ValueBinding<string> m_SurfaceScalesJson;
        private ValueBinding<string> m_SurfaceEdgeFadesJson;
        private ValueBinding<string> m_SurfaceEdgeFadeEndsJson;
        private ValueBinding<string> m_SurfaceEdgeNoisesJson;
        private ValueBinding<string> m_SurfaceEdgeNoiseScalesJson;
        private ValueBinding<string> m_SurfaceBaseColorsJson;
        private ValueBinding<bool> m_SurfaceScaleEditorOpen;
        private ValueBinding<string> m_SurfaceScaleSurfaceId;
        private ValueBinding<string> m_SurfaceScaleSurfaceName;
        private ValueBinding<int> m_SurfaceScaleHundredths;
        private ValueBinding<float> m_SurfaceEdgeFadePercent;
        private ValueBinding<float> m_SurfaceEdgeFadeEndPercent;
        private ValueBinding<float> m_SurfaceEdgeNoisePercent;
        private ValueBinding<float> m_SurfaceEdgeNoiseScale;
        private ValueBinding<float> m_SurfaceEdgeFadeDefault;
        private ValueBinding<float> m_SurfaceEdgeNoiseDefault;
        private ValueBinding<float> m_SurfaceEdgeNoiseScaleDefault;
        private ValueBinding<float> m_SurfaceBaseColorHue;
        private ValueBinding<float> m_SurfaceBaseColorSaturation;
        private ValueBinding<float> m_SurfaceBaseColorBrightness;
        private ValueBinding<float> m_SurfaceBaseColorAlpha;
        private ValueBinding<int> m_SurfaceScaleRevertVersion;
        private int m_SurfaceScaleRevertCounter;
        private ValueBinding<int> m_SurfaceScaleEditorX;
        private ValueBinding<int> m_SurfaceScaleEditorY;
        private ValueBinding<string> m_SurfaceScaleEditorOrigin;
        private EntityQuery m_SurfaceQuery;
        private EntityQuery m_SurfaceScaleInstancesQuery;
        private int m_LastDialogFontSize = -1;
        private bool? m_LastShowInGameSurfacesAsTargets;
        private bool? m_LastShowSpecialIndustrySurfacesAsTargets;
        private bool? m_LastShowModSurfacesAsSources;
        private bool? m_LastShowRevertAllButton;
        private bool? m_LastShowSurfaceBaseColorSettings;
        private bool m_SurfaceCatalogBuildStarted;
        private Entity m_TransparentSurfaceEntity;
        private bool m_TransparentSurfaceInitializationAttempted;
        private AreaBatchSystem m_AreaBatchSystem;
        private Task<string> m_SurfaceCatalogBuildTask;
        private Task<string> m_AreaCatalogBuildTask;
        private int m_SurfaceCatalogSurfaceCount;
        private int m_SurfaceCatalogModSurfaceCount;
        private int m_AreaCatalogIndustryCount;
        private bool m_CustomTextureScanStarted;
        private Task<List<CustomTextureFile>> m_CustomTextureScanTask;
        private List<CustomTextureFile> m_CustomTextureFiles;
        private int m_CustomTextureFileIndex;
        // Custom surfaces are registered in the same root-folder/path order as
        // the picker catalog; monotonically increasing menu priorities preserve
        // that order in the game's Tiles / Surfaces menu.
        private int m_CustomSurfaceMenuPriority;
        private readonly Dictionary<string, CustomSurfaceInfo> m_CustomSurfacesById = new Dictionary<string, CustomSurfaceInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> m_SurfaceScaleValues = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> m_SurfaceScaleBaseValues = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> m_SurfaceEdgeFadeValues = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> m_SurfaceEdgeFadeEndValues = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float2> m_SurfaceEdgeFadeBaseValues = new Dictionary<string, float2>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> m_SurfaceEdgeNoiseValues = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> m_SurfaceEdgeNoiseScaleValues = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, Color> m_SurfaceBaseColors = new Dictionary<string, Color>(StringComparer.Ordinal);
        private readonly Dictionary<string, Color> m_SurfaceBaseColorDefaults = new Dictionary<string, Color>(StringComparer.Ordinal);
        private readonly Dictionary<string, float2> m_SurfaceEdgeNoiseBaseValues = new Dictionary<string, float2>(StringComparer.Ordinal);
        private readonly HashSet<string> m_SurfaceScaleMaterialCloneIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_SurfaceScaleMaterialUnavailableLogged = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> m_RestoredSurfaceScaleIds = new HashSet<string>(StringComparer.Ordinal);
        private string m_ActiveSurfaceScaleId;
        private float m_ActiveSurfaceScaleBefore;
        private float m_ActiveSurfaceScaleSavedValue;
        private bool m_ActiveSurfaceScaleHadSavedValue;
        private float m_ActiveSurfaceScaleDefault;
        private float m_ActiveSurfaceEdgeNoiseDefault;
        private bool m_SurfaceScaleEditorIsOpen;
        private bool m_RestorePickerOnNextToolbarOpen;
        private readonly Dictionary<int, SurfacePreview> m_SurfacePreviewCache = new Dictionary<int, SurfacePreview>();
        private readonly Dictionary<string, SurfacePreview> m_SurfaceAssetPreviewCache = new Dictionary<string, SurfacePreview>(StringComparer.Ordinal);
        private int m_CustomTextureRegisteredCount;
        private ILocalAssetDatabase m_CustomTextureAssetDatabase;
        private Task m_CustomTextureAssetDatabaseRegistrationTask;
        private bool m_CustomTextureAssetDatabaseReady;
        private bool m_CustomTextureAssetDatabaseFailed;

        private sealed class CustomTextureFile
        {
            public string FullPath;
            public string RelativePath;
            public string RootFolder;
            public string DisplayName;
        }

        private sealed class CustomSurfaceInfo
        {
            public string RelativePath;
            public string DisplayName;
            public string Group;
            public string Thumbnail;
            public string TooltipThumbnail;
        }

        private sealed class SurfacePreview
        {
            public string Thumbnail;
            public string TooltipThumbnail;
        }

        private sealed class SurfacePreviewRequest
        {
            public SurfacePrefab Prefab;
            public Entity SurfaceEntity;
            public Dictionary<string, string> CatalogEntry;
        }

        private List<Dictionary<string, string>> m_SurfaceCatalogEntries;
        private readonly List<SurfacePreviewRequest> m_SurfacePreviewQueue = new List<SurfacePreviewRequest>();
        private int m_SurfacePreviewQueueIndex;
        private static readonly FieldInfo s_AreaBatchUvScaleField = typeof(AreaBatchSystem).GetField(
            "m_UVScale",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly int s_BaseColorProperty = Shader.PropertyToID("_BaseColor");
        private static readonly int s_MetallicProperty = Shader.PropertyToID("_Metallic");
        private static readonly int s_SmoothnessProperty = Shader.PropertyToID("_Smoothness");
        private static readonly int s_NormalOpacityProperty = Shader.PropertyToID("_NormalOpacity");
        private static readonly int s_MetallicOpacityProperty = Shader.PropertyToID("_MetallicOpacity");
        private static readonly int s_NormalAlphaSourceProperty = Shader.PropertyToID("_NormalAlphaSource");
        private static readonly int s_MetallicAlphaSourceProperty = Shader.PropertyToID("_MetallicAlphaSource");
        private static readonly int s_EdgeNormalProperty = Shader.PropertyToID("colossal_EdgeNormal");
        private static readonly int s_EdgeFadeRangeProperty = Shader.PropertyToID("colossal_EdgeFadeRange");
        private static readonly int s_EdgeNormalRangeProperty = Shader.PropertyToID("colossal_EdgeNormalRange");
        private static readonly int s_EdgeNoiseProperty = Shader.PropertyToID("colossal_EdgeNoise");
        private bool m_SurfaceCatalogPublished;
        private bool m_SurfaceDefaultsCaptured;

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
            AddBinding(m_ShowSurfaceBaseColorSettings = new ValueBinding<bool>(
                "replacementsolutions",
                "showSurfaceBaseColorSettings",
                settings?.ShowSurfaceBaseColorSettings ?? false));
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
            LoadSurfaceScaleValues(settings?.SurfaceScalesJson);
            AddBinding(m_SurfaceScalesJson = new ValueBinding<string>(
                "replacementsolutions", "surfaceScalesJson", SerializeSurfaceScaleValues()));
            LoadSurfaceEdgeFadeValues(settings?.SurfaceEdgeFadesJson);
            AddBinding(m_SurfaceEdgeFadesJson = new ValueBinding<string>(
                "replacementsolutions", "surfaceEdgeFadesJson", SerializeSurfaceEdgeFadeValues()));
            LoadSurfaceEdgeFadeEndValues(settings?.SurfaceEdgeFadeEndsJson);
            AddBinding(m_SurfaceEdgeFadeEndsJson = new ValueBinding<string>(
                "replacementsolutions", "surfaceEdgeFadeEndsJson", SerializeSurfaceEdgeFadeEndValues()));
            LoadSurfaceEdgeNoiseValues(settings?.SurfaceEdgeNoisesJson);
            AddBinding(m_SurfaceEdgeNoisesJson = new ValueBinding<string>(
                "replacementsolutions", "surfaceEdgeNoisesJson", SerializeSurfaceEdgeNoiseValues()));
            LoadSurfaceEdgeNoiseScaleValues(settings?.SurfaceEdgeNoiseScalesJson);
            AddBinding(m_SurfaceEdgeNoiseScalesJson = new ValueBinding<string>(
                "replacementsolutions", "surfaceEdgeNoiseScalesJson", SerializeSurfaceEdgeNoiseScaleValues()));
            LoadSurfaceBaseColors(settings?.SurfaceBaseColorsJson);
            AddBinding(m_SurfaceBaseColorsJson = new ValueBinding<string>(
                "replacementsolutions", "surfaceBaseColorsJson", SerializeSurfaceBaseColors()));
            AddBinding(m_SurfaceScaleEditorOpen = new ValueBinding<bool>("replacementsolutions", "surfaceScaleEditorOpen", false));
            AddBinding(m_SurfaceScaleSurfaceId = new ValueBinding<string>("replacementsolutions", "surfaceScaleSurfaceId", ""));
            AddBinding(m_SurfaceScaleSurfaceName = new ValueBinding<string>("replacementsolutions", "surfaceScaleSurfaceName", ""));
            AddBinding(m_SurfaceScaleHundredths = new ValueBinding<int>("replacementsolutions", "surfaceScaleHundredths", 100));
            AddBinding(m_SurfaceEdgeFadePercent = new ValueBinding<float>("replacementsolutions", "surfaceEdgeFadePercent", 0f));
            AddBinding(m_SurfaceEdgeFadeEndPercent = new ValueBinding<float>("replacementsolutions", "surfaceEdgeFadeEndPercent", 0f));
            AddBinding(m_SurfaceEdgeNoisePercent = new ValueBinding<float>("replacementsolutions", "surfaceEdgeNoisePercent", 0f));
            AddBinding(m_SurfaceEdgeNoiseScale = new ValueBinding<float>("replacementsolutions", "surfaceEdgeNoiseScale", 0.2f));
            AddBinding(m_SurfaceEdgeFadeDefault = new ValueBinding<float>("replacementsolutions", "surfaceEdgeFadeDefault", 0f));
            AddBinding(m_SurfaceEdgeNoiseDefault = new ValueBinding<float>("replacementsolutions", "surfaceEdgeNoiseDefault", 0f));
            AddBinding(m_SurfaceEdgeNoiseScaleDefault = new ValueBinding<float>("replacementsolutions", "surfaceEdgeNoiseScaleDefault", 0.2f));
            AddBinding(m_SurfaceBaseColorHue = new ValueBinding<float>("replacementsolutions", "surfaceBaseColorHue", 0f));
            AddBinding(m_SurfaceBaseColorSaturation = new ValueBinding<float>("replacementsolutions", "surfaceBaseColorSaturation", 0f));
            AddBinding(m_SurfaceBaseColorBrightness = new ValueBinding<float>("replacementsolutions", "surfaceBaseColorBrightness", 1f));
            AddBinding(m_SurfaceBaseColorAlpha = new ValueBinding<float>("replacementsolutions", "surfaceBaseColorAlpha", 1f));
            AddBinding(m_SurfaceScaleRevertVersion = new ValueBinding<int>("replacementsolutions", "surfaceScaleRevertVersion", 0));
            AddBinding(m_SurfaceScaleEditorX = new ValueBinding<int>("replacementsolutions", "surfaceScaleEditorX", settings?.SurfaceParametersX ?? -1));
            AddBinding(m_SurfaceScaleEditorY = new ValueBinding<int>("replacementsolutions", "surfaceScaleEditorY", settings?.SurfaceParametersY ?? -1));
            AddBinding(m_SurfaceScaleEditorOrigin = new ValueBinding<string>("replacementsolutions", "surfaceScaleEditorOrigin", "picker"));

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
            m_SurfaceScaleInstancesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadWrite<Batch>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            AddBinding(new TriggerBinding("replacementsolutions", "toggleDialog", () =>
                ToggleModDialogs()));
            AddBinding(new TriggerBinding("replacementsolutions", "closeDialog", () => SetDialogOpen(false)));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setDialogPosition", SetDialogPosition));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setDialogSize", SetDialogSize));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "openSurfacePicker", OpenSurfacePicker));
            AddBinding(new TriggerBinding("replacementsolutions", "closeSurfacePicker", CloseSurfacePicker));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "openSurfaceScaleEditor", OpenSurfaceScaleEditor));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setSurfaceScaleEditorPosition", SetSurfaceScaleEditorPosition));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setSurfaceScalePreview", SetSurfaceScalePreview));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setSurfaceEdgeFadePreview", SetSurfaceEdgeFadePreview));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setSurfaceEdgeFadeEndPreview", SetSurfaceEdgeFadeEndPreview));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setSurfaceEdgeNoisePreview", SetSurfaceEdgeNoisePreview));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setSurfaceEdgeNoiseScalePreview", SetSurfaceEdgeNoiseScalePreview));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setSurfaceBaseColorPreview", SetSurfaceBaseColorPreview));
            AddBinding(new TriggerBinding("replacementsolutions", "revertSurfaceScale", RevertSurfaceScale));
            AddBinding(new TriggerBinding("replacementsolutions", "applySurfaceScale", ApplySurfaceScale));
            AddBinding(new TriggerBinding("replacementsolutions", "cancelSurfaceScale", CancelSurfaceScale));
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
            PrefabSystem prefabSystem = World.GetExistingSystemManaged<PrefabSystem>();
            bool prefabsReady = prefabSystem != null && !m_SurfaceQuery.IsEmptyIgnoreFilter;
            if (prefabsReady && !m_SurfaceDefaultsCaptured)
            {
                CaptureSurfaceDefaults(prefabSystem);
                m_SurfaceDefaultsCaptured = true;
            }
            if (mapReady && prefabsReady)
            {
                RestoreConfiguredSurfaceScales(prefabSystem);
            }
            if (!m_CustomTextureScanStarted)
            {
                StartCustomTextureScan();
            }

            if (m_CustomTextureScanTask != null && m_CustomTextureScanTask.IsCompleted)
            {
                if (m_CustomTextureScanTask.Status == TaskStatus.RanToCompletion)
                {
                    m_CustomTextureFiles = m_CustomTextureScanTask.Result;
                    Mod.Log.InfoFormat("Custom surface texture scan found {0} supported images", m_CustomTextureFiles.Count);
                }
                else
                {
                    Mod.Log.Error(m_CustomTextureScanTask.Exception, "Custom surface texture scan failed");
                    m_CustomTextureFiles = new List<CustomTextureFile>();
                }
                m_CustomTextureScanTask = null;
            }

            if (m_CustomTextureAssetDatabaseRegistrationTask != null && m_CustomTextureAssetDatabaseRegistrationTask.IsCompleted)
            {
                if (m_CustomTextureAssetDatabaseRegistrationTask.Status == TaskStatus.RanToCompletion)
                {
                    m_CustomTextureAssetDatabaseReady = true;
                }
                else
                {
                    m_CustomTextureAssetDatabaseFailed = true;
                    Mod.Log.Error(m_CustomTextureAssetDatabaseRegistrationTask.Exception, "Custom texture asset database registration failed");
                }
                m_CustomTextureAssetDatabaseRegistrationTask = null;
            }

            if (!m_SurfaceCatalogBuildStarted && prefabsReady && m_CustomTextureFiles != null)
            {
                if (m_CustomTextureAssetDatabaseReady)
                {
                    RegisterCustomTextures(prefabSystem, maxPerFrame: 16);
                }

                if ((m_CustomTextureAssetDatabaseReady || m_CustomTextureAssetDatabaseFailed) &&
                    mapReady && m_CustomTextureFileIndex >= m_CustomTextureFiles.Count)
                {
                    Mod.Log.InfoFormat("Custom surface registration complete: surfaces={0}", m_CustomTextureRegisteredCount);
                    StartSurfaceCatalogBuild();
                }
            }

            if (m_SurfaceCatalogBuildTask != null && m_SurfaceCatalogBuildTask.IsCompleted)
            {
                if (m_SurfaceCatalogBuildTask.Status == TaskStatus.RanToCompletion)
                {
                    if (m_SurfaceCatalogEntries != null)
                    {
                        m_SurfaceCatalogEntries.Sort((first, second) =>
                            string.Compare(first["name"], second["name"], StringComparison.CurrentCultureIgnoreCase));
                    }
                    Mod.Log.InfoFormat("Preparing texture previews before publishing surface catalog: surfaces={0}", m_SurfaceCatalogSurfaceCount);
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

            UpdateSurfaceCatalogPreviews(maxPerFrame: 8);

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

            bool showSurfaceBaseColorSettings = Mod.Settings?.ShowSurfaceBaseColorSettings ?? false;
            if (showSurfaceBaseColorSettings != m_LastShowSurfaceBaseColorSettings)
            {
                m_LastShowSurfaceBaseColorSettings = showSurfaceBaseColorSettings;
                m_ShowSurfaceBaseColorSettings.Update(showSurfaceBaseColorSettings);
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

        private void ToggleModDialogs()
        {
            if (Mod.Settings == null)
            {
                return;
            }

            bool anyDialogOpen = Mod.Settings.DialogOpen || Mod.Settings.SurfacePickerOpen || m_SurfaceScaleEditorIsOpen;
            if (anyDialogOpen)
            {
                if (m_SurfaceScaleEditorIsOpen)
                {
                    CancelSurfaceScale();
                }

                m_RestorePickerOnNextToolbarOpen = Mod.Settings.SurfacePickerOpen;
                CloseSurfacePicker();
                SetDialogOpen(false);
                return;
            }

            SetDialogOpen(true);
            if (m_RestorePickerOnNextToolbarOpen)
            {
                m_RestorePickerOnNextToolbarOpen = false;
                OpenSurfacePicker(Mod.Settings.SurfacePickerContextJson);
            }
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

        private void LoadSurfaceScaleValues(string json)
        {
            m_SurfaceScaleValues.Clear();
            try
            {
                Dictionary<string, float> values = JSON.MakeInto<Dictionary<string, float>>(JSON.Load(json ?? "{}"));
                if (values == null)
                {
                    return;
                }

                foreach (KeyValuePair<string, float> value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value.Key) && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value))
                    {
                        m_SurfaceScaleValues[value.Key] = ClampSurfaceScale(value.Value);
                    }
                }
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface scale values");
            }
        }

        private string SerializeSurfaceScaleValues()
        {
            return JSON.Dump(m_SurfaceScaleValues);
        }

        private void LoadSurfaceEdgeFadeValues(string json)
        {
            m_SurfaceEdgeFadeValues.Clear();
            try
            {
                Dictionary<string, float> values = JSON.MakeInto<Dictionary<string, float>>(JSON.Load(json ?? "{}"));
                if (values == null)
                {
                    return;
                }

                foreach (KeyValuePair<string, float> value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value.Key))
                    {
                        float percent = Mathf.Clamp(value.Value, 0f, 10f);
                        if (percent > 0f)
                        {
                            m_SurfaceEdgeFadeValues[value.Key] = percent;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface edge fade values");
            }
        }

        private string SerializeSurfaceEdgeFadeValues()
        {
            return JSON.Dump(m_SurfaceEdgeFadeValues);
        }

        private void LoadSurfaceEdgeFadeEndValues(string json)
        {
            m_SurfaceEdgeFadeEndValues.Clear();
            try
            {
                Dictionary<string, float> values = JSON.MakeInto<Dictionary<string, float>>(JSON.Load(json ?? "{}"));
                if (values == null) return;
                foreach (KeyValuePair<string, float> value in values)
                    if (!string.IsNullOrWhiteSpace(value.Key)) m_SurfaceEdgeFadeEndValues[value.Key] = Mathf.Clamp(value.Value, 0f, 100f);
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface edge fade end values");
            }
        }

        private string SerializeSurfaceEdgeFadeEndValues()
        {
            return JSON.Dump(m_SurfaceEdgeFadeEndValues);
        }

        private void LoadSurfaceEdgeNoiseValues(string json)
        {
            m_SurfaceEdgeNoiseValues.Clear();
            try
            {
                Dictionary<string, float> values = JSON.MakeInto<Dictionary<string, float>>(JSON.Load(json ?? "{}"));
                if (values == null) return;
                foreach (KeyValuePair<string, float> value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value.Key))
                    {
                        m_SurfaceEdgeNoiseValues[value.Key] = Mathf.Clamp(value.Value, 0f, 10f);
                    }
                }
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface edge noise values");
            }
        }

        private string SerializeSurfaceEdgeNoiseValues()
        {
            return JSON.Dump(m_SurfaceEdgeNoiseValues);
        }

        private void LoadSurfaceEdgeNoiseScaleValues(string json)
        {
            m_SurfaceEdgeNoiseScaleValues.Clear();
            try
            {
                Dictionary<string, float> values = JSON.MakeInto<Dictionary<string, float>>(JSON.Load(json ?? "{}"));
                if (values == null) return;
                foreach (KeyValuePair<string, float> value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value.Key))
                        m_SurfaceEdgeNoiseScaleValues[value.Key] = Mathf.Clamp(value.Value, 0.01f, 10f);
                }
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface edge noise scale values");
            }
        }

        private string SerializeSurfaceEdgeNoiseScaleValues()
        {
            return JSON.Dump(m_SurfaceEdgeNoiseScaleValues);
        }

        private void LoadSurfaceBaseColors(string json)
        {
            m_SurfaceBaseColors.Clear();
            try
            {
                Dictionary<string, string> values = JSON.MakeInto<Dictionary<string, string>>(JSON.Load(json ?? "{}"));
                if (values == null) return;
                foreach (KeyValuePair<string, string> value in values)
                {
                    string[] parts = (value.Value ?? string.Empty).Split(',');
                    if (parts.Length == 4 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) &&
                        float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g) &&
                        float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b) &&
                        float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float a))
                    {
                        m_SurfaceBaseColors[value.Key] = new Color(r, g, b, a);
                    }
                }
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not load saved surface base colors");
            }
        }

        private string SerializeSurfaceBaseColors()
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Color> value in m_SurfaceBaseColors)
            {
                values[value.Key] = string.Join(",", value.Value.r.ToString(System.Globalization.CultureInfo.InvariantCulture), value.Value.g.ToString(System.Globalization.CultureInfo.InvariantCulture), value.Value.b.ToString(System.Globalization.CultureInfo.InvariantCulture), value.Value.a.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return JSON.Dump(values);
        }

        private static readonly float[] s_SurfaceScaleStops = Enumerable.Range(0, 39)
            .Select(index => Mathf.Round((0.1f + index * 0.05f) * 100f) / 100f)
            .ToArray();

        private static float ClampSurfaceScale(float scale)
        {
            return Mathf.Clamp(scale, 1f, 50f);
        }

        private void OpenSurfaceScaleEditor(string request)
        {
            string surfaceId = request;
            string origin = "picker";
            int lastComma = request?.LastIndexOf(',') ?? -1;
            int yComma = lastComma > 0 ? request.LastIndexOf(',', lastComma - 1) : -1;
            int xComma = yComma > 0 ? request.LastIndexOf(',', yComma - 1) : -1;
            if (xComma > 0)
            {
                origin = request.Substring(lastComma + 1) == "main" ? "main" : "picker";
                surfaceId = request.Substring(0, xComma);
            }

            if (string.IsNullOrWhiteSpace(surfaceId) ||
                !TryFindSurfacePrefab(surfaceId, out _, out PrefabBase prefab, out RenderedArea renderedArea))
            {
                return;
            }

            m_ActiveSurfaceScaleId = surfaceId;
            m_ActiveSurfaceScaleHadSavedValue = m_SurfaceScaleValues.TryGetValue(surfaceId, out m_ActiveSurfaceScaleSavedValue);
            float defaultUvScale = m_SurfaceScaleBaseValues.TryGetValue(surfaceId, out float capturedUvScale)
                ? capturedUvScale
                : renderedArea.m_UVScale;
            m_ActiveSurfaceScaleBefore = m_ActiveSurfaceScaleHadSavedValue
                ? m_ActiveSurfaceScaleSavedValue
                : ClampSurfaceScale(Mathf.Abs(defaultUvScale) > 0.0001f ? 1f / defaultUvScale : 1f);
            m_ActiveSurfaceScaleDefault = ClampSurfaceScale(Mathf.Abs(defaultUvScale) > 0.0001f ? 1f / defaultUvScale : 1f);
            m_ActiveSurfaceScaleCurrent = m_ActiveSurfaceScaleBefore;
            m_ActiveSurfaceEdgeFadeHadSavedValue = m_SurfaceEdgeFadeValues.TryGetValue(surfaceId, out m_ActiveSurfaceEdgeFadeSavedValue);
            float2 defaultEdgeFade = m_SurfaceEdgeFadeBaseValues.TryGetValue(surfaceId, out float2 capturedEdgeFade)
                ? capturedEdgeFade
                : renderedArea.m_EdgeFadeRange;
            m_ActiveSurfaceEdgeFadeBefore = m_ActiveSurfaceEdgeFadeHadSavedValue
                ? m_ActiveSurfaceEdgeFadeSavedValue
                : 0f;
            m_ActiveSurfaceEdgeFadeCurrent = m_ActiveSurfaceEdgeFadeBefore;
            m_ActiveSurfaceEdgeFadeEndHadSavedValue = m_SurfaceEdgeFadeEndValues.TryGetValue(surfaceId, out m_ActiveSurfaceEdgeFadeEndSavedValue);
            m_ActiveSurfaceEdgeFadeEndBefore = m_ActiveSurfaceEdgeFadeEndHadSavedValue
                ? m_ActiveSurfaceEdgeFadeEndSavedValue
                : defaultEdgeFade.y;
            m_ActiveSurfaceEdgeFadeEndCurrent = m_ActiveSurfaceEdgeFadeEndBefore;
            m_ActiveSurfaceEdgeNoiseHadSavedValue = m_SurfaceEdgeNoiseValues.TryGetValue(surfaceId, out m_ActiveSurfaceEdgeNoiseSavedValue);
            float2 defaultEdgeNoise = m_SurfaceEdgeNoiseBaseValues.TryGetValue(surfaceId, out float2 capturedEdgeNoise)
                ? capturedEdgeNoise
                : renderedArea.m_EdgeNoise;
            // Edge noise is previewed from the prefab's default value. The
            // persisted custom value is intentionally not used to initialize
            // this editor control.
            m_ActiveSurfaceEdgeNoiseBefore = defaultEdgeNoise.x;
            m_ActiveSurfaceEdgeNoiseCurrent = m_ActiveSurfaceEdgeNoiseBefore;
            m_ActiveSurfaceEdgeNoiseDefault = m_ActiveSurfaceEdgeNoiseBefore;
            m_ActiveSurfaceEdgeNoiseScaleHadSavedValue = m_SurfaceEdgeNoiseScaleValues.TryGetValue(surfaceId, out m_ActiveSurfaceEdgeNoiseScaleSavedValue);
            m_ActiveSurfaceEdgeNoiseScaleBefore = m_ActiveSurfaceEdgeNoiseScaleHadSavedValue
                ? m_ActiveSurfaceEdgeNoiseScaleSavedValue
                : Mathf.Max(0.01f, defaultEdgeNoise.y);
            m_ActiveSurfaceEdgeNoiseScaleDefault = Mathf.Max(0.01f, defaultEdgeNoise.y);
            m_ActiveSurfaceEdgeNoiseScaleCurrent = m_ActiveSurfaceEdgeNoiseScaleBefore;
            m_ActiveSurfaceBaseColorHadSavedValue = m_SurfaceBaseColors.TryGetValue(surfaceId, out m_ActiveSurfaceBaseColorSavedValue);
            m_ActiveSurfaceBaseColorDefault = m_SurfaceBaseColorDefaults.TryGetValue(surfaceId, out Color capturedBaseColor)
                ? capturedBaseColor
                : renderedArea.m_BaseColor;
            m_ActiveSurfaceBaseColorBefore = m_ActiveSurfaceBaseColorHadSavedValue
                ? m_ActiveSurfaceBaseColorSavedValue
                : m_ActiveSurfaceBaseColorDefault;
            m_ActiveSurfaceBaseColorCurrent = m_ActiveSurfaceBaseColorBefore;
            Color.RGBToHSV(m_ActiveSurfaceBaseColorCurrent, out float baseHue, out float baseSaturation, out float baseValue);
            m_SurfaceScaleSurfaceId.Update(surfaceId);
            m_SurfaceScaleSurfaceName.Update(prefab.name ?? surfaceId);
            m_SurfaceScaleEditorOrigin.Update(origin);
            m_SurfaceScaleHundredths.Update(Mathf.RoundToInt(m_ActiveSurfaceScaleCurrent * 100f));
            m_SurfaceEdgeFadePercent.Update(m_ActiveSurfaceEdgeFadeCurrent);
            m_SurfaceEdgeFadeEndPercent.Update(m_ActiveSurfaceEdgeFadeEndCurrent);
            m_SurfaceEdgeNoisePercent.Update(m_ActiveSurfaceEdgeNoiseCurrent);
            m_SurfaceEdgeNoiseScale.Update(m_ActiveSurfaceEdgeNoiseScaleCurrent);
            // Zero is the UI sentinel for the prefab's default fade pair.
            m_SurfaceEdgeFadeDefault.Update(0f);
            m_SurfaceEdgeNoiseDefault.Update(defaultEdgeNoise.x);
            m_SurfaceEdgeNoiseScaleDefault.Update(m_ActiveSurfaceEdgeNoiseScaleDefault);
            m_SurfaceBaseColorHue.Update(baseHue);
            m_SurfaceBaseColorSaturation.Update(baseSaturation);
            m_SurfaceBaseColorBrightness.Update(baseValue);
            m_SurfaceBaseColorAlpha.Update(m_ActiveSurfaceBaseColorCurrent.a);
            m_SurfaceScaleEditorIsOpen = true;
            m_SurfaceScaleEditorOpen.Update(true);
        }

        private void SetSurfaceScaleEditorPosition(string value)
        {
            string[] parts = (value ?? string.Empty).Split(',');
            if (parts.Length < 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)) return;
            m_SurfaceScaleEditorX.Update(x);
            m_SurfaceScaleEditorY.Update(y);
            if (Mod.Settings != null)
            {
                Mod.Settings.SurfaceParametersX = x;
                Mod.Settings.SurfaceParametersY = y;
                Mod.Settings.ApplyAndSave();
            }
        }

        private float m_ActiveSurfaceScaleCurrent = 1f;
        private float m_ActiveSurfaceEdgeFadeBefore;
        private float m_ActiveSurfaceEdgeFadeSavedValue;
        private float m_ActiveSurfaceEdgeFadeCurrent;
        private bool m_ActiveSurfaceEdgeFadeHadSavedValue;
        private float m_ActiveSurfaceEdgeFadeEndBefore;
        private float m_ActiveSurfaceEdgeFadeEndSavedValue;
        private float m_ActiveSurfaceEdgeFadeEndCurrent;
        private bool m_ActiveSurfaceEdgeFadeEndHadSavedValue;
        private float m_ActiveSurfaceEdgeNoiseBefore;
        private float m_ActiveSurfaceEdgeNoiseSavedValue;
        private float m_ActiveSurfaceEdgeNoiseCurrent;
        private bool m_ActiveSurfaceEdgeNoiseHadSavedValue;
        private float m_ActiveSurfaceEdgeNoiseScaleBefore;
        private float m_ActiveSurfaceEdgeNoiseScaleDefault;
        private float m_ActiveSurfaceEdgeNoiseScaleSavedValue;
        private float m_ActiveSurfaceEdgeNoiseScaleCurrent;
        private bool m_ActiveSurfaceEdgeNoiseScaleHadSavedValue;
        private Color m_ActiveSurfaceBaseColorBefore;
        private Color m_ActiveSurfaceBaseColorDefault;
        private Color m_ActiveSurfaceBaseColorCurrent;
        private Color m_ActiveSurfaceBaseColorSavedValue;
        private bool m_ActiveSurfaceBaseColorHadSavedValue;

        private void SetSurfaceScalePreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId) || !int.TryParse(value, out int hundredths))
            {
                return;
            }

            hundredths = Mathf.Clamp(hundredths, 100, 5000);
            Mod.Log.InfoFormat("Surface slider input: kind='Scale', raw='{0}', parsedHundredths={1}", value, hundredths);
            m_ActiveSurfaceScaleCurrent = ClampSurfaceScale(hundredths / 100f);
            SetSurfaceScaleValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceScaleHundredths.Update(Mathf.RoundToInt(m_ActiveSurfaceScaleCurrent * 100f));
            m_SurfaceScalesJson.Update(SerializeSurfaceScaleValues());
        }

        private void SetSurfaceEdgeFadePreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId) || !float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float percent))
            {
                return;
            }

            Mod.Log.InfoFormat("Surface slider input: kind='Edge fade', raw='{0}', parsed={1}, maximum={2}", value, percent, 20f);
            m_ActiveSurfaceEdgeFadeCurrent = Mathf.Clamp(percent, 0f, 10f);
            SetSurfaceEdgeFadeValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeFadeCurrent);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceEdgeFadePercent.Update(m_ActiveSurfaceEdgeFadeCurrent);
            m_SurfaceEdgeFadesJson.Update(SerializeSurfaceEdgeFadeValues());
        }

        private void SetSurfaceEdgeFadeEndPreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId) || !float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float percent)) return;
            m_ActiveSurfaceEdgeFadeEndCurrent = Mathf.Clamp(percent, 0f, 100f);
            SetSurfaceEdgeFadeEndValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeFadeEndCurrent);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceEdgeFadeEndPercent.Update(m_ActiveSurfaceEdgeFadeEndCurrent);
            m_SurfaceEdgeFadeEndsJson.Update(SerializeSurfaceEdgeFadeEndValues());
        }

        private void SetSurfaceEdgeNoisePreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId) || !float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float percent)) return;
            Mod.Log.InfoFormat("Surface slider input: kind='Edge noise', raw='{0}', parsed={1}, maximum={2}", value, percent, 50f);
            m_ActiveSurfaceEdgeNoiseCurrent = Mathf.Clamp(percent, 0f, 10f);
            SetSurfaceEdgeNoiseValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeNoiseCurrent);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceEdgeNoisePercent.Update(m_ActiveSurfaceEdgeNoiseCurrent);
            m_SurfaceEdgeNoisesJson.Update(SerializeSurfaceEdgeNoiseValues());
        }

        private void SetSurfaceEdgeNoiseScalePreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId) || !float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float scale)) return;
            m_ActiveSurfaceEdgeNoiseScaleCurrent = Mathf.Clamp(scale, 0.01f, 10f);
            SetSurfaceEdgeNoiseScaleValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeNoiseScaleCurrent);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceEdgeNoiseScale.Update(m_ActiveSurfaceEdgeNoiseScaleCurrent);
            m_SurfaceEdgeNoiseScalesJson.Update(SerializeSurfaceEdgeNoiseScaleValues());
        }

        private void SetSurfaceBaseColorPreview(string value)
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId)) return;
            string[] parts = (value ?? string.Empty).Split(',');
            if (parts.Length != 4 ||
                !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float hue) ||
                !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float saturation) ||
                !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float brightness) ||
                !float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float alpha)) return;
            m_ActiveSurfaceBaseColorCurrent = Color.HSVToRGB(Mathf.Repeat(hue, 1f), Mathf.Clamp01(saturation), Mathf.Clamp01(brightness));
            m_ActiveSurfaceBaseColorCurrent.a = Mathf.Clamp01(alpha);
            if (m_SurfaceBaseColorDefaults.TryGetValue(m_ActiveSurfaceScaleId, out Color defaultBaseColor) &&
                ColorDifference(m_ActiveSurfaceBaseColorCurrent, defaultBaseColor) < 0.0001f)
                m_SurfaceBaseColors.Remove(m_ActiveSurfaceScaleId);
            else
                m_SurfaceBaseColors[m_ActiveSurfaceScaleId] = m_ActiveSurfaceBaseColorCurrent;
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceBaseColorsJson.Update(SerializeSurfaceBaseColors());
        }

        private void ApplySurfaceScale()
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId))
            {
                m_SurfaceScaleEditorOpen.Update(false);
                return;
            }

            if (Mathf.Abs(m_ActiveSurfaceScaleCurrent - m_ActiveSurfaceScaleDefault) < 0.0001f)
                m_SurfaceScaleValues.Remove(m_ActiveSurfaceScaleId);
            else
                SetSurfaceScaleValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent);
            SetSurfaceEdgeFadeValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeFadeCurrent);
            // Edge fade zero means the complete prefab default pair, so the
            // hidden end value must not be persisted independently.
            if (Mathf.Abs(m_ActiveSurfaceEdgeFadeCurrent) < 0.0001f)
                m_SurfaceEdgeFadeEndValues.Remove(m_ActiveSurfaceScaleId);
            else
                SetSurfaceEdgeFadeEndValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeFadeEndCurrent);
            if (Mathf.Abs(m_ActiveSurfaceEdgeNoiseCurrent - m_ActiveSurfaceEdgeNoiseDefault) < 0.0001f)
                m_SurfaceEdgeNoiseValues.Remove(m_ActiveSurfaceScaleId);
            else
                SetSurfaceEdgeNoiseValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeNoiseCurrent);
            if (Mathf.Abs(m_ActiveSurfaceEdgeNoiseScaleCurrent - m_ActiveSurfaceEdgeNoiseScaleDefault) < 0.0001f)
                m_SurfaceEdgeNoiseScaleValues.Remove(m_ActiveSurfaceScaleId);
            else
                SetSurfaceEdgeNoiseScaleValue(m_ActiveSurfaceScaleId, m_ActiveSurfaceEdgeNoiseScaleCurrent);
            if (ColorDifference(m_ActiveSurfaceBaseColorCurrent, m_ActiveSurfaceBaseColorDefault) < 0.0001f)
                m_SurfaceBaseColors.Remove(m_ActiveSurfaceScaleId);
            else
                m_SurfaceBaseColors[m_ActiveSurfaceScaleId] = m_ActiveSurfaceBaseColorCurrent;
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            if (Mod.Settings != null)
            {
                Mod.Settings.SurfaceScalesJson = SerializeSurfaceScaleValues();
                Mod.Settings.SurfaceEdgeFadesJson = SerializeSurfaceEdgeFadeValues();
                Mod.Settings.SurfaceEdgeFadeEndsJson = SerializeSurfaceEdgeFadeEndValues();
                Mod.Settings.SurfaceEdgeNoisesJson = SerializeSurfaceEdgeNoiseValues();
                Mod.Settings.SurfaceEdgeNoiseScalesJson = SerializeSurfaceEdgeNoiseScaleValues();
                Mod.Settings.SurfaceBaseColorsJson = SerializeSurfaceBaseColors();
                Mod.Settings.ApplyAndSave();
            }

            m_SurfaceScalesJson.Update(SerializeSurfaceScaleValues());
            m_SurfaceEdgeFadesJson.Update(SerializeSurfaceEdgeFadeValues());
            m_SurfaceEdgeFadeEndsJson.Update(SerializeSurfaceEdgeFadeEndValues());
            m_SurfaceEdgeNoisesJson.Update(SerializeSurfaceEdgeNoiseValues());
            m_SurfaceEdgeNoiseScalesJson.Update(SerializeSurfaceEdgeNoiseScaleValues());
            m_SurfaceBaseColorsJson.Update(SerializeSurfaceBaseColors());
            CloseSurfaceScaleEditor();
        }

        private void RevertSurfaceScale()
        {
            if (string.IsNullOrEmpty(m_ActiveSurfaceScaleId)) return;
            m_SurfaceScaleValues.Remove(m_ActiveSurfaceScaleId);
            m_SurfaceEdgeFadeValues.Remove(m_ActiveSurfaceScaleId);
            m_SurfaceEdgeFadeEndValues.Remove(m_ActiveSurfaceScaleId);
            m_SurfaceEdgeNoiseValues.Remove(m_ActiveSurfaceScaleId);
            m_SurfaceEdgeNoiseScaleValues.Remove(m_ActiveSurfaceScaleId);
            m_SurfaceBaseColors.Remove(m_ActiveSurfaceScaleId);
            m_ActiveSurfaceScaleCurrent = m_ActiveSurfaceScaleDefault;
            m_ActiveSurfaceEdgeFadeCurrent = 0;
            m_ActiveSurfaceEdgeNoiseCurrent = m_ActiveSurfaceEdgeNoiseDefault;
            m_ActiveSurfaceEdgeNoiseScaleCurrent = m_ActiveSurfaceEdgeNoiseScaleDefault;
            m_ActiveSurfaceBaseColorCurrent = m_ActiveSurfaceBaseColorDefault;
            Color.RGBToHSV(m_ActiveSurfaceBaseColorCurrent, out float revertHue, out float revertSaturation, out float revertValue);
            ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleCurrent, updatePrefab: true);
            m_SurfaceScaleHundredths.Update(Mathf.RoundToInt(m_ActiveSurfaceScaleCurrent * 100f));
            m_SurfaceEdgeFadePercent.Update(0);
            m_SurfaceEdgeFadeEndPercent.Update(0);
            m_SurfaceEdgeNoisePercent.Update(m_ActiveSurfaceEdgeNoiseCurrent);
            m_SurfaceEdgeNoiseScale.Update(m_ActiveSurfaceEdgeNoiseScaleCurrent);
            m_SurfaceBaseColorHue.Update(revertHue);
            m_SurfaceBaseColorSaturation.Update(revertSaturation);
            m_SurfaceBaseColorBrightness.Update(revertValue);
            m_SurfaceBaseColorAlpha.Update(m_ActiveSurfaceBaseColorCurrent.a);
            m_SurfaceScaleRevertVersion.Update(++m_SurfaceScaleRevertCounter);
            m_SurfaceScalesJson.Update(SerializeSurfaceScaleValues());
            m_SurfaceEdgeFadesJson.Update(SerializeSurfaceEdgeFadeValues());
            m_SurfaceEdgeNoisesJson.Update(SerializeSurfaceEdgeNoiseValues());
            m_SurfaceEdgeNoiseScalesJson.Update(SerializeSurfaceEdgeNoiseScaleValues());
            m_SurfaceBaseColorsJson.Update(SerializeSurfaceBaseColors());
            if (Mod.Settings != null)
            {
                Mod.Settings.SurfaceScalesJson = SerializeSurfaceScaleValues();
                Mod.Settings.SurfaceEdgeFadesJson = SerializeSurfaceEdgeFadeValues();
                Mod.Settings.SurfaceEdgeFadeEndsJson = SerializeSurfaceEdgeFadeEndValues();
                Mod.Settings.SurfaceEdgeNoisesJson = SerializeSurfaceEdgeNoiseValues();
                Mod.Settings.SurfaceEdgeNoiseScalesJson = SerializeSurfaceEdgeNoiseScaleValues();
                Mod.Settings.SurfaceBaseColorsJson = SerializeSurfaceBaseColors();
                Mod.Settings.ApplyAndSave();
            }
        }

        private void CancelSurfaceScale()
        {
            if (!string.IsNullOrEmpty(m_ActiveSurfaceScaleId))
            {
                if (m_ActiveSurfaceScaleHadSavedValue)
                {
                    m_SurfaceScaleValues[m_ActiveSurfaceScaleId] = m_ActiveSurfaceScaleSavedValue;
                }
                else
                {
                    m_SurfaceScaleValues.Remove(m_ActiveSurfaceScaleId);
                }

                if (m_ActiveSurfaceEdgeFadeHadSavedValue)
                {
                    m_SurfaceEdgeFadeValues[m_ActiveSurfaceScaleId] = m_ActiveSurfaceEdgeFadeSavedValue;
                }
                else
                {
                    m_SurfaceEdgeFadeValues.Remove(m_ActiveSurfaceScaleId);
                }

                if (m_ActiveSurfaceEdgeFadeEndHadSavedValue)
                    m_SurfaceEdgeFadeEndValues[m_ActiveSurfaceScaleId] = m_ActiveSurfaceEdgeFadeEndSavedValue;
                else
                    m_SurfaceEdgeFadeEndValues.Remove(m_ActiveSurfaceScaleId);

                if (m_ActiveSurfaceEdgeNoiseHadSavedValue)
                    m_SurfaceEdgeNoiseValues[m_ActiveSurfaceScaleId] = m_ActiveSurfaceEdgeNoiseSavedValue;
                else
                    m_SurfaceEdgeNoiseValues.Remove(m_ActiveSurfaceScaleId);

                if (m_ActiveSurfaceEdgeNoiseScaleHadSavedValue)
                    m_SurfaceEdgeNoiseScaleValues[m_ActiveSurfaceScaleId] = m_ActiveSurfaceEdgeNoiseScaleSavedValue;
                else
                    m_SurfaceEdgeNoiseScaleValues.Remove(m_ActiveSurfaceScaleId);

                if (m_ActiveSurfaceBaseColorHadSavedValue)
                    m_SurfaceBaseColors[m_ActiveSurfaceScaleId] = m_ActiveSurfaceBaseColorSavedValue;
                else
                    m_SurfaceBaseColors.Remove(m_ActiveSurfaceScaleId);

                ApplySurfaceScaleToPrefab(m_ActiveSurfaceScaleId, m_ActiveSurfaceScaleBefore, updatePrefab: true);
                m_SurfaceScalesJson.Update(SerializeSurfaceScaleValues());
                m_SurfaceEdgeFadesJson.Update(SerializeSurfaceEdgeFadeValues());
                m_SurfaceEdgeFadeEndsJson.Update(SerializeSurfaceEdgeFadeEndValues());
                m_SurfaceEdgeNoisesJson.Update(SerializeSurfaceEdgeNoiseValues());
                m_SurfaceEdgeNoiseScalesJson.Update(SerializeSurfaceEdgeNoiseScaleValues());
                m_SurfaceBaseColorsJson.Update(SerializeSurfaceBaseColors());
            }

            CloseSurfaceScaleEditor();
        }

        private void CloseSurfaceScaleEditor()
        {
            m_ActiveSurfaceScaleId = null;
            m_SurfaceScaleEditorIsOpen = false;
            m_SurfaceScaleSurfaceId.Update("");
            m_SurfaceScaleSurfaceName.Update("");
            m_SurfaceEdgeFadePercent.Update(0);
            m_SurfaceEdgeFadeEndPercent.Update(0);
            m_SurfaceEdgeNoisePercent.Update(0);
            m_SurfaceEdgeNoiseScale.Update(0.2f);
            m_SurfaceScaleEditorOpen.Update(false);
        }

        private void SetSurfaceScaleValue(string surfaceId, float scale)
        {
            scale = ClampSurfaceScale(scale);
            if (Mathf.Abs(scale - 1f) < 0.001f)
            {
                m_SurfaceScaleValues.Remove(surfaceId);
            }
            else
            {
                m_SurfaceScaleValues[surfaceId] = scale;
            }
        }

        private static float ColorDifference(Color first, Color second)
        {
            return Mathf.Max(Mathf.Abs(first.r - second.r), Mathf.Abs(first.g - second.g), Mathf.Abs(first.b - second.b), Mathf.Abs(first.a - second.a));
        }

        private void SetSurfaceEdgeFadeValue(string surfaceId, float percent)
        {
            percent = Mathf.Clamp(percent, 0f, 10f);
            if (Mathf.Abs(percent) < 0.0001f)
            {
                m_SurfaceEdgeFadeValues.Remove(surfaceId);
            }
            else
            {
                m_SurfaceEdgeFadeValues[surfaceId] = percent;
            }
        }

        private void SetSurfaceEdgeFadeEndValue(string surfaceId, float percent)
        {
            percent = Mathf.Clamp(percent, 0f, 100f);
            if (Mathf.Abs(percent) < 0.0001f) m_SurfaceEdgeFadeEndValues.Remove(surfaceId);
            else m_SurfaceEdgeFadeEndValues[surfaceId] = percent;
        }

        private void SetSurfaceEdgeNoiseValue(string surfaceId, float percent)
        {
            percent = Mathf.Clamp(percent, 0f, 10f);
            if (Mathf.Abs(percent) < 0.0001f) m_SurfaceEdgeNoiseValues.Remove(surfaceId);
            else m_SurfaceEdgeNoiseValues[surfaceId] = percent;
        }

        private void SetSurfaceEdgeNoiseScaleValue(string surfaceId, float value)
        {
            m_SurfaceEdgeNoiseScaleValues[surfaceId] = Mathf.Clamp(value, 0.01f, 10f);
        }


        private bool TryFindSurfacePrefab(string surfaceId, out Entity surfaceEntity, out PrefabBase surfacePrefab, out RenderedArea renderedArea)
        {
            using (NativeArray<Entity> entities = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if ((!EntityManager.HasComponent<SurfaceData>(entity) && !EntityManager.HasComponent<AreaData>(entity)) ||
                        !m_PrefabSystemForScale().TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab prefab) ||
                        prefab == null || !string.Equals(prefab.GetPrefabID().ToString(), surfaceId, StringComparison.Ordinal) ||
                        !prefab.TryGet<RenderedArea>(out renderedArea) || renderedArea == null)
                    {
                        continue;
                    }

                    surfaceEntity = entity;
                    surfacePrefab = prefab;
                    return true;
                }
            }

            surfaceEntity = Entity.Null;
            surfacePrefab = null;
            renderedArea = null;
            return false;
        }

        private PrefabSystem m_PrefabSystemForScale()
        {
            return World.GetExistingSystemManaged<PrefabSystem>();
        }

        private void RestoreConfiguredSurfaceScales(PrefabSystem prefabSystem)
        {
            m_AreaBatchSystem ??= World.GetOrCreateSystemManaged<AreaBatchSystem>();
            HashSet<string> configuredSurfaceIds = new HashSet<string>(m_SurfaceScaleValues.Keys, StringComparer.Ordinal);
            configuredSurfaceIds.UnionWith(m_SurfaceEdgeFadeValues.Keys);
            configuredSurfaceIds.UnionWith(m_SurfaceEdgeFadeEndValues.Keys);
            configuredSurfaceIds.UnionWith(m_SurfaceEdgeNoiseValues.Keys);
            configuredSurfaceIds.UnionWith(m_SurfaceEdgeNoiseScaleValues.Keys);
            configuredSurfaceIds.UnionWith(m_SurfaceBaseColors.Keys);
            foreach (string surfaceId in configuredSurfaceIds)
            {
                if (m_RestoredSurfaceScaleIds.Contains(surfaceId))
                {
                    continue;
                }

                float scale = m_SurfaceScaleValues.TryGetValue(surfaceId, out float savedScale)
                    ? savedScale
                    : (m_SurfaceScaleBaseValues.TryGetValue(surfaceId, out float baseUvScale) && Mathf.Abs(baseUvScale) > 0.0001f
                        ? ClampSurfaceScale(1f / baseUvScale)
                        : 1f);
                if (TryFindSurfacePrefab(surfaceId, out _, out PrefabBase _, out RenderedArea renderedArea))
                {
                    if (ApplySurfaceScaleToPrefab(surfaceId, scale, updatePrefab: true, prefabSystem))
                    {
                        m_RestoredSurfaceScaleIds.Add(surfaceId);
                    }
                }
            }
        }

        // Capture the unmodified prefab appearance before a map can instantiate
        // surfaces or restore previously saved runtime changes.
        private void CaptureSurfaceDefaults(PrefabSystem prefabSystem)
        {
            using (NativeArray<Entity> entities = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (!prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab prefab) || prefab == null ||
                        !prefab.TryGet<RenderedArea>(out RenderedArea renderedArea) || renderedArea == null)
                    {
                        continue;
                    }

                    string surfaceId = prefab.GetPrefabID().ToString();
                    Material material = renderedArea.m_Material;
                    float2 edgeFade = renderedArea.m_EdgeFadeRange;
                    float2 edgeNoise = renderedArea.m_EdgeNoise;
                    Color baseColor = renderedArea.m_BaseColor;
                    if (material != null && material.HasProperty(s_EdgeFadeRangeProperty))
                    {
                        Vector4 value = material.GetVector(s_EdgeFadeRangeProperty);
                        edgeFade = new float2(value.x, value.y);
                    }
                    if (material != null && material.HasProperty(s_EdgeNoiseProperty))
                    {
                        Vector4 value = material.GetVector(s_EdgeNoiseProperty);
                        edgeNoise = new float2(value.x, value.y);
                    }
                    if (material != null && material.HasProperty(s_BaseColorProperty)) baseColor = material.GetColor(s_BaseColorProperty);

                    if (!m_SurfaceScaleBaseValues.ContainsKey(surfaceId)) m_SurfaceScaleBaseValues[surfaceId] = renderedArea.m_UVScale;
                    if (!m_SurfaceEdgeFadeBaseValues.ContainsKey(surfaceId)) m_SurfaceEdgeFadeBaseValues[surfaceId] = edgeFade;
                    if (!m_SurfaceEdgeNoiseBaseValues.ContainsKey(surfaceId)) m_SurfaceEdgeNoiseBaseValues[surfaceId] = edgeNoise;
                    if (!m_SurfaceBaseColorDefaults.ContainsKey(surfaceId)) m_SurfaceBaseColorDefaults[surfaceId] = baseColor;
                }
            }
        }

        private bool ApplySurfaceScaleToPrefab(string surfaceId, float scale, bool updatePrefab, PrefabSystem prefabSystem = null)
        {
            prefabSystem ??= World.GetExistingSystemManaged<PrefabSystem>();
            if (prefabSystem == null)
            {
                return false;
            }

            m_AreaBatchSystem ??= World.GetOrCreateSystemManaged<AreaBatchSystem>();
            bool applied = false;
            using (NativeArray<Entity> entities = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if ((!EntityManager.HasComponent<SurfaceData>(entity) && !EntityManager.HasComponent<AreaData>(entity)) ||
                        !prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab prefab) || prefab == null ||
                        !string.Equals(prefab.GetPrefabID().ToString(), surfaceId, StringComparison.Ordinal) ||
                        !prefab.TryGet<RenderedArea>(out RenderedArea renderedArea) || renderedArea == null)
                    {
                        continue;
                    }

                    int prefabBatchIndex = EntityManager.HasComponent<RenderedAreaData>(entity)
                        ? EntityManager.GetComponentData<RenderedAreaData>(entity).m_BatchIndex
                        : -1;
                    Material sourceMaterial = renderedArea.m_Material;
                    int materialUvScaleProperty = s_AreaBatchUvScaleField != null
                        ? (int)s_AreaBatchUvScaleField.GetValue(m_AreaBatchSystem)
                        : 0;
                    bool sourceHasScale = sourceMaterial != null && materialUvScaleProperty != 0 &&
                        sourceMaterial.HasProperty(materialUvScaleProperty);
                    if (!sourceHasScale && prefabBatchIndex >= 0 &&
                        m_AreaBatchSystem.GetAreaBatch(prefabBatchIndex, out _, out _, out _, out Material batchMaterial, out _, out _, out _) &&
                        batchMaterial != null && materialUvScaleProperty != 0 && batchMaterial.HasProperty(materialUvScaleProperty))
                    {
                        sourceMaterial = batchMaterial;
                        sourceHasScale = true;
                    }

                    // A prefab's batch may not exist yet when settings are restored
                    // just after map load. Try a live instance batch and retry on
                    // later updates if neither material is ready.
                    if (!sourceHasScale)
                    {
                        using (NativeArray<Entity> instances = m_SurfaceScaleInstancesQuery.ToEntityArray(Allocator.Temp))
                        {
                            foreach (Entity instance in instances)
                            {
                                if (!EntityManager.Exists(instance) ||
                                    EntityManager.GetComponentData<PrefabRef>(instance).m_Prefab != entity)
                                {
                                    continue;
                                }

                                int instanceBatch = EntityManager.GetComponentData<Batch>(instance).m_BatchIndex;
                                if (m_AreaBatchSystem.GetAreaBatch(instanceBatch, out _, out _, out _, out Material instanceMaterial, out _, out _, out _) &&
                                    instanceMaterial != null && materialUvScaleProperty != 0 && instanceMaterial.HasProperty(materialUvScaleProperty))
                                {
                                    sourceMaterial = instanceMaterial;
                                    sourceHasScale = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (!sourceHasScale || sourceMaterial == null)
                    {
                        if (m_SurfaceScaleMaterialUnavailableLogged.Add(surfaceId))
                        {
                            Mod.Log.InfoFormat(
                                "Surface texture scale is waiting for its AreaBatch material: surface='{0}', prefabBatch={1}",
                                prefab.name,
                                prefabBatchIndex);
                        }
                        continue;
                    }

                    if (!m_SurfaceScaleBaseValues.TryGetValue(surfaceId, out float baseScale))
                    {
                        baseScale = sourceMaterial.GetFloat(materialUvScaleProperty);
                        m_SurfaceScaleBaseValues[surfaceId] = baseScale;
                    }

                    if (!m_SurfaceEdgeFadeBaseValues.ContainsKey(surfaceId))
                    {
                        float2 baseEdgeFadeRange = renderedArea.m_EdgeFadeRange;
                        if (sourceMaterial.HasProperty(s_EdgeFadeRangeProperty))
                        {
                            Vector4 value = sourceMaterial.GetVector(s_EdgeFadeRangeProperty);
                            baseEdgeFadeRange = new float2(value.x, value.y);
                        }

                        m_SurfaceEdgeFadeBaseValues[surfaceId] = baseEdgeFadeRange;
                    }

                    if (!m_SurfaceEdgeNoiseBaseValues.ContainsKey(surfaceId))
                    {
                        float2 baseEdgeNoise = renderedArea.m_EdgeNoise;
                        if (sourceMaterial.HasProperty(s_EdgeNoiseProperty))
                        {
                            Vector4 value = sourceMaterial.GetVector(s_EdgeNoiseProperty);
                            baseEdgeNoise = new float2(value.x, value.y);
                        }
                        m_SurfaceEdgeNoiseBaseValues[surfaceId] = baseEdgeNoise;
                    }

                    // AreaBatchSystem applies RenderedArea.m_UVScale only when the
                    // component uses OverrideMaterial. Keep a private material copy
                    // and copy its scalar settings into RenderedArea before enabling
                    // that native path, so this does not mutate shared materials or
                    // reset the source surface's appearance.
                    if (!m_SurfaceScaleMaterialCloneIds.Contains(surfaceId) && sourceMaterial != null)
                    {
                        Material prefabMaterial = new Material(sourceMaterial)
                        {
                            name = "Replacement Solutions Surface Material (" + prefab.name + ")",
                        };
                        CopyMaterialSettingsToRenderedArea(prefabMaterial, renderedArea);
                        renderedArea.m_Material = prefabMaterial;
                        renderedArea.m_Version = RenderedArea.Version.OverrideMaterial;
                        m_SurfaceScaleMaterialCloneIds.Add(surfaceId);
                        Mod.Log.InfoFormat(
                            "Surface texture scale material prepared: surface='{0}', sourceMaterial='{1}', baseline={2}, shader='{3}', version={4}",
                            prefab.name,
                            sourceMaterial.name,
                            baseScale,
                            sourceMaterial.shader != null ? sourceMaterial.shader.name : "<no shader>",
                            renderedArea.m_Version);
                    }

                    // The editor exposes an intuitive texture-size factor. The game
                    // stores the reciprocal UV scale, so apply exactly 1 / Scale.
                    float userScale = ClampSurfaceScale(scale);
                    float scaledUv = 1f / userScale;
                    renderedArea.m_UVScale = scaledUv;
                    renderedArea.m_Material.SetFloat(materialUvScaleProperty, scaledUv);
                    float edgeFadePercent = m_SurfaceEdgeFadeValues.TryGetValue(surfaceId, out float savedEdgeFade)
                        ? savedEdgeFade
                        : m_SurfaceEdgeFadeBaseValues[surfaceId].x;
                    float edgeFadeEndPercent = m_SurfaceEdgeFadeEndValues.TryGetValue(surfaceId, out float savedEdgeFadeEnd)
                        ? savedEdgeFadeEnd
                        : m_SurfaceEdgeFadeBaseValues[surfaceId].y;
                    float2 edgeFadeRange = ExpandSurfaceEdgeFadeRange(
                        m_SurfaceEdgeFadeBaseValues[surfaceId],
                        edgeFadePercent,
                        edgeFadeEndPercent);
                    renderedArea.m_EdgeFadeRange = edgeFadeRange;
                    if (s_EdgeFadeRangeProperty != 0 && renderedArea.m_Material.HasProperty(s_EdgeFadeRangeProperty))
                    {
                        renderedArea.m_Material.SetVector(
                            s_EdgeFadeRangeProperty,
                            new Vector4(edgeFadeRange.x, edgeFadeRange.y, 0f, 0f));
                    }
                    float edgeNoisePercent = m_SurfaceEdgeNoiseValues.TryGetValue(surfaceId, out float savedEdgeNoise)
                        ? savedEdgeNoise
                        : m_SurfaceEdgeNoiseBaseValues[surfaceId].x;
                    float2 edgeNoise = m_SurfaceEdgeNoiseBaseValues[surfaceId];
                    // The shader's first EdgeNoise component is noise strength; the
                    // second is its scale. Leave the source scale intact and expose
                    // strength as a percentage so zero restores the exact source.
                    // Keep the exact user input in the material/batch. The UI
                    // range is intentionally not normalized here.
                    edgeNoise.x = edgeNoisePercent;
                    if (m_SurfaceEdgeNoiseScaleValues.TryGetValue(surfaceId, out float savedEdgeNoiseScale))
                        edgeNoise.y = Mathf.Max(0.01f, savedEdgeNoiseScale);
                    Mod.Log.InfoFormat(
                        "Surface appearance output: surface='{0}', scale={1}, uvScale={2}, edgeFadeInput={3}, edgeFadeEndInput={4}, edgeFadeRange=({5},{6}), edgeNoiseInput={7}, edgeNoise=({8},{9})",
                        prefab.name,
                        userScale,
                        scaledUv,
                        edgeFadePercent,
                        edgeFadeEndPercent,
                        edgeFadeRange.x,
                        edgeFadeRange.y,
                        edgeNoisePercent,
                        edgeNoise.x,
                        edgeNoise.y);
                    renderedArea.m_EdgeNoise = edgeNoise;
                    if (s_EdgeNoiseProperty != 0 && renderedArea.m_Material.HasProperty(s_EdgeNoiseProperty))
                    {
                        renderedArea.m_Material.SetVector(s_EdgeNoiseProperty, new Vector4(edgeNoise.x, edgeNoise.y, 0f, 0f));
                    }
                    Color baseColor = m_SurfaceBaseColors.TryGetValue(surfaceId, out Color savedBaseColor)
                        ? savedBaseColor
                        : (m_SurfaceBaseColorDefaults.TryGetValue(surfaceId, out Color defaultBaseColor) ? defaultBaseColor : Color.white);
                    renderedArea.m_BaseColor = baseColor;
                    if (renderedArea.m_Material.HasProperty(s_BaseColorProperty)) renderedArea.m_Material.SetColor(s_BaseColorProperty, baseColor);
                    if (updatePrefab)
                    {
                        prefabSystem.UpdatePrefab(prefab);
                    }

                    SetAreaBatchUvScale(prefabBatchIndex, scaledUv);
                    SetAreaBatchEdgeFadeRange(prefabBatchIndex, edgeFadeRange);
                    SetAreaBatchEdgeNoise(prefabBatchIndex, edgeNoise);
                    SetAreaBatchBaseColor(prefabBatchIndex, baseColor);

                    // Existing area instances can hold their own batch index even
                    // though they share this surface prefab. Update those batches
                    // too, otherwise only newly created/replaced surfaces use the
                    // selected UV scale.
                    using (NativeArray<Entity> instances = m_SurfaceScaleInstancesQuery.ToEntityArray(Allocator.Temp))
                    {
                        foreach (Entity instance in instances)
                        {
                            if (!EntityManager.Exists(instance) ||
                                EntityManager.GetComponentData<PrefabRef>(instance).m_Prefab != entity)
                            {
                                continue;
                            }

                            int instanceBatch = EntityManager.GetComponentData<Batch>(instance).m_BatchIndex;
                            SetAreaBatchUvScale(instanceBatch, scaledUv);
                            SetAreaBatchEdgeFadeRange(instanceBatch, edgeFadeRange);
                            SetAreaBatchEdgeNoise(instanceBatch, edgeNoise);
                            SetAreaBatchBaseColor(instanceBatch, baseColor);
                        }
                    }

                    // AreaBatchSystem may keep the material in its enabled-shader
                    // cache after its properties were changed. Notify it once so
                    // the new edge fade reaches the visible batches immediately.
                    m_AreaBatchSystem.EnabledShadersUpdated();

                    Mod.Log.InfoFormat(
                        "Surface appearance applied: surface='{0}', scale={1}, uvScale={2}, edgeFadePercent={3}, edgeFadeRange=({4},{5}), edgeNoisePercent={6}, edgeNoise=({7},{8}), prefabBatch={9}, updatedPrefab={10}",
                        prefab.name,
                        userScale,
                        scaledUv,
                        edgeFadePercent,
                        edgeFadeRange.x,
                        edgeFadeRange.y,
                        edgeNoisePercent,
                        edgeNoise.x,
                        edgeNoise.y,
                        prefabBatchIndex,
                        updatePrefab);
                    applied = true;
                }
            }

            if (applied && (m_SurfaceScaleValues.ContainsKey(surfaceId) || m_SurfaceEdgeFadeValues.ContainsKey(surfaceId) || m_SurfaceEdgeNoiseValues.ContainsKey(surfaceId) || m_SurfaceEdgeNoiseScaleValues.ContainsKey(surfaceId) || m_SurfaceBaseColors.ContainsKey(surfaceId)))
            {
                m_RestoredSurfaceScaleIds.Add(surfaceId);
            }
            else
            {
                m_RestoredSurfaceScaleIds.Remove(surfaceId);
            }
            return applied;
        }

        private static float2 ExpandSurfaceEdgeFadeRange(float2 baseRange, float startPercent, float endPercent)
        {
            return new float2(
                startPercent <= 0
                    ? baseRange.x
                    : startPercent,
                startPercent <= 0
                    ? baseRange.y
                    : 0.001f);
        }


        private static void CopyMaterialSettingsToRenderedArea(Material material, RenderedArea renderedArea)
        {
            if (material.HasProperty(s_BaseColorProperty)) renderedArea.m_BaseColor = material.GetColor(s_BaseColorProperty);
            if (material.HasProperty(s_MetallicProperty)) renderedArea.m_Metallic = material.GetFloat(s_MetallicProperty);
            if (material.HasProperty(s_SmoothnessProperty)) renderedArea.m_Smoothness = material.GetFloat(s_SmoothnessProperty);
            if (material.HasProperty(s_NormalOpacityProperty)) renderedArea.m_NormalOpacity = material.GetFloat(s_NormalOpacityProperty);
            if (material.HasProperty(s_MetallicOpacityProperty)) renderedArea.m_MetallicOpacity = material.GetFloat(s_MetallicOpacityProperty);
            if (material.HasProperty(s_NormalAlphaSourceProperty)) renderedArea.m_NormalAlphaSource = material.GetFloat(s_NormalAlphaSourceProperty);
            if (material.HasProperty(s_MetallicAlphaSourceProperty)) renderedArea.m_MetallicAlphaSource = material.GetFloat(s_MetallicAlphaSourceProperty);
            if (material.HasProperty(s_EdgeNormalProperty)) renderedArea.m_EdgeNormal = material.GetFloat(s_EdgeNormalProperty);
            if (material.HasProperty(s_EdgeFadeRangeProperty))
            {
                Vector4 value = material.GetVector(s_EdgeFadeRangeProperty);
                renderedArea.m_EdgeFadeRange = new float2(value.x, value.y);
            }
            if (material.HasProperty(s_EdgeNormalRangeProperty))
            {
                Vector4 value = material.GetVector(s_EdgeNormalRangeProperty);
                renderedArea.m_EdgeNormalRange = new float2(value.x, value.y);
            }
            if (material.HasProperty(s_EdgeNoiseProperty))
            {
                Vector4 value = material.GetVector(s_EdgeNoiseProperty);
                renderedArea.m_EdgeNoise = new float2(value.x, value.y);
            }
        }

        private void SetAreaBatchUvScale(int batchIndex, float uvScale)
        {
            if (m_AreaBatchSystem == null || batchIndex < 0 || batchIndex >= m_AreaBatchSystem.GetBatchCount() ||
                s_AreaBatchUvScaleField == null)
            {
                return;
            }

            int uvScaleProperty = (int)s_AreaBatchUvScaleField.GetValue(m_AreaBatchSystem);
            if (uvScaleProperty == 0 ||
                !m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out Material material, out _, out _, out _) ||
                material == null || !material.HasProperty(uvScaleProperty))
            {
                return;
            }

            material.SetFloat(uvScaleProperty, uvScale);
        }

        private void SetAreaBatchEdgeFadeRange(int batchIndex, float2 edgeFadeRange)
        {
            if (m_AreaBatchSystem == null || batchIndex < 0 || batchIndex >= m_AreaBatchSystem.GetBatchCount() ||
                s_EdgeFadeRangeProperty == 0 ||
                !m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out Material material, out _, out _, out _) ||
                material == null || !material.HasProperty(s_EdgeFadeRangeProperty))
            {
                return;
            }

            material.SetVector(
                s_EdgeFadeRangeProperty,
                new Vector4(edgeFadeRange.x, edgeFadeRange.y, 0f, 0f));
        }

        private void SetAreaBatchEdgeNoise(int batchIndex, float2 edgeNoise)
        {
            if (m_AreaBatchSystem == null || batchIndex < 0 || batchIndex >= m_AreaBatchSystem.GetBatchCount() ||
                s_EdgeNoiseProperty == 0 ||
                !m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out Material material, out _, out _, out _) ||
                material == null || !material.HasProperty(s_EdgeNoiseProperty))
            {
                return;
            }

            material.SetVector(s_EdgeNoiseProperty, new Vector4(edgeNoise.x, edgeNoise.y, 0f, 0f));
        }

        private void SetAreaBatchBaseColor(int batchIndex, Color color)
        {
            if (m_AreaBatchSystem == null || batchIndex < 0 || batchIndex >= m_AreaBatchSystem.GetBatchCount() ||
                s_BaseColorProperty == 0 ||
                !m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out Material material, out _, out _, out _) ||
                material == null || !material.HasProperty(s_BaseColorProperty)) return;
            material.SetColor(s_BaseColorProperty, color);
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

        private void StartCustomTextureScan()
        {
            m_CustomTextureScanStarted = true;
            m_CustomTextureAssetDatabase = AssetDatabase.GetTransient();
            m_CustomTextureAssetDatabaseRegistrationTask = AssetDatabase.global.RegisterDatabase(m_CustomTextureAssetDatabase);
            string modsDataRoot = GetModsDataRoot();
            string ownTextureDirectory = Path.Combine(modsDataRoot, "Replacement Solutions");
            try
            {
                Directory.CreateDirectory(ownTextureDirectory);
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Could not create the Replacement Solutions texture folder");
            }

            m_CustomTextureScanTask = Task.Run(() =>
            {
                var result = new List<CustomTextureFile>();
                if (!Directory.Exists(modsDataRoot))
                {
                    return result;
                }

                foreach (string rootDirectory in Directory.GetDirectories(modsDataRoot))
                {
                    string rootName = Path.GetFileName(rootDirectory);
                    if (ShouldSkipCustomTextureDirectory(rootName))
                    {
                        continue;
                    }

                    string prefix = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    IEnumerable<string> files;
                    try
                    {
                        files = Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories).ToArray();
                    }
                    catch (Exception exception)
                    {
                        Mod.Log.Warn(exception, "Could not enumerate custom texture files under " + rootDirectory);
                        continue;
                    }

                    foreach (string filePath in files)
                    {
                        if (!IsSupportedCustomTextureFile(filePath))
                        {
                            continue;
                        }

                        FileInfo fileInfo = new FileInfo(filePath);
                        if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > 8 * 1024 * 1024)
                        {
                            continue;
                        }

                        string relativePath = filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            ? filePath.Substring(prefix.Length)
                            : Path.GetFileName(filePath);
                        if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .Any(ShouldSkipCustomTextureDirectory))
                        {
                            continue;
                        }
                        result.Add(new CustomTextureFile
                        {
                            FullPath = filePath,
                            RelativePath = relativePath,
                            RootFolder = rootName,
                            DisplayName = Path.GetFileNameWithoutExtension(filePath),
                        });
                    }
                }

                return result
                    .OrderBy(item => item.RootFolder, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.RelativePath, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            });
        }

        private static string GetModsDataRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Colossal Order", "Cities Skylines II", "ModsData");
        }

        private static bool IsSupportedCustomTextureExtension(string extension)
        {
            return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupportedCustomTextureFile(string path)
        {
            if (!IsSupportedCustomTextureExtension(Path.GetExtension(path)))
            {
                return false;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            string[] nonColorMapSuffixes =
            {
                "_normal", "_normalmap", "_mask", "_maskmap", "_metallic", "_roughness",
                "_ao", "_ambientocclusion", "_height", "_displacement", "_emission",
                "_emissive", "_opacity", "_specular", "_orm",
            };
            return !nonColorMapSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        private static bool ShouldSkipCustomTextureDirectory(string directoryName)
        {
            return string.IsNullOrWhiteSpace(directoryName) ||
                   directoryName.StartsWith(".", StringComparison.Ordinal) ||
                   directoryName.IndexOf("thumbnails", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   directoryName.Equals("Cache", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("Caches", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("Thumbnails", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("Thumbnail", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("Thumbs", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("Thumb", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("CustomThumbnails", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("Icons", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("Icon", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("Previews", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("Preview", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
                   directoryName.Equals("Tmp", StringComparison.OrdinalIgnoreCase);
        }

        private void RegisterCustomTextures(PrefabSystem prefabSystem, int maxPerFrame)
        {
            if (prefabSystem == null || m_CustomTextureFiles == null)
            {
                return;
            }

            int processed = 0;
            while (m_CustomTextureFileIndex < m_CustomTextureFiles.Count && processed < maxPerFrame)
            {
                CustomTextureFile file = m_CustomTextureFiles[m_CustomTextureFileIndex++];
                processed++;
                try
                {
                    byte[] bytes = File.ReadAllBytes(file.FullPath);
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                    {
                        name = "Replacement Solutions - " + file.DisplayName,
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Bilinear,
                        anisoLevel = 4,
                    };
                    if (!ImageConversion.LoadImage(texture, bytes, false) || texture.width < 128 || texture.height < 128 || texture.width > 8192 || texture.height > 8192)
                    {
                        UnityEngine.Object.Destroy(texture);
                        Mod.Log.WarnFormat("Custom surface texture skipped (unsupported or invalid image): '{0}'", file.FullPath);
                        continue;
                    }

                    string relativeKey = file.RootFolder + "/" + file.RelativePath.Replace('\\', '/');
                    string stableHash = GetStableCustomSurfaceHash(relativeKey);
                    Colossal.Hash128 textureGuid = default;
                    textureGuid.Calculate("Replacement Solutions/" + relativeKey.ToLowerInvariant());
                    TextureAsset textureAsset = m_CustomTextureAssetDatabase.AddAsset<TextureAsset, Texture>(
                        AssetDataPath.Create("Custom Surfaces", stableHash),
                        texture,
                        textureGuid);
                    SurfacePreview preview = CreateSurfacePreview(texture);
                    if (textureAsset == null || preview?.Thumbnail == null ||
                        !TryCreateCustomSurface(prefabSystem, file, stableHash, textureAsset, preview.Thumbnail, out Entity surfaceEntity))
                    {
                        UnityEngine.Object.Destroy(texture);
                        continue;
                    }

                    if (!prefabSystem.TryGetPrefab<SurfacePrefab>(surfaceEntity, out SurfacePrefab registeredPrefab) || registeredPrefab == null)
                    {
                        UnityEngine.Object.Destroy(texture);
                        continue;
                    }
                    string relativePath = file.RootFolder + "/" + file.RelativePath.Replace('\\', '/');
                    m_CustomSurfacesById[registeredPrefab.GetPrefabID().ToString()] = new CustomSurfaceInfo
                    {
                        RelativePath = relativePath,
                        DisplayName = file.RootFolder + " - " + file.DisplayName,
                        Group = file.RootFolder,
                        Thumbnail = preview.Thumbnail,
                        TooltipThumbnail = preview.TooltipThumbnail,
                    };
                    m_CustomTextureRegisteredCount++;
                }
                catch (Exception exception)
                {
                    Mod.Log.Error(exception, "Custom surface texture registration failed for " + file.FullPath);
                }
            }
        }

        private bool TryCreateCustomSurface(
            PrefabSystem prefabSystem,
            CustomTextureFile file,
            string stableHash,
            TextureAsset textureAsset,
            string thumbnail,
            out Entity surfaceEntity)
        {
            surfaceEntity = Entity.Null;
            SurfacePrefab template = null;
            using (NativeArray<Entity> surfaces = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in surfaces)
                {
                    if (!prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab candidate) || candidate == null ||
                        !candidate.TryGet<RenderedArea>(out RenderedArea rendered) || rendered == null)
                    {
                        continue;
                    }

                    if (string.Equals(candidate.name, ReplacementRuleCatalog.TransparentSurfaceTemplateName, StringComparison.OrdinalIgnoreCase))
                    {
                        template = candidate;
                        break;
                    }

                    if (template == null)
                    {
                        template = candidate;
                    }
                }
            }

            if (template == null)
            {
                Mod.Log.WarnFormat("Custom surface skipped because no RenderedArea template was available: '{0}'", file.FullPath);
                return false;
            }

            string safeModName = SanitizePrefabName(file.RootFolder);
            string safeName = SanitizePrefabName(file.DisplayName);
            string prefabName = safeModName + " - " + safeName;
            SurfacePrefab custom = (SurfacePrefab)template.Clone(prefabName);
            custom.Remove<UIObject>();
            UIAssetCategoryPrefab surfaceCategory = FindTilesSurfacesMenuCategory(prefabSystem);
            if (surfaceCategory != null)
            {
                UIObject uiObject = custom.AddComponent<UIObject>();
                uiObject.m_Group = surfaceCategory;
                uiObject.m_Priority = m_CustomSurfaceMenuPriority++;
                uiObject.m_Icon = thumbnail;
            }
            else
            {
                Mod.Log.WarnFormat("Custom surface could not be added to the Tiles / Surfaces menu because its category was not found: '{0}'", file.FullPath);
            }
            if (!custom.TryGet<RenderedArea>(out RenderedArea renderedArea) || renderedArea == null)
            {
                return false;
            }

            renderedArea.m_Version = RenderedArea.Version.OverrideMaterial;
            renderedArea.m_Material = null;
            renderedArea.m_BaseColor = Color.white;
            renderedArea.m_BaseColorMap = textureAsset;
            renderedArea.m_NormalMap = null;
            renderedArea.m_MaskMap = null;
            renderedArea.m_Metallic = 0f;
            renderedArea.m_Smoothness = 0f;

            if (!prefabSystem.AddPrefab(custom))
            {
                Mod.Log.WarnFormat("Custom surface registration failed: PrefabSystem.AddPrefab returned false for '{0}'", file.FullPath);
                return false;
            }

            surfaceEntity = prefabSystem.GetEntity(custom);
            prefabSystem.UpdatePrefab(custom);
            string localeId = GameManager.instance?.localizationManager?.fallbackLocaleId ?? "en-US";
            GameManager.instance?.localizationManager?.AddSource(localeId, new CustomSurfaceNameLocale(
                "Assets.NAME[" + custom.name + "]",
                file.RootFolder + " - " + file.DisplayName));
            if (surfaceCategory != null)
            {
                Mod.Log.InfoFormat(
                    "Custom surface added to game menu: surface='{0}', menu='{1}', category='{2}'",
                    custom.name,
                    surfaceCategory.m_Menu != null ? surfaceCategory.m_Menu.name : "<no menu>",
                    surfaceCategory.name);
            }
            return surfaceEntity != Entity.Null;
        }

        private sealed class CustomSurfaceNameLocale : Colossal.IDictionarySource
        {
            private readonly string m_Key;
            private readonly string m_Value;

            public CustomSurfaceNameLocale(string key, string value)
            {
                m_Key = key;
                m_Value = value;
            }

            public IEnumerable<KeyValuePair<string, string>> ReadEntries(
                IList<Colossal.IDictionaryEntryError> errors,
                Dictionary<string, int> indexCounts)
            {
                return new[] { new KeyValuePair<string, string>(m_Key, m_Value) };
            }

            public void Unload()
            {
            }
        }

        private UIAssetCategoryPrefab FindTilesSurfacesMenuCategory(PrefabSystem prefabSystem)
        {
            if (prefabSystem == null)
            {
                return null;
            }

            // Reuse the category assigned to the game's own Tiles Surface prefabs;
            // this remains correct if the game localizes or renames the menu labels.
            using (NativeArray<Entity> surfaces = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in surfaces)
                {
                    if (!prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab surface) || surface == null ||
                        surface.isBuiltin != true ||
                        !(surface.name.StartsWith("Tiles Surface", StringComparison.OrdinalIgnoreCase) ||
                          surface.name.StartsWith("Tile Surface", StringComparison.OrdinalIgnoreCase)) ||
                        !surface.TryGet<UIObject>(out UIObject uiObject) ||
                        !(uiObject.m_Group is UIAssetCategoryPrefab category) || category.m_Menu == null)
                    {
                        continue;
                    }

                    return category;
                }
            }

            // Fallback for game versions that rename the built-in surface assets.
            EntityManager entityManager = prefabSystem.World.EntityManager;
            using EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<UIAssetMenuData>(),
                ComponentType.ReadOnly<UIGroupElement>());
            using NativeArray<Entity> menus = query.ToEntityArray(Allocator.Temp);
            foreach (Entity menuEntity in menus)
            {
                if (!prefabSystem.TryGetPrefab<UIAssetMenuPrefab>(menuEntity, out UIAssetMenuPrefab menu) || menu == null)
                {
                    continue;
                }

                DynamicBuffer<UIGroupElement> categories = entityManager.GetBuffer<UIGroupElement>(menuEntity, true);
                foreach (UIGroupElement categoryElement in categories)
                {
                    if (!prefabSystem.TryGetPrefab<UIAssetCategoryPrefab>(categoryElement.m_Prefab, out UIAssetCategoryPrefab category) ||
                        category == null || category.m_Menu == null)
                    {
                        continue;
                    }

                    string menuName = menu.name ?? string.Empty;
                    string categoryName = category.name ?? string.Empty;
                    bool hasTiles = menuName.IndexOf("tile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        categoryName.IndexOf("tile", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool hasSurfaces = menuName.IndexOf("surface", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        categoryName.IndexOf("surface", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hasTiles && hasSurfaces)
                    {
                        return category;
                    }
                }
            }

            return null;
        }

        private static string SanitizePrefabName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Texture";
            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                builder.Append(char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '_');
            }
            return builder.ToString();
        }

        private static string GetStableCustomSurfaceHash(string relativeKey)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(relativeKey.ToLowerInvariant())))
                    .Replace("-", string.Empty).Substring(0, 16);
            }
        }

        private static SurfacePreview CreateSurfacePreview(Texture source)
        {
            string thumbnail = CaptureTexturePreview(source, 128);
            string tooltip = CaptureTexturePreview(source, 384);
            return new SurfacePreview { Thumbnail = thumbnail, TooltipThumbnail = tooltip ?? thumbnail };
        }

        private SurfacePreview GetSurfaceTexturePreview(SurfacePrefab prefab, Entity surfaceEntity)
        {
            RenderedArea renderedArea = null;
            if (prefab != null && prefab.TryGet<RenderedArea>(out renderedArea) && renderedArea != null)
            {
                if (renderedArea.m_BaseColorMap != null && renderedArea.m_BaseColorMap.guid.isValid)
                {
                    string assetGuid = renderedArea.m_BaseColorMap.guid.ToString();
                    if (m_SurfaceAssetPreviewCache.TryGetValue(assetGuid, out SurfacePreview cachedAssetPreview))
                    {
                        return cachedAssetPreview;
                    }

                    TextureAsset textureAsset = AssetDatabase.global.GetAsset<TextureAsset>(renderedArea.m_BaseColorMap.guid);
                    if (textureAsset != null)
                    {
                        bool loadedTextureAsset = false;
                        try
                        {
                            Texture baseColorTexture = textureAsset.Load<Texture2D>(0);
                            loadedTextureAsset = true;
                            SurfacePreview assetPreview = CreateSurfacePreview(baseColorTexture);
                            if (assetPreview.Thumbnail != null)
                            {
                                m_SurfaceAssetPreviewCache[assetGuid] = assetPreview;
                                return assetPreview;
                            }
                        }
                        catch (Exception exception)
                        {
                            Mod.Log.Warn(exception, "Could not load the prefab texture for a surface preview: " + prefab.name);
                        }
                        finally
                        {
                            if (loadedTextureAsset)
                            {
                                textureAsset.Unload();
                            }
                        }
                    }
                }

                SurfacePreview materialPreview = GetMaterialTexturePreview(renderedArea.m_Material);
                if (materialPreview != null)
                {
                    return materialPreview;
                }
            }

            // Some older/modded surfaces only expose their texture through a runtime area batch.
            if (prefab == null || m_AreaBatchSystem == null)
            {
                return null;
            }

            if (surfaceEntity == Entity.Null || !EntityManager.HasComponent<RenderedAreaData>(surfaceEntity))
            {
                return null;
            }
            int batchIndex = EntityManager.GetComponentData<RenderedAreaData>(surfaceEntity).m_BatchIndex;
            if (batchIndex < 0 || batchIndex >= m_AreaBatchSystem.GetBatchCount() ||
                !m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out Material material, out _, out _, out _) || material == null)
            {
                return null;
            }

            return GetMaterialTexturePreview(material);
        }

        private SurfacePreview GetMaterialTexturePreview(Material material)
        {
            int property = Shader.PropertyToID("_BaseColorMap");
            Texture texture = material != null && material.HasProperty(property) ? material.GetTexture(property) : null;
            if (texture == null) return null;
            int textureId = texture.GetInstanceID();
            if (m_SurfacePreviewCache.TryGetValue(textureId, out SurfacePreview cached))
            {
                return cached;
            }

            SurfacePreview preview = CreateSurfacePreview(texture);
            if (preview.Thumbnail != null)
            {
                m_SurfacePreviewCache[textureId] = preview;
                return preview;
            }
            return null;
        }

        private void UpdateSurfaceCatalogPreviews(int maxPerFrame)
        {
            if (m_SurfaceCatalogPublished || m_SurfaceCatalogEntries == null || m_SurfaceCatalogBuildTask != null)
            {
                return;
            }

            int processed = 0;
            while (m_SurfacePreviewQueueIndex < m_SurfacePreviewQueue.Count && processed < maxPerFrame)
            {
                SurfacePreviewRequest request = m_SurfacePreviewQueue[m_SurfacePreviewQueueIndex++];
                processed++;
                SurfacePreview preview = GetSurfaceTexturePreview(request.Prefab, request.SurfaceEntity);
                if (preview == null)
                {
                    continue;
                }

                request.CatalogEntry["thumbnail"] = preview.Thumbnail;
                request.CatalogEntry["tooltipThumbnail"] = preview.TooltipThumbnail;
            }

            if (m_SurfacePreviewQueueIndex >= m_SurfacePreviewQueue.Count)
            {
                m_SurfaceCatalog.Update(JSON.Dump(m_SurfaceCatalogEntries));
                m_SurfaceCatalogPublished = true;
                Mod.Log.InfoFormat(
                    "Surface catalog published after texture previews: surfaces={0}, modSurfaces={1}",
                    m_SurfaceCatalogSurfaceCount,
                    m_SurfaceCatalogModSurfaceCount);
            }
        }

        private static string CaptureTexturePreview(Texture source, int size)
        {
            if (source == null) return null;
            RenderTexture renderTarget = null;
            Texture2D readback = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                renderTarget = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(source, renderTarget);
                RenderTexture.active = renderTarget;
                readback = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
                readback.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                readback.Apply(false, false);
                byte[] png = readback.EncodeToPNG();
                return "data:image/png;base64," + Convert.ToBase64String(png);
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not generate a top-down surface texture preview");
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (readback != null) UnityEngine.Object.Destroy(readback);
                if (renderTarget != null) RenderTexture.ReleaseTemporary(renderTarget);
            }
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
            Dictionary<Entity, string> surfaceMenuPaths = GetSurfaceMenuPaths(prefabSystem);
            int modSurfaceCount = 0;
            int surfacesWithoutMenuSkipped = 0;
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
                    bool isCustomSurface = m_CustomSurfacesById.TryGetValue(id, out CustomSurfaceInfo customSurface);
                    string name = isCustomSurface ? customSurface.DisplayName : prefab.name ?? id;
                    bool isTransparentSurface = ReplacementRuleCatalog.IsTransparentSurface(name);
                    if (IsExcludedSurfaceName(name) && !isTransparentSurface && !isCustomSurface)
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
                    bool isMod = !isCustomSurface && !prefab.isBuiltin && IsOwnedByMod(prefab);
                    string assetIcon = isTransparentSurface || isCustomSurface ? null : GetAssetIcon(prefab);
                    string menuPath = isTransparentSurface
                        ? "In-game / Transparent Surface"
                        : isCustomSurface
                            ? customSurface.RelativePath
                        : surfaceMenuPaths.TryGetValue(entity, out string registeredMenuPath)
                            ? registeredMenuPath
                            : GetSurfaceMenuPath(prefab);
                    if (!isTransparentSurface && !isCustomSurface && string.Equals(menuPath, "In-game asset", StringComparison.Ordinal))
                    {
                        surfacesWithoutMenuSkipped++;
                        continue;
                    }

                    string group = isTransparentSurface ? "In-game" : isCustomSurface ? customSurface.Group : GetSurfaceGroup(prefab, menuPath);
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

                    var catalogEntry = new Dictionary<string, string>
                    {
                        ["id"] = id,
                        ["name"] = name,
                        ["thumbnail"] = isTransparentSurface
                            ? "coui://replacementsolutions/images/TransparentSurface.svg"
                            : isCustomSurface
                                ? customSurface.Thumbnail
                                : GetSurfaceThumbnail(prefab, assetIcon),
                        ["tooltipThumbnail"] = isTransparentSurface
                            ? "coui://replacementsolutions/images/TransparentSurface.svg"
                            : isCustomSurface
                                ? customSurface.TooltipThumbnail
                                : GetLargeThumbnail(prefab, assetIcon),
                        ["group"] = group,
                        ["menuPath"] = menuPath,
                        ["isMod"] = isMod.ToString(),
                        ["customTexture"] = isCustomSurface.ToString(),
                    };
                    catalog.Add(catalogEntry);
                    if (!isTransparentSurface && !isCustomSurface)
                    {
                        m_SurfacePreviewQueue.Add(new SurfacePreviewRequest
                        {
                            Prefab = prefab,
                            SurfaceEntity = entity,
                            CatalogEntry = catalogEntry,
                        });
                    }
                }
            }

            Mod.Log.InfoFormat(
                "Surface catalog snapshot captured after map load: surfaces={0}, modSurfaces={1}, menuRegisteredSurfaces={2}, surfacesWithoutMenuSkipped={3}, queriedPrefabs={4}, treeSuppressionFlagsAdded={5}, anarchyEnabled={6}",
                catalog.Count,
                modSurfaceCount,
                surfaceMenuPaths.Count,
                surfacesWithoutMenuSkipped,
                m_SurfaceQuery.CalculateEntityCount(),
                surfacesWithTreeSuppression,
                !shouldSuppressTrees);
            m_SurfaceCatalogSurfaceCount = catalog.Count;
            m_SurfaceCatalogModSurfaceCount = modSurfaceCount;
            m_SurfaceCatalogEntries = catalog;
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
                   string.Equals(name, "Clip Surface", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Surface Area", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Missing Area", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSurfaceGroup(SurfacePrefab prefab, string menuPath)
        {
            string name = prefab.name ?? string.Empty;
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
            if (prefab != null && prefab.TryGet<UIObject>(out UIObject uiObject) && uiObject?.m_Group != null)
            {
                if (uiObject.m_Group is UIAssetCategoryPrefab category && category.m_Menu != null)
                {
                    return category.m_Menu.name + " / " + category.name;
                }

                return uiObject.m_Group.name;
            }

            return "In-game asset";
        }

        private Dictionary<Entity, string> GetSurfaceMenuPaths(PrefabSystem prefabSystem)
        {
            Dictionary<Entity, string> menuPaths = new Dictionary<Entity, string>();
            EntityManager entityManager = prefabSystem.World.EntityManager;
            using (EntityQuery query = entityManager.CreateEntityQuery(
                       ComponentType.ReadOnly<PrefabData>(),
                       ComponentType.ReadOnly<UIAssetMenuData>(),
                       ComponentType.ReadOnly<UIGroupElement>()))
            using (NativeArray<Entity> menus = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity menuEntity in menus)
                {
                    if (!prefabSystem.TryGetPrefab<UIAssetMenuPrefab>(menuEntity, out UIAssetMenuPrefab menu) || menu == null)
                    {
                        continue;
                    }

                    DynamicBuffer<UIGroupElement> categories = entityManager.GetBuffer<UIGroupElement>(menuEntity, true);
                    foreach (UIGroupElement categoryEntry in categories)
                    {
                        if (!prefabSystem.TryGetPrefab<UIAssetCategoryPrefab>(categoryEntry.m_Prefab, out UIAssetCategoryPrefab category) ||
                            category == null)
                        {
                            continue;
                        }

                        DynamicBuffer<UIGroupElement> menuEntries = entityManager.GetBuffer<UIGroupElement>(categoryEntry.m_Prefab, true);
                        string path = menu.name + " / " + category.name;
                        foreach (UIGroupElement menuEntry in menuEntries)
                        {
                            if (prefabSystem.TryGetPrefab<SurfacePrefab>(menuEntry.m_Prefab, out SurfacePrefab surface) && surface != null)
                            {
                                menuPaths[menuEntry.m_Prefab] = path;
                            }
                        }
                    }
                }
            }

            // Some built-in surfaces have a registered UIObjectData group even
            // when a category buffer does not list them directly. Resolve that
            // group through the same category-to-menu relationship used by the
            // asset browser.
            using (NativeArray<Entity> surfaces = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity surfaceEntity in surfaces)
                {
                    if (!entityManager.HasComponent<UIObjectData>(surfaceEntity))
                    {
                        continue;
                    }

                    Entity groupEntity = entityManager.GetComponentData<UIObjectData>(surfaceEntity).m_Group;
                    if (!prefabSystem.TryGetPrefab<UIAssetCategoryPrefab>(groupEntity, out UIAssetCategoryPrefab category) ||
                        category == null || category.m_Menu == null)
                    {
                        continue;
                    }

                    menuPaths[surfaceEntity] = category.m_Menu.name + " / " + category.name;
                }
            }

            return menuPaths;
        }

        private static string GetLargeThumbnail(SurfacePrefab prefab, string icon)
        {
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

        private static string GetSurfaceThumbnail(SurfacePrefab prefab, string icon)
        {
            return icon ?? ImageSystem.GetThumbnail(prefab) ?? "Media/Placeholder.svg";
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
