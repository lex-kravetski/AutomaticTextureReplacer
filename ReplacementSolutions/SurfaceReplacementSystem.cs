using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Colossal.Json;
using Colossal.Serialization.Entities;
using Game;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using PrefabSubArea = Game.Prefabs.SubArea;
using PrefabSubAreaNode = Game.Prefabs.SubAreaNode;

namespace AutomaticTextureReplacer
{
    public sealed partial class SurfaceReplacementSystem : GameSystemBase
    {
        private sealed class ReplacementRequest
        {
            public string RuleId;
            public string RulesJson;
            public bool RevertAll;
            public bool AreaRules;
        }

        private sealed class PendingRevertChange
        {
            public Entity Area;
            public Entity DefaultPrefab;
        }

        private sealed class TemporaryPreviewReplacementTrace
        {
            public Entity InitialOwner;
            public Entity OriginalPrefab;
            public Entity TargetPrefab;
            public int OriginalBatchIndex;
            public int ReplacedAtFrame;
            public bool CleanupFlagsLogged;
            public bool LongLivedWarningLogged;
        }

        private sealed class PendingBatchDiagnostic
        {
            public Entity Area;
            public Entity ExpectedPrefab;
            public int ExpectedBatchIndex;
            public int SourceBatchIndex;
            public int PreviousBatchIndex;
            public string SourceName;
            public string TargetName;
            public int FramesChecked;
            public bool RebuildAttempted;
        }

        private sealed class PendingConstructionBatchRefresh
        {
            public Entity SourcePrefab;
            public Entity RuleSourcePrefab;
            public Entity TargetPrefab;
            public Entity ConstructionOwner;
            public OwnerDefinition ConstructionOwnerDefinition;
            public bool HasConstructionOwnerDefinition;
            public bool MatchIndustryType;
            public int FramesChecked;
            public bool SurfaceAreaObserved;
            public bool SpecialIndustryFieldObserved;
        }

        private sealed class SpecialIndustryPrefabSnapshot
        {
            public PrefabBase Prefab;
            public RenderedArea OriginalRenderedArea;
            public MasterArea MasterArea;
            public SlaveAreaInfo[] OriginalSlaveAreas;
            public Entity CurrentTargetSurface;
        }

        private readonly struct IndustryOverlayReplacementContext
        {
            public readonly Entity IndustryRoot;
            public readonly Entity IndustryPrefab;
            public readonly Entity TargetSurfacePrefab;
            public readonly EntityCommandBuffer CommandBuffer;
            public readonly long OperationId;

            public IndustryOverlayReplacementContext(
                Entity industryRoot,
                Entity industryPrefab,
                Entity targetSurfacePrefab,
                EntityCommandBuffer commandBuffer,
                long operationId)
            {
                IndustryRoot = industryRoot;
                IndustryPrefab = industryPrefab;
                TargetSurfacePrefab = targetSurfacePrefab;
                CommandBuffer = commandBuffer;
                OperationId = operationId;
            }
        }

        public struct SpecialIndustryLotReplacement : IComponentData
        {
            public Entity OriginalPrefab;
            public Entity TargetSurfacePrefab;
        }

        public struct SpecialIndustrySurfaceOverlayState : IComponentData
        {
            public Entity OriginalIndustryPrefab;
            public Entity TargetSurfacePrefab;
        }

        // Runtime-only industry prefab clones are renderer implementation details,
        // not catalog entries that should invalidate construction replacement maps.
        public struct TransparentIndustryPrefabCloneTag : IComponentData
        {
        }

        public sealed class SurfaceReplacementRule
        {
            public int id { get; set; }
            public bool enabled { get; set; }
            public List<string> sourceSurfaceIds { get; set; } = new List<string>();
            public List<string> targetSurfaceIds { get; set; } = new List<string>();
        }

        public sealed class AreaReplacementRule
        {
            public int id { get; set; }
            public bool enabled { get; set; }
            public List<string> sourceAreaIds { get; set; } = new List<string>();
            public List<string> targetSurfaceIds { get; set; } = new List<string>();
        }

