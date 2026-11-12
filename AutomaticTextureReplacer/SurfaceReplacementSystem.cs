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
        private readonly HashSet<Entity> m_ExpansionSuppressedAreas = new HashSet<Entity>();
        private readonly Dictionary<Entity, Entity> m_ReplacementMaterialSources = new Dictionary<Entity, Entity>();
        private readonly HashSet<Entity> m_LoggedConstructionGeometry = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ProcessedCreatedSurfaceAreas = new HashSet<Entity>();
        private readonly HashSet<Entity> m_QueuedConstructionSurfaceAreas = new HashSet<Entity>();
        private readonly Dictionary<Entity, UnityEngine.Material> m_AppliedReplacementMaterials = new Dictionary<Entity, UnityEngine.Material>();
        private readonly Dictionary<Entity, Entity> m_AppliedReplacementMaterialSources = new Dictionary<Entity, Entity>();
        private readonly HashSet<Entity> m_IndustryAreasScheduledThisFrame = new HashSet<Entity>();
        private readonly Dictionary<PrefabBase, SpecialIndustryPrefabSnapshot> m_SpecialIndustryPrefabSnapshots = new Dictionary<PrefabBase, SpecialIndustryPrefabSnapshot>();
        private int m_ConstructionRefreshGeneration = -1;

        private EntityQuery m_SurfacePrefabQuery;
        private EntityQuery m_SurfaceAreaQuery;
        private EntityQuery m_NewBuildingSurfaceQuery;
        private EntityQuery m_TempConstructionSurfaceQuery;
        private EntityQuery m_ChangedSurfaceAreaQuery;
        private EntityQuery m_SpecialIndustryAreaQuery;
        private EntityQuery m_ChangedSpecialIndustryAreaQuery;
        private EntityQuery m_ChangedSpecialIndustryRootAreaQuery;
        private EntityQuery m_ChangedIndustrySurfaceOverlayQuery;
        private EntityQuery m_CreatedSpecialIndustryFieldQuery;
        private EntityQuery m_AreaBatchEntityQuery;
        private PrefabSystem m_PrefabSystem;
        private AreaBatchSystem m_AreaBatchSystem;
        private ToolOutputBarrier m_ToolOutputBarrier;
        private long m_NextOperationId;

        private static readonly FieldInfo s_NativeBatchDataField = typeof(AreaBatchSystem).GetField(
            "m_NativeBatchData",
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

        public static void QueueConstructionBatchRefresh(Entity sourcePrefab, Entity targetPrefab, Entity constructionOwner = default)
        {
            lock (s_RequestLock)
            {
                if (s_ConstructionBatchRefreshRequests.Any(request => request.SourcePrefab == sourcePrefab && request.TargetPrefab == targetPrefab))
                {
                    return;
                }

                s_ConstructionBatchRefreshRequests.Add(new PendingConstructionBatchRefresh
                {
                    SourcePrefab = sourcePrefab,
                    TargetPrefab = targetPrefab,
                    ConstructionOwner = constructionOwner,
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
                },
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<ExtractorAreaData>(),
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
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
        }

        protected override void OnUpdate()
        {
            ProcessNewBuildingSurfaces();
            // Copy the entries before iterating. Reapplying the material settings can
            // discover/cache a replacement and add an entry to the dictionary, which
            // must not mutate the collection being enumerated.
            foreach (KeyValuePair<Entity, Entity> replacement in m_ReplacementMaterialSources.ToArray())
            {
                CopyReplacementEdgeMaterialSettings(replacement.Value, replacement.Key);
            }

            foreach (Entity area in m_ExpansionSuppressedAreas.ToArray())
            {
                ClearReplacementAreaExpansion(area);
            }

            m_IndustryAreasScheduledThisFrame.Clear();
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
        }

        private void ProcessNewBuildingSurfaces()
        {
            m_ProcessedCreatedSurfaceAreas.RemoveWhere(area =>
                !EntityManager.Exists(area) || !EntityManager.HasComponent<Created>(area));
            if (Mod.Settings == null || !Mod.Settings.AlsoNewBuildings || m_NewBuildingSurfaceQuery.IsEmptyIgnoreFilter)
                return;
            NewBuildingSurfaceReplacementSystem rules = World.GetExistingSystemManaged<NewBuildingSurfaceReplacementSystem>();
            if (rules == null) return;

            using (NativeArray<Entity> areas = m_NewBuildingSurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                EntityCommandBuffer commands = m_ToolOutputBarrier.CreateCommandBuffer();
                foreach (Entity area in areas)
                {
                    if (!m_ProcessedCreatedSurfaceAreas.Add(area))
                        continue;
                    Entity building = GetOwningBuildingEntity(area);
                    Entity buildingPrefab = building != Entity.Null && EntityManager.HasComponent<PrefabRef>(building)
                        ? EntityManager.GetComponentData<PrefabRef>(building).m_Prefab
                        : Entity.Null;
                    if (buildingPrefab == Entity.Null && EntityManager.HasComponent<OwnerDefinition>(area))
                    {
                        OwnerDefinition ownerDefinition = EntityManager.GetComponentData<OwnerDefinition>(area);
                        if (ownerDefinition.m_Prefab != Entity.Null && EntityManager.HasComponent<BuildingData>(ownerDefinition.m_Prefab))
                            buildingPrefab = ownerDefinition.m_Prefab;
                    }
                    if (buildingPrefab == Entity.Null || IsIndustryExtractionArea(area, FindSpecialIndustryOwner(area)))
                        continue;
                    Entity source = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                    if (!rules.TryChooseSurfaceReplacement(source, out Entity target))
                        continue;

                    LogSurfaceGeometry("construction-before", area, source);
                    ApplySurfacePrefabReplacement(
                        area,
                        source,
                        target,
                        commands,
                        m_PrefabSystem.GetPrefabName(source),
                        m_PrefabSystem.GetPrefabName(target));
                    Mod.Log.InfoFormat("New building surface replacement scheduled after creation: area={0}, building={1}, source='{2}', target='{3}'", area, building, m_PrefabSystem.GetPrefabName(source), m_PrefabSystem.GetPrefabName(target));
                }
            }
        }

        protected override void OnDestroy()
        {
            foreach (SpecialIndustryPrefabSnapshot snapshot in m_SpecialIndustryPrefabSnapshots.Values)
            {
                RestoreSpecialIndustryPrefabSnapshot(snapshot, scheduleUpdate: false);
            }
            m_SpecialIndustryPrefabSnapshots.Clear();
            base.OnDestroy();
            lock (s_RequestLock)
            {
                s_Requests.Clear();
                s_ConstructionBatchRefreshRequests.Clear();
            }

            m_PendingConstructionBatchRefreshes.Clear();
        }

        private void ProcessPendingConstructionBatchRefreshes()
        {
            m_QueuedConstructionSurfaceAreas.RemoveWhere(area =>
                !EntityManager.Exists(area) || !EntityManager.HasComponent<Temp>(area));
            lock (s_RequestLock)
            {
                foreach (PendingConstructionBatchRefresh request in s_ConstructionBatchRefreshRequests)
                {
                    if (!m_PendingConstructionBatchRefreshes.Any(pending =>
                            pending.MatchIndustryType == request.MatchIndustryType &&
                            pending.SourcePrefab == request.SourcePrefab &&
                            pending.ConstructionOwner == request.ConstructionOwner &&
                            pending.HasConstructionOwnerDefinition == request.HasConstructionOwnerDefinition &&
                            (!request.HasConstructionOwnerDefinition || pending.ConstructionOwnerDefinition.Equals(request.ConstructionOwnerDefinition)) &&
                            (request.MatchIndustryType || pending.TargetPrefab == request.TargetPrefab)))
                    {
                        m_PendingConstructionBatchRefreshes.Add(request);
                    }
                }
                s_ConstructionBatchRefreshRequests.Clear();
            }

            // A creation definition can be observed after the user has disabled a
            // rule. Revalidate queued work against the current settings at consume time.
            if (m_PendingConstructionBatchRefreshes.Count > 0 && !Mod.Settings.AlsoNewBuildings)
            {
                m_PendingConstructionBatchRefreshes.Clear();
            }
            else if (m_PendingConstructionBatchRefreshes.Count > 0)
            {
                HashSet<string> enabledMappings = GetEnabledConstructionMappings();
                m_PendingConstructionBatchRefreshes.RemoveAll(request =>
                    !enabledMappings.Contains((request.MatchIndustryType ? "I|" : "S|") + GetPrefabIdString(request.SourcePrefab) + "|" + GetPrefabIdString(request.TargetPrefab)));
            }

            using (NativeArray<Entity> changedAreas = m_ChangedSurfaceAreaQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Entity> specialIndustryAreas = m_ChangedSpecialIndustryAreaQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Entity> changedIndustryRoots = m_ChangedSpecialIndustryRootAreaQuery.ToEntityArray(Allocator.Temp))
            {
                if (m_PendingConstructionBatchRefreshes.Count == 0)
                {
                    return;
                }

                foreach (Entity area in changedAreas)
                {
                    Entity currentPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                    if (m_PendingConstructionBatchRefreshes.Any(request => !request.MatchIndustryType && request.TargetPrefab == currentPrefab) &&
                        m_LoggedConstructionGeometry.Add(area))
                    {
                        LogSurfaceGeometry("construction", area, currentPrefab);
                    }
                    foreach (PendingConstructionBatchRefresh request in m_PendingConstructionBatchRefreshes)
                    {
                        if (request.TargetPrefab != currentPrefab)
                        {
                            continue;
                        }

                        string sourceName = m_PrefabSystem.GetPrefabName(request.SourcePrefab);
                        string targetName = m_PrefabSystem.GetPrefabName(request.TargetPrefab);
                        // The construction path changes the prefab before the area
                        // is created, so it does not pass through ProcessReplacement.
                        // Apply the source edge settings here as well; otherwise a
                        // mod surface can retain its larger edge expansion and spill
                        // over adjacent roads.
                        PrepareSurfaceReplacement(area, request.SourcePrefab, request.TargetPrefab, sourceName, targetName);
                        request.SurfaceAreaObserved = true;
                    }
                }

                EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
                bool hasSpecialFieldUpdates = false;

                // Once the area exists, match it to the exact building that
                // produced the CreationDefinition. This remains valid after the
                // temporary creation tags have already been removed.
                using (NativeArray<Entity> builtAreas = m_SurfaceAreaQuery.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity area in builtAreas)
                    {
                        if (!EntityManager.HasComponent<PrefabRef>(area))
                            continue;
                        Entity source = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                        Entity owner = GetOwningBuildingEntity(area);
                        if (owner == Entity.Null)
                            continue;
                        foreach (PendingConstructionBatchRefresh request in m_PendingConstructionBatchRefreshes)
                        {
                            if (request.MatchIndustryType || request.SourcePrefab != source ||
                                request.ConstructionOwner == Entity.Null || request.ConstructionOwner != owner ||
                                !m_QueuedConstructionSurfaceAreas.Add(area))
                                continue;

                            ApplySurfacePrefabReplacement(
                                area,
                                source,
                                request.TargetPrefab,
                                commandBuffer,
                                m_PrefabSystem.GetPrefabName(source),
                                m_PrefabSystem.GetPrefabName(request.TargetPrefab));
                            request.SurfaceAreaObserved = true;
                            Mod.Log.InfoFormat(
                                "Automatic construction surface swapped by owner match: area={0}, owner={1}, source='{2}', target='{3}'",
                                area, owner, m_PrefabSystem.GetPrefabName(source), m_PrefabSystem.GetPrefabName(request.TargetPrefab));
                            break;
                        }
                    }
                }

                // Generate the area with its original prefab first. Swap only the
                // new temporary area before ApplyAreasSystem makes it permanent.
                if (!m_TempConstructionSurfaceQuery.IsEmptyIgnoreFilter)
                {
                    using NativeArray<Entity> tempAreas = m_TempConstructionSurfaceQuery.ToEntityArray(Allocator.Temp);
                    Mod.Log.InfoFormat("Construction temporary surface scan: pending={0}, tempAreas={1}", m_PendingConstructionBatchRefreshes.Count, tempAreas.Length);
                    foreach (Entity area in tempAreas)
                    {
                        Temp temp = EntityManager.GetComponentData<Temp>(area);
                        if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Hidden)) != 0)
                            continue;
                        Mod.Log.InfoFormat("Construction temporary surface candidate: area={0}, prefab='{1}', flags={2}", area, m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab), temp.m_Flags);
                        if ((temp.m_Flags & TempFlags.Create) == 0)
                            continue;
                        Entity source = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                        foreach (PendingConstructionBatchRefresh request in m_PendingConstructionBatchRefreshes)
                        {
                            if (request.MatchIndustryType || request.SourcePrefab != source ||
                                !m_QueuedConstructionSurfaceAreas.Add(area))
                                continue;

                            ApplySurfacePrefabReplacement(
                                area,
                                source,
                                request.TargetPrefab,
                                commandBuffer,
                                m_PrefabSystem.GetPrefabName(source),
                                m_PrefabSystem.GetPrefabName(request.TargetPrefab));
                            request.SurfaceAreaObserved = true;
                            Mod.Log.InfoFormat(
                                "Automatic construction surface swapped after area creation: area={0}, source='{1}', target='{2}'",
                                area, m_PrefabSystem.GetPrefabName(source), m_PrefabSystem.GetPrefabName(request.TargetPrefab));
                            break;
                        }
                    }
                }

                // Forestry and any other extractor with its render batch on the
                // root lot may have no SubArea buffer at all. Detect its creation
                // or contour edits through the root Node buffer instead.
                foreach (Entity industryRoot in changedIndustryRoots)
                {
                    Entity industryPrefab = GetOriginalIndustryLotPrefab(industryRoot);
                    if (industryPrefab == Entity.Null ||
                        !EntityManager.HasComponent<ExtractorAreaData>(industryPrefab) ||
                        !TryGetRenderedAreaBatchIndex(industryPrefab, industryRoot, out _) ||
                        GetRenderableIndustrySubAreas(industryRoot).Count > 0)
                    {
                        continue;
                    }

                    string lotName = m_PrefabSystem.GetPrefabName(industryPrefab);
                    foreach (PendingConstructionBatchRefresh request in m_PendingConstructionBatchRefreshes)
                    {
                        if (request.MatchIndustryType && request.ConstructionOwner == Entity.Null && !request.HasConstructionOwnerDefinition)
                        {
                            continue;
                        }

                        if (request.SpecialIndustryFieldObserved)
                        {
                            continue;
                        }

                        string sourceName = m_PrefabSystem.GetPrefabName(request.SourcePrefab);
                        if (!MatchesIndustryConstructionRequest(request, industryRoot))
                        {
                            continue;
                        }

                        if (m_IndustryAreasScheduledThisFrame.Add(industryRoot) &&
                            TryQueueIndustrySurfaceOverlay(
                                industryRoot,
                                industryPrefab,
                                request.TargetPrefab,
                                commandBuffer,
                                ++m_NextOperationId))
                        {
                            request.SpecialIndustryFieldObserved = true;
                            hasSpecialFieldUpdates = true;
                            Mod.Log.InfoFormat(
                                "Automatic root-batch industry overlay scheduled from changed nodes: lot={0}, prefab='{1}', source='{2}', target='{3}'",
                                industryRoot,
                                lotName,
                                sourceName,
                                m_PrefabSystem.GetPrefabName(request.TargetPrefab));
                        }
                        break;
                    }
                }

                foreach (Entity industryArea in specialIndustryAreas)
                {
                    if (!EntityManager.HasComponent<PrefabRef>(industryArea) ||
                        !EntityManager.HasComponent<Game.Areas.SubArea>(industryArea))
                    {
                        continue;
                    }

                    Entity industryPrefab = GetOriginalIndustryLotPrefab(industryArea);
                    if (industryPrefab == Entity.Null || !EntityManager.HasComponent<ExtractorAreaData>(industryPrefab))
                    {
                        continue;
                    }
                    string industryLotName = m_PrefabSystem.GetPrefabName(industryPrefab);
                    Mod.Log.InfoFormat(
                        "Pending construction refresh examining changed industry sub-areas: lot={0}, lotPrefab='{1}', childCount={2}, pendingMappings={3}",
                        industryArea,
                        industryLotName,
                        EntityManager.GetBuffer<Game.Areas.SubArea>(industryArea, true).Length,
                        m_PendingConstructionBatchRefreshes.Count);

                    // Root-batch extractors (Ore, Oil and any other extractor
                    // configured this way) expose the area surface on the lot
                    // prefab itself. Their SubArea buffer can change after the
                    // Created tag has already been consumed, so handle them here,
                    // on the observed owner/buffer update path.
                    if (EntityManager.HasComponent<ExtractorAreaData>(industryPrefab) &&
                        EntityManager.HasBuffer<Game.Areas.Node>(industryArea) &&
                        TryGetRenderedAreaBatchIndex(industryPrefab, industryArea, out _) &&
                        GetRenderableIndustrySubAreas(industryArea).Count == 0)
                    {
                        foreach (PendingConstructionBatchRefresh request in m_PendingConstructionBatchRefreshes)
                        {
                            if (request.MatchIndustryType && request.ConstructionOwner == Entity.Null && !request.HasConstructionOwnerDefinition)
                            {
                                continue;
                            }

                            if (request.SpecialIndustryFieldObserved)
                            {
                                continue;
                            }

                            string sourceName = m_PrefabSystem.GetPrefabName(request.SourcePrefab);
                            if (!MatchesIndustryConstructionRequest(request, industryArea))
                            {
                                continue;
                            }

                            if (m_IndustryAreasScheduledThisFrame.Add(industryArea) &&
                                TryQueueIndustrySurfaceOverlay(
                                    industryArea,
                                    industryPrefab,
                                    request.TargetPrefab,
                                    commandBuffer,
                                    ++m_NextOperationId))
                            {
                                request.SpecialIndustryFieldObserved = true;
                                hasSpecialFieldUpdates = true;
                                Mod.Log.InfoFormat(
                                    "Automatic root-batch industry overlay scheduled from changed sub-area: lot={0}, prefab='{1}', source='{2}', target='{3}'",
                                    industryArea,
                                    industryLotName,
                                    sourceName,
                                    m_PrefabSystem.GetPrefabName(request.TargetPrefab));
                            }
                            break;
                        }

                        continue;
                    }

                    DynamicBuffer<Game.Areas.SubArea> subAreas = EntityManager.GetBuffer<Game.Areas.SubArea>(industryArea, true);
                    bool fieldUpdatedForIndustryArea = false;
                    for (int i = 0; i < subAreas.Length; i++)
                    {
                        Entity fieldArea = subAreas[i].m_Area;
                        if (!EntityManager.HasComponent<PrefabRef>(fieldArea))
                        {
                            continue;
                        }

                        Entity currentFieldPrefab = EntityManager.GetComponentData<PrefabRef>(fieldArea).m_Prefab;
                        foreach (PendingConstructionBatchRefresh request in m_PendingConstructionBatchRefreshes)
                        {
                            if (request.MatchIndustryType && request.ConstructionOwner == Entity.Null && !request.HasConstructionOwnerDefinition)
                            {
                                continue;
                            }

                            if (request.SpecialIndustryFieldObserved)
                            {
                                continue;
                            }

                            Entity originalFieldPrefab = GetOriginalIndustryLotPrefab(fieldArea);
                            if (originalFieldPrefab == Entity.Null ||
                                !MatchesIndustryConstructionRequest(request, industryArea) ||
                                !TryGetRenderedAreaBatchIndex(originalFieldPrefab, fieldArea, out _) ||
                                IsUsingSpecialIndustryTarget(fieldArea, request.TargetPrefab))
                            {
                                continue;
                            }

                            if (!m_IndustryAreasScheduledThisFrame.Add(fieldArea))
                            {
                                continue;
                            }
                            ApplySurfacePrefabReplacement(
                                fieldArea,
                                originalFieldPrefab,
                                request.TargetPrefab,
                                commandBuffer,
                                m_PrefabSystem.GetPrefabName(request.SourcePrefab),
                                m_PrefabSystem.GetPrefabName(request.TargetPrefab),
                                originalFieldPrefab);
                            Mod.Log.InfoFormat(
                                "Automatic special-industry lot replacement scheduled from changed sub-area: industry={0}, lot={1}, field={2}, source='{3}', targetSurface='{4}'",
                                industryArea,
                                industryLotName,
                                fieldArea,
                                m_PrefabSystem.GetPrefabName(request.SourcePrefab),
                                m_PrefabSystem.GetPrefabName(request.TargetPrefab));
                            request.SpecialIndustryFieldObserved = true;
                            hasSpecialFieldUpdates = true;
                            fieldUpdatedForIndustryArea = true;
                            break;
                        }

                        if (fieldUpdatedForIndustryArea)
                        {
                            break;
                        }
                    }
                }

                if (hasSpecialFieldUpdates)
                {
                    Mod.Log.Info("Automatic special-industry field update batch submitted");
                }

            }

            for (int i = m_PendingConstructionBatchRefreshes.Count - 1; i >= 0; i--)
            {
                PendingConstructionBatchRefresh request = m_PendingConstructionBatchRefreshes[i];
                request.FramesChecked++;
                if (request.SpecialIndustryFieldObserved || request.FramesChecked >= 3600)
                {
                    if (!request.SurfaceAreaObserved && !request.SpecialIndustryFieldObserved)
                    {
                        Mod.Log.WarnFormat(
                            "Construction surface refresh expired before a created area was observed: source='{0}', target='{1}'",
                            m_PrefabSystem.GetPrefabName(request.SourcePrefab),
                            m_PrefabSystem.GetPrefabName(request.TargetPrefab));
                    }
                    m_PendingConstructionBatchRefreshes.RemoveAt(i);
                }
            }
        }

        private HashSet<string> GetEnabledConstructionMappings()
        {
            var mappings = new HashSet<string>(StringComparer.Ordinal);
            if (Mod.Settings == null)
                return mappings;

            List<SurfaceReplacementRule> rules;
            try
            {
                rules = JSON.MakeInto<List<SurfaceReplacementRule>>(JSON.Load(Mod.Settings.RulesJson ?? "[]")) ?? new List<SurfaceReplacementRule>();
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Failed to revalidate pending automatic surface replacements");
                return mappings;
            }

            var targetsBySourceId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (SurfaceReplacementRule rule in rules)
            {
                if (rule == null || !rule.enabled || rule.sourceSurfaceIds == null || rule.targetSurfaceIds == null)
                    continue;
                foreach (string sourceId in rule.sourceSurfaceIds)
                {
                    if (string.IsNullOrEmpty(sourceId))
                        continue;
                    string sourceKey = "S|" + sourceId;
                    if (!targetsBySourceId.TryGetValue(sourceKey, out HashSet<string> targetIds))
                        targetsBySourceId[sourceKey] = targetIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string targetId in rule.targetSurfaceIds)
                        if (!string.IsNullOrEmpty(targetId))
                            targetIds.Add(targetId);
                }
            }

            List<AreaReplacementRule> areaRules;
            try
            {
                areaRules = JSON.MakeInto<List<AreaReplacementRule>>(JSON.Load(Mod.Settings.AreaRulesJson ?? "[]")) ?? new List<AreaReplacementRule>();
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Failed to revalidate pending automatic industry-area replacements");
                areaRules = new List<AreaReplacementRule>();
            }

            foreach (AreaReplacementRule rule in areaRules)
            {
                if (rule == null || !rule.enabled || rule.sourceAreaIds == null || rule.targetSurfaceIds == null)
                    continue;
                foreach (string industryId in rule.sourceAreaIds)
                {
                    if (string.IsNullOrEmpty(industryId))
                        continue;
                    string sourceKey = "I|" + industryId;
                    if (!targetsBySourceId.TryGetValue(sourceKey, out HashSet<string> targetIds))
                        targetsBySourceId[sourceKey] = targetIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string targetId in rule.targetSurfaceIds)
                        if (!string.IsNullOrEmpty(targetId))
                            targetIds.Add(targetId);
                }
            }

            foreach (KeyValuePair<string, HashSet<string>> mapping in targetsBySourceId)
                foreach (string targetId in mapping.Value)
                    mappings.Add(mapping.Key + "|" + targetId);
            return mappings;
        }

        private string GetPrefabIdString(Entity prefabEntity)
        {
            return m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase prefab) && prefab != null
                ? prefab.GetPrefabID().ToString()
                : string.Empty;
        }

        private void ProcessCreatedSpecialIndustryFields()
        {
            if (m_PendingConstructionBatchRefreshes.Count == 0 || m_CreatedSpecialIndustryFieldQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            using (NativeArray<Entity> fields = m_CreatedSpecialIndustryFieldQuery.ToEntityArray(Allocator.Temp))
            {
                EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
                int replacements = 0;
                foreach (Entity fieldArea in fields)
                {
                    Entity fieldPrefab = GetOriginalIndustryLotPrefab(fieldArea);
                    if (fieldPrefab == Entity.Null || !EntityManager.HasComponent<Game.Areas.Lot>(fieldArea))
                    {
                        continue;
                    }

                    if (EntityManager.HasComponent<ExtractorAreaData>(fieldPrefab))
                    {
                        if (!EntityManager.HasBuffer<Game.Areas.Node>(fieldArea) ||
                            !TryGetRenderedAreaBatchIndex(fieldPrefab, fieldArea, out _) ||
                            GetRenderableIndustrySubAreas(fieldArea).Count > 0)
                        {
                            continue;
                        }

                        foreach (PendingConstructionBatchRefresh request in m_PendingConstructionBatchRefreshes)
                        {
                            if (request.SpecialIndustryFieldObserved)
                            {
                                continue;
                            }

                            if (!MatchesIndustryConstructionRequest(request, fieldArea)) continue;
                            if (m_IndustryAreasScheduledThisFrame.Add(fieldArea) &&
                                TryQueueIndustrySurfaceOverlay(fieldArea, fieldPrefab, request.TargetPrefab, commandBuffer, ++m_NextOperationId))
                            {
                                request.SpecialIndustryFieldObserved = true;
                                replacements++;
                            }
                            break;
                        }

                        continue;
                    }

                    Entity industryArea = FindSpecialIndustryOwner(fieldArea);
                    if (industryArea == Entity.Null || !TryGetRenderedAreaBatchIndex(fieldPrefab, fieldArea, out _)) continue;

                    foreach (PendingConstructionBatchRefresh request in m_PendingConstructionBatchRefreshes)
                    {
                        if (request.SpecialIndustryFieldObserved)
                        {
                            continue;
                        }

                        if (!MatchesIndustryConstructionRequest(request, industryArea) || IsUsingSpecialIndustryTarget(fieldArea, request.TargetPrefab)) continue;
                        if (!m_IndustryAreasScheduledThisFrame.Add(fieldArea)) continue;
                        ApplySurfacePrefabReplacement(
                            fieldArea,
                            fieldPrefab,
                            request.TargetPrefab,
                            commandBuffer,
                            m_PrefabSystem.GetPrefabName(fieldPrefab),
                            m_PrefabSystem.GetPrefabName(request.TargetPrefab),
                            fieldPrefab);
                        request.SpecialIndustryFieldObserved = true;
                        replacements++;
                        break;
                    }
                }

                if (replacements > 0)
                    Mod.Log.InfoFormat("Automatic created special-industry fields submitted: count={0}", replacements);
            }
        }

        private void ProcessUpdatedSpecialIndustryAreaBindings()
        {
            using (NativeArray<Entity> industryAreas = m_ChangedSpecialIndustryAreaQuery.ToEntityArray(Allocator.Temp))
            {
                if (industryAreas.Length == 0)
                {
                    return;
                }

                EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
                int updatedFields = 0;
                foreach (Entity industryArea in industryAreas)
                {
                    Entity industryPrefab = GetOriginalIndustryLotPrefab(industryArea);
                    if (industryPrefab == Entity.Null || !EntityManager.HasComponent<ExtractorAreaData>(industryPrefab) ||
                        !EntityManager.HasBuffer<Game.Areas.SubArea>(industryArea))
                    {
                        continue;
                    }

                    if (EntityManager.HasComponent<SpecialIndustrySurfaceOverlayState>(industryArea))
                    {
                        SynchronizeIndustrySurfaceOverlay(industryArea, commandBuffer);
                        continue;
                    }

                    DynamicBuffer<Game.Areas.SubArea> subAreas = EntityManager.GetBuffer<Game.Areas.SubArea>(industryArea, true);
                    Entity replacementSurface = Entity.Null;
                    List<Entity> unconvertedLotAreas = new List<Entity>();
                    HashSet<Entity> replacedSurfaceAreas = new HashSet<Entity>();
                    for (int i = 0; i < subAreas.Length; i++)
                    {
                        Entity childArea = subAreas[i].m_Area;
                        if (!EntityManager.HasComponent<PrefabRef>(childArea))
                        {
                            continue;
                        }

                        Entity childPrefab = EntityManager.GetComponentData<PrefabRef>(childArea).m_Prefab;
                        if (childPrefab == Entity.Null)
                        {
                            continue;
                        }

                        // ATR variants keep the original Lot/Extractor data, so detect them
                        // through our marker before treating ordinary Lot children as fresh.
                        if (EntityManager.HasComponent<SpecialIndustryLotReplacement>(childArea))
                        {
                            Entity targetSurface = EntityManager.GetComponentData<SpecialIndustryLotReplacement>(childArea).TargetSurfacePrefab;
                            if (targetSurface != Entity.Null)
                                replacementSurface = targetSurface;
                            replacedSurfaceAreas.Add(childArea);
                        }
                        else if (EntityManager.HasComponent<SurfaceData>(childPrefab))
                        {
                            replacementSurface = childPrefab;
                            replacedSurfaceAreas.Add(childArea);
                        }
                        else if (EntityManager.HasComponent<Game.Areas.Lot>(childArea) && !EntityManager.HasComponent<SurfaceData>(childPrefab))
                        {
                            unconvertedLotAreas.Add(childArea);
                        }
                    }

                    if (replacementSurface == Entity.Null || unconvertedLotAreas.Count == 0)
                    {
                        continue;
                    }

                    foreach (Entity fieldArea in unconvertedLotAreas)
                    {
                        if (!m_IndustryAreasScheduledThisFrame.Add(fieldArea))
                            continue;
                        Entity oldPrefab = EntityManager.GetComponentData<PrefabRef>(fieldArea).m_Prefab;
                        ApplySurfacePrefabReplacement(
                            fieldArea,
                            oldPrefab,
                            replacementSurface,
                            commandBuffer,
                            m_PrefabSystem.GetPrefabName(oldPrefab),
                            m_PrefabSystem.GetPrefabName(replacementSurface),
                            oldPrefab);
                        Mod.Log.InfoFormat(
                            "Special-industry field replacement binding reapplied after area edit: lot={0}, mapFeature={1}, field={2}, oldPrefab='{3}', targetSurface='{4}'",
                            industryArea,
                            EntityManager.GetComponentData<ExtractorAreaData>(industryPrefab).m_MapFeature,
                            fieldArea,
                            m_PrefabSystem.GetPrefabName(oldPrefab),
                            m_PrefabSystem.GetPrefabName(replacementSurface));
                        updatedFields++;
                    }

                    // Editing an extractor field creates a fresh Lot child. The old child
                    // has already been changed to a SurfacePrefab, so vanilla area editing
                    // no longer recognizes it as the generated field to retire.
                    if (unconvertedLotAreas.Count > 0 && replacedSurfaceAreas.Count > 0)
                    {
                        DynamicBuffer<Game.Areas.SubArea> writableSubAreas = EntityManager.GetBuffer<Game.Areas.SubArea>(industryArea);
                        for (int i = writableSubAreas.Length - 1; i >= 0; i--)
                        {
                            Entity oldFieldSurface = writableSubAreas[i].m_Area;
                            if (!replacedSurfaceAreas.Contains(oldFieldSurface))
                            {
                                continue;
                            }

                            writableSubAreas.RemoveAt(i);
                            if (EntityManager.Exists(oldFieldSurface) && !EntityManager.HasComponent<Deleted>(oldFieldSurface))
                            {
                                commandBuffer.AddComponent<Deleted>(oldFieldSurface);
                            }
                            Mod.Log.InfoFormat(
                                "Stale special-industry field surface removed after contour edit: lot={0}, oldArea={1}, replacementPrefab='{2}'",
                                industryArea,
                                oldFieldSurface,
                                m_PrefabSystem.GetPrefabName(replacementSurface));
                        }
                    }
                }

                if (updatedFields > 0)
                {
                    Mod.Log.InfoFormat("Special-industry field edit bindings reapplied: fields={0}", updatedFields);
                }
            }
        }

        private void ProcessUpdatedIndustrySurfaceOverlays()
        {
            using (NativeArray<Entity> industryAreas = m_ChangedIndustrySurfaceOverlayQuery.ToEntityArray(Allocator.Temp))
            {
                if (industryAreas.Length == 0)
                {
                    return;
                }

                EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
                foreach (Entity industryArea in industryAreas)
                {
                    SynchronizeIndustrySurfaceOverlay(industryArea, commandBuffer);
                }
            }
        }

        private static bool TryDequeue(out ReplacementRequest request)
        {
            lock (s_RequestLock)
            {
                if (s_Requests.Count > 0)
                {
                    request = s_Requests.Dequeue();
                    return true;
                }
            }

            request = null;
            return false;
        }

        private void ProcessRevertAll(bool areasOnly)
        {
            ResetConstructionBatchRefreshes();
            m_PendingConstructionBatchRefreshes.Clear();
            m_ExpansionSuppressedAreas.Clear();
            m_ReplacementMaterialSources.Clear();
            m_AppliedReplacementMaterials.Clear();
            m_AppliedReplacementMaterialSources.Clear();
            Mod.Log.Info("Pending automatic construction surface replacements cancelled for Revert All");

            int scanned = 0;
            int alreadyDefault = 0;
            int changed = 0;
            int noDefaultMapping = 0;
            int skippedExamples = 0;
            System.Random random = new System.Random();
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            if (!areasOnly)
            {
                using (NativeArray<Entity> areaEntities = m_SurfaceAreaQuery.ToEntityArray(Allocator.Temp))
                {
                    scanned = areaEntities.Length;
                    foreach (Entity area in areaEntities)
                    {
                        Entity industryOwner = FindSpecialIndustryOwner(area);
                        if (IsIndustrySurfaceOverlay(area) ||
                            EntityManager.HasComponent<SpecialIndustryLotReplacement>(area) ||
                            IsIndustryExtractionArea(area, industryOwner))
                        {
                            continue;
                        }

                        PrefabRef currentRef = EntityManager.GetComponentData<PrefabRef>(area);
                        if (!TryGetDefaultSurfacePrefab(area, currentRef.m_Prefab, random, out Entity owner, out int slot, out Entity defaultPrefab, out bool matchesDefault))
                        {
                            noDefaultMapping++;
                            if (skippedExamples < 12)
                            {
                                Entity currentPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                                Mod.Log.WarnFormat(
                                    "Revert All skipped area without a matching asset sub-area: area={0}, currentSurface='{1}'",
                                    area,
                                    m_PrefabSystem.GetPrefabName(currentPrefab));
                                skippedExamples++;
                            }
                            continue;
                        }

                        if (matchesDefault || currentRef.m_Prefab == defaultPrefab)
                        {
                            alreadyDefault++;
                            continue;
                        }

                        string previousName = m_PrefabSystem.GetPrefabName(currentRef.m_Prefab);
                        string defaultName = m_PrefabSystem.GetPrefabName(defaultPrefab);
                        commandBuffer.SetComponent(area, new PrefabRef { m_Prefab = defaultPrefab });
                        if (!EntityManager.HasComponent<Updated>(area))
                        {
                            commandBuffer.AddComponent<Updated>(area);
                        }
                        m_PendingRevertChanges.Add(new PendingRevertChange
                        {
                            Area = area,
                            DefaultPrefab = defaultPrefab,
                        });
                        changed++;
                        Mod.Log.InfoFormat(
                            "Surface revert scheduled: area={0}, owner={1}, assetSubAreaIndex={2}, previous='{3}', default='{4}'",
                            area,
                            owner,
                            slot,
                            previousName,
                            defaultName);
                    }
                }
            }

            if (areasOnly)
            {
                using (NativeArray<Entity> lotAreas = m_SpecialIndustryAreaQuery.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity area in lotAreas)
                    {
                        if (EntityManager.HasComponent<SpecialIndustrySurfaceOverlayState>(area))
                        {
                            SpecialIndustrySurfaceOverlayState overlayState = EntityManager.GetComponentData<SpecialIndustrySurfaceOverlayState>(area);
                            Entity overlay = FindIndustrySurfaceOverlay(area, overlayState.TargetSurfacePrefab);
                            if (overlay != Entity.Null)
                            {
                                commandBuffer.AddComponent<Deleted>(overlay);
                            }
                            commandBuffer.RemoveComponent<SpecialIndustrySurfaceOverlayState>(area);
                            Mod.Log.InfoFormat(
                                "Special-industry surface overlay reverted: root={0}, overlay={1}, target='{2}'",
                                area,
                                overlay,
                                overlayState.TargetSurfacePrefab != Entity.Null
                                    ? m_PrefabSystem.GetPrefabName(overlayState.TargetSurfacePrefab)
                                    : "<none>");
                            changed++;
                            continue;
                        }

                        if (!EntityManager.HasComponent<SpecialIndustryLotReplacement>(area) ||
                            !EntityManager.HasComponent<PrefabRef>(area))
                        {
                            continue;
                        }

                        Entity originalPrefab = EntityManager.GetComponentData<SpecialIndustryLotReplacement>(area).OriginalPrefab;
                        Entity currentPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                        if (originalPrefab == Entity.Null || currentPrefab == originalPrefab)
                        {
                            continue;
                        }

                        commandBuffer.SetComponent(area, new PrefabRef { m_Prefab = originalPrefab });
                        commandBuffer.RemoveComponent<SpecialIndustryLotReplacement>(area);
                        if (!EntityManager.HasComponent<Updated>(area))
                        {
                            commandBuffer.AddComponent<Updated>(area);
                        }
                        QueueBatchDiagnostic(
                            area,
                            currentPrefab,
                            originalPrefab,
                            m_PrefabSystem.GetPrefabName(currentPrefab),
                            m_PrefabSystem.GetPrefabName(originalPrefab));
                        m_PendingRevertChanges.Add(new PendingRevertChange
                        {
                            Area = area,
                            DefaultPrefab = originalPrefab,
                        });
                        changed++;
                        Mod.Log.InfoFormat(
                            "Special-industry lot revert scheduled: area={0}, previous='{1}', original='{2}'",
                            area,
                            m_PrefabSystem.GetPrefabName(currentPrefab),
                            m_PrefabSystem.GetPrefabName(originalPrefab));
                    }
                }

                RestoreAllSpecialIndustryPrefabSnapshots();
            }


            Mod.Log.InfoFormat(
                "Revert All scan finished: areasOnly={0}, surfaceAreas={1}, changed={2}, alreadyDefault={3}, noDefaultMapping={4}",
                areasOnly,
                scanned,
                changed,
                alreadyDefault,
                noDefaultMapping);
        }

        private bool ApplySurfacePrefabReplacement(
            Entity area,
            Entity sourcePrefab,
            Entity targetPrefab,
            EntityCommandBuffer commandBuffer,
            string sourceName,
            string targetName,
            Entity originalSpecialIndustryPrefab = default)
        {
            if (area == Entity.Null || sourcePrefab == Entity.Null || targetPrefab == Entity.Null ||
                !EntityManager.Exists(area) || !EntityManager.HasComponent<PrefabRef>(area))
            {
                return false;
            }

            if (originalSpecialIndustryPrefab != Entity.Null)
            {
                var marker = new SpecialIndustryLotReplacement
                {
                    OriginalPrefab = originalSpecialIndustryPrefab,
                    TargetSurfacePrefab = targetPrefab,
                };
                if (EntityManager.HasComponent<SpecialIndustryLotReplacement>(area))
                {
                    commandBuffer.SetComponent(area, marker);
                }
                else
                {
                    commandBuffer.AddComponent(area, marker);
                }
            }

            PrepareSurfaceReplacement(area, sourcePrefab, targetPrefab, sourceName, targetName);
            commandBuffer.SetComponent(area, new PrefabRef { m_Prefab = targetPrefab });
            if (!EntityManager.HasComponent<Updated>(area))
            {
                commandBuffer.AddComponent<Updated>(area);
            }
            return true;
        }

        private bool ApplySurfacePrefabReplacement(
            Entity area,
            Entity targetPrefab,
            EntityCommandBuffer commandBuffer,
            Entity originalSpecialIndustryPrefab = default,
            string sourceNameOverride = null)
        {
            if (area == Entity.Null || !EntityManager.Exists(area) ||
                !EntityManager.HasComponent<PrefabRef>(area))
            {
                return false;
            }

            Entity sourcePrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
            string sourceName = string.IsNullOrEmpty(sourceNameOverride)
                ? m_PrefabSystem.GetPrefabName(sourcePrefab)
                : sourceNameOverride;
            return ApplySurfacePrefabReplacement(
                area,
                sourcePrefab,
                targetPrefab,
                commandBuffer,
                sourceName,
                m_PrefabSystem.GetPrefabName(targetPrefab),
                originalSpecialIndustryPrefab);
        }

        private void PrepareSurfaceReplacement(Entity area, Entity sourcePrefab, Entity targetPrefab, string sourceName, string targetName)
        {
            ClearReplacementAreaExpansion(area);
            CopyReplacementEdgeMaterialSettings(sourcePrefab, targetPrefab);
            QueueBatchDiagnostic(area, sourcePrefab, targetPrefab, sourceName, targetName);
        }

        private void ClearReplacementAreaExpansion(Entity area)
        {
            if (area == Entity.Null || !EntityManager.Exists(area))
            {
                return;
            }

            // Newly created areas can receive their Expand buffer after the
            // replacement is observed. Keep tracking the area so a later frame
            // clears the buffer as soon as the game creates it.
            m_ExpansionSuppressedAreas.Add(area);
            if (!EntityManager.HasBuffer<Game.Areas.Expand>(area))
            {
                return;
            }

            DynamicBuffer<Game.Areas.Expand> expands = EntityManager.GetBuffer<Game.Areas.Expand>(area);
            for (int i = 0; i < expands.Length; i++)
            {
                expands[i] = default(Game.Areas.Expand);
            }
        }

        private void CopyReplacementEdgeMaterialSettings(Entity sourcePrefab, Entity targetPrefab)
        {
            if (sourcePrefab == Entity.Null || targetPrefab == Entity.Null ||
                !EntityManager.HasComponent<RenderedAreaData>(targetPrefab) ||
                !m_PrefabSystem.TryGetPrefab<PrefabBase>(sourcePrefab, out PrefabBase source) ||
                source == null || !source.TryGet<RenderedArea>(out RenderedArea sourceRendered) || sourceRendered == null)
            {
                return;
            }

            m_ReplacementMaterialSources[targetPrefab] = sourcePrefab;

            int sourceBatchIndex = EntityManager.HasComponent<RenderedAreaData>(sourcePrefab)
                ? EntityManager.GetComponentData<RenderedAreaData>(sourcePrefab).m_BatchIndex
                : -1;
            int batchIndex = EntityManager.GetComponentData<RenderedAreaData>(targetPrefab).m_BatchIndex;
            if (!m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out UnityEngine.Material material, out _, out _, out _) ||
                material == null)
            {
                return;
            }

            // A material is normally rebuilt only when its batch changes. Do not
            // rewrite it (and do not log) on every simulation frame.
            if (m_AppliedReplacementMaterials.TryGetValue(targetPrefab, out UnityEngine.Material appliedMaterial) &&
                ReferenceEquals(appliedMaterial, material) &&
                m_AppliedReplacementMaterialSources.TryGetValue(targetPrefab, out Entity appliedSource) &&
                appliedSource == sourcePrefab)
            {
                return;
            }

            material.SetFloat(UnityEngine.Shader.PropertyToID("colossal_EdgeNormal"), sourceRendered.m_EdgeNormal);
            material.SetVector(UnityEngine.Shader.PropertyToID("colossal_EdgeFadeRange"), new UnityEngine.Vector4(sourceRendered.m_EdgeFadeRange.x, sourceRendered.m_EdgeFadeRange.y, 0f, 0f));
            material.SetVector(UnityEngine.Shader.PropertyToID("colossal_EdgeNormalRange"), new UnityEngine.Vector4(sourceRendered.m_EdgeNormalRange.x, sourceRendered.m_EdgeNormalRange.y, 0f, 0f));
            material.SetVector(UnityEngine.Shader.PropertyToID("colossal_EdgeNoise"), new UnityEngine.Vector4(sourceRendered.m_EdgeNoise.x, sourceRendered.m_EdgeNoise.y, 0f, 0f));
            if (sourceBatchIndex >= 0 &&
                m_AreaBatchSystem.GetAreaBatch(sourceBatchIndex, out _, out _, out _, out UnityEngine.Material sourceMaterial, out _, out _, out _) &&
                sourceMaterial != null)
            {
                int areaParameters = UnityEngine.Shader.PropertyToID("colossal_AreaParameters");
                if (sourceMaterial.HasProperty(areaParameters))
                {
                    material.SetVector(areaParameters, sourceMaterial.GetVector(areaParameters));
                }
            }
            m_AppliedReplacementMaterials[targetPrefab] = material;
            m_AppliedReplacementMaterialSources[targetPrefab] = sourcePrefab;
            Mod.Log.InfoFormat(
                "Copied source surface edge material settings: source='{0}', target='{1}', batch={2}, edgeNormal={3}, fade={4}/{5}",
                m_PrefabSystem.GetPrefabName(sourcePrefab),
                m_PrefabSystem.GetPrefabName(targetPrefab),
                batchIndex,
                sourceRendered.m_EdgeNormal,
                sourceRendered.m_EdgeFadeRange.x,
                sourceRendered.m_EdgeFadeRange.y);
        }

        private bool TryGetDefaultSurfacePrefab(
            Entity area,
            Entity currentSurface,
            System.Random random,
            out Entity owner,
            out int slot,
            out Entity defaultPrefab,
            out bool matchesDefault)
        {
            owner = Entity.Null;
            slot = -1;
            defaultPrefab = Entity.Null;
            matchesDefault = false;
            if (!EntityManager.HasBuffer<Game.Areas.Node>(area))
            {
                return false;
            }

            DynamicBuffer<Game.Areas.Node> areaNodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
            Entity child = area;
            for (int depth = 0; depth < 16 && child != Entity.Null; depth++)
            {
                if (!EntityManager.HasComponent<Owner>(child))
                {
                    break;
                }

                Entity candidateOwner = EntityManager.GetComponentData<Owner>(child).m_Owner;
                if (candidateOwner == Entity.Null)
                {
                    break;
                }

                if (EntityManager.HasComponent<Game.Objects.Transform>(candidateOwner) && EntityManager.HasComponent<PrefabRef>(candidateOwner))
                {
                    Entity ownerPrefab = EntityManager.GetComponentData<PrefabRef>(candidateOwner).m_Prefab;
                    if (EntityManager.HasBuffer<PrefabSubArea>(ownerPrefab) && EntityManager.HasBuffer<PrefabSubAreaNode>(ownerPrefab))
                    {
                        DynamicBuffer<PrefabSubArea> prefabSubAreas = EntityManager.GetBuffer<PrefabSubArea>(ownerPrefab, true);
                        DynamicBuffer<PrefabSubAreaNode> prefabNodes = EntityManager.GetBuffer<PrefabSubAreaNode>(ownerPrefab, true);
                        Game.Objects.Transform ownerTransform = EntityManager.GetComponentData<Game.Objects.Transform>(candidateOwner);
                        int runtimeIndex = -1;
                        if (EntityManager.HasBuffer<Game.Areas.SubArea>(candidateOwner))
                        {
                            DynamicBuffer<Game.Areas.SubArea> runtimeSubAreas = EntityManager.GetBuffer<Game.Areas.SubArea>(candidateOwner, true);
                            for (int runtimeSlot = 0; runtimeSlot < runtimeSubAreas.Length; runtimeSlot++)
                            {
                                if (runtimeSubAreas[runtimeSlot].m_Area == area)
                                {
                                    runtimeIndex = runtimeSlot;
                                    break;
                                }
                            }
                        }

                        float bestDistance = float.MaxValue;
                        int bestSlot = -1;
                        Entity bestDefaultSurface = Entity.Null;
                        bool bestMatchesDefault = false;
                        for (int i = 0; i < prefabSubAreas.Length; i++)
                        {
                            int2 nodeRange = prefabSubAreas[i].m_NodeRange;
                            float distance = GetSubAreaGeometryDistance(areaNodes, ownerTransform, prefabNodes, nodeRange);
                            Mod.Log.InfoFormat(
                                "Revert geometry candidate: area={0}, owner={1}, ownerAsset='{2}', runtimeIndex={3}, slot={4}, areaNodes={5}, range=({6},{7}), meanBoundaryDistance={8:F3}m",
                                area,
                                candidateOwner,
                                m_PrefabSystem.GetPrefabName(ownerPrefab),
                                runtimeIndex,
                                i,
                                areaNodes.Length,
                                nodeRange.x,
                                nodeRange.y,
                                distance);
                            if (distance == float.MaxValue)
                            {
                                continue;
                            }

                            Entity candidatePrefab = prefabSubAreas[i].m_Prefab;
                            if (TryResolveConcreteSurface(candidatePrefab, currentSurface, random, out Entity concreteSurface, out bool candidateMatchesDefault) &&
                                (distance < bestDistance ||
                                 (math.abs(distance - bestDistance) <= 0.0001f && candidateMatchesDefault && !bestMatchesDefault)))
                            {
                                bestDistance = distance;
                                bestSlot = i;
                                bestDefaultSurface = concreteSurface;
                                bestMatchesDefault = candidateMatchesDefault;
                            }
                        }

                        if (bestSlot >= 0)
                        {
                            owner = candidateOwner;
                            slot = bestSlot;
                            defaultPrefab = bestDefaultSurface;
                            matchesDefault = bestMatchesDefault;
                            Mod.Log.InfoFormat(
                                "Revert geometry selected: area={0}, owner={1}, slot={2}, meanBoundaryDistance={3:F3}m, default='{4}'",
                                area,
                                owner,
                                slot,
                                bestDistance,
                                m_PrefabSystem.GetPrefabName(defaultPrefab));
                            return true;
                        }
                    }
                }

                child = candidateOwner;
            }

            return false;
        }

        private float GetSubAreaGeometryDistance(
            DynamicBuffer<Game.Areas.Node> areaNodes,
            Game.Objects.Transform ownerTransform,
            DynamicBuffer<PrefabSubAreaNode> prefabNodes,
            int2 nodeRange)
        {
            if (nodeRange.x < 0 || nodeRange.y <= nodeRange.x || nodeRange.y > prefabNodes.Length ||
                areaNodes.Length < 3)
            {
                return float.MaxValue;
            }

            int prefabNodeCount = nodeRange.y - nodeRange.x;
            float totalDistance = 0f;
            for (int i = 0; i < areaNodes.Length; i++)
            {
                totalDistance += GetPointToPrefabPolygonDistance(
                    areaNodes[i].m_Position.xz,
                    ownerTransform,
                    prefabNodes,
                    nodeRange,
                    prefabNodeCount);
            }

            for (int i = 0; i < prefabNodeCount; i++)
            {
                float2 point = ObjectUtils.LocalToWorld(
                    ownerTransform,
                    prefabNodes[nodeRange.x + i].m_Position).xz;
                totalDistance += GetPointToAreaPolygonDistance(point, areaNodes);
            }

            return totalDistance / (areaNodes.Length + prefabNodeCount);
        }

        private static float GetPointToPrefabPolygonDistance(
            float2 point,
            Game.Objects.Transform transform,
            DynamicBuffer<PrefabSubAreaNode> nodes,
            int2 nodeRange,
            int count)
        {
            float bestDistanceSquared = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                float2 a = ObjectUtils.LocalToWorld(transform, nodes[nodeRange.x + i].m_Position).xz;
                float2 b = ObjectUtils.LocalToWorld(transform, nodes[nodeRange.x + (i + 1) % count].m_Position).xz;
                bestDistanceSquared = math.min(bestDistanceSquared, DistanceToSegmentSquared(point, a, b));
            }

            return math.sqrt(bestDistanceSquared);
        }

        private static float GetPointToAreaPolygonDistance(float2 point, DynamicBuffer<Game.Areas.Node> nodes)
        {
            float bestDistanceSquared = float.MaxValue;
            for (int i = 0; i < nodes.Length; i++)
            {
                float2 a = nodes[i].m_Position.xz;
                float2 b = nodes[(i + 1) % nodes.Length].m_Position.xz;
                bestDistanceSquared = math.min(bestDistanceSquared, DistanceToSegmentSquared(point, a, b));
            }

            return math.sqrt(bestDistanceSquared);
        }

        private static float DistanceToSegmentSquared(float2 point, float2 a, float2 b)
        {
            float2 edge = b - a;
            float edgeLengthSquared = math.lengthsq(edge);
            float t = edgeLengthSquared > 0f
                ? math.saturate(math.dot(point - a, edge) / edgeLengthSquared)
                : 0f;
            return math.distancesq(point, a + edge * t);
        }

        private bool TryResolveConcreteSurface(
            Entity assetSurface,
            Entity currentSurface,
            System.Random random,
            out Entity concreteSurface,
            out bool matchesDefault)
        {
            concreteSurface = Entity.Null;
            matchesDefault = false;
            if (assetSurface == Entity.Null)
            {
                return false;
            }

            if (EntityManager.HasBuffer<PlaceholderObjectElement>(assetSurface))
            {
                DynamicBuffer<PlaceholderObjectElement> variants = EntityManager.GetBuffer<PlaceholderObjectElement>(assetSurface, true);
                List<KeyValuePair<Entity, int>> candidates = new List<KeyValuePair<Entity, int>>();
                int totalWeight = 0;
                bool currentIsVariant = false;
                for (int i = 0; i < variants.Length; i++)
                {
                    Entity variant = variants[i].m_Object;
                    if (variant == Entity.Null || !EntityManager.HasComponent<SurfaceData>(variant) ||
                        IsExcludedSurfaceName(m_PrefabSystem.GetPrefabName(variant)))
                    {
                        continue;
                    }

                    int weight = EntityManager.HasComponent<SpawnableObjectData>(variant)
                        ? Math.Max(0, EntityManager.GetComponentData<SpawnableObjectData>(variant).m_Probability)
                        : 1;
                    if (weight == 0)
                    {
                        continue;
                    }

                    candidates.Add(new KeyValuePair<Entity, int>(variant, weight));
                    totalWeight += weight;
                    currentIsVariant |= variant == currentSurface;
                }

                if (candidates.Count == 0)
                {
                    return false;
                }

                if (currentIsVariant)
                {
                    concreteSurface = currentSurface;
                    matchesDefault = true;
                    return true;
                }

                int choice = random.Next(totalWeight);
                foreach (KeyValuePair<Entity, int> candidate in candidates)
                {
                    if (choice < candidate.Value)
                    {
                        concreteSurface = candidate.Key;
                        return true;
                    }

                    choice -= candidate.Value;
                }

                concreteSurface = candidates[candidates.Count - 1].Key;
                return true;
            }

            if (EntityManager.HasComponent<SurfaceData>(assetSurface) &&
                !IsExcludedSurfaceName(m_PrefabSystem.GetPrefabName(assetSurface)))
            {
                concreteSurface = assetSurface;
                matchesDefault = assetSurface == currentSurface;
                return true;
            }

            return false;
        }

        private void ProcessPendingRevertRefresh()
        {
            if (m_PendingRevertChanges.Count == 0)
            {
                return;
            }

            int applied = 0;
            int missing = 0;
            foreach (PendingRevertChange change in m_PendingRevertChanges)
            {
                if (!EntityManager.Exists(change.Area) || !EntityManager.HasComponent<PrefabRef>(change.Area))
                {
                    missing++;
                    continue;
                }

                if (EntityManager.GetComponentData<PrefabRef>(change.Area).m_Prefab == change.DefaultPrefab)
                {
                    applied++;
                }
            }

            if (applied == 0 && missing < m_PendingRevertChanges.Count)
            {
                return;
            }

            Mod.Log.InfoFormat(
                "Revert All prefab updates applied: applied={0}, missing={1}; rebuilding area render batches",
                applied,
                missing);
            RebuildAreaBatchCaches();
            m_PendingRevertChanges.Clear();
        }

        private void ProcessReplacement(ReplacementRequest request)
        {
            long operationId = ++m_NextOperationId;
            bool replaceSingleRule = !string.IsNullOrWhiteSpace(request.RuleId);
            int selectedRuleId = 0;
            if (replaceSingleRule && !int.TryParse(request.RuleId, out selectedRuleId))
            {
                Mod.Log.WarnFormat("Surface replacement ignored: invalid rule id '{0}'", request.RuleId);
                return;
            }

            Dictionary<string, Entity> surfaceEntitiesById = new Dictionary<string, Entity>(StringComparer.Ordinal);
            Dictionary<Entity, string> surfaceIdsByEntity = new Dictionary<Entity, string>();
            using (NativeArray<Entity> prefabEntities = m_SurfacePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefabEntity in prefabEntities)
                {
                    if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    bool isActualSurface = EntityManager.HasComponent<SurfaceData>(prefabEntity);
                    if (!isActualSurface)
                    {
                        continue;
                    }

                    string id = prefab.GetPrefabID().ToString();
                    if (IsExcludedSurfaceName(prefab.name) || surfaceEntitiesById.ContainsKey(id))
                    {
                        continue;
                    }

                    surfaceEntitiesById.Add(id, prefabEntity);
                    surfaceIdsByEntity[prefabEntity] = id;
                }
            }

            List<SurfaceReplacementRule> rules;
            try
            {
                rules = JSON.MakeInto<List<SurfaceReplacementRule>>(JSON.Load(request.RulesJson)) ??
                        new List<SurfaceReplacementRule>();
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Failed to read surface replacement rules");
                return;
            }

            Mod.Log.InfoFormat(
                "Surface replacement started: mode={0}, ruleId={1}, ruleCount={2}, scope={3}, surfaceAreaCount={4}, lotAreaCount={5}, catalogSurfaceCount={6}",
                replaceSingleRule ? "single rule" : "Replace All",
                replaceSingleRule ? selectedRuleId.ToString() : "all enabled",
                rules.Count,
                Mod.Settings?.ReplacementScope ?? "assets",
                m_SurfaceAreaQuery.CalculateEntityCount(),
                m_SpecialIndustryAreaQuery.CalculateEntityCount(),
                surfaceEntitiesById.Count);

            LogSpecialIndustryAreaInventory(operationId);

            foreach (SurfaceReplacementRule rule in rules)
            {
                if (rule == null)
                {
                    continue;
                }

                bool selected = replaceSingleRule ? rule.id == selectedRuleId : rule.enabled;
                if (!selected)
                {
                    if (!replaceSingleRule)
                    {
                        Mod.Log.InfoFormat("Replacement rule skipped: id={0}, enabled=false", rule.id);
                    }
                    continue;
                }

                Mod.Log.InfoFormat(
                    "Replacement rule selected: id={0}, enabled={1}, source=[{2}], target=[{3}]",
                    rule.id,
                    rule.enabled,
                    DescribeSurfaceIds(rule.sourceSurfaceIds, surfaceEntitiesById),
                    DescribeSurfaceIds(rule.targetSurfaceIds, surfaceEntitiesById));

                if (rule.sourceSurfaceIds == null || rule.targetSurfaceIds == null)
                {
                    Mod.Log.WarnFormat("Replacement rule skipped: id={0} has a null source or target list", rule.id);
                    continue;
                }

            }

            Dictionary<string, HashSet<string>> targetIdsBySource = ReplacementRuleCatalog.BuildSurfaceTargetIds(
                rules,
                surfaceEntitiesById,
                enabledOnly: !replaceSingleRule,
                selectedRuleId: replaceSingleRule ? selectedRuleId : (int?)null);

            Dictionary<string, List<Entity>> replacementsBySource = new Dictionary<string, List<Entity>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, HashSet<string>> pair in targetIdsBySource)
            {
                List<Entity> targets = pair.Value
                    .Where(id => id != pair.Key && surfaceEntitiesById.ContainsKey(id))
                    .Select(id => surfaceEntitiesById[id])
                    .ToList();
                if (targets.Count > 0)
                {
                    replacementsBySource[pair.Key] = targets;
                    Mod.Log.InfoFormat(
                        "Replacement mapping built: source='{0}' -> [{1}]",
                        GetSurfaceDisplayName(pair.Key, surfaceEntitiesById),
                        string.Join(", ", targets.Select(m_PrefabSystem.GetPrefabName)));
                }
                else
                {
                    Mod.Log.WarnFormat("Replacement mapping unusable: source='{0}' has no distinct valid target", GetSurfaceDisplayName(pair.Key, surfaceEntitiesById));
                }
            }

            if (replacementsBySource.Count == 0)
            {
                Mod.Log.Warn("Surface replacement stopped: no usable source-to-target mappings were built");
                return;
            }

            System.Random random = new System.Random();
            EntityCommandBuffer updateCommandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            bool hasUpdateCommandBuffer = false;
            int scanned = 0;
            int unmatched = 0;
            int skippedOutOfScopeAreas = 0;
            int queued = 0;
            string replacementScope = Mod.Settings?.ReplacementScope ?? "assets";
            bool includeAssets = replacementScope == "assets" || replacementScope == "all";
            bool includeManualAreas = replacementScope == "hand-drawn-surfaces" || replacementScope == "all";
            HashSet<string> scannedAssets = new HashSet<string>(StringComparer.Ordinal);
            HashSet<Entity> scheduledAreas = new HashSet<Entity>();
            Dictionary<string, List<string>> surfacesByAsset = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (NativeArray<Entity> areaEntities = m_SurfaceAreaQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity areaEntity in areaEntities)
                {
                    scanned++;
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(areaEntity);
                    Entity buildingEntity = GetOwningBuildingEntity(areaEntity);
                    Entity specialIndustryOwner = FindSpecialIndustryOwner(areaEntity);
                    if (IsIndustryExtractionArea(areaEntity, specialIndustryOwner))
                    {
                        skippedOutOfScopeAreas++;
                        Mod.Log.InfoFormat(
                            "Surface area delegated to special-industry lot scan: area={0}, industry={1}, prefab='{2}'",
                            areaEntity,
                            specialIndustryOwner,
                            m_PrefabSystem.GetPrefabName(prefabRef.m_Prefab));
                        continue;
                    }
                    bool isAssetSurface = buildingEntity != Entity.Null;
                    if ((isAssetSurface && !includeAssets) || (!isAssetSurface && !includeManualAreas))
                    {
                        skippedOutOfScopeAreas++;
                        continue;
                    }

                    string assetName = buildingEntity != Entity.Null
                        ? GetAssetName(buildingEntity)
                        : GetAssetName(Entity.Null);
                    scannedAssets.Add(assetName);
                    string surfaceName = surfaceIdsByEntity.TryGetValue(prefabRef.m_Prefab, out string detectedSurfaceId)
                        ? GetSurfaceDisplayName(detectedSurfaceId, surfaceEntitiesById)
                        : m_PrefabSystem.GetPrefabName(prefabRef.m_Prefab) + " [unresolved prefab entity=" + prefabRef.m_Prefab + "]";
                    if (!surfacesByAsset.TryGetValue(assetName, out List<string> assetSurfaces))
                    {
                        assetSurfaces = new List<string>();
                        surfacesByAsset.Add(assetName, assetSurfaces);
                    }
                    assetSurfaces.Add("areaEntity=" + areaEntity + ", surfaceEntity=" + prefabRef.m_Prefab + ", surface='" + surfaceName + "'");

                    if (!surfaceIdsByEntity.TryGetValue(prefabRef.m_Prefab, out string sourceId))
                    {
                        unmatched++;
                        Mod.Log.InfoFormat(
                            "Surface area scanned: asset='{0}', areaEntity={1}, surfacePrefab='{2}', result=no catalog match",
                            assetName,
                            areaEntity,
                            m_PrefabSystem.GetPrefabName(prefabRef.m_Prefab));
                        continue;
                    }

                    string sourceName = GetSurfaceDisplayName(sourceId, surfaceEntitiesById);
                    if (!replacementsBySource.TryGetValue(sourceId, out List<Entity> targets))
                    {
                        unmatched++;
                        Mod.Log.InfoFormat(
                            "Surface area scanned: asset='{0}', areaEntity={1}, surface='{2}', result=no matching rule",
                            assetName,
                            areaEntity,
                            sourceName);
                        continue;
                    }

                    Entity replacement = targets[random.Next(targets.Count)];
                    LogSurfaceGeometry("button-before", areaEntity, prefabRef.m_Prefab);
                    Entity ownerEntity = EntityManager.HasComponent<Owner>(areaEntity)
                        ? EntityManager.GetComponentData<Owner>(areaEntity).m_Owner
                        : Entity.Null;
                    ApplySurfacePrefabReplacement(
                        areaEntity,
                        replacement,
                        updateCommandBuffer,
                        sourceNameOverride: sourceName);
                    hasUpdateCommandBuffer = true;
                    scheduledAreas.Add(areaEntity);

                    queued++;
                    Mod.Log.InfoFormat(
                        "Surface prefab replacement scheduled: asset='{0}', ownerEntity={1}, areaEntity={2}, areaOwner={3}, source='{4}', replacement='{5}', candidateCount={6}",
                        assetName,
                        buildingEntity,
                        areaEntity,
                        ownerEntity,
                        sourceName,
                        m_PrefabSystem.GetPrefabName(replacement),
                        targets.Count);
                }
            }

            foreach (KeyValuePair<string, List<string>> asset in surfacesByAsset)
            {
                Mod.Log.InfoFormat(
                    "Surface inventory before replacement: asset='{0}', areaCount={1}, surfaces=[{2}]",
                    asset.Key,
                    asset.Value.Count,
                    string.Join("; ", asset.Value));
            }

            Mod.Log.InfoFormat(
                "Surface replacement scan finished: scannedSurfaceAreas={0}, assetsWithSurfaceAreas={1}, skippedOutOfScopeAreas={2}, unmatchedSurfaceAreas={3}, replacementsQueued={4}, sourceMappings={5}",
                scanned,
                scannedAssets.Count,
                skippedOutOfScopeAreas,
                unmatched,
                queued,
                replacementsBySource.Count);
            if (hasUpdateCommandBuffer)
            {
                Mod.Log.InfoFormat(
                    "Surface prefab update batch submitted: operationId={0}, areaCount={1}",
                    operationId,
                    queued);
            }
        }

        private void ProcessIndustryAreaReplacement(ReplacementRequest request)
        {
            long operationId = ++m_NextOperationId;
            bool replaceSingleRule = !string.IsNullOrWhiteSpace(request.RuleId);
            if (replaceSingleRule && !int.TryParse(request.RuleId, out int selectedRuleId))
            {
                Mod.Log.WarnFormat("Special-industry area replacement ignored: invalid rule id '{0}'", request.RuleId);
                return;
            }
            int selectedId = replaceSingleRule ? int.Parse(request.RuleId) : 0;

            List<AreaReplacementRule> rules;
            try
            {
                rules = JSON.MakeInto<List<AreaReplacementRule>>(JSON.Load(request.RulesJson)) ?? new List<AreaReplacementRule>();
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Failed to read special-industry area replacement rules");
                return;
            }

            Dictionary<string, Entity> surfaceEntitiesById = new Dictionary<string, Entity>(StringComparer.Ordinal);
            using (NativeArray<Entity> prefabEntities = m_SurfacePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefabEntity in prefabEntities)
                {
                    if (!EntityManager.HasComponent<SurfaceData>(prefabEntity) ||
                        !m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase prefab) || prefab == null ||
                        IsExcludedSurfaceName(prefab.name))
                    {
                        continue;
                    }

                    string id = prefab.GetPrefabID().ToString();
                    if (!surfaceEntitiesById.ContainsKey(id))
                    {
                        surfaceEntitiesById.Add(id, prefabEntity);
                    }
                }
            }

            Dictionary<string, HashSet<Entity>> targetsByIndustryId = new Dictionary<string, HashSet<Entity>>(StringComparer.Ordinal);
            foreach (AreaReplacementRule rule in rules)
            {
                if (rule == null || (replaceSingleRule ? rule.id != selectedId : !rule.enabled))
                {
                    continue;
                }

                Mod.Log.InfoFormat(
                    "Special-industry area rule selected: id={0}, enabled={1}, sourceIndustries=[{2}], targets=[{3}]",
                    rule.id,
                    rule.enabled,
                    string.Join(", ", rule.sourceAreaIds ?? new List<string>()),
                    string.Join(", ", (rule.targetSurfaceIds ?? new List<string>()).Select(id => surfaceEntitiesById.TryGetValue(id, out Entity entity) ? m_PrefabSystem.GetPrefabName(entity) : id)));

                if (rule.sourceAreaIds == null || rule.targetSurfaceIds == null)
                {
                    continue;
                }

                foreach (string industryId in rule.sourceAreaIds.Where(id => !string.IsNullOrEmpty(id)))
                {
                    if (!targetsByIndustryId.TryGetValue(industryId, out HashSet<Entity> targets))
                    {
                        targets = new HashSet<Entity>();
                        targetsByIndustryId.Add(industryId, targets);
                    }

                    foreach (string targetId in rule.targetSurfaceIds)
                    {
                        if (!string.IsNullOrEmpty(targetId) && surfaceEntitiesById.TryGetValue(targetId, out Entity target))
                        {
                            targets.Add(target);
                        }
                    }
                }
            }

            Dictionary<string, List<Entity>> replacementTargets = targetsByIndustryId
                .Select(pair => new { pair.Key, Targets = pair.Value.ToList() })
                .Where(pair => pair.Targets.Count > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Targets, StringComparer.Ordinal);
            if (replacementTargets.Count == 0)
            {
                Mod.Log.Warn("Special-industry area replacement stopped: no usable industry-to-surface mappings were built");
                return;
            }

            System.Random random = new System.Random();
            EntityCommandBuffer updateCommandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            bool hasUpdateCommandBuffer = false;
            int industriesScanned = 0;
            int areasQueued = 0;
            using (NativeArray<Entity> lotAreas = m_SpecialIndustryAreaQuery.ToEntityArray(Allocator.Temp))
            {
                HashSet<Entity> processedIndustryRoots = new HashSet<Entity>();
                foreach (Entity lotArea in lotAreas)
                {
                    Entity industryRoot = FindSpecialIndustryOwner(lotArea);
                    if (industryRoot == Entity.Null || !processedIndustryRoots.Add(industryRoot))
                    {
                        continue;
                    }

                    Entity industryLotPrefab = GetOriginalIndustryLotPrefab(industryRoot);
                    if (industryLotPrefab == Entity.Null || !EntityManager.HasComponent<ExtractorAreaData>(industryLotPrefab))
                    {
                        continue;
                    }

                    Entity industryBuildingPrefab = GetSpecialIndustryBuildingPrefab(industryRoot);
                    string buildingPrefabId = GetPrefabIdString(industryBuildingPrefab);
                    string lotPrefabId = GetPrefabIdString(industryLotPrefab);
                    List<Entity> targetCandidates = new List<Entity>();
                    if (!string.IsNullOrEmpty(buildingPrefabId) && replacementTargets.TryGetValue(buildingPrefabId, out List<Entity> buildingTargets))
                    {
                        targetCandidates.AddRange(buildingTargets);
                    }
                    if (!string.IsNullOrEmpty(lotPrefabId) && replacementTargets.TryGetValue(lotPrefabId, out List<Entity> lotTargets))
                    {
                        targetCandidates.AddRange(lotTargets);
                    }
                    targetCandidates = targetCandidates.Distinct().ToList();
                    if (targetCandidates.Count == 0 || !TryGetManagedPrefab(industryLotPrefab, out PrefabBase industryLotAsset))
                    {
                        continue;
                    }

                    industriesScanned++;
                    bool sharedPrefabAlreadyModified = m_SpecialIndustryPrefabSnapshots.ContainsKey(industryLotAsset);
                    List<Entity> renderableChildren = GetRenderableIndustrySubAreas(industryRoot);
                    bool rootHasRenderedBatch = TryGetRenderedAreaBatchIndex(industryLotPrefab, industryRoot, out _);
                    bool rootStoresSurfaceBatch = sharedPrefabAlreadyModified || (rootHasRenderedBatch && renderableChildren.Count == 0);
                    Entity currentOverlayTarget = EntityManager.HasComponent<SpecialIndustrySurfaceOverlayState>(industryRoot)
                        ? EntityManager.GetComponentData<SpecialIndustrySurfaceOverlayState>(industryRoot).TargetSurfacePrefab
                        : Entity.Null;
                    List<Entity> eligibleTargets = rootStoresSurfaceBatch
                        ? targetCandidates.Where(target => target != currentOverlayTarget).ToList()
                        : targetCandidates.Where(target => renderableChildren.Any(child =>
                            !IsUsingSpecialIndustryTarget(child, target) &&
                            (!EntityManager.HasComponent<PrefabRef>(child) || EntityManager.GetComponentData<PrefabRef>(child).m_Prefab != target))).ToList();
                    if (sharedPrefabAlreadyModified)
                    {
                        Entity previousTarget = m_SpecialIndustryPrefabSnapshots[industryLotAsset].CurrentTargetSurface;
                        eligibleTargets = eligibleTargets.Where(target => target != previousTarget).ToList();
                    }
                    if (eligibleTargets.Count == 0)
                    {
                        continue;
                    }

                    Entity targetSurface = eligibleTargets[random.Next(eligibleTargets.Count)];
                    string sourceLabel = !string.IsNullOrEmpty(buildingPrefabId) ? m_PrefabSystem.GetPrefabName(industryBuildingPrefab) : m_PrefabSystem.GetPrefabName(industryLotPrefab);
                    if (rootStoresSurfaceBatch)
                    {
                        if (!m_IndustryAreasScheduledThisFrame.Add(industryRoot) ||
                            !TryQueueIndustrySurfaceOverlay(industryRoot, industryLotPrefab, targetSurface, updateCommandBuffer, operationId))
                        {
                            continue;
                        }

                        Mod.Log.InfoFormat(
                            "Special-industry extraction surface overlay queued: operation={0}, industry={1}, type='{2}', lot='{3}', mapFeature={4}, target='{5}'",
                            operationId,
                            industryRoot,
                            sourceLabel,
                            industryLotAsset.name,
                            EntityManager.GetComponentData<ExtractorAreaData>(industryLotPrefab).m_MapFeature,
                            m_PrefabSystem.GetPrefabName(targetSurface));
                    }
                    else
                    {
                        foreach (Entity childArea in renderableChildren)
                        {
                            if (!m_IndustryAreasScheduledThisFrame.Add(childArea))
                            {
                                continue;
                            }

                            Entity originalPrefab = EntityManager.HasComponent<SpecialIndustryLotReplacement>(childArea)
                                ? EntityManager.GetComponentData<SpecialIndustryLotReplacement>(childArea).OriginalPrefab
                                : GetOriginalIndustryLotPrefab(childArea);
                            PrefabRef oldPrefabRef = EntityManager.GetComponentData<PrefabRef>(childArea);
                            if (originalPrefab == Entity.Null)
                            {
                                originalPrefab = oldPrefabRef.m_Prefab;
                            }

                            ApplySurfacePrefabReplacement(
                                childArea,
                                originalPrefab,
                                targetSurface,
                                updateCommandBuffer,
                                sourceLabel,
                                m_PrefabSystem.GetPrefabName(targetSurface),
                                originalPrefab);
                            if (EntityManager.HasComponent<SpecialIndustrySurfaceOverlayState>(industryRoot))
                            {
                                updateCommandBuffer.SetComponent(industryRoot, new SpecialIndustrySurfaceOverlayState
                                {
                                    OriginalIndustryPrefab = industryLotPrefab,
                                    TargetSurfacePrefab = targetSurface,
                                });
                            }
                            areasQueued++;
                            hasUpdateCommandBuffer = true;
                        }
                    }

                    if (rootStoresSurfaceBatch)
                    {
                        hasUpdateCommandBuffer = true;
                        areasQueued++;
                    }
                }
            }

            Mod.Log.InfoFormat(
                "Special-industry area replacement finished: operation={0}, ruleCount={1}, sourceIndustries={2}, industriesScanned={3}, areasQueued={4}",
                operationId,
                rules.Count,
                replacementTargets.Count,
                industriesScanned,
                areasQueued);
            if (hasUpdateCommandBuffer)
            {
                Mod.Log.InfoFormat("Special-industry area update batch submitted: operation={0}, areas={1}", operationId, areasQueued);
            }
        }

        private void LogSurfaceGeometry(string path, Entity area, Entity prefab)
        {
            Entity building = GetOwningBuildingEntity(area);
            string nodes = "<none>";
            string offsets = "<none>";
            if (EntityManager.HasBuffer<Game.Areas.Node>(area))
            {
                DynamicBuffer<Game.Areas.Node> buffer = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                nodes = string.Join("; ", Enumerable.Range(0, buffer.Length).Select(i => buffer[i].m_Position.ToString()));
            }
            if (EntityManager.HasBuffer<Game.Areas.Expand>(area))
            {
                DynamicBuffer<Game.Areas.Expand> buffer = EntityManager.GetBuffer<Game.Areas.Expand>(area, true);
                offsets = string.Join("; ", Enumerable.Range(0, buffer.Length).Select(i => buffer[i].m_Offset.ToString()));
            }
            using (NativeArray<ComponentType> components = EntityManager.GetComponentTypes(area, Allocator.Temp))
            {
                Mod.Log.InfoFormat(
                    "Surface geometry comparison: path={0}, area={1}, building={2}, asset='{3}', prefab='{4}', components=[{5}], nodes=[{6}], expansionOffsets=[{7}]",
                    path, area, building, GetAssetName(building), m_PrefabSystem.GetPrefabName(prefab),
                    string.Join(", ", components.ToArray().Select(component => component.ToString())), nodes, offsets);
            }
        }

        private void QueueBatchDiagnostic(Entity areaEntity, Entity sourcePrefab, Entity targetPrefab, string sourceName, string targetName)
        {
            int expectedBatchIndex = EntityManager.HasComponent<RenderedAreaData>(targetPrefab)
                ? EntityManager.GetComponentData<RenderedAreaData>(targetPrefab).m_BatchIndex
                : -1;
            int previousBatchIndex = EntityManager.HasComponent<Batch>(areaEntity)
                ? EntityManager.GetComponentData<Batch>(areaEntity).m_BatchIndex
                : -1;
            int sourceBatchIndex = EntityManager.HasComponent<RenderedAreaData>(sourcePrefab)
                ? EntityManager.GetComponentData<RenderedAreaData>(sourcePrefab).m_BatchIndex
                : -1;
            float sourceExpandAmount = EntityManager.HasComponent<RenderedAreaData>(sourcePrefab)
                ? EntityManager.GetComponentData<RenderedAreaData>(sourcePrefab).m_ExpandAmount
                : -1f;
            float targetExpandAmount = EntityManager.HasComponent<RenderedAreaData>(targetPrefab)
                ? EntityManager.GetComponentData<RenderedAreaData>(targetPrefab).m_ExpandAmount
                : -1f;
            int expandCount = EntityManager.HasBuffer<Game.Areas.Expand>(areaEntity)
                ? EntityManager.GetBuffer<Game.Areas.Expand>(areaEntity, true).Length
                : 0;
            if (m_PendingBatchDiagnostics.Any(diagnostic =>
                    diagnostic.Area == areaEntity &&
                    diagnostic.ExpectedPrefab == targetPrefab &&
                    diagnostic.SourceBatchIndex == sourceBatchIndex))
            {
                return;
            }

            m_PendingBatchDiagnostics.Add(new PendingBatchDiagnostic
            {
                Area = areaEntity,
                ExpectedPrefab = targetPrefab,
                ExpectedBatchIndex = expectedBatchIndex,
                SourceBatchIndex = sourceBatchIndex,
                PreviousBatchIndex = previousBatchIndex,
                SourceName = sourceName,
                TargetName = targetName,
            });
            Mod.Log.InfoFormat(
                "Area batch diagnostic queued: area={0}, source='{1}', sourcePrefabBatch={2}, sourceExpand={3}, target='{4}', targetPrefabBatch={5}, targetExpand={6}, previousAreaBatch={7}, expandCount={8}",
                areaEntity,
                sourceName,
                sourceBatchIndex,
                sourceExpandAmount,
                targetName,
                expectedBatchIndex,
                targetExpandAmount,
                previousBatchIndex,
                expandCount);
        }

        private void LogPendingBatchDiagnostics()
        {
            bool rebuildAreaBatchCaches = false;
            for (int i = m_PendingBatchDiagnostics.Count - 1; i >= 0; i--)
            {
                PendingBatchDiagnostic diagnostic = m_PendingBatchDiagnostics[i];
                diagnostic.FramesChecked++;
                if (!EntityManager.Exists(diagnostic.Area))
                {
                    Mod.Log.WarnFormat(
                        "Area batch diagnostic stopped: area no longer exists; area={0}, target='{1}'",
                        diagnostic.Area,
                        diagnostic.TargetName);
                    m_PendingBatchDiagnostics.RemoveAt(i);
                    continue;
                }

                Entity currentPrefab = EntityManager.HasComponent<PrefabRef>(diagnostic.Area)
                    ? EntityManager.GetComponentData<PrefabRef>(diagnostic.Area).m_Prefab
                    : Entity.Null;
                string currentPrefabName = currentPrefab != Entity.Null
                    ? m_PrefabSystem.GetPrefabName(currentPrefab)
                    : "<no PrefabRef>";
                int currentPrefabBatch = currentPrefab != Entity.Null && EntityManager.HasComponent<RenderedAreaData>(currentPrefab)
                    ? EntityManager.GetComponentData<RenderedAreaData>(currentPrefab).m_BatchIndex
                    : -1;
                bool hasSpecialIndustryMarker = EntityManager.HasComponent<SpecialIndustryLotReplacement>(diagnostic.Area);
                bool currentPrefabIsSurface = currentPrefab != Entity.Null && EntityManager.HasComponent<SurfaceData>(currentPrefab);
                bool canRebuildAreaBatch = !hasSpecialIndustryMarker || currentPrefabIsSurface;
                bool hasBatch = EntityManager.HasComponent<Batch>(diagnostic.Area);
                Batch batch = hasBatch ? EntityManager.GetComponentData<Batch>(diagnostic.Area) : default(Batch);
                bool hasUpdated = EntityManager.HasComponent<Updated>(diagnostic.Area);
                bool prefabMatches = currentPrefab == diagnostic.ExpectedPrefab;
                // PrefabSystem assigns a batch to newly registered visual variants
                // asynchronously. The value captured when queuing can be stale (often 0).
                if (prefabMatches && currentPrefabBatch >= 0)
                {
                    diagnostic.ExpectedBatchIndex = currentPrefabBatch;
                }
                bool batchMatches = hasBatch && batch.m_BatchIndex == diagnostic.ExpectedBatchIndex;
                Mod.Log.InfoFormat(
                    "Area batch diagnostic: area={0}, check={1}, currentPrefab='{2}', currentPrefabBatch={3}, expectedPrefab='{4}', expectedBatch={5}, actualBatch={6}, allocated={7}, visible={8}, hasUpdated={9}, prefabMatches={10}, batchMatches={11}",
                    diagnostic.Area,
                    diagnostic.FramesChecked,
                    currentPrefabName,
                    currentPrefabBatch,
                    diagnostic.TargetName,
                    diagnostic.ExpectedBatchIndex,
                    hasBatch ? batch.m_BatchIndex : -1,
                    hasBatch ? batch.m_AllocatedSize : 0,
                    hasBatch ? batch.m_VisibleCount : 0,
                    hasUpdated,
                    prefabMatches,
                    batchMatches);

                if (prefabMatches && !batchMatches && diagnostic.ExpectedBatchIndex >= 0)
                {
                    if (canRebuildAreaBatch && !diagnostic.RebuildAttempted)
                    {
                        diagnostic.RebuildAttempted = true;
                        diagnostic.FramesChecked = 0;
                        rebuildAreaBatchCaches = true;
                        Mod.Log.WarnFormat(
                            "Area batch index is stale after surface replacement; scheduling AreaBatchSystem rebuild: area={0}, expectedBatch={1}, actualBatch={2}",
                            diagnostic.Area,
                            diagnostic.ExpectedBatchIndex,
                            hasBatch ? batch.m_BatchIndex : -1);
                        continue;
                    }

                    if (diagnostic.FramesChecked < 8)
                    {
                        continue;
                    }

                    Mod.Log.WarnFormat(
                        "Area batch index did not converge to its registered prefab batch: area={0}, expectedBatch={1}, actualBatch={2}; leaving AreaBatchSystem state intact",
                        diagnostic.Area,
                        diagnostic.ExpectedBatchIndex,
                        hasBatch ? batch.m_BatchIndex : -1);
                    m_PendingBatchDiagnostics.RemoveAt(i);
                    continue;
                }

                if (prefabMatches && batchMatches)
                {
                    bool sourceContainsArea = LogBatchMembership(diagnostic);
                    bool targetContainsArea = TryBatchContainsArea(diagnostic.ExpectedBatchIndex, diagnostic.Area, out _);
                    if (sourceContainsArea || !targetContainsArea)
                    {
                        if (canRebuildAreaBatch && !diagnostic.RebuildAttempted)
                        {
                            diagnostic.RebuildAttempted = true;
                            diagnostic.FramesChecked = 0;
                            rebuildAreaBatchCaches = true;
                            Mod.Log.WarnFormat(
                                "Area batch membership is stale after surface replacement; scheduling AreaBatchSystem rebuild: area={0}, sourceBatch={1}, targetBatch={2}, sourceContainsArea={3}, targetContainsArea={4}",
                                diagnostic.Area,
                                diagnostic.SourceBatchIndex,
                                diagnostic.ExpectedBatchIndex,
                                sourceContainsArea,
                                targetContainsArea);
                            continue;
                        }

                        if (diagnostic.FramesChecked < 8)
                        {
                            continue;
                        }

                        Mod.Log.WarnFormat(
                            "Area batch membership did not settle: area={0}, sourceBatch={1}, targetBatch={2}, sourceContainsArea={3}, targetContainsArea={4}; leaving AreaBatchSystem state intact",
                            diagnostic.Area,
                            diagnostic.SourceBatchIndex,
                            diagnostic.ExpectedBatchIndex,
                            sourceContainsArea,
                            targetContainsArea);
                        m_PendingBatchDiagnostics.RemoveAt(i);
                        continue;
                    }

                    Mod.Log.InfoFormat(
                        "Area batch membership successfully transferred: area={0}, sourceBatch={1}, target='{2}', batch={3}",
                        diagnostic.Area,
                        diagnostic.SourceBatchIndex,
                        diagnostic.TargetName,
                        diagnostic.ExpectedBatchIndex);
                    m_PendingBatchDiagnostics.RemoveAt(i);
                }
                else if (diagnostic.FramesChecked >= 8)
                {
                    Mod.Log.WarnFormat(
                        "Area batch diagnostic timed out: area={0}, source='{1}', target='{2}', expectedBatch={3}, previousBatch={4}",
                        diagnostic.Area,
                        diagnostic.SourceName,
                        diagnostic.TargetName,
                        diagnostic.ExpectedBatchIndex,
                        diagnostic.PreviousBatchIndex);
                    m_PendingBatchDiagnostics.RemoveAt(i);
                }
            }

            if (rebuildAreaBatchCaches)
            {
                RebuildAreaBatchCaches();
            }

        }

        private bool LogBatchMembership(PendingBatchDiagnostic diagnostic)
        {
            try
            {
                string sourceMaterial = GetAreaBatchMaterialName(diagnostic.SourceBatchIndex);
                string targetMaterial = GetAreaBatchMaterialName(diagnostic.ExpectedBatchIndex);
                bool sourceContainsArea = TryBatchContainsArea(diagnostic.SourceBatchIndex, diagnostic.Area, out int sourceAreaCount);
                bool targetContainsArea = TryBatchContainsArea(diagnostic.ExpectedBatchIndex, diagnostic.Area, out int targetAreaCount);
                Mod.Log.InfoFormat(
                    "Area batch membership: area={0}, sourceBatch={1}, sourceMaterial='{2}', sourceAreaCount={3}, sourceContainsArea={4}, targetBatch={5}, targetMaterial='{6}', targetAreaCount={7}, targetContainsArea={8}",
                    diagnostic.Area,
                    diagnostic.SourceBatchIndex,
                    sourceMaterial,
                    sourceAreaCount,
                    sourceContainsArea,
                    diagnostic.ExpectedBatchIndex,
                    targetMaterial,
                    targetAreaCount,
                    targetContainsArea);
                return sourceContainsArea;
            }
            catch (Exception exception)
            {
                Mod.Log.WarnFormat(
                    "Area batch membership diagnostic failed: area={0}, error={1}",
                    diagnostic.Area,
                    exception);
                return false;
            }
        }

        private void RebuildAreaBatchCaches()
        {
            m_AreaBatchSystem.PreDeserialize(default(Context));
            NativeArray<Entity> areas = m_AreaBatchEntityQuery.ToEntityArray(Allocator.Temp);
            int areaCount = areas.Length;
            try
            {
                for (int i = 0; i < areas.Length; i++)
                {
                    EntityManager.SetComponentData(areas[i], default(Batch));
                }
            }
            finally
            {
                areas.Dispose();
            }

            Mod.Log.WarnFormat(
                "AreaBatchSystem cache rebuild requested: resetBatchComponents={0}; visible area batches will be regenerated by the game",
                areaCount);
        }

        private string GetAreaBatchMaterialName(int batchIndex)
        {
            if (batchIndex < 0 || batchIndex >= m_AreaBatchSystem.GetBatchCount())
            {
                return "<invalid batch>";
            }

            if (m_AreaBatchSystem.GetAreaBatch(
                batchIndex,
                out _,
                out _,
                out _,
                out UnityEngine.Material material,
                out _,
                out _,
                out _))
            {
                return material != null ? material.name : "<no material>";
            }

            return "<batch unavailable>";
        }

        private bool TryBatchContainsArea(int batchIndex, Entity area, out int areaCount)
        {
            areaCount = 0;
            if (s_NativeBatchDataField == null || batchIndex < 0 || batchIndex >= m_AreaBatchSystem.GetBatchCount())
            {
                return false;
            }

            object nativeBatchData = s_NativeBatchDataField.GetValue(m_AreaBatchSystem);
            object batch = GetIndexedValue(nativeBatchData, batchIndex);
            FieldInfo areaMetaDataField = batch.GetType().GetField("m_AreaMetaData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object areaMetaData = areaMetaDataField?.GetValue(batch);
            if (areaMetaData == null)
            {
                return false;
            }

            int length = GetCollectionLength(areaMetaData);
            bool containsArea = false;
            for (int i = 0; i < length; i++)
            {
                object item = GetIndexedValue(areaMetaData, i);
                FieldInfo entityField = item.GetType().GetField("m_Entity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (entityField?.GetValue(item) is Entity indexedArea)
                {
                    if (indexedArea == area)
                    {
                        containsArea = true;
                    }
                }
            }

            areaCount = length;
            return containsArea;
        }

        private static int GetCollectionLength(object collection)
        {
            PropertyInfo lengthProperty = collection.GetType().GetProperty("Length", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? collection.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return lengthProperty != null ? (int)lengthProperty.GetValue(collection) : 0;
        }

        private static object GetIndexedValue(object collection, int index)
        {
            Type type = collection.GetType();
            PropertyInfo itemProperty = type.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (itemProperty != null)
            {
                return itemProperty.GetValue(collection, new object[] { index });
            }

            MethodInfo elementAt = type.GetMethod("ElementAt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
            if (elementAt != null)
            {
                return elementAt.Invoke(collection, new object[] { index });
            }

            throw new InvalidOperationException("Indexed collection accessor was not found for " + type.FullName);
        }

        private string DescribeSurfaceIds(IEnumerable<string> ids, Dictionary<string, Entity> surfaceEntitiesById)
        {
            return ids == null
                ? "<none>"
                : string.Join(", ", ids.Select(id => GetSurfaceDisplayName(id, surfaceEntitiesById)));
        }

        private string GetSurfaceDisplayName(string id, Dictionary<string, Entity> surfaceEntitiesById)
        {
            return !string.IsNullOrEmpty(id) && surfaceEntitiesById.TryGetValue(id, out Entity entity)
                ? m_PrefabSystem.GetPrefabName(entity) + " [" + id + "]"
                : id ?? "<null>";
        }

        private Entity GetOwningBuildingEntity(Entity areaEntity)
        {
            Entity current = areaEntity;
            HashSet<Entity> visited = new HashSet<Entity>();
            for (int depth = 0; depth < 16 && current != Entity.Null && visited.Add(current); depth++)
            {
                if (EntityManager.HasComponent<Building>(current))
                {
                    if (EntityManager.HasComponent<PrefabRef>(current))
                    {
                        return current;
                    }

                    return current;
                }

                if (!EntityManager.HasComponent<Owner>(current))
                {
                    break;
                }

                current = EntityManager.GetComponentData<Owner>(current).m_Owner;
            }

            return Entity.Null;
        }

        private Entity GetSpecialIndustryBuildingPrefab(Entity industryArea)
        {
            Entity building = GetOwningBuildingEntity(industryArea);
            return building != Entity.Null && EntityManager.HasComponent<PrefabRef>(building)
                ? EntityManager.GetComponentData<PrefabRef>(building).m_Prefab
                : Entity.Null;
        }

        private bool MatchesIndustryConstructionRequest(PendingConstructionBatchRefresh request, Entity industryArea)
        {
            if (!request.MatchIndustryType)
            {
                return false;
            }

            Entity building = GetOwningBuildingEntity(industryArea);
            if (building == Entity.Null || GetSpecialIndustryBuildingPrefab(industryArea) != request.SourcePrefab)
            {
                return false;
            }

            if (request.ConstructionOwner != Entity.Null && building != request.ConstructionOwner)
            {
                return false;
            }

            // OwnerDefinition preserves the exact building instance's prefab
            // and transform before CreationDefinition.m_Owner has been resolved
            // to an Entity. Use that transform so other instances of the same
            // industry prefab cannot match this construction request.
            if (!request.HasConstructionOwnerDefinition)
            {
                return request.ConstructionOwner != Entity.Null;
            }

            if (!EntityManager.HasComponent<Game.Objects.Transform>(building))
            {
                return false;
            }

            Game.Objects.Transform transform = EntityManager.GetComponentData<Game.Objects.Transform>(building);
            float3 positionDelta = transform.m_Position - request.ConstructionOwnerDefinition.m_Position;
            float rotationAlignment = math.abs(math.dot(transform.m_Rotation.value, request.ConstructionOwnerDefinition.m_Rotation.value));
            return math.lengthsq(positionDelta) <= 0.25f && rotationAlignment >= 0.999f;
        }

        private Entity FindSpecialIndustryOwner(Entity areaEntity)
        {
            Entity current = areaEntity;
            HashSet<Entity> visited = new HashSet<Entity>();
            for (int depth = 0; depth < 16 && current != Entity.Null && visited.Add(current); depth++)
            {
                Entity prefab = GetOriginalIndustryLotPrefab(current);
                if (EntityManager.HasComponent<SpecialIndustryLotReplacement>(current) ||
                    (prefab != Entity.Null && EntityManager.HasComponent<ExtractorAreaData>(prefab)))
                {
                    return current;
                }

                if (!EntityManager.HasComponent<Owner>(current))
                {
                    break;
                }

                current = EntityManager.GetComponentData<Owner>(current).m_Owner;
            }

            return Entity.Null;
        }

        private bool IsIndustryExtractionArea(Entity area, Entity industryOwner)
        {
            return industryOwner != Entity.Null &&
                   EntityManager.HasComponent<Game.Areas.Lot>(industryOwner) &&
                   EntityManager.HasComponent<Game.Areas.Surface>(area);
        }

        private Entity GetOriginalIndustryLotPrefab(Entity area)
        {
            if (area == Entity.Null)
            {
                return Entity.Null;
            }

            if (EntityManager.HasComponent<SpecialIndustryLotReplacement>(area))
            {
                Entity originalPrefab = EntityManager.GetComponentData<SpecialIndustryLotReplacement>(area).OriginalPrefab;
                if (originalPrefab != Entity.Null)
                {
                    return originalPrefab;
                }
            }

            return EntityManager.HasComponent<PrefabRef>(area)
                ? EntityManager.GetComponentData<PrefabRef>(area).m_Prefab
                : Entity.Null;
        }

        private bool TryGetManagedPrefab(Entity prefabEntity, out PrefabBase prefab)
        {
            return m_PrefabSystem.TryGetPrefab(prefabEntity, out prefab);
        }

        private List<Entity> GetRenderableIndustrySubAreas(Entity industryRoot)
        {
            List<Entity> result = new List<Entity>();
            if (industryRoot == Entity.Null || !EntityManager.HasBuffer<Game.Areas.SubArea>(industryRoot))
            {
                return result;
            }

            DynamicBuffer<Game.Areas.SubArea> subAreas = EntityManager.GetBuffer<Game.Areas.SubArea>(industryRoot, true);
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity childArea = subAreas[i].m_Area;
                if (!EntityManager.HasComponent<PrefabRef>(childArea))
                {
                    continue;
                }

                Entity childPrefab = GetOriginalIndustryLotPrefab(childArea);
                if (TryGetRenderedAreaBatchIndex(childPrefab, childArea, out _))
                {
                    result.Add(childArea);
                }
            }

            return result;
        }

        private bool TryQueueIndustrySurfaceOverlay(
            Entity industryRoot,
            Entity industryPrefab,
            Entity targetSurfacePrefab,
            EntityCommandBuffer commandBuffer,
            long operationId)
        {
            if (industryRoot == Entity.Null || industryPrefab == Entity.Null || targetSurfacePrefab == Entity.Null ||
                !EntityManager.Exists(industryRoot) ||
                !EntityManager.HasBuffer<Game.Areas.Node>(industryRoot) ||
                !EntityManager.HasComponent<RenderedAreaData>(targetSurfacePrefab) ||
                !EntityManager.HasComponent<SurfaceData>(targetSurfacePrefab))
            {
                Mod.Log.WarnFormat(
                    "Special-industry surface overlay skipped: operation={0}, root={1}, reason=missing root Node buffer or target rendered surface",
                    operationId,
                    industryRoot);
                return false;
            }

            // Some extractor root areas (notably Forestry Lot) have no child
            // sub-areas until we add a replacement overlay. Give the owner the
            // buffer needed by SubAreaReferencesSystem before creating the owned
            // surface; leave its ExtractorAreaData and root prefab untouched.
            if (!EntityManager.HasBuffer<Game.Areas.SubArea>(industryRoot))
            {
                commandBuffer.AddBuffer<Game.Areas.SubArea>(industryRoot);
                Mod.Log.InfoFormat(
                    "Special-industry root SubArea buffer creation queued: operation={0}, root={1}, prefab='{2}'",
                    operationId,
                    industryRoot,
                    m_PrefabSystem.GetPrefabName(industryPrefab));
            }

            DynamicBuffer<Game.Areas.Node> rootNodes = EntityManager.GetBuffer<Game.Areas.Node>(industryRoot, true);
            if (rootNodes.Length < 3)
            {
                Mod.Log.WarnFormat(
                    "Special-industry surface overlay skipped: operation={0}, root={1}, nodeCount={2}",
                    operationId,
                    industryRoot,
                    rootNodes.Length);
                return false;
            }

            SpecialIndustrySurfaceOverlayState currentState = EntityManager.HasComponent<SpecialIndustrySurfaceOverlayState>(industryRoot)
                ? EntityManager.GetComponentData<SpecialIndustrySurfaceOverlayState>(industryRoot)
                : default(SpecialIndustrySurfaceOverlayState);
            if (currentState.TargetSurfacePrefab == targetSurfacePrefab &&
                FindIndustrySurfaceOverlay(industryRoot, currentState.TargetSurfacePrefab) != Entity.Null)
            {
                return false;
            }

            if (currentState.TargetSurfacePrefab != Entity.Null)
            {
                Entity previousOverlay = FindIndustrySurfaceOverlay(industryRoot, currentState.TargetSurfacePrefab);
                if (previousOverlay != Entity.Null && !EntityManager.HasComponent<Deleted>(previousOverlay))
                {
                    commandBuffer.AddComponent<Deleted>(previousOverlay);
                }
            }

            Entity definition = commandBuffer.CreateEntity();
            commandBuffer.AddComponent(definition, new CreationDefinition
            {
                m_Prefab = targetSurfacePrefab,
                m_Owner = industryRoot,
                m_Flags = CreationFlags.Permanent,
                m_RandomSeed = new System.Random().Next(),
            });
            DynamicBuffer<Game.Areas.Node> definitionNodes = commandBuffer.AddBuffer<Game.Areas.Node>(definition);
            for (int i = 0; i < rootNodes.Length; i++)
            {
                definitionNodes.Add(rootNodes[i]);
            }
            commandBuffer.AddComponent<Updated>(definition);

            SpecialIndustrySurfaceOverlayState updatedState = new SpecialIndustrySurfaceOverlayState
            {
                OriginalIndustryPrefab = currentState.OriginalIndustryPrefab != Entity.Null
                    ? currentState.OriginalIndustryPrefab
                    : industryPrefab,
                TargetSurfacePrefab = targetSurfacePrefab,
            };
            if (EntityManager.HasComponent<SpecialIndustrySurfaceOverlayState>(industryRoot))
            {
                commandBuffer.SetComponent(industryRoot, updatedState);
            }
            else
            {
                commandBuffer.AddComponent(industryRoot, updatedState);
            }

            Mod.Log.InfoFormat(
                "Special-industry surface overlay creation queued: operation={0}, root={1}, target='{2}', nodeCount={3}, rootPrefabUntouched=True",
                operationId,
                industryRoot,
                m_PrefabSystem.GetPrefabName(targetSurfacePrefab),
                rootNodes.Length);
            return true;
        }

        private void SynchronizeIndustrySurfaceOverlay(Entity industryRoot, EntityCommandBuffer commandBuffer)
        {
            SpecialIndustrySurfaceOverlayState state = EntityManager.GetComponentData<SpecialIndustrySurfaceOverlayState>(industryRoot);
            Entity overlay = FindIndustrySurfaceOverlay(industryRoot, state.TargetSurfacePrefab);
            if (overlay == Entity.Null)
            {
                TryQueueIndustrySurfaceOverlay(industryRoot, state.OriginalIndustryPrefab, state.TargetSurfacePrefab, commandBuffer, ++m_NextOperationId);
                return;
            }

            if (!EntityManager.HasBuffer<Game.Areas.Node>(industryRoot) || !EntityManager.HasBuffer<Game.Areas.Node>(overlay))
            {
                return;
            }

            DynamicBuffer<Game.Areas.Node> rootNodes = EntityManager.GetBuffer<Game.Areas.Node>(industryRoot, true);
            DynamicBuffer<Game.Areas.Node> overlayNodes = EntityManager.GetBuffer<Game.Areas.Node>(overlay, true);
            bool nodesMatch = rootNodes.Length == overlayNodes.Length;
            if (nodesMatch)
            {
                for (int i = 0; i < rootNodes.Length; i++)
                {
                    if (!rootNodes[i].Equals(overlayNodes[i]))
                    {
                        nodesMatch = false;
                        break;
                    }
                }
            }

            if (nodesMatch)
            {
                return;
            }

            DynamicBuffer<Game.Areas.Node> updatedNodes = commandBuffer.SetBuffer<Game.Areas.Node>(overlay);
            for (int i = 0; i < rootNodes.Length; i++)
            {
                updatedNodes.Add(rootNodes[i]);
            }
            if (!EntityManager.HasComponent<Updated>(overlay))
            {
                commandBuffer.AddComponent<Updated>(overlay);
            }
            Mod.Log.InfoFormat(
                "Special-industry surface overlay geometry synchronized: root={0}, overlay={1}, nodes={2}",
                industryRoot,
                overlay,
                rootNodes.Length);
        }

        private Entity FindIndustrySurfaceOverlay(Entity industryRoot, Entity targetSurfacePrefab)
        {
            if (industryRoot == Entity.Null || targetSurfacePrefab == Entity.Null ||
                !EntityManager.Exists(industryRoot) || !EntityManager.HasBuffer<Game.Areas.SubArea>(industryRoot))
            {
                return Entity.Null;
            }

            DynamicBuffer<Game.Areas.SubArea> subAreas = EntityManager.GetBuffer<Game.Areas.SubArea>(industryRoot, true);
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity child = subAreas[i].m_Area;
                if (!EntityManager.Exists(child) || EntityManager.HasComponent<Deleted>(child) ||
                    !EntityManager.HasComponent<Owner>(child) ||
                    EntityManager.GetComponentData<Owner>(child).m_Owner != industryRoot ||
                    !EntityManager.HasComponent<PrefabRef>(child) ||
                    EntityManager.GetComponentData<PrefabRef>(child).m_Prefab != targetSurfacePrefab)
                {
                    continue;
                }

                Entity childPrefab = EntityManager.GetComponentData<PrefabRef>(child).m_Prefab;
                if (EntityManager.HasComponent<SurfaceData>(childPrefab))
                {
                    return child;
                }
            }

            return Entity.Null;
        }

        private bool IsIndustrySurfaceOverlay(Entity area)
        {
            Entity child = area;
            for (int depth = 0; depth < 16 && child != Entity.Null && EntityManager.Exists(child); depth++)
            {
                if (!EntityManager.HasComponent<Owner>(child))
                {
                    return false;
                }

                child = EntityManager.GetComponentData<Owner>(child).m_Owner;
                if (child != Entity.Null && EntityManager.HasComponent<SpecialIndustrySurfaceOverlayState>(child))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryConvertSharedIndustryPrefabToChildSurface(
            PrefabBase industryPrefab,
            Entity targetSurfacePrefab,
            long operationId,
            out int oldRootBatch)
        {
            oldRootBatch = -1;
            if (industryPrefab == null ||
                !EntityManager.HasComponent<ExtractorAreaData>(m_PrefabSystem.GetEntity(industryPrefab)) ||
                !m_PrefabSystem.TryGetPrefab(targetSurfacePrefab, out PrefabBase targetPrefab) ||
                !targetPrefab.TryGet<RenderedArea>(out _))
            {
                Mod.Log.WarnFormat(
                    "Special-industry root conversion skipped: root='{0}', target='{1}', reason=missing extractor data or target RenderedArea",
                    industryPrefab != null ? industryPrefab.name : "<null>",
                    targetSurfacePrefab != Entity.Null ? m_PrefabSystem.GetPrefabName(targetSurfacePrefab) : "<null>");
                return false;
            }

            Entity industryPrefabEntity = m_PrefabSystem.GetEntity(industryPrefab);
            if (EntityManager.HasComponent<RenderedAreaData>(industryPrefabEntity))
            {
                oldRootBatch = EntityManager.GetComponentData<RenderedAreaData>(industryPrefabEntity).m_BatchIndex;
            }

            if (!m_SpecialIndustryPrefabSnapshots.TryGetValue(industryPrefab, out SpecialIndustryPrefabSnapshot snapshot))
            {
                industryPrefab.TryGet<RenderedArea>(out RenderedArea originalRenderedArea);
                MasterArea originalMasterArea = industryPrefab.GetComponent<MasterArea>();
                snapshot = new SpecialIndustryPrefabSnapshot
                {
                    Prefab = industryPrefab,
                    OriginalRenderedArea = originalRenderedArea,
                    MasterArea = originalMasterArea,
                    OriginalSlaveAreas = CloneSlaveAreaInfo(originalMasterArea?.m_SlaveAreas),
                };
                m_SpecialIndustryPrefabSnapshots.Add(industryPrefab, snapshot);
            }
            else if (snapshot.CurrentTargetSurface == targetSurfacePrefab)
            {
                return false;
            }

            RestoreSpecialIndustryPrefabSnapshot(snapshot, scheduleUpdate: false);

            MasterArea masterArea = industryPrefab.GetComponent<MasterArea>();
            if (masterArea == null)
            {
                masterArea = industryPrefab.AddComponent<MasterArea>();
            }

            SlaveAreaInfo[] existingSlaveAreas = masterArea.m_SlaveAreas ?? Array.Empty<SlaveAreaInfo>();
            bool alreadyConfigured = existingSlaveAreas.Any(info => info != null && info.m_Area != null &&
                m_PrefabSystem.TryGetEntity(info.m_Area, out Entity configuredPrefab) &&
                configuredPrefab == targetSurfacePrefab);
            if (!alreadyConfigured)
            {
                SlaveAreaInfo[] updatedSlaveAreas = new SlaveAreaInfo[existingSlaveAreas.Length + 1];
                Array.Copy(existingSlaveAreas, updatedSlaveAreas, existingSlaveAreas.Length);
                updatedSlaveAreas[updatedSlaveAreas.Length - 1] = new SlaveAreaInfo
                {
                    m_Area = (AreaPrefab)targetPrefab,
                };
                masterArea.m_SlaveAreas = updatedSlaveAreas;
            }

            industryPrefab.Remove<RenderedArea>();
            snapshot.CurrentTargetSurface = targetSurfacePrefab;
            m_PrefabSystem.UpdatePrefab(industryPrefab);

            Mod.Log.InfoFormat(
                "Special-industry shared prefab updated: operation={0}, rootPrefab='{1}', originalRootBatch={2}, keptExtractorData=True, keptExistingSubAreas={3}, addedChildSurface='{4}', updatePrefabQueued=True",
                operationId,
                industryPrefab.name,
                oldRootBatch,
                existingSlaveAreas.Length,
                targetPrefab.name);
            return true;
        }

        private void RestoreAllSpecialIndustryPrefabSnapshots()
        {
            int restored = 0;
            foreach (SpecialIndustryPrefabSnapshot snapshot in m_SpecialIndustryPrefabSnapshots.Values)
            {
                RestoreSpecialIndustryPrefabSnapshot(snapshot, scheduleUpdate: true);
                restored++;
            }

            m_SpecialIndustryPrefabSnapshots.Clear();
            if (restored > 0)
            {
                Mod.Log.InfoFormat("Special-industry shared prefabs restored: count={0}", restored);
            }
        }

        private void RestoreSpecialIndustryPrefabSnapshot(SpecialIndustryPrefabSnapshot snapshot, bool scheduleUpdate)
        {
            PrefabBase prefab = snapshot.Prefab;
            if (snapshot.OriginalRenderedArea != null)
            {
                if (prefab.TryGet<RenderedArea>(out _))
                {
                    prefab.AddComponentFrom(snapshot.OriginalRenderedArea);
                }
                else
                {
                    prefab.AddComponentFrom(snapshot.OriginalRenderedArea);
                }
            }
            else
            {
                prefab.Remove<RenderedArea>();
            }

            MasterArea currentMasterArea = prefab.GetComponent<MasterArea>();
            if (snapshot.MasterArea == null)
            {
                if (currentMasterArea != null)
                {
                    prefab.Remove<MasterArea>();
                }
            }
            else
            {
                if (currentMasterArea == null)
                {
                    currentMasterArea = prefab.AddComponent<MasterArea>();
                }
                currentMasterArea.m_SlaveAreas = CloneSlaveAreaInfo(snapshot.OriginalSlaveAreas);
            }

            if (scheduleUpdate)
            {
                m_PrefabSystem.UpdatePrefab(prefab);
            }
        }

        private static SlaveAreaInfo[] CloneSlaveAreaInfo(SlaveAreaInfo[] source)
        {
            if (source == null)
            {
                return null;
            }

            SlaveAreaInfo[] result = new SlaveAreaInfo[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                result[i] = source[i] == null
                    ? null
                    : new SlaveAreaInfo { m_Area = source[i].m_Area };
            }

            return result;
        }

        private bool IsUsingSpecialIndustryTarget(Entity area, Entity targetSurfacePrefab)
        {
            return EntityManager.HasComponent<SpecialIndustryLotReplacement>(area) &&
                   EntityManager.GetComponentData<SpecialIndustryLotReplacement>(area).TargetSurfacePrefab == targetSurfacePrefab;
        }

        private bool TryGetRenderedAreaBatchIndex(Entity prefab, Entity area, out int batchIndex)
        {
            if (prefab != Entity.Null && EntityManager.HasComponent<RenderedAreaData>(prefab))
            {
                batchIndex = EntityManager.GetComponentData<RenderedAreaData>(prefab).m_BatchIndex;
                if (batchIndex >= 0)
                {
                    return true;
                }
            }

            if (area != Entity.Null && EntityManager.HasComponent<Batch>(area))
            {
                batchIndex = EntityManager.GetComponentData<Batch>(area).m_BatchIndex;
                return batchIndex >= 0;
            }

            batchIndex = -1;
            return false;
        }

        private void LogSpecialIndustryAreaInventory(long operationId)
        {
            using (NativeArray<Entity> areas = m_SpecialIndustryAreaQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity area in areas)
                {
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(area);
                    Entity prefab = prefabRef.m_Prefab;
                    if (prefab == Entity.Null || !EntityManager.HasComponent<ExtractorAreaData>(prefab))
                    {
                        continue;
                    }

                    ExtractorAreaData extractor = EntityManager.GetComponentData<ExtractorAreaData>(prefab);
                    Entity owner = EntityManager.HasComponent<Owner>(area)
                        ? EntityManager.GetComponentData<Owner>(area).m_Owner
                        : Entity.Null;
                    int nodeCount = EntityManager.HasBuffer<Game.Areas.Node>(area) ? EntityManager.GetBuffer<Game.Areas.Node>(area).Length : 0;
                    int subAreaCount = EntityManager.HasBuffer<Game.Areas.SubArea>(area) ? EntityManager.GetBuffer<Game.Areas.SubArea>(area).Length : 0;
                    int renderBatch = EntityManager.HasComponent<RenderedAreaData>(prefab)
                        ? EntityManager.GetComponentData<RenderedAreaData>(prefab).m_BatchIndex
                        : -1;

                    Mod.Log.InfoFormat(
                        "Special-industry lot: operation={0}, entity={1}, owner={2}, prefab='{3}', mapFeature={4}, hasSurfaceData={5}, renderBatch={6}, nodeCount={7}, subAreaCount={8}",
                        operationId,
                        area,
                        owner,
                        m_PrefabSystem.GetPrefabName(prefab),
                        extractor.m_MapFeature,
                        EntityManager.HasComponent<SurfaceData>(prefab),
                        renderBatch,
                        nodeCount,
                        subAreaCount);

                    if (!EntityManager.HasBuffer<Game.Areas.SubArea>(area))
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Areas.SubArea> subAreas = EntityManager.GetBuffer<Game.Areas.SubArea>(area, true);
                    for (int i = 0; i < subAreas.Length; i++)
                    {
                        Entity subArea = subAreas[i].m_Area;
                        Entity subAreaPrefab = EntityManager.HasComponent<PrefabRef>(subArea)
                            ? EntityManager.GetComponentData<PrefabRef>(subArea).m_Prefab
                            : Entity.Null;
                        string subAreaPrefabName = subAreaPrefab != Entity.Null
                            ? m_PrefabSystem.GetPrefabName(subAreaPrefab)
                            : "<no PrefabRef>";
                        string areaType = subAreaPrefab != Entity.Null && EntityManager.HasComponent<AreaGeometryData>(subAreaPrefab)
                            ? EntityManager.GetComponentData<AreaGeometryData>(subAreaPrefab).m_Type.ToString()
                            : "<none>";
                        int subAreaRenderBatch = subAreaPrefab != Entity.Null && EntityManager.HasComponent<RenderedAreaData>(subAreaPrefab)
                            ? EntityManager.GetComponentData<RenderedAreaData>(subAreaPrefab).m_BatchIndex
                            : -1;
                        int runtimeBatch = EntityManager.HasComponent<Batch>(subArea)
                            ? EntityManager.GetComponentData<Batch>(subArea).m_BatchIndex
                            : -1;
                        Mod.Log.InfoFormat(
                            "Special-industry sub-area: operation={0}, lot={1}, index={2}, entity={3}, prefab='{4}', areaType={5}, hasSurface={6}, hasSurfaceData={7}, renderBatch={8}, runtimeBatch={9}",
                            operationId,
                            area,
                            i,
                            subArea,
                            subAreaPrefabName,
                            areaType,
                            EntityManager.HasComponent<Game.Areas.Surface>(subArea),
                            subAreaPrefab != Entity.Null && EntityManager.HasComponent<SurfaceData>(subAreaPrefab),
                            subAreaRenderBatch,
                            runtimeBatch);

                        LogSpecialIndustryPrefabChildren(operationId, area, subAreaPrefab, 1, new HashSet<Entity>());
                        LogSpecialIndustryRuntimeChildren(operationId, area, subArea, 1, new HashSet<Entity>());
                    }
                }
            }
        }

        private void LogSpecialIndustryPrefabChildren(long operationId, Entity lot, Entity prefab, int depth, HashSet<Entity> visited)
        {
            if (prefab == Entity.Null || depth > 4 || !visited.Add(prefab) || !EntityManager.HasBuffer<PrefabSubArea>(prefab))
            {
                return;
            }

            DynamicBuffer<PrefabSubArea> children = EntityManager.GetBuffer<PrefabSubArea>(prefab, true);
            for (int i = 0; i < children.Length; i++)
            {
                Entity childPrefab = children[i].m_Prefab;
                if (childPrefab == Entity.Null)
                {
                    continue;
                }

                int renderBatch = EntityManager.HasComponent<RenderedAreaData>(childPrefab)
                    ? EntityManager.GetComponentData<RenderedAreaData>(childPrefab).m_BatchIndex
                    : -1;
                Mod.Log.InfoFormat(
                    "Special-industry prefab child: operation={0}, lot={1}, depth={2}, parentPrefab='{3}', index={4}, prefab='{5}', hasSurfaceData={6}, hasExtractorAreaData={7}, hasAreaGeometryData={8}, renderBatch={9}, nestedSubAreas={10}",
                    operationId,
                    lot,
                    depth,
                    m_PrefabSystem.GetPrefabName(prefab),
                    i,
                    m_PrefabSystem.GetPrefabName(childPrefab),
                    EntityManager.HasComponent<SurfaceData>(childPrefab),
                    EntityManager.HasComponent<ExtractorAreaData>(childPrefab),
                    EntityManager.HasComponent<AreaGeometryData>(childPrefab),
                    renderBatch,
                    EntityManager.HasBuffer<PrefabSubArea>(childPrefab) ? EntityManager.GetBuffer<PrefabSubArea>(childPrefab, true).Length : 0);
                LogSpecialIndustryPrefabChildren(operationId, lot, childPrefab, depth + 1, visited);
            }
        }

        private void LogSpecialIndustryRuntimeChildren(long operationId, Entity lot, Entity area, int depth, HashSet<Entity> visited)
        {
            if (area == Entity.Null || depth > 4 || !visited.Add(area) || !EntityManager.HasBuffer<Game.Areas.SubArea>(area))
            {
                return;
            }

            DynamicBuffer<Game.Areas.SubArea> children = EntityManager.GetBuffer<Game.Areas.SubArea>(area, true);
            for (int i = 0; i < children.Length; i++)
            {
                Entity childArea = children[i].m_Area;
                if (childArea == Entity.Null)
                {
                    continue;
                }

                Entity childPrefab = EntityManager.HasComponent<PrefabRef>(childArea)
                    ? EntityManager.GetComponentData<PrefabRef>(childArea).m_Prefab
                    : Entity.Null;
                int runtimeBatch = EntityManager.HasComponent<Batch>(childArea)
                    ? EntityManager.GetComponentData<Batch>(childArea).m_BatchIndex
                    : -1;
                Mod.Log.InfoFormat(
                    "Special-industry runtime child: operation={0}, lot={1}, depth={2}, parentArea={3}, index={4}, area={5}, prefab='{6}', hasSurface={7}, nestedSubAreas={8}, runtimeBatch={9}",
                    operationId,
                    lot,
                    depth,
                    area,
                    i,
                    childArea,
                    childPrefab != Entity.Null ? m_PrefabSystem.GetPrefabName(childPrefab) : "<no PrefabRef>",
                    EntityManager.HasComponent<Game.Areas.Surface>(childArea),
                    EntityManager.HasBuffer<Game.Areas.SubArea>(childArea) ? EntityManager.GetBuffer<Game.Areas.SubArea>(childArea, true).Length : 0,
                    runtimeBatch);
                LogSpecialIndustryRuntimeChildren(operationId, lot, childArea, depth + 1, visited);
            }
        }

        private string GetAssetName(Entity buildingEntity)
        {
            if (buildingEntity == Entity.Null)
            {
                return "unowned/map surface area";
            }

            return EntityManager.HasComponent<PrefabRef>(buildingEntity)
                ? m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(buildingEntity).m_Prefab) + " [buildingEntity=" + buildingEntity + "]"
                : "building entity " + buildingEntity;
        }

        private static bool IsExcludedSurfaceName(string name)
        {
            return string.IsNullOrEmpty(name) || name.EndsWith("Placeholder", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Surface Area", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Missing Area", StringComparison.OrdinalIgnoreCase);
        }
    }
}
