using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
        private ValueBinding<bool> m_AlsoNewSurfaceBuildings;
        private ValueBinding<bool> m_AlsoNewIndustryAreas;
        private ValueBinding<bool> m_AlsoNewRoads;
        private ValueBinding<string> m_RoadRulesJson;
        private ValueBinding<string> m_RoadCatalog;
        private ValueBinding<bool> m_RoadPickerOpen;
        private ValueBinding<string> m_RoadPickerContext;
        private ValueBinding<string> m_RoadPickerCollapsedGroups;
        private ValueBinding<int> m_RoadPickerX;
        private ValueBinding<int> m_RoadPickerY;
        private ValueBinding<int> m_RoadPickerWidth;
        private ValueBinding<int> m_RoadPickerHeight;
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
        private EntityQuery m_RoadPrefabQuery;
        private string m_LastRoadCatalog;
        private bool m_RoadCatalogBuildCompleted;
        private bool m_RoadStartupTexturePreGenerationScheduled;
        private readonly Dictionary<string, TextureAsset> m_RoadDefiningTextures = new Dictionary<string, TextureAsset>(StringComparer.Ordinal);
        private readonly Dictionary<int, RoadTextureGenerationState> m_RoadTextureGenerationStates = new Dictionary<int, RoadTextureGenerationState>();
        private readonly HashSet<int> m_PendingRoadReplacementRules = new HashSet<int>();
        private readonly object m_RoadTextureGenerationLock = new object();
        private EntityQuery m_SurfaceScaleInstancesQuery;
        private int m_LastDialogFontSize = -1;
        private bool? m_LastShowInGameSurfacesAsTargets;
        private bool? m_LastShowSpecialIndustrySurfacesAsTargets;
        private bool? m_LastShowModSurfacesAsSources;
        private bool? m_LastShowRevertAllButton;
        private bool? m_LastShowSurfaceBaseColorSettings;
        private bool m_SurfaceCatalogBuildStarted;
        private int m_LastCatalogSurfacePrefabCount = -1;
        private int m_CatalogSurfacePrefabStableFrames;
        private Entity m_TransparentSurfaceEntity;
        private bool m_TransparentSurfaceInitializationAttempted;
        private AreaBatchSystem m_AreaBatchSystem;
        private Task<string> m_SurfaceCatalogBuildTask;
        private Task<string> m_AreaCatalogBuildTask;
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
            AddBinding(m_AlsoNewSurfaceBuildings = new ValueBinding<bool>(
                "replacementsolutions",
                "alsoNewSurfaceBuildings",
                settings?.AlsoNewSurfaceBuildings ?? false));
            AddBinding(m_AlsoNewIndustryAreas = new ValueBinding<bool>(
                "replacementsolutions",
                "alsoNewIndustryAreas",
                settings?.AlsoNewIndustryAreas ?? false));
            AddBinding(m_AlsoNewRoads = new ValueBinding<bool>("replacementsolutions", "alsoNewRoads", settings?.AlsoNewRoads ?? false));
            AddBinding(m_RoadRulesJson = new ValueBinding<string>("replacementsolutions", "roadRulesJson", settings?.RoadRulesJson ?? "[]"));
            AddBinding(m_RoadCatalog = new ValueBinding<string>("replacementsolutions", "roadCatalog", "[]"));
            AddBinding(m_RoadPickerOpen = new ValueBinding<bool>("replacementsolutions", "roadPickerOpen", settings?.RoadPickerOpen ?? false));
            AddBinding(m_RoadPickerContext = new ValueBinding<string>("replacementsolutions", "roadPickerContext", settings?.RoadPickerContextJson ?? "{}"));
            AddBinding(m_RoadPickerCollapsedGroups = new ValueBinding<string>("replacementsolutions", "roadPickerCollapsedGroups", settings?.RoadPickerCollapsedGroupsJson ?? "{}"));
            AddBinding(m_RoadPickerX = new ValueBinding<int>("replacementsolutions", "roadPickerX", settings?.RoadPickerX ?? -1));
            AddBinding(m_RoadPickerY = new ValueBinding<int>("replacementsolutions", "roadPickerY", settings?.RoadPickerY ?? -1));
            AddBinding(m_RoadPickerWidth = new ValueBinding<int>("replacementsolutions", "roadPickerWidth", settings?.RoadPickerWidth ?? -1));
            AddBinding(m_RoadPickerHeight = new ValueBinding<int>("replacementsolutions", "roadPickerHeight", settings?.RoadPickerHeight ?? -1));
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
            m_RoadPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetGeometryData>());
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
            AddBinding(new TriggerBinding<bool>("replacementsolutions", "setAlsoNewSurfaceBuildings", SetAlsoNewSurfaceBuildings));
            AddBinding(new TriggerBinding<bool>("replacementsolutions", "setAlsoNewIndustryAreas", SetAlsoNewIndustryAreas));
            AddBinding(new TriggerBinding("replacementsolutions", "revertAllSurfaces", RevertAllSurfaces));
            AddBinding(new TriggerBinding("replacementsolutions", "revertAllAreas", () => SurfaceReplacementSystem.QueueAreaRevertAll()));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "saveRoadRules", SetRoadRulesJson));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "openRoadPicker", OpenRoadPicker));
            AddBinding(new TriggerBinding("replacementsolutions", "closeRoadPicker", CloseRoadPicker));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setRoadPickerPosition", SetRoadPickerPosition));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setRoadPickerSize", SetRoadPickerSize));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "saveRoadPickerCollapsedGroups", SetRoadPickerCollapsedGroups));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "replaceRoads", ReplaceRoads));
            AddBinding(new TriggerBinding("replacementsolutions", "revertAllRoads", RevertAllRoads));
            AddBinding(new TriggerBinding<bool>("replacementsolutions", "setAlsoNewRoads", SetAlsoNewRoads));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setSurfacePickerPosition", SetSurfacePickerPosition));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "setSurfacePickerSize", SetSurfacePickerSize));
            AddBinding(new TriggerBinding<string>("replacementsolutions", "saveSurfacePickerCollapsedGroups", SetSurfacePickerCollapsedGroups));
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            ProcessRoadTextureGenerationTasks();

            // The catalog can be built at the main menu once surface prefabs and custom
            // textures are ready; only operations that touch live area batches wait for
            // the map to finish loading.
            GameManager gameManager = GameManager.instance;
            bool mapReady = gameManager != null &&
                !gameManager.isGameLoading &&
                gameManager.shouldUpdateWorld &&
                (string.Equals(gameManager.gameMode.ToString(), "Game", StringComparison.Ordinal) ||
                 string.Equals(gameManager.gameMode.ToString(), "Editor", StringComparison.Ordinal));
            PrefabSystem prefabSystem = World.GetExistingSystemManaged<PrefabSystem>();
            if (!m_RoadCatalogBuildCompleted && prefabSystem != null && !m_RoadPrefabQuery.IsEmptyIgnoreFilter)
            {
                string roadCatalog = BuildRoadCatalog(prefabSystem);
                if (!string.Equals(roadCatalog, m_LastRoadCatalog, StringComparison.Ordinal))
                {
                    m_LastRoadCatalog = roadCatalog;
                    m_RoadCatalog.Update(roadCatalog);
                }
            }
            bool prefabsReady = prefabSystem != null && !m_SurfaceQuery.IsEmptyIgnoreFilter;
            int catalogSurfacePrefabCount = prefabsReady ? m_SurfaceQuery.CalculateEntityCount() : 0;
            if (catalogSurfacePrefabCount != m_LastCatalogSurfacePrefabCount)
            {
                m_LastCatalogSurfacePrefabCount = catalogSurfacePrefabCount;
                m_CatalogSurfacePrefabStableFrames = 0;
            }
            else if (prefabsReady)
            {
                m_CatalogSurfacePrefabStableFrames++;
            }
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

                bool gameStarted = gameManager != null && !gameManager.isGameLoading;
                bool catalogReady = mapReady || m_CatalogSurfacePrefabStableFrames >= 120;
                if ((m_CustomTextureAssetDatabaseReady || m_CustomTextureAssetDatabaseFailed) &&
                    gameStarted && catalogReady && m_CustomTextureFileIndex >= m_CustomTextureFiles.Count)
                {
                    StartSurfaceCatalogBuild();
                }
            }

            TryScheduleRoadTexturePreGeneration();

            if (m_SurfaceCatalogBuildTask != null && m_SurfaceCatalogBuildTask.IsCompleted)
            {
                if (m_SurfaceCatalogBuildTask.Status == TaskStatus.RanToCompletion)
                {
                    if (m_SurfaceCatalogEntries != null)
                    {
                        m_SurfaceCatalogEntries.Sort((first, second) =>
                            string.Compare(first["name"], second["name"], StringComparison.CurrentCultureIgnoreCase));
                    }
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
                    Mod.Log.InfoFormat("Special industry menu catalog cached: entries={0}", m_AreaCatalogIndustryCount);
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

            bool anyDialogOpen = Mod.Settings.DialogOpen || Mod.Settings.SurfacePickerOpen || Mod.Settings.RoadPickerOpen || m_SurfaceScaleEditorIsOpen;
            if (anyDialogOpen)
            {
                if (m_SurfaceScaleEditorIsOpen)
                {
                    CancelSurfaceScale();
                }

                if (Mod.Settings.RoadPickerOpen)
                {
                    CloseRoadPicker();
                }
                else
                {
                    m_RestorePickerOnNextToolbarOpen = Mod.Settings.SurfacePickerOpen;
                    CloseSurfacePicker();
                }
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
                Mod.Settings.RoadPickerOpen = false;
                Mod.Settings.ApplyAndSave();
            }
            m_RoadPickerOpen.Update(false);
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

        private void SetRoadRulesJson(string value)
        {
            if (Mod.Settings == null) return;
            string json = string.IsNullOrWhiteSpace(value) ? "[]" : value;
            string previousJson = Mod.Settings.RoadRulesJson ?? "[]";
            Mod.Settings.RoadRulesJson = json;
            Mod.Settings.ApplyAndSave();
            m_RoadRulesJson.Update(json);
            GenerateChangedRoadAreaTextures(previousJson, json);
        }

        private void OpenRoadPicker(string contextJson)
        {
            if (Mod.Settings != null)
            {
                Mod.Settings.RoadPickerContextJson = contextJson ?? "{}";
                Mod.Settings.RoadPickerOpen = true;
                Mod.Settings.ApplyAndSave();
            }
            m_RoadPickerContext.Update(contextJson ?? "{}");
            m_RoadPickerOpen.Update(true);
        }

        private void CloseRoadPicker()
        {
            if (Mod.Settings != null)
            {
                Mod.Settings.RoadPickerOpen = false;
                Mod.Settings.ApplyAndSave();
            }
            m_RoadPickerOpen.Update(false);
        }

        private void SetRoadPickerPosition(string value)
        {
            string[] parts = value?.Split(',');
            if (Mod.Settings == null || parts == null || parts.Length != 2 ||
                !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)) return;
            Mod.Settings.RoadPickerX = x;
            Mod.Settings.RoadPickerY = y;
            Mod.Settings.ApplyAndSave();
            m_RoadPickerX.Update(x);
            m_RoadPickerY.Update(y);
        }

        private void SetRoadPickerSize(string value)
        {
            string[] parts = value?.Split(',');
            if (Mod.Settings == null || parts == null || parts.Length != 2 ||
                !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height)) return;
            width = Math.Max(520, Math.Min(width, 1800));
            height = Math.Max(360, Math.Min(height, 1300));
            Mod.Settings.RoadPickerWidth = width;
            Mod.Settings.RoadPickerHeight = height;
            Mod.Settings.ApplyAndSave();
            m_RoadPickerWidth.Update(width);
            m_RoadPickerHeight.Update(height);
        }

        private void SetRoadPickerCollapsedGroups(string value)
        {
            if (Mod.Settings == null) return;
            Mod.Settings.RoadPickerCollapsedGroupsJson = string.IsNullOrWhiteSpace(value) ? "{}" : value;
            Mod.Settings.ApplyAndSave();
            m_RoadPickerCollapsedGroups.Update(Mod.Settings.RoadPickerCollapsedGroupsJson);
        }

        private void ReplaceRoads(string requestedRuleId)
        {
            if (!int.TryParse(requestedRuleId, out int ruleId))
            {
                Mod.Log.WarnFormat("Road replacement request has an invalid rule id: '{0}'", requestedRuleId);
                return;
            }

            if (m_RoadTextureGenerationStates.TryGetValue(ruleId, out RoadTextureGenerationState generation) &&
                (generation.Task == null || !generation.Task.IsCompleted))
            {
                m_PendingRoadReplacementRules.Add(ruleId);
                Mod.Log.InfoFormat("Road replacement queued until texture preparation completes: ruleId='{0}'", ruleId);
                return;
            }
            if (generation != null && generation.Task != null && generation.Task.Status != TaskStatus.RanToCompletion)
            {
                Mod.Log.WarnFormat("Road replacement was not started because texture preparation failed or was cancelled: ruleId='{0}'", ruleId);
                return;
            }

            RoadReplacementSystem.QueueReplacement(ruleId.ToString(), Mod.Settings?.RoadRulesJson ?? "[]");
        }

        private void RevertAllRoads()
        {
            RoadReplacementSystem.QueueRevertAll();
        }

        private void SetAlsoNewRoads(bool value)
        {
            if (Mod.Settings == null) return;
            Mod.Settings.AlsoNewRoads = value;
            Mod.Settings.ApplyAndSave();
            m_AlsoNewRoads.Update(value);
        }

        private string BuildRoadCatalog(PrefabSystem prefabSystem)
        {
            using (NativeArray<Entity> entities = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    if (!prefabSystem.TryGetPrefab<NetGeometryPrefab>(entity, out NetGeometryPrefab prefab) || prefab == null ||
                        !string.Equals(prefab.name, "Small Road", StringComparison.OrdinalIgnoreCase)) continue;
                    string icon = ImageSystem.GetIcon(prefab);
                    if (string.IsNullOrWhiteSpace(icon)) icon = ImageSystem.GetThumbnail(prefab);
                    m_RoadDefiningTextures.Clear();
                    if (!TryFindRoadDefiningTextures(prefab, new HashSet<NetSectionPrefab>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                        return "[]";
                    TextureAsset baseColor = m_RoadDefiningTextures.TryGetValue("_BaseColorMap", out TextureAsset foundBaseColor) ? foundBaseColor : null;
                    var areaEntries = new List<Dictionary<string, string>>();
                    Texture2D sourceTexture = null;
                    bool loadedSource = false;
                    try
                    {
                        if (baseColor != null)
                        {
                            sourceTexture = baseColor.Load<Texture2D>(0, TextureAsset.KeepOnCPU.Object);
                            loadedSource = true;
                        }
                        foreach (RoadAtlasRegion region in RoadAtlasRegions.RoadEu)
                        {
                            string thumbnail = sourceTexture == null ? "Media/Placeholder.svg" : CaptureRoadRegionPreview(sourceTexture, region, 128);
                            string tooltipThumbnail = sourceTexture == null ? "Media/Placeholder.svg" : CaptureRoadRegionPreview(sourceTexture, region, 384, cropToMaximumSquare: true);
                            areaEntries.Add(new Dictionary<string, string>
                            {
                                ["id"] = region.Id,
                                ["name"] = region.Name,
                                ["x"] = region.X.ToString(),
                                ["y"] = region.Y.ToString(),
                                ["width"] = region.Width.ToString(),
                                ["height"] = (sourceTexture == null ? region.Height : region.GetHeight(sourceTexture.height)).ToString(),
                                ["scaleMode"] = region.ScaleMode.ToString(),
                                ["thumbnail"] = thumbnail ?? "Media/Placeholder.svg",
                                ["tooltipThumbnail"] = tooltipThumbnail ?? thumbnail ?? "Media/Placeholder.svg",
                            });
                        }
                    }
                    catch (Exception exception)
                    {
                        Mod.Log.Warn(exception, "Could not create previews for the RoadEU atlas regions");
                    }
                    finally
                    {
                        if (loadedSource) baseColor.Unload();
                    }
                    if (areaEntries.Count == 0)
                        foreach (RoadAtlasRegion region in RoadAtlasRegions.RoadEu)
                            areaEntries.Add(new Dictionary<string, string>
                            {
                                ["id"] = region.Id,
                                ["name"] = region.Name,
                                ["x"] = region.X.ToString(),
                                ["y"] = region.Y.ToString(),
                                ["width"] = region.Width.ToString(),
                                ["height"] = region.Height.ToString(),
                                ["scaleMode"] = region.ScaleMode.ToString(),
                                ["thumbnail"] = "Media/Placeholder.svg",
                                ["tooltipThumbnail"] = "Media/Placeholder.svg",
                            });

                    var entry = new Dictionary<string, string>
                    {
                        ["id"] = RoadAtlasRegions.RoadEuCatalogId,
                        ["name"] = prefab.name,
                        ["thumbnail"] = string.IsNullOrWhiteSpace(icon) ? "Media/Placeholder.svg" : icon,
                        ["group"] = "Roads",
                        ["areasJson"] = JSON.Dump(areaEntries),
                    };
                    m_RoadCatalogBuildCompleted = true;
                    return JSON.Dump(new[] { entry });
                }
            }
            return "[]";
        }

        private bool TryFindRoadDefiningTextures(NetGeometryPrefab geometry, HashSet<NetSectionPrefab> visitedSections, HashSet<string> visitedPieces)
        {
            if (geometry?.m_Sections == null) return false;
            foreach (NetSectionInfo sectionInfo in geometry.m_Sections)
                if (sectionInfo?.m_Section != null)
                    TryFindRoadDefiningTextures(sectionInfo.m_Section, visitedSections, visitedPieces);
            return m_RoadDefiningTextures.ContainsKey("_BaseColorMap");
        }

        private bool TryFindRoadDefiningTextures(NetSectionPrefab section, HashSet<NetSectionPrefab> visitedSections, HashSet<string> visitedPieces)
        {
            if (section == null || !visitedSections.Add(section)) return false;
            if (section.m_SubSections != null)
                foreach (NetSubSectionInfo subSection in section.m_SubSections)
                    if (subSection?.m_Section != null)
                        TryFindRoadDefiningTextures(subSection.m_Section, visitedSections, visitedPieces);
            if (section.m_Pieces != null)
            foreach (NetPieceInfo pieceInfo in section.m_Pieces)
            {
                NetPiecePrefab piece = pieceInfo?.m_Piece;
                if (piece == null || !visitedPieces.Add(piece.GetPrefabID().ToString()) || piece.surfaceAssets == null) continue;
                foreach (SurfaceAsset surface in piece.surfaceAssets)
                {
                    if (surface == null) continue;
                    bool loaded = false;
                    try
                    {
                        surface.LoadProperties(false);
                        loaded = true;
                        foreach (KeyValuePair<string, TextureAsset> texture in surface.textures)
                        {
                            if (texture.Value == null) continue;
                            RoadAtlasTextureLayer layer = RoadAtlasRegions.RoadEuTextureLayers.FirstOrDefault(candidate =>
                                string.Equals(candidate.MaterialProperty, texture.Key, StringComparison.Ordinal) &&
                                string.Equals(candidate.TextureAssetName, texture.Value.name, StringComparison.Ordinal));
                            if (layer == null) continue;
                            m_RoadDefiningTextures[texture.Key] = texture.Value;
                        }
                    }
                    finally
                    {
                        if (loaded) surface.Unload();
                    }
                }
            }
            return m_RoadDefiningTextures.ContainsKey("_BaseColorMap");
        }

        private static string CaptureRoadRegionPreview(Texture source, RoadAtlasRegion region, int size, bool cropToMaximumSquare = false)
        {
            int height = region.GetHeight(source.height);
            int maximumCropSide = Math.Min(region.Width, height);
            int cropSide = cropToMaximumSquare ? maximumCropSide : Math.Min(size, maximumCropSide);
            if (cropSide <= 0) return null;
            RenderTexture target = null;
            Texture2D readback = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Vector2 scale = new Vector2((float)cropSide / source.width, (float)cropSide / source.height);
                Vector2 offset = new Vector2((float)region.X / source.width, 1f - (float)(region.Y + cropSide) / source.height);
                Graphics.Blit(source, target, scale, offset);
                RenderTexture.active = target;
                readback = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
                readback.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                readback.Apply(false, false);
                return "data:image/png;base64," + Convert.ToBase64String(readback.EncodeToPNG());
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not crop a road atlas region preview: " + region.Name);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (readback != null) UnityEngine.Object.Destroy(readback);
                if (target != null) RenderTexture.ReleaseTemporary(target);
            }
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
            if (Mod.Settings == null || (value != "surfaces" && value != "areas" && value != "roads"))
            {
                return;
            }

            Mod.Settings.ActiveMainTab = value;
            Mod.Settings.ApplyAndSave();
            m_ActiveMainTab.Update(value);
        }

        private void SetAlsoNewSurfaceBuildings(bool value)
        {
            if (Mod.Settings == null)
            {
                return;
            }

            Mod.Settings.AlsoNewSurfaceBuildings = value;
            Mod.Settings.ApplyAndSave();
            m_AlsoNewSurfaceBuildings.Update(value);
        }

        private void SetAlsoNewIndustryAreas(bool value)
        {
            if (Mod.Settings == null)
            {
                return;
            }

            Mod.Settings.AlsoNewIndustryAreas = value;
            Mod.Settings.ApplyAndSave();
            m_AlsoNewIndustryAreas.Update(value);
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


    }
}