        private static readonly object s_RequestLock = new object();
        private static readonly Queue<ReplacementRequest> s_Requests = new Queue<ReplacementRequest>();
        private static readonly List<PendingConstructionBatchRefresh> s_ConstructionBatchRefreshRequests = new List<PendingConstructionBatchRefresh>();
        private static int s_ConstructionRefreshGeneration;
        private readonly List<PendingBatchDiagnostic> m_PendingBatchDiagnostics = new List<PendingBatchDiagnostic>();
        private readonly List<PendingRevertChange> m_PendingRevertChanges = new List<PendingRevertChange>();
        private readonly List<PendingConstructionBatchRefresh> m_PendingConstructionBatchRefreshes = new List<PendingConstructionBatchRefresh>();
        private readonly Dictionary<Entity, TemporaryPreviewReplacementTrace> m_TemporaryPreviewReplacementTraces = new Dictionary<Entity, TemporaryPreviewReplacementTrace>();
        private readonly Dictionary<Entity, Entity> m_ReplacementMaterialSources = new Dictionary<Entity, Entity>();
        private readonly HashSet<Entity> m_LoggedConstructionGeometry = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ProcessedCreatedSurfaceAreas = new HashSet<Entity>();
        private readonly HashSet<Entity> m_QueuedConstructionSurfaceAreas = new HashSet<Entity>();
        private readonly Dictionary<Entity, UnityEngine.Material> m_AppliedReplacementMaterials = new Dictionary<Entity, UnityEngine.Material>();
        private readonly Dictionary<Entity, Entity> m_AppliedReplacementMaterialSources = new Dictionary<Entity, Entity>();
        private readonly HashSet<string> m_LoggedDecalMaskIssues = new HashSet<string>();
        private readonly HashSet<Entity> m_IndustryAreasScheduledThisFrame = new HashSet<Entity>();
        private readonly HashSet<Entity> m_IndustryAreasRevertedThisFrame = new HashSet<Entity>();
        private readonly HashSet<Entity> m_LoggedDeferredIndustryRoots = new HashSet<Entity>();
        private readonly Dictionary<PrefabBase, SpecialIndustryPrefabSnapshot> m_SpecialIndustryPrefabSnapshots = new Dictionary<PrefabBase, SpecialIndustryPrefabSnapshot>();
        private readonly Dictionary<Entity, PrefabBase> m_TransparentIndustryPrefabClones = new Dictionary<Entity, PrefabBase>();
        private EntityQuery m_ExtractorIndustryPrefabQuery;
        private int m_PreWarmedExtractorPrefabCount = -1;
        private int m_ConstructionRefreshGeneration = -1;

        private EntityQuery m_SurfacePrefabQuery;
        private EntityQuery m_AllPrefabQuery;
        private EntityQuery m_SurfaceAreaQuery;
        private EntityQuery m_NewBuildingSurfaceQuery;
        private EntityQuery m_TempConstructionSurfaceQuery;
        private EntityQuery m_ChangedSurfaceAreaQuery;
        private EntityQuery m_SpecialIndustryAreaQuery;
        private EntityQuery m_ChangedSpecialIndustryAreaQuery;
        private EntityQuery m_ChangedSpecialIndustryRootAreaQuery;
        private EntityQuery m_ChangedIndustrySurfaceOverlayQuery;
        private EntityQuery m_DeferredIndustryRootCloneQuery;
        private EntityQuery m_PendingCreationDefinitionQuery;
        private EntityQuery m_CreatedSpecialIndustryFieldQuery;
        private EntityQuery m_AreaBatchEntityQuery;
        private PrefabSystem m_PrefabSystem;
        private AreaBatchSystem m_AreaBatchSystem;
        private ToolOutputBarrier m_ToolOutputBarrier;
        private long m_NextOperationId;

