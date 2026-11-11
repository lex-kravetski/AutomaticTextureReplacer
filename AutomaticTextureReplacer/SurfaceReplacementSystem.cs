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
            public int FramesChecked;
            public bool SurfaceAreaObserved;
            public bool SpecialIndustryFieldObserved;
        }

        private sealed class BatchReplacementCandidate
        {
            public Entity SourcePrefab;
            public Entity TargetPrefab;
        }

        public struct SpecialIndustryLotReplacement : IComponentData
        {
            public Entity OriginalPrefab;
            public Entity TargetSurfacePrefab;
        }

        public sealed class SurfaceReplacementRule
        {
            public int id { get; set; }
            public bool enabled { get; set; }
            public List<string> sourceSurfaceIds { get; set; } = new List<string>();
            public List<string> targetSurfaceIds { get; set; } = new List<string>();
        }

        private static readonly object s_RequestLock = new object();
        private static readonly Queue<ReplacementRequest> s_Requests = new Queue<ReplacementRequest>();
        private static readonly List<PendingConstructionBatchRefresh> s_ConstructionBatchRefreshRequests = new List<PendingConstructionBatchRefresh>();
        private static int s_ConstructionRefreshGeneration;
        private readonly List<PendingBatchDiagnostic> m_PendingBatchDiagnostics = new List<PendingBatchDiagnostic>();
        private readonly List<PendingRevertChange> m_PendingRevertChanges = new List<PendingRevertChange>();
        private readonly List<PendingConstructionBatchRefresh> m_PendingConstructionBatchRefreshes = new List<PendingConstructionBatchRefresh>();
        private readonly HashSet<Entity> m_IndustryAreasScheduledThisFrame = new HashSet<Entity>();
        private int m_ConstructionRefreshGeneration = -1;

        private EntityQuery m_SurfacePrefabQuery;
        private EntityQuery m_SurfaceAreaQuery;
        private EntityQuery m_ChangedSurfaceAreaQuery;
        private EntityQuery m_SpecialIndustryAreaQuery;
        private EntityQuery m_ChangedSpecialIndustryAreaQuery;
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
                });
            }

            Mod.Log.InfoFormat(
                "Surface replacement request queued: mode={0}, ruleId={1}",
                string.IsNullOrWhiteSpace(requestedRuleId) ? "Replace All" : "single rule",
                string.IsNullOrWhiteSpace(requestedRuleId) ? "all enabled" : requestedRuleId);
        }

        public static void QueueRevertAll()
        {
            lock (s_RequestLock)
            {
                s_Requests.Enqueue(new ReplacementRequest
                {
                    RuleId = "",
                    RulesJson = "[]",
                    RevertAll = true,
                });
            }

            Mod.Log.Info("Revert All request queued");
        }

        public static void QueueConstructionBatchRefresh(Entity sourcePrefab, Entity targetPrefab)
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
                });
            }
        }

        public static void ResetConstructionBatchRefreshes()
        {
            lock (s_RequestLock)
            {
                s_ConstructionBatchRefreshRequests.Clear();
                s_ConstructionRefreshGeneration++;
            }
        }

        public static bool MatchesSpecialIndustryLot(string surfacePrefabName, string lotPrefabName)
        {
            if (string.IsNullOrWhiteSpace(surfacePrefabName) || string.IsNullOrWhiteSpace(lotPrefabName))
            {
                return false;
            }

            int surfaceSuffix = surfacePrefabName.IndexOf(" Surface", StringComparison.OrdinalIgnoreCase);
            int wordEnd = surfacePrefabName.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            bool isFishLotSource = surfacePrefabName.IndexOf("Fish", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                   surfacePrefabName.IndexOf("Lot", StringComparison.OrdinalIgnoreCase) >= 0;
            if ((!isFishLotSource && surfaceSuffix <= 0) || wordEnd <= 0)
            {
                return false;
            }

            string industryPrefix = surfacePrefabName.Substring(0, wordEnd).Trim();
            return industryPrefix.Length > 0 && lotPrefabName.StartsWith(industryPrefix, StringComparison.OrdinalIgnoreCase);
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
            ProcessUpdatedSpecialIndustryAreaBindings();
            LogPendingBatchDiagnostics();
            while (TryDequeue(out ReplacementRequest request))
            {
                if (request.RevertAll)
                {
                    ProcessRevertAll();
                }
                else
                {
                    ProcessReplacement(request);
                }
            }
        }

        protected override void OnDestroy()
        {
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
            lock (s_RequestLock)
            {
                foreach (PendingConstructionBatchRefresh request in s_ConstructionBatchRefreshRequests)
                {
                    if (!m_PendingConstructionBatchRefreshes.Any(pending => pending.SourcePrefab == request.SourcePrefab && pending.TargetPrefab == request.TargetPrefab))
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
                    !enabledMappings.Contains(GetPrefabIdString(request.SourcePrefab) + "|" + GetPrefabIdString(request.TargetPrefab)));
            }

            using (NativeArray<Entity> changedAreas = m_ChangedSurfaceAreaQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Entity> specialIndustryAreas = m_ChangedSpecialIndustryAreaQuery.ToEntityArray(Allocator.Temp))
            {
                if (m_PendingConstructionBatchRefreshes.Count == 0)
                {
                    return;
                }

                foreach (Entity area in changedAreas)
                {
                    Entity currentPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                    foreach (PendingConstructionBatchRefresh request in m_PendingConstructionBatchRefreshes)
                    {
                        if (request.TargetPrefab != currentPrefab)
                        {
                            continue;
                        }

                        string sourceName = m_PrefabSystem.GetPrefabName(request.SourcePrefab);
                        string targetName = m_PrefabSystem.GetPrefabName(request.TargetPrefab);
                        QueueBatchDiagnostic(area, request.SourcePrefab, request.TargetPrefab, sourceName, targetName);
                        request.SurfaceAreaObserved = true;
                    }
                }

                EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
                bool hasSpecialFieldUpdates = false;
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
                            Entity originalFieldPrefab = GetOriginalIndustryLotPrefab(fieldArea);
                            if (originalFieldPrefab == Entity.Null ||
                                !MatchesSpecialIndustryLot(
                                    m_PrefabSystem.GetPrefabName(request.SourcePrefab),
                                    m_PrefabSystem.GetPrefabName(originalFieldPrefab)) ||
                                !TryGetRenderedAreaBatchIndex(originalFieldPrefab, fieldArea, out _) ||
                                IsUsingSpecialIndustryTarget(fieldArea, request.TargetPrefab))
                            {
                                continue;
                            }

                            if (!m_IndustryAreasScheduledThisFrame.Add(fieldArea))
                            {
                                continue;
                            }
                            var replacementMarker = new SpecialIndustryLotReplacement { OriginalPrefab = originalFieldPrefab, TargetSurfacePrefab = request.TargetPrefab };
                            if (EntityManager.HasComponent<SpecialIndustryLotReplacement>(fieldArea))
                                commandBuffer.SetComponent(fieldArea, replacementMarker);
                            else
                                commandBuffer.AddComponent(fieldArea, replacementMarker);
                            commandBuffer.SetComponent(fieldArea, new PrefabRef { m_Prefab = request.TargetPrefab });
                            if (!EntityManager.HasComponent<Updated>(fieldArea))
                            {
                                commandBuffer.AddComponent<Updated>(fieldArea);
                            }
                            QueueBatchDiagnostic(
                                fieldArea,
                                request.SourcePrefab,
                                request.TargetPrefab,
                                m_PrefabSystem.GetPrefabName(request.SourcePrefab),
                                m_PrefabSystem.GetPrefabName(request.TargetPrefab));
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
                    if (!targetsBySourceId.TryGetValue(sourceId, out HashSet<string> targetIds))
                        targetsBySourceId[sourceId] = targetIds = new HashSet<string>(StringComparer.Ordinal);
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
                    if (fieldPrefab == Entity.Null ||
                        EntityManager.HasComponent<ExtractorAreaData>(fieldPrefab) ||
                        !EntityManager.HasComponent<Game.Areas.Lot>(fieldArea))
                    {
                        continue;
                    }

                    Entity industryArea = FindSpecialIndustryOwner(fieldArea);
                    if (industryArea == Entity.Null)
                    {
                        continue;
                    }

                    if (!TryGetRenderedAreaBatchIndex(fieldPrefab, fieldArea, out _))
                    {
                        continue;
                    }

                    string industryLotName = m_PrefabSystem.GetPrefabName(fieldPrefab);
                    foreach (PendingConstructionBatchRefresh request in m_PendingConstructionBatchRefreshes)
                    {
                        string sourceName = m_PrefabSystem.GetPrefabName(request.SourcePrefab);
                        if (!MatchesSpecialIndustryLot(sourceName, industryLotName) ||
                            IsUsingSpecialIndustryTarget(fieldArea, request.TargetPrefab))
                        {
                            continue;
                        }

                        if (!m_IndustryAreasScheduledThisFrame.Add(fieldArea))
                        {
                            continue;
                        }
                        var replacementMarker = new SpecialIndustryLotReplacement { OriginalPrefab = fieldPrefab, TargetSurfacePrefab = request.TargetPrefab };
                        if (EntityManager.HasComponent<SpecialIndustryLotReplacement>(fieldArea))
                            commandBuffer.SetComponent(fieldArea, replacementMarker);
                        else
                            commandBuffer.AddComponent(fieldArea, replacementMarker);
                        commandBuffer.SetComponent(fieldArea, new PrefabRef { m_Prefab = request.TargetPrefab });
                        if (!EntityManager.HasComponent<Updated>(fieldArea))
                        {
                            commandBuffer.AddComponent<Updated>(fieldArea);
                        }
                        QueueBatchDiagnostic(
                            fieldArea,
                            fieldPrefab,
                            request.TargetPrefab,
                            m_PrefabSystem.GetPrefabName(fieldPrefab),
                            m_PrefabSystem.GetPrefabName(request.TargetPrefab));
                        request.SpecialIndustryFieldObserved = true;
                        Mod.Log.InfoFormat(
                            "Automatic created special-industry lot replacement scheduled: industry={0}, lot={1}, field={2}, source='{3}', targetSurface='{4}'",
                            industryArea,
                            industryLotName,
                            fieldArea,
                            sourceName,
                            m_PrefabSystem.GetPrefabName(request.TargetPrefab));
                        replacements++;
                        break;
                    }
                }

                if (replacements > 0)
                {
                    Mod.Log.InfoFormat("Automatic created special-industry fields submitted: count={0}", replacements);
                }
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
                        PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(fieldArea);
                        prefabRef.m_Prefab = replacementSurface;
                        commandBuffer.SetComponent(fieldArea, prefabRef);
                        var replacementMarker = new SpecialIndustryLotReplacement { OriginalPrefab = oldPrefab, TargetSurfacePrefab = replacementSurface };
                        if (EntityManager.HasComponent<SpecialIndustryLotReplacement>(fieldArea))
                            commandBuffer.SetComponent(fieldArea, replacementMarker);
                        else
                            commandBuffer.AddComponent(fieldArea, replacementMarker);
                        if (!EntityManager.HasComponent<Updated>(fieldArea))
                        {
                            commandBuffer.AddComponent<Updated>(fieldArea);
                        }

                        QueueBatchDiagnostic(
                            fieldArea,
                            oldPrefab,
                            replacementSurface,
                            m_PrefabSystem.GetPrefabName(oldPrefab),
                            m_PrefabSystem.GetPrefabName(replacementSurface));
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

        private void ProcessRevertAll()
        {
            int scanned = 0;
            int alreadyDefault = 0;
            int changed = 0;
            int noDefaultMapping = 0;
            int skippedExamples = 0;
            System.Random random = new System.Random();
            EntityCommandBuffer commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            using (NativeArray<Entity> areaEntities = m_SurfaceAreaQuery.ToEntityArray(Allocator.Temp))
            {
                scanned = areaEntities.Length;
                foreach (Entity area in areaEntities)
                {
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

            using (NativeArray<Entity> lotAreas = m_SpecialIndustryAreaQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity area in lotAreas)
                {
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

            Mod.Log.InfoFormat(
                "Revert All scan finished: surfaceAreas={0}, changed={1}, alreadyDefault={2}, noDefaultMapping={3}",
                scanned,
                changed,
                alreadyDefault,
                noDefaultMapping);
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
                    bool isFishAreaSource = EntityManager.HasComponent<ExtractorAreaData>(prefabEntity) &&
                                            EntityManager.GetComponentData<ExtractorAreaData>(prefabEntity).m_MapFeature == MapFeature.Fish &&
                                            string.Equals(m_PrefabSystem.GetPrefabName(prefabEntity), "Inland Fish Farm Lot", StringComparison.OrdinalIgnoreCase);
                    if (!isActualSurface && !isFishAreaSource)
                    {
                        continue;
                    }

                    string id = prefab.GetPrefabID().ToString();
                    if ((isActualSurface && IsExcludedSurfaceName(prefab.name)) || surfaceEntitiesById.ContainsKey(id))
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

            Dictionary<string, HashSet<string>> targetIdsBySource = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
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

                foreach (string sourceId in rule.sourceSurfaceIds)
                {
                    if (string.IsNullOrEmpty(sourceId))
                    {
                        continue;
                    }

                    if (!targetIdsBySource.TryGetValue(sourceId, out HashSet<string> targetIds))
                    {
                        targetIds = new HashSet<string>(StringComparer.Ordinal);
                        targetIdsBySource.Add(sourceId, targetIds);
                    }

                    foreach (string targetId in rule.targetSurfaceIds)
                    {
                        if (!string.IsNullOrEmpty(targetId) && surfaceEntitiesById.TryGetValue(targetId, out Entity targetEntity) &&
                            EntityManager.HasComponent<SurfaceData>(targetEntity))
                        {
                            targetIds.Add(targetId);
                        }
                        else
                        {
                            Mod.Log.WarnFormat("Replacement rule id={0}: target surface id '{1}' is not in the current catalog", rule.id, targetId ?? "<null>");
                        }
                    }
                }
            }

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
            int specialIndustryFieldsScanned = 0;
            int specialIndustryFieldsQueued = 0;
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
                    if (specialIndustryOwner != Entity.Null)
                    {
                        skippedOutOfScopeAreas++;
                        Mod.Log.InfoFormat(
                            "Surface area delegated to special-industry lot scan: area={0}, industry={1}, prefab='{2}'",
                            areaEntity,
                            specialIndustryOwner,
                            m_PrefabSystem.GetPrefabName(prefabRef.m_Prefab));
                        continue;
                    }
                    bool isAssetSurface = buildingEntity != Entity.Null || specialIndustryOwner != Entity.Null;
                    if ((isAssetSurface && !includeAssets) || (!isAssetSurface && !includeManualAreas))
                    {
                        skippedOutOfScopeAreas++;
                        continue;
                    }

                    string assetName = buildingEntity != Entity.Null
                        ? GetAssetName(buildingEntity)
                        : specialIndustryOwner != Entity.Null
                            ? GetAssetName(specialIndustryOwner) + " [special-industry area]"
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
                    Entity ownerEntity = EntityManager.HasComponent<Owner>(areaEntity)
                        ? EntityManager.GetComponentData<Owner>(areaEntity).m_Owner
                        : Entity.Null;
                    updateCommandBuffer.SetComponent(areaEntity, new PrefabRef { m_Prefab = replacement });
                    if (!EntityManager.HasComponent<Updated>(areaEntity))
                    {
                        updateCommandBuffer.AddComponent<Updated>(areaEntity);
                    }
                    QueueBatchDiagnostic(
                        areaEntity,
                        prefabRef.m_Prefab,
                        replacement,
                        sourceName,
                        m_PrefabSystem.GetPrefabName(replacement));
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

            if (includeAssets)
            {
                Dictionary<string, List<BatchReplacementCandidate>> candidatesByIndustry = new Dictionary<string, List<BatchReplacementCandidate>>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, List<Entity>> sourceMapping in replacementsBySource)
                {
                    if (!surfaceEntitiesById.TryGetValue(sourceMapping.Key, out Entity sourcePrefab) ||
                        !AutomaticTextureReplacerUISystem.IsSpecialIndustrySurface(m_PrefabSystem, sourcePrefab))
                    {
                        continue;
                    }

                    string industryPrefix = GetSpecialIndustryFamilyPrefix(sourcePrefab);
                    if (string.IsNullOrEmpty(industryPrefix))
                    {
                        continue;
                    }

                    if (!candidatesByIndustry.TryGetValue(industryPrefix, out List<BatchReplacementCandidate> candidates))
                    {
                        candidates = new List<BatchReplacementCandidate>();
                        candidatesByIndustry.Add(industryPrefix, candidates);
                    }

                    foreach (Entity targetPrefab in sourceMapping.Value)
                    {
                        if (!candidates.Any(candidate => candidate.SourcePrefab == sourcePrefab && candidate.TargetPrefab == targetPrefab))
                        {
                            candidates.Add(new BatchReplacementCandidate { SourcePrefab = sourcePrefab, TargetPrefab = targetPrefab });
                        }
                    }
                }

                using (NativeArray<Entity> specialIndustryAreas = m_SpecialIndustryAreaQuery.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity lotArea in specialIndustryAreas)
                    {
                        Entity lotPrefab = GetOriginalIndustryLotPrefab(lotArea);
                        if (lotPrefab == Entity.Null ||
                            EntityManager.HasComponent<ExtractorAreaData>(lotPrefab) ||
                            !TryGetRenderedAreaBatchIndex(lotPrefab, lotArea, out int sourceBatchIndex))
                        {
                            continue;
                        }
                        string lotName = m_PrefabSystem.GetPrefabName(lotPrefab);
                        string family = GetFirstWord(lotName);
                        if (!candidatesByIndustry.TryGetValue(family, out List<BatchReplacementCandidate> candidates) || candidates.Count == 0)
                        {
                            continue;
                        }

                        specialIndustryFieldsScanned++;
                        List<BatchReplacementCandidate> eligible = candidates
                            .Where(candidate => !IsUsingSpecialIndustryTarget(lotArea, candidate.TargetPrefab))
                            .ToList();
                        if (eligible.Count == 0)
                        {
                            continue;
                        }

                        if (scheduledAreas.Contains(lotArea))
                        {
                            continue;
                        }

                        BatchReplacementCandidate candidate = eligible[random.Next(eligible.Count)];
                        string sourceName = m_PrefabSystem.GetPrefabName(candidate.SourcePrefab);
                        string targetName = m_PrefabSystem.GetPrefabName(candidate.TargetPrefab);
                        if (!m_IndustryAreasScheduledThisFrame.Add(lotArea))
                            continue;
                        var replacementMarker = new SpecialIndustryLotReplacement { OriginalPrefab = lotPrefab, TargetSurfacePrefab = candidate.TargetPrefab };
                        if (EntityManager.HasComponent<SpecialIndustryLotReplacement>(lotArea))
                            updateCommandBuffer.SetComponent(lotArea, replacementMarker);
                        else
                        {
                            updateCommandBuffer.AddComponent(lotArea, replacementMarker);
                        }
                        updateCommandBuffer.SetComponent(lotArea, new PrefabRef { m_Prefab = candidate.TargetPrefab });
                        if (!EntityManager.HasComponent<Updated>(lotArea))
                        {
                            updateCommandBuffer.AddComponent<Updated>(lotArea);
                        }
                        QueueBatchDiagnostic(lotArea, lotPrefab, candidate.TargetPrefab, sourceName, targetName);
                        scheduledAreas.Add(lotArea);
                        hasUpdateCommandBuffer = true;
                        queued++;
                        specialIndustryFieldsQueued++;
                        Mod.Log.InfoFormat(
                            "Special-industry lot surface replacement scheduled: operation={0}, lot={1}, lotPrefab='{2}', batch={3}, source='{4}', targetSurface='{5}', candidates={6}",
                            operationId,
                            lotArea,
                            lotName,
                            sourceBatchIndex,
                            sourceName,
                            targetName,
                            eligible.Count);
                    }
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
                "Surface replacement scan finished: scannedSurfaceAreas={0}, specialIndustryFieldsScanned={1}, specialIndustryFieldsQueued={2}, assetsWithSurfaceAreas={3}, skippedOutOfScopeAreas={4}, unmatchedSurfaceAreas={5}, replacementsQueued={6}, sourceMappings={7}",
                scanned,
                specialIndustryFieldsScanned,
                specialIndustryFieldsQueued,
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
                "Area batch diagnostic queued: area={0}, source='{1}', sourcePrefabBatch={2}, target='{3}', targetPrefabBatch={4}, previousAreaBatch={5}",
                areaEntity,
                sourceName,
                sourceBatchIndex,
                targetName,
                expectedBatchIndex,
                previousBatchIndex);
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
                    if (!EntityManager.HasComponent<SpecialIndustryLotReplacement>(diagnostic.Area) && !diagnostic.RebuildAttempted)
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
                        if (!EntityManager.HasComponent<SpecialIndustryLotReplacement>(diagnostic.Area) && !diagnostic.RebuildAttempted)
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

        private string GetSpecialIndustryFamilyPrefix(Entity surfacePrefab)
        {
            if (!AutomaticTextureReplacerUISystem.IsSpecialIndustrySurface(m_PrefabSystem, surfacePrefab))
            {
                return null;
            }

            string name = m_PrefabSystem.GetPrefabName(surfacePrefab);
            int surfaceSuffix = name.IndexOf(" Surface", StringComparison.OrdinalIgnoreCase);
            if (surfaceSuffix > 0)
            {
                return GetFirstWord(name);
            }

            return EntityManager.HasComponent<ExtractorAreaData>(surfacePrefab) &&
                   EntityManager.GetComponentData<ExtractorAreaData>(surfacePrefab).m_MapFeature == MapFeature.Fish
                ? GetFirstWord(name)
                : null;
        }

        private static string GetFirstWord(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            int wordEnd = name.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            return wordEnd > 0 ? name.Substring(0, wordEnd).Trim() : name.Trim();
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
