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
    public sealed partial class SurfaceReplacementSystem
    {        private void LogSurfaceGeometry(string path, Entity area, Entity prefab)
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
            // Temporary areas are frequently replaced by placement tools and
            // preset generators. Their batch membership is transient; resetting
            // the global area batch cache for them can race their deletion.
            if (!IsReplacementAreaUsable(areaEntity) || EntityManager.HasComponent<Temp>(areaEntity))
            {
                return;
            }

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

            // A new replacement/revert supersedes any still-pending check for
            // this same area. Keep the diagnostic aligned with its latest
            // PrefabRef instead of rebuilding a stale target batch.
            m_PendingBatchDiagnostics.RemoveAll(diagnostic => diagnostic.Area == areaEntity);

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
                if (!EntityManager.Exists(diagnostic.Area) || EntityManager.HasComponent<Deleted>(diagnostic.Area))
                {
                    Mod.Log.WarnFormat(
                        "Area batch diagnostic stopped: area no longer exists or is deleted; area={0}, target='{1}'",
                        diagnostic.Area,
                        diagnostic.TargetName);
                    m_PendingBatchDiagnostics.RemoveAt(i);
                    continue;
                }

                if (EntityManager.HasComponent<Temp>(diagnostic.Area))
                {
                    Mod.Log.InfoFormat(
                        "Area batch diagnostic stopped for transient area: area={0}, target='{1}'",
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
                // A cloned extractor root keeps its Lot/Extractor components, so
                // the older guard excluded it even though its PrefabRef now
                // points at a new render batch. Farm replacements happened to
                // rebuild the global cache later, making the stone lot update.
                bool currentPrefabIsOurTransparentClone = currentPrefab != Entity.Null &&
                    m_PrefabSystem.TryGetPrefab<PrefabBase>(currentPrefab, out PrefabBase currentClone) &&
                    m_TransparentIndustryPrefabClones.Values.Contains(currentClone);
                bool canRebuildAreaBatch = !hasSpecialIndustryMarker || currentPrefabIsSurface ||
                    currentPrefabIsOurTransparentClone;
                bool hasBatch = EntityManager.HasComponent<Batch>(diagnostic.Area);
                Batch batch = hasBatch ? EntityManager.GetComponentData<Batch>(diagnostic.Area) : default(Batch);
                bool hasUpdated = EntityManager.HasComponent<Updated>(diagnostic.Area);
                bool prefabMatches = currentPrefab == diagnostic.ExpectedPrefab ||
                    (hasSpecialIndustryMarker &&
                     string.Equals(currentPrefabName, diagnostic.TargetName, StringComparison.Ordinal));
                // PrefabSystem assigns a batch to newly registered visual variants
                // asynchronously. The value captured when queuing can be stale (often 0).
                if (prefabMatches && currentPrefabBatch >= 0)
                {
                    diagnostic.ExpectedPrefab = currentPrefab;
                    diagnostic.ExpectedBatchIndex = currentPrefabBatch;
                }
                if (currentPrefabIsOurTransparentClone && prefabMatches && hasBatch &&
                    batch.m_BatchIndex >= 0 && TryBatchContainsArea(batch.m_BatchIndex, diagnostic.Area, out _))
                {
                    string runtimeBatchMaterial = GetAreaBatchMaterialName(batch.m_BatchIndex);
                    string expectedRuntimeMaterial = "Area batch (" + currentPrefabName + ")";
                    if (string.Equals(runtimeBatchMaterial, expectedRuntimeMaterial, StringComparison.Ordinal))
                    {
                        // A registered runtime clone can have prefab BatchIndex 0 while
                        // AreaBatchSystem has already assigned its valid runtime batch.
                        // Trust the runtime membership/material instead of globally
                        // rebuilding every area batch for this expected index mismatch.
                        diagnostic.ExpectedBatchIndex = batch.m_BatchIndex;
                    }
                }
                if (hasSpecialIndustryMarker && prefabMatches && currentPrefabBatch > 0 &&
                    (diagnostic.FramesChecked == 1 || diagnostic.FramesChecked == 2) &&
                    currentPrefabBatch < m_AreaBatchSystem.GetBatchCount())
                {
                    bool texturesReady = m_AreaBatchSystem.GetAreaBatch(currentPrefabBatch, out _, out _, out _,
                        out UnityEngine.Material batchMaterial, out _, out _, out _);
                    if (m_PrefabSystem.TryGetPrefab<PrefabBase>(currentPrefab, out PrefabBase currentAsset) &&
                        currentAsset != null && currentAsset.TryGet<RenderedArea>(out RenderedArea rendered) &&
                        rendered != null)
                    {
                        Mod.Log.InfoFormat(
                            "Transparent industry render path: area={0}, prefabEntity={1}, batch={2}, material='{3}', shader='{4}', alpha={5}, maps={6}/{7}/{8}, texturesReady={9}",
                            diagnostic.Area,
                            currentPrefab,
                            currentPrefabBatch,
                            batchMaterial != null ? batchMaterial.name : "<none>",
                            batchMaterial != null ? batchMaterial.shader.name : "<none>",
                            batchMaterial != null && batchMaterial.HasProperty("_BaseColor")
                                ? batchMaterial.GetColor("_BaseColor").a : rendered.m_BaseColor.a,
                            rendered.m_BaseColorMap != null,
                            rendered.m_NormalMap != null,
                            rendered.m_MaskMap != null,
                            texturesReady);
                    }
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
                    // A new internal industry clone often has no prefab batch index
                    // for a few frames even after AreaBatchSystem has assigned a
                    // valid runtime batch to its area. Let that registration settle
                    // before requesting a global cache rebuild.
                    bool cloneRegistrationStillSettling = currentPrefabIsOurTransparentClone &&
                        diagnostic.FramesChecked < 8;
                    if (canRebuildAreaBatch && !diagnostic.RebuildAttempted && !cloneRegistrationStillSettling)
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
                    bool sourceAndTargetAreSameBatch = diagnostic.SourceBatchIndex >= 0 &&
                        diagnostic.SourceBatchIndex == diagnostic.ExpectedBatchIndex;
                    if ((!sourceAndTargetAreSameBatch && sourceContainsArea) || !targetContainsArea)
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
                    Entity area = areas[i];
                    if (EntityManager.Exists(area) && !EntityManager.HasComponent<Deleted>(area) &&
                        !EntityManager.HasComponent<Temp>(area) && EntityManager.HasComponent<Batch>(area))
                    {
                        EntityManager.SetComponentData(area, default(Batch));
                    }
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

        private static bool TryGetParenthesizedContent(string value, out string content)
        {
            content = null;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            int openParenthesis = value.IndexOf('(');
            int closeParenthesis = value.LastIndexOf(')');
            if (openParenthesis < 0 || closeParenthesis <= openParenthesis + 1)
            {
                return false;
            }

            content = value.Substring(openParenthesis + 1, closeParenthesis - openParenthesis - 1);
            return true;
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