        private static readonly FieldInfo s_NativeBatchDataField = typeof(AreaBatchSystem).GetField(
            "m_NativeBatchData",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_AreaBatchDataDependenciesField = typeof(AreaBatchSystem).GetField(
            "m_DataDependencies",
            BindingFlags.Instance | BindingFlags.NonPublic);

        public static void QueueReplacement(string requestedRuleId, string rulesJson)
        {
            lock (s_RequestLock)
            {
                s_Requests.Enqueue(new ReplacementRequest
                {
                    RuleId = requestedRuleId ?? "",
                    RulesJson = rulesJson ?? "[]",
                    RevertAll = false,
                    AreaRules = false,
                });
            }

            Mod.Log.InfoFormat(
                "Surface replacement request queued: mode={0}, ruleId={1}",
                string.IsNullOrWhiteSpace(requestedRuleId) ? "Replace All" : "single rule",
                string.IsNullOrWhiteSpace(requestedRuleId) ? "all enabled" : requestedRuleId);
        }

        public static void QueueRevertAll()
        {
            QueueRevertAll(areas: false);
        }

        public static void QueueAreaReplacement(string requestedRuleId, string rulesJson)
        {
            lock (s_RequestLock)
            {
                s_Requests.Enqueue(new ReplacementRequest
                {
                    RuleId = requestedRuleId ?? "",
                    RulesJson = rulesJson ?? "[]",
                    RevertAll = false,
                    AreaRules = true,
                });
            }

            Mod.Log.InfoFormat(
                "Special-industry area replacement request queued: mode={0}, ruleId={1}",
                string.IsNullOrWhiteSpace(requestedRuleId) ? "Replace All" : "single rule",
                string.IsNullOrWhiteSpace(requestedRuleId) ? "all enabled" : requestedRuleId);
        }

        public static void QueueAreaRevertAll()
        {
            QueueRevertAll(areas: true);
        }

        private static void QueueRevertAll(bool areas)
        {
            // A construction can emit several CreationDefinitions while its
            // area is being drawn. Cancel any queued random targets so they
            // cannot recreate an overlay immediately after Revert All.
            ResetConstructionBatchRefreshes();
            lock (s_RequestLock)
            {
                s_Requests.Enqueue(new ReplacementRequest
                {
                    RuleId = "",
                    RulesJson = "[]",
                    RevertAll = true,
                    AreaRules = areas,
                });
            }

            Mod.Log.InfoFormat("Revert All request queued: areas={0}", areas);
        }

        public static void QueueConstructionBatchRefresh(
            Entity sourcePrefab,
            Entity targetPrefab,
            Entity constructionOwner = default,
            OwnerDefinition constructionOwnerDefinition = default,
            bool hasConstructionOwnerDefinition = false,
            Entity ruleSourcePrefab = default)
        {
            if (ruleSourcePrefab == Entity.Null)
            {
                ruleSourcePrefab = sourcePrefab;
            }

            lock (s_RequestLock)
            {
                if (s_ConstructionBatchRefreshRequests.Any(request =>
                        !request.MatchIndustryType &&
                        request.SourcePrefab == sourcePrefab &&
                        request.TargetPrefab == targetPrefab &&
                        request.ConstructionOwner == constructionOwner &&
                        request.HasConstructionOwnerDefinition == hasConstructionOwnerDefinition &&
                        (!hasConstructionOwnerDefinition || request.ConstructionOwnerDefinition.Equals(constructionOwnerDefinition))))
                {
                    return;
                }

                s_ConstructionBatchRefreshRequests.Add(new PendingConstructionBatchRefresh
                {
                    SourcePrefab = sourcePrefab,
                    RuleSourcePrefab = ruleSourcePrefab,
                    TargetPrefab = targetPrefab,
                    ConstructionOwner = constructionOwner,
                    ConstructionOwnerDefinition = constructionOwnerDefinition,
                    HasConstructionOwnerDefinition = hasConstructionOwnerDefinition,
                    MatchIndustryType = false,
                });
            }
        }

        public static void QueueConstructionIndustryReplacement(
            Entity industryPrefab,
            Entity targetPrefab,
            Entity constructionOwner,
            OwnerDefinition constructionOwnerDefinition,
            bool hasConstructionOwnerDefinition)
        {
            lock (s_RequestLock)
            {
                if (s_ConstructionBatchRefreshRequests.Any(request => request.MatchIndustryType &&
                        request.SourcePrefab == industryPrefab &&
                        request.ConstructionOwner == constructionOwner &&
                        request.HasConstructionOwnerDefinition == hasConstructionOwnerDefinition &&
                        (!hasConstructionOwnerDefinition || request.ConstructionOwnerDefinition.Equals(constructionOwnerDefinition))))
                {
                    return;
                }

                s_ConstructionBatchRefreshRequests.Add(new PendingConstructionBatchRefresh
                {
                    SourcePrefab = industryPrefab,
                    TargetPrefab = targetPrefab,
                    ConstructionOwner = constructionOwner,
                    ConstructionOwnerDefinition = constructionOwnerDefinition,
                    HasConstructionOwnerDefinition = hasConstructionOwnerDefinition,
                    MatchIndustryType = true,
                });
            }
        }

        public static bool MatchesIndustryTypeLot(EntityManager entityManager, PrefabSystem prefabSystem, Entity industryTypePrefab, Entity lotPrefab)
        {
            if (industryTypePrefab == Entity.Null || lotPrefab == Entity.Null ||
                !prefabSystem.TryGetPrefab<PrefabBase>(industryTypePrefab, out PrefabBase industryType) || industryType == null ||
                !prefabSystem.TryGetPrefab<PrefabBase>(lotPrefab, out PrefabBase lot) || lot == null)
            {
                return false;
            }

            if (entityManager.HasComponent<ExtractorAreaData>(industryTypePrefab) &&
                entityManager.HasComponent<ExtractorAreaData>(lotPrefab))
            {
                return entityManager.GetComponentData<ExtractorAreaData>(industryTypePrefab).m_MapFeature ==
                       entityManager.GetComponentData<ExtractorAreaData>(lotPrefab).m_MapFeature;
            }

            string industryName = industryType.name ?? string.Empty;
            string lotName = lot.name ?? string.Empty;
            int wordEnd = industryName.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            if (wordEnd > 0 && lotName.StartsWith(industryName.Substring(0, wordEnd), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // The UI calls the inland fish extractor Aquaculture, while its lot
            // prefab is named Inland Fish Farm Lot. Both sides carry the game's
            // MapFeature when available; this fallback covers versions where the
            // menu placeholder does not expose ExtractorAreaData itself.
            return industryName.IndexOf("Aquaculture", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   entityManager.HasComponent<ExtractorAreaData>(lotPrefab) &&
                   entityManager.GetComponentData<ExtractorAreaData>(lotPrefab).m_MapFeature == MapFeature.Fish;
        }

        public static void ResetConstructionBatchRefreshes()
        {
            lock (s_RequestLock)
            {
                s_ConstructionBatchRefreshRequests.Clear();
                s_ConstructionRefreshGeneration++;
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_AreaBatchSystem = World.GetOrCreateSystemManaged<AreaBatchSystem>();
            m_ToolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();

            m_SurfacePrefabQuery = GetEntityQuery(
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<SurfaceData>(),
                    },
                    None = new[]
                    {
                        ComponentType.ReadOnly<Deleted>(),
                        ComponentType.ReadOnly<TransparentIndustryPrefabCloneTag>(),
                    },
                },
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<ExtractorAreaData>(),
                    },
                    None = new[]
                    {
                        ComponentType.ReadOnly<Deleted>(),
                        ComponentType.ReadOnly<TransparentIndustryPrefabCloneTag>(),
                    },
                });
            m_AllPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
            m_ExtractorIndustryPrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<ExtractorAreaData>(),
                    ComponentType.ReadOnly<RenderedAreaData>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<TransparentIndustryPrefabCloneTag>(),
                },
            });
            m_SurfaceAreaQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Areas.Surface>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            m_NewBuildingSurfaceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Areas.Surface>(), ComponentType.ReadOnly<Created>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_TempConstructionSurfaceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Areas.Surface>(), ComponentType.ReadOnly<Temp>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });
            m_ChangedSurfaceAreaQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Areas.Surface>(),
                    ComponentType.ReadOnly<Batch>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            m_ChangedSurfaceAreaQuery.SetChangedVersionFilter(ComponentType.ReadOnly<PrefabRef>());
            m_SpecialIndustryAreaQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Areas.Lot>(),
                    ComponentType.ReadOnly<Geometry>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            m_ChangedSpecialIndustryAreaQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Areas.Lot>(),
                    ComponentType.ReadOnly<Geometry>(),
                    ComponentType.ReadOnly<Game.Areas.SubArea>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            m_ChangedSpecialIndustryAreaQuery.SetChangedVersionFilter(ComponentType.ReadOnly<Game.Areas.SubArea>());
            m_ChangedSpecialIndustryRootAreaQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Areas.Lot>(),
                    ComponentType.ReadOnly<Geometry>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            m_ChangedSpecialIndustryRootAreaQuery.SetChangedVersionFilter(ComponentType.ReadOnly<Game.Areas.Node>());
            m_ChangedIndustrySurfaceOverlayQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Areas.Lot>(),
                    ComponentType.ReadOnly<Geometry>(),
                    ComponentType.ReadOnly<Game.Areas.SubArea>(),
                    ComponentType.ReadOnly<Game.Areas.Node>(),
                    ComponentType.ReadOnly<SpecialIndustrySurfaceOverlayState>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            m_ChangedIndustrySurfaceOverlayQuery.SetChangedVersionFilter(new[]
            {
                ComponentType.ReadOnly<Game.Areas.SubArea>(),
                ComponentType.ReadOnly<Game.Areas.Node>(),
            });
            m_DeferredIndustryRootCloneQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Areas.Lot>(),
                    ComponentType.ReadOnly<Game.Areas.SubArea>(),
                    ComponentType.ReadOnly<SpecialIndustrySurfaceOverlayState>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<SpecialIndustryLotReplacement>(),
                },
            });
            m_PendingCreationDefinitionQuery = GetEntityQuery(ComponentType.ReadOnly<CreationDefinition>());
            m_CreatedSpecialIndustryFieldQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Areas.Lot>(),
                    ComponentType.ReadOnly<Geometry>(),
                    ComponentType.ReadOnly<Created>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            m_AreaBatchEntityQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<Batch>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
        }

        protected override void OnUpdate()
        {
            PrewarmTransparentIndustryPrefabClones();
            ProcessNewBuildingSurfaces();
            TraceTemporaryPreviewReplacementLifetimes();
            // Copy the entries before iterating. Reapplying source edge settings can
            // discover/cache a replacement and add an entry to the dictionary, which
            // must not mutate the collection being enumerated.
            foreach (KeyValuePair<Entity, Entity> replacement in m_ReplacementMaterialSources.ToArray())
            {
                CopyReplacementDecalLayerMask(replacement.Value, replacement.Key);
            }

            m_IndustryAreasScheduledThisFrame.Clear();
            m_IndustryAreasRevertedThisFrame.Clear();
            int generation;
            lock (s_RequestLock)
            {
                generation = s_ConstructionRefreshGeneration;
            }
            if (generation != m_ConstructionRefreshGeneration)
            {
                m_ConstructionRefreshGeneration = generation;
                m_PendingConstructionBatchRefreshes.Clear();
                Mod.Log.Info("Discarded stale automatic construction replacements after rules changed");
            }

            ProcessPendingRevertRefresh();
            ProcessTemporarySurfacePreviews();
            ProcessPendingConstructionBatchRefreshes();
            ProcessCreatedSpecialIndustryFields();
            ProcessUpdatedIndustrySurfaceOverlays();
            ProcessUpdatedSpecialIndustryAreaBindings();
            LogPendingBatchDiagnostics();
            while (TryDequeue(out ReplacementRequest request))
            {
                if (request.RevertAll)
                {
                    ProcessRevertAll(request.AreaRules);
                }
                else if (request.AreaRules)
                {
                    ProcessIndustryAreaReplacement(request);
                }
                else
                {
                    ProcessReplacement(request);
                }
            }
            ProcessDeferredIndustryRootClones();
            ProcessPendingTemporarySurfaceReplacements();
        }


    }
}
