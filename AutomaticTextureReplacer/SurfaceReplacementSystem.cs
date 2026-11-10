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
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace AutomaticTextureReplacer
{
    public sealed partial class SurfaceReplacementSystem : GameSystemBase
    {
        private sealed class ReplacementRequest
        {
            public string RuleId;
            public string RulesJson;
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

        public sealed class SurfaceReplacementRule
        {
            public int id { get; set; }
            public bool enabled { get; set; }
            public List<string> sourceSurfaceIds { get; set; } = new List<string>();
            public List<string> targetSurfaceIds { get; set; } = new List<string>();
        }

        private static readonly object s_RequestLock = new object();
        private static readonly Queue<ReplacementRequest> s_Requests = new Queue<ReplacementRequest>();
        private readonly List<PendingBatchDiagnostic> m_PendingBatchDiagnostics = new List<PendingBatchDiagnostic>();

        private EntityQuery m_SurfacePrefabQuery;
        private EntityQuery m_SurfaceAreaQuery;
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
                });
            }

            Mod.Log.InfoFormat(
                "Surface replacement request queued: mode={0}, ruleId={1}",
                string.IsNullOrWhiteSpace(requestedRuleId) ? "Replace All" : "single rule",
                string.IsNullOrWhiteSpace(requestedRuleId) ? "all enabled" : requestedRuleId);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_AreaBatchSystem = World.GetOrCreateSystemManaged<AreaBatchSystem>();
            m_ToolOutputBarrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();

            m_SurfacePrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<SurfaceData>(),
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
            m_AreaBatchEntityQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<Batch>() },
                None = new[] { ComponentType.ReadOnly<Temp>() },
            });
        }

        protected override void OnUpdate()
        {
            LogPendingBatchDiagnostics();
            while (TryDequeue(out ReplacementRequest request))
            {
                ProcessReplacement(request);
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            lock (s_RequestLock)
            {
                s_Requests.Clear();
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
                    if (!m_PrefabSystem.TryGetPrefab<SurfacePrefab>(prefabEntity, out SurfacePrefab prefab) || prefab == null)
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
                "Surface replacement started: mode={0}, ruleId={1}, ruleCount={2}, mapSurfaceAreaCount={3}, catalogSurfaceCount={4}",
                replaceSingleRule ? "single rule" : "Replace All",
                replaceSingleRule ? selectedRuleId.ToString() : "all enabled",
                rules.Count,
                m_SurfaceAreaQuery.CalculateEntityCount(),
                surfaceEntitiesById.Count);

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
                        if (!string.IsNullOrEmpty(targetId) && surfaceEntitiesById.ContainsKey(targetId))
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
            int queued = 0;
            HashSet<string> scannedAssets = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, List<string>> surfacesByAsset = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (NativeArray<Entity> areaEntities = m_SurfaceAreaQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity areaEntity in areaEntities)
                {
                    scanned++;
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(areaEntity);
                    Entity buildingEntity = GetOwningBuildingEntity(areaEntity);
                    string assetName = GetAssetName(buildingEntity);
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
                "Surface replacement scan finished: scannedSurfaceAreas={0}, assetsWithSurfaceAreas={1}, unmatchedSurfaceAreas={2}, replacementsQueued={3}, sourceMappings={4}",
                scanned,
                scannedAssets.Count,
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

                if (prefabMatches && batchMatches)
                {
                    bool sourceContainsArea = LogBatchMembership(diagnostic);
                    bool targetContainsArea = TryBatchContainsArea(diagnostic.ExpectedBatchIndex, diagnostic.Area, out _);
                    if (sourceContainsArea || !targetContainsArea)
                    {
                        if (!diagnostic.RebuildAttempted)
                        {
                            diagnostic.RebuildAttempted = true;
                            diagnostic.FramesChecked = 0;
                            rebuildAreaBatchCaches = true;
                            Mod.Log.WarnFormat(
                                "Area batch membership is stale; scheduling a full AreaBatchSystem rebuild: area={0}, sourceBatch={1}, targetBatch={2}, sourceContainsArea={3}, targetContainsArea={4}",
                                diagnostic.Area,
                                diagnostic.SourceBatchIndex,
                                diagnostic.ExpectedBatchIndex,
                                sourceContainsArea,
                                targetContainsArea);
                            continue;
                        }

                        if (diagnostic.FramesChecked < 4)
                        {
                            continue;
                        }

                        Mod.Log.ErrorFormat(
                            "Area batch membership remains stale after rebuild: area={0}, sourceBatch={1}, targetBatch={2}, sourceContainsArea={3}, targetContainsArea={4}",
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
