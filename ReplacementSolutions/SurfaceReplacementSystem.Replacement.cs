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
    {        private static bool TryDequeue(out ReplacementRequest request)
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
            m_ReplacementMaterialSources.Clear();
            m_MissingDecalMaterialSinceFrame.Clear();
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
                        ApplySurfacePrefabRevert(area, defaultPrefab, commandBuffer);
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
                            if (QueueIndustrySurfaceOverlayRevert(area, commandBuffer))
                            {
                                changed++;
                            }
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

                        ApplySurfacePrefabRevert(area, originalPrefab, commandBuffer, removeSpecialIndustryMarker: true);
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

        /// <summary>
        /// Shared dispatcher for replacing the prefab of an existing area entity.
        /// Ordinary building and hand-painted surfaces, plus extractor fields that
        /// already exist as child Lot entities (such as farm/fish fields), use this
        /// path. The latter pass originalSpecialIndustryPrefab so the original Lot
        /// can be restored and its extractor identity retained.
        ///
        /// Root-batch extractors are different: their rendered surface is stored on
        /// the root Lot prefab itself. Those must keep that root PrefabRef and use
        /// TryQueueIndustrySurfaceOverlay to add an owner-linked child surface; a
        /// direct root swap would discard the Lot's ExtractorAreaData and area type.
        /// Temporary preview entities are routed through the same-update batch
        /// release queue below because placement tools may destroy them immediately.
        /// </summary>
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
                !IsReplacementAreaUsable(area))
            {
                return false;
            }

            if (EntityManager.HasComponent<Temp>(area))
            {
                // Preview entities already have a native allocation in their
                // source prefab's batch. Queue them for the synchronous cleanup
                // path instead of changing PrefabRef while those old indexes live.
                int originalBatchIndex = EntityManager.HasComponent<Batch>(area)
                    ? EntityManager.GetComponentData<Batch>(area).m_BatchIndex
                    : -1;
                Entity originalOwner = EntityManager.HasComponent<Owner>(area)
                    ? EntityManager.GetComponentData<Owner>(area).m_Owner
                    : Entity.Null;
                Temp originalTemp = EntityManager.GetComponentData<Temp>(area);
                TemporaryAreaBatchReleaseQueue.Enqueue(new TemporaryAreaBatchReleaseQueue.Request
                {
                    Area = area,
                    SourcePrefab = sourcePrefab,
                    TargetPrefab = targetPrefab,
                    InitialOwner = originalOwner,
                    OriginalTemp = originalTemp,
                    OriginalBatchIndex = originalBatchIndex,
                    SourceName = sourceName,
                    TargetName = targetName,
                });
                Mod.Log.InfoFormat(
                    "Temporary surface replacement queued for safe batch release: area={0}, source='{1}', target='{2}', oldBatch={3}",
                    area,
                    sourceName,
                    targetName,
                    originalBatchIndex);
                return true;
            }

            if (originalSpecialIndustryPrefab != Entity.Null)
            {
                // Child Lot areas for farm/fish extraction fields still carry
                // industry-specific data. Keep the original Lot identity in a
                // per-instance marker while rendering this instance as a surface.
                // Root-batch extractors do not use this marker route; they receive
                // a separate overlay so their root Lot prefab remains intact.
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

        /// <summary>
        /// Finalizes the temporary-preview route after AreaBatchSystem has released
        /// the source allocation. At this point Batch can be reset safely, the target
        /// PrefabRef assigned, and the original Temp state restored before the tool
        /// gets another chance to destroy the preview entity.
        /// </summary>
        internal bool CompleteTemporarySurfaceReplacement(
            Entity area,
            Entity sourcePrefab,
            Entity targetPrefab,
            string sourceName,
            string targetName,
            Entity initialOwner,
            int originalBatchIndex,
            Temp originalTemp,
            bool hadTemp)
        {
            if (!EntityManager.Exists(area) || !EntityManager.HasComponent<PrefabRef>(area) ||
                EntityManager.HasComponent<Deleted>(area))
            {
                return false;
            }

            if (EntityManager.HasComponent<Batch>(area))
            {
                Batch batch = EntityManager.GetComponentData<Batch>(area);
                if (batch.m_AllocatedSize != 0)
                {
                    Mod.Log.WarnFormat(
                        "Temporary surface batch release has not completed; keeping original prefab: area={0}, allocated={1}, oldBatch={2}",
                        area,
                        batch.m_AllocatedSize,
                        batch.m_BatchIndex);
                    if (hadTemp && !EntityManager.HasComponent<Temp>(area))
                    {
                        EntityManager.AddComponentData(area, originalTemp);
                    }
                    return false;
                }

                // AreaBatchSystem has now released the allocation and removed
                // its old per-batch metadata. It is safe to clear the remaining
                // indexes before the target prefab gets a fresh allocation.
                EntityManager.SetComponentData(area, default(Batch));
            }

            PrepareSurfaceReplacement(area, sourcePrefab, targetPrefab, sourceName, targetName);
            EntityManager.SetComponentData(area, new PrefabRef { m_Prefab = targetPrefab });
            if (hadTemp && !EntityManager.HasComponent<Temp>(area))
            {
                EntityManager.AddComponentData(area, originalTemp);
            }
            if (!EntityManager.HasComponent<Updated>(area))
            {
                EntityManager.AddComponent<Updated>(area);
            }

            if (!m_TemporaryPreviewReplacementTraces.TryGetValue(area, out TemporaryPreviewReplacementTrace trace))
            {
                m_TemporaryPreviewReplacementTraces.Add(area, new TemporaryPreviewReplacementTrace
                {
                    InitialOwner = initialOwner,
                    OriginalPrefab = sourcePrefab,
                    TargetPrefab = targetPrefab,
                    OriginalBatchIndex = originalBatchIndex,
                    ReplacedAtFrame = UnityEngine.Time.frameCount,
                });
            }
            else
            {
                trace.TargetPrefab = targetPrefab;
            }

            Mod.Log.InfoFormat(
                "Temporary construction surface replaced after old batch release: area={0}, source='{1}', target='{2}', oldBatch={3}",
                area,
                sourceName,
                targetName,
                originalBatchIndex);
            return true;
        }

        /// <summary>
        /// Convenience route for callers that know only the target. Resolve the
        /// source from this area's current PrefabRef, then delegate to the shared
        /// dispatcher. sourceNameOverride is used when tools wrap source names
        /// (for example, a Placemaker overlay name containing the base surface).
        /// </summary>
        private bool ApplySurfacePrefabReplacement(
            Entity area,
            Entity targetPrefab,
            EntityCommandBuffer commandBuffer,
            Entity originalSpecialIndustryPrefab = default,
            string sourceNameOverride = null)
        {
            if (area == Entity.Null || !IsReplacementAreaUsable(area))
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

        private bool IsReplacementAreaUsable(Entity area)
        {
            if (area == Entity.Null || !EntityManager.Exists(area) ||
                EntityManager.HasComponent<Deleted>(area) || !EntityManager.HasComponent<PrefabRef>(area))
            {
                return false;
            }

            if (EntityManager.HasComponent<Temp>(area))
            {
                Temp temp = EntityManager.GetComponentData<Temp>(area);
                if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Hidden)) != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private bool MatchesConstructionSurfaceRequest(PendingConstructionBatchRefresh request, Entity building)
        {
            if (request.ConstructionOwner != Entity.Null)
            {
                if (building != Entity.Null && request.ConstructionOwner == building)
                {
                    return true;
                }
            }

            if (!request.HasConstructionOwnerDefinition || building == Entity.Null ||
                !EntityManager.HasComponent<PrefabRef>(building) ||
                !EntityManager.HasComponent<Game.Objects.Transform>(building))
            {
                return false;
            }

            if (EntityManager.GetComponentData<PrefabRef>(building).m_Prefab != request.ConstructionOwnerDefinition.m_Prefab)
            {
                return false;
            }

            Game.Objects.Transform transform = EntityManager.GetComponentData<Game.Objects.Transform>(building);
            float3 positionDelta = transform.m_Position - request.ConstructionOwnerDefinition.m_Position;
            float rotationAlignment = math.abs(math.dot(
                transform.m_Rotation.value,
                request.ConstructionOwnerDefinition.m_Rotation.value));
            return math.lengthsq(positionDelta) <= 0.25f && rotationAlignment >= 0.999f;
        }

        private void EnsureToolOutputCommandBuffer(ref EntityCommandBuffer commandBuffer)
        {
            if (!commandBuffer.IsCreated)
            {
                commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
            }
        }

        private void PrepareSurfaceReplacement(
            Entity area,
            Entity sourcePrefab,
            Entity targetPrefab,
            string sourceName,
            string targetName)
        {
            // Keep the target prefab's geometric expand amount. Copying the source
            // amount can make mod textures extend past the road-facing edge; the
            // per-area Expand buffer already carries the contour adjustments.
            CopyReplacementDecalLayerMask(sourcePrefab, targetPrefab);
            QueueBatchDiagnostic(area, sourcePrefab, targetPrefab, sourceName, targetName);
        }

        private bool ApplySurfacePrefabRevert(
            Entity area,
            Entity originalPrefab,
            EntityCommandBuffer commandBuffer,
            bool removeSpecialIndustryMarker = false)
        {
            if (area == Entity.Null || originalPrefab == Entity.Null || !EntityManager.Exists(area) ||
                !EntityManager.HasComponent<PrefabRef>(area))
            {
                return false;
            }

            Entity currentPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
            if (currentPrefab == originalPrefab)
            {
                return false;
            }

            PrepareSurfaceReplacement(
                area,
                currentPrefab,
                originalPrefab,
                m_PrefabSystem.GetPrefabName(currentPrefab),
                m_PrefabSystem.GetPrefabName(originalPrefab));
            commandBuffer.SetComponent(area, new PrefabRef { m_Prefab = originalPrefab });
            if (removeSpecialIndustryMarker && EntityManager.HasComponent<SpecialIndustryLotReplacement>(area))
            {
                commandBuffer.RemoveComponent<SpecialIndustryLotReplacement>(area);
            }
            if (!EntityManager.HasComponent<Updated>(area))
            {
                commandBuffer.AddComponent<Updated>(area);
            }
            return true;
        }

        private bool QueueIndustrySurfaceOverlayRevert(Entity industryRoot, EntityCommandBuffer commandBuffer)
        {
            if (industryRoot == Entity.Null || !EntityManager.HasComponent<SpecialIndustrySurfaceOverlayState>(industryRoot))
            {
                return false;
            }

            m_IndustryAreasRevertedThisFrame.Add(industryRoot);
            SpecialIndustrySurfaceOverlayState overlayState = EntityManager.GetComponentData<SpecialIndustrySurfaceOverlayState>(industryRoot);
            Entity overlay = FindIndustrySurfaceOverlay(industryRoot, overlayState.TargetSurfacePrefab);
            if (overlay != Entity.Null && !EntityManager.HasComponent<Deleted>(overlay))
            {
                commandBuffer.AddComponent<Deleted>(overlay);
            }
            if (EntityManager.HasComponent<SpecialIndustryLotReplacement>(industryRoot) &&
                EntityManager.HasComponent<PrefabRef>(industryRoot))
            {
                SpecialIndustryLotReplacement replacement = EntityManager.GetComponentData<SpecialIndustryLotReplacement>(industryRoot);
                if (replacement.OriginalPrefab != Entity.Null)
                {
                    Entity currentPrefab = EntityManager.GetComponentData<PrefabRef>(industryRoot).m_Prefab;
                    if (currentPrefab != replacement.OriginalPrefab)
                    {
                        QueueBatchDiagnostic(
                            industryRoot,
                            currentPrefab,
                            replacement.OriginalPrefab,
                            m_PrefabSystem.GetPrefabName(currentPrefab),
                            m_PrefabSystem.GetPrefabName(replacement.OriginalPrefab));
                    }
                    commandBuffer.SetComponent(industryRoot, new PrefabRef { m_Prefab = replacement.OriginalPrefab });
                    commandBuffer.RemoveComponent<SpecialIndustryLotReplacement>(industryRoot);
                    if (!EntityManager.HasComponent<Updated>(industryRoot))
                    {
                        commandBuffer.AddComponent<Updated>(industryRoot);
                    }
                }
            }
            commandBuffer.RemoveComponent<SpecialIndustrySurfaceOverlayState>(industryRoot);
            Mod.Log.InfoFormat(
                "Special-industry surface overlay reverted: root={0}, overlay={1}, target='{2}'",
                industryRoot,
                overlay,
                overlayState.TargetSurfacePrefab != Entity.Null
                    ? m_PrefabSystem.GetPrefabName(overlayState.TargetSurfacePrefab)
                    : "<none>");
            return true;
        }

        private void CopyReplacementDecalLayerMask(Entity sourcePrefab, Entity targetPrefab)
        {
            if (sourcePrefab == Entity.Null || targetPrefab == Entity.Null ||
                !EntityManager.HasComponent<RenderedAreaData>(targetPrefab))
            {
                return;
            }

            if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(sourcePrefab, out PrefabBase source) || source == null)
            {
                LogDecalMaskIssue(sourcePrefab, targetPrefab, "source prefab object unavailable");
                return;
            }
            if (!source.TryGet<RenderedArea>(out RenderedArea sourceRendered) || sourceRendered == null)
            {
                LogDecalMaskIssue(sourcePrefab, targetPrefab, "source RenderedArea component unavailable");
                return;
            }

            m_ReplacementMaterialSources[targetPrefab] = sourcePrefab;
            int sourceDecalLayerMask = m_EffectiveDecalLayerMasks.TryGetValue(sourcePrefab, out int effectiveSourceMask)
                ? effectiveSourceMask
                : (int)sourceRendered.m_DecalLayerMask;
            m_EffectiveDecalLayerMasks[targetPrefab] = sourceDecalLayerMask;

            int batchIndex = EntityManager.GetComponentData<RenderedAreaData>(targetPrefab).m_BatchIndex;
            if (!m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out UnityEngine.Material material, out _, out _, out _) ||
                material == null)
            {
                // A newly registered area batch can take several frames to acquire its material.
                // Keep retrying, but report only a prolonged failure rather than normal initialization.
                if (!m_MissingDecalMaterialSinceFrame.TryGetValue(targetPrefab, out int firstMissingFrame))
                {
                    m_MissingDecalMaterialSinceFrame[targetPrefab] = UnityEngine.Time.frameCount;
                }
                else if (UnityEngine.Time.frameCount - firstMissingFrame >= 600)
                {
                    LogDecalMaskIssue(sourcePrefab, targetPrefab, "target area batch material still unavailable after 600 frames; retrying");
                }
                return;
            }
            m_MissingDecalMaterialSinceFrame.Remove(targetPrefab);

            // A material is normally rebuilt only when its batch changes. Do not
            // rewrite it (and do not log) on every simulation frame.
            if (m_AppliedReplacementMaterials.TryGetValue(targetPrefab, out UnityEngine.Material appliedMaterial) &&
                ReferenceEquals(appliedMaterial, material) &&
                m_AppliedReplacementMaterialSources.TryGetValue(targetPrefab, out Entity appliedSource) &&
                appliedSource == sourcePrefab)
            {
                return;
            }

            int decalLayerMask = UnityEngine.Shader.PropertyToID("colossal_DecalLayerMask");
            bool hasDecalLayerMask = material.HasProperty(decalLayerMask);
            if (hasDecalLayerMask)
            {
                material.SetFloat(decalLayerMask, math.asfloat(sourceDecalLayerMask));
            }

            m_AppliedReplacementMaterials[targetPrefab] = material;
            m_AppliedReplacementMaterialSources[targetPrefab] = sourcePrefab;
            if (m_PrefabSystem.TryGetPrefab<PrefabBase>(targetPrefab, out PrefabBase target) &&
                target != null && target.TryGet<RenderedArea>(out RenderedArea targetRendered) && targetRendered != null)
            {
                Mod.Log.InfoFormat(
                    "Copied source surface decal layer mask: source='{0}', effectiveMask=0x{1:X}, target='{2}', previousMask={3}, materialHasMask={4}, batch={5}",
                    m_PrefabSystem.GetPrefabName(sourcePrefab),
                    sourceDecalLayerMask,
                    m_PrefabSystem.GetPrefabName(targetPrefab),
                    targetRendered.m_DecalLayerMask,
                    hasDecalLayerMask,
                    batchIndex);
            }
        }

        private void LogDecalMaskIssue(Entity sourcePrefab, Entity targetPrefab, string reason)
        {
            string key = sourcePrefab.Index + ":" + targetPrefab.Index + ":" + reason;
            if (m_LoggedDecalMaskIssues.Add(key))
            {
                Mod.Log.WarnFormat(
                    "Could not copy replacement surface decal layer mask: source='{0}', target='{1}', reason={2}",
                    m_PrefabSystem.GetPrefabName(sourcePrefab),
                    m_PrefabSystem.GetPrefabName(targetPrefab),
                    reason);
            }
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
            Dictionary<string, string> surfaceIdsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<Entity, string> parenthesizedSurfaceNames = new Dictionary<Entity, string>();
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
                    if ((IsExcludedSurfaceName(prefab.name) && !ReplacementRuleCatalog.IsTransparentSurface(prefab.name)) ||
                        surfaceEntitiesById.ContainsKey(id))
                    {
                        continue;
                    }

                    surfaceEntitiesById.Add(id, prefabEntity);
                    surfaceIdsByEntity[prefabEntity] = id;
                    if (TryGetParenthesizedContent(prefab.name, out string parenthesizedContent))
                    {
                        parenthesizedSurfaceNames[prefabEntity] = parenthesizedContent;
                    }
                    else if (!surfaceIdsByName.ContainsKey(prefab.name))
                    {
                        surfaceIdsByName.Add(prefab.name, id);
                    }
                }
            }

            // Some tools wrap the selected surface name in parentheses, for
            // example "Placemaker Overlay (Tiles Surface 03)". Resolve any
            // configured surface name contained in that text to its rule ID.
            foreach (KeyValuePair<Entity, string> surface in parenthesizedSurfaceNames)
            {
                string originalSurfaceName = null;
                string originalSurfaceId = null;
                foreach (KeyValuePair<string, string> knownSurface in surfaceIdsByName)
                {
                    if (knownSurface.Key.Length <= (originalSurfaceName?.Length ?? 0) ||
                        surface.Value.IndexOf(knownSurface.Key, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    originalSurfaceName = knownSurface.Key;
                    originalSurfaceId = knownSurface.Value;
                }

                if (originalSurfaceId != null)
                {
                    surfaceIdsByEntity[surface.Key] = originalSurfaceId;
                    Mod.Log.InfoFormat("Resolved parenthesized surface wrapper: wrapper='{0}', source='{1}'",
                        m_PrefabSystem.GetPrefabName(surface.Key),
                        GetSurfaceDisplayName(originalSurfaceId, surfaceEntitiesById));
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


    }
}
