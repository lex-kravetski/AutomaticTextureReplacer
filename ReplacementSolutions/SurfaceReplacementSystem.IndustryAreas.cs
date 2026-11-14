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
    {        private void ProcessIndustryAreaReplacement(ReplacementRequest request)
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
                        (IsExcludedSurfaceName(prefab.name) && !ReplacementRuleCatalog.IsTransparentSurface(prefab.name)))
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

            }

            Dictionary<string, Entity> industriesById = new Dictionary<string, Entity>(StringComparer.Ordinal);
            using (NativeArray<Entity> prefabEntities = m_AllPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefabEntity in prefabEntities)
                {
                    if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    string id = prefab.GetPrefabID().ToString();
                    if (!industriesById.ContainsKey(id))
                    {
                        industriesById.Add(id, prefabEntity);
                    }
                }
            }

            Dictionary<string, HashSet<string>> targetIdsByIndustry = ReplacementRuleCatalog.BuildIndustryTargetIds(
                rules,
                industriesById,
                surfaceEntitiesById,
                enabledOnly: !replaceSingleRule,
                selectedRuleId: replaceSingleRule ? selectedId : (int?)null);
            Dictionary<string, List<Entity>> replacementTargets = targetIdsByIndustry
                .Select(pair => new
                {
                    pair.Key,
                    Targets = pair.Value
                        .Where(surfaceEntitiesById.ContainsKey)
                        .Select(id => surfaceEntitiesById[id])
                        .ToList(),
                })
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

        private bool TryUseTransparentIndustryPrefabClone(
            Entity industryRoot,
            Entity originalIndustryPrefab,
            EntityCommandBuffer commandBuffer,
            long operationId,
            out Entity cloneEntity)
        {
            cloneEntity = Entity.Null;
            if (industryRoot == Entity.Null || originalIndustryPrefab == Entity.Null ||
                !EntityManager.Exists(industryRoot) || !EntityManager.HasComponent<PrefabRef>(industryRoot) ||
                !m_PrefabSystem.TryGetPrefab<PrefabBase>(originalIndustryPrefab, out PrefabBase originalPrefab) ||
                originalPrefab == null || !originalPrefab.TryGet<RenderedArea>(out RenderedArea originalRendered) ||
                originalRendered == null)
            {
                return false;
            }

            Entity transparentSurfaceEntity = FindTransparentSurfacePrefab();

            if (transparentSurfaceEntity == Entity.Null ||
                !m_PrefabSystem.TryGetPrefab<SurfacePrefab>(transparentSurfaceEntity, out SurfacePrefab transparentSurface) ||
                transparentSurface == null || !transparentSurface.TryGet<RenderedArea>(out RenderedArea transparentRendered) ||
                transparentRendered == null || transparentRendered.m_Material == null)
            {
                Mod.Log.WarnFormat(
                    "Transparent industry prefab clone skipped: operation={0}, source='{1}', reason=transparent material unavailable",
                    operationId,
                    originalPrefab.name);
                return false;
            }

            if (!EnsureTransparentIndustryPrefabClone(
                    originalIndustryPrefab,
                    originalPrefab,
                    transparentRendered.m_Material,
                    out cloneEntity,
                    out PrefabBase clone))
            {
                return false;
            }

            if (!clone.TryGet<RenderedArea>(out RenderedArea cloneRendered) || cloneRendered == null)
            {
                return false;
            }

            int cloneBatch = EntityManager.HasComponent<RenderedAreaData>(cloneEntity)
                ? EntityManager.GetComponentData<RenderedAreaData>(cloneEntity).m_BatchIndex
                : -1;
            Mod.Log.InfoFormat(
                "Transparent industry prefab clone render data: source='{0}', clone='{1}', prefabEntity={2}, batch={3}, hasExtractorAreaData={4}, baseAlpha={5}, baseMap={6}, normalMap={7}, maskMap={8}",
                originalPrefab.name,
                clone.name,
                cloneEntity,
                cloneBatch,
                EntityManager.HasComponent<ExtractorAreaData>(cloneEntity),
                cloneRendered.m_BaseColor.a,
                cloneRendered.m_BaseColorMap != null,
                cloneRendered.m_NormalMap != null,
                cloneRendered.m_MaskMap != null);

            QueueTransparentIndustryRootAssignment(industryRoot, originalIndustryPrefab,
                cloneEntity, transparentSurfaceEntity, commandBuffer);

            Mod.Log.InfoFormat(
                "Transparent industry prefab clone assigned: operation={0}, root={1}, source='{2}', clone='{3}'",
                operationId,
                industryRoot,
                originalPrefab.name,
                clone.name);
            return true;
        }

        // These are private copies of extractor Lot prefabs that have both
        // ExtractorAreaData and RenderedAreaData: root-batch industries (such as
        // forestry, ore, oil, and stone) render their default ground surface on the
        // Lot itself instead of on a child Surface. The clone keeps the extractor
        // data but uses the transparent material, allowing the replacement overlay
        // to cover the original ground without changing the industry's type.
        // Build these after the prefab catalog and transparent material are ready,
        // before construction can trigger PrefabSystem registration during a build.
        private void PrewarmTransparentIndustryPrefabClones()
        {
            int prefabCount = m_ExtractorIndustryPrefabQuery.CalculateEntityCount();
            if (prefabCount == 0 || prefabCount == m_PreWarmedExtractorPrefabCount)
            {
                return;
            }

            Entity transparentSurfaceEntity = FindTransparentSurfacePrefab();
            if (transparentSurfaceEntity == Entity.Null ||
                !m_PrefabSystem.TryGetPrefab<SurfacePrefab>(transparentSurfaceEntity, out SurfacePrefab transparentSurface) ||
                transparentSurface == null || !transparentSurface.TryGet<RenderedArea>(out RenderedArea transparentRendered) ||
                transparentRendered == null || transparentRendered.m_Material == null)
            {
                return;
            }

            using (NativeArray<Entity> prefabs = m_ExtractorIndustryPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefabEntity in prefabs)
                {
                    if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }
                    if (prefab.name.EndsWith(" - Replacement Solutions Transparent", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    EnsureTransparentIndustryPrefabClone(
                        prefabEntity,
                        prefab,
                        transparentRendered.m_Material,
                        out _,
                        out _);
                }
            }

            m_PreWarmedExtractorPrefabCount = m_ExtractorIndustryPrefabQuery.CalculateEntityCount();
        }

        private Entity FindTransparentSurfacePrefab()
        {
            using (NativeArray<Entity> surfaces = m_SurfacePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity surfaceEntity in surfaces)
                {
                    if (m_PrefabSystem.TryGetPrefab<SurfacePrefab>(surfaceEntity, out SurfacePrefab surface) &&
                        surface != null && ReplacementRuleCatalog.IsTransparentSurface(surface.name))
                    {
                        return surfaceEntity;
                    }
                }
            }

            return Entity.Null;
        }

        private bool EnsureTransparentIndustryPrefabClone(
            Entity originalIndustryPrefab,
            PrefabBase originalPrefab,
            UnityEngine.Material transparentMaterial,
            out Entity cloneEntity,
            out PrefabBase clone)
        {
            cloneEntity = Entity.Null;
            clone = null;
            if (originalIndustryPrefab == Entity.Null || originalPrefab == null || transparentMaterial == null)
            {
                return false;
            }

            if (m_TransparentIndustryPrefabClones.TryGetValue(originalIndustryPrefab, out PrefabBase cachedClone) &&
                m_PrefabSystem.TryGetEntity(cachedClone, out cloneEntity) && cloneEntity != Entity.Null &&
                EntityManager.Exists(cloneEntity))
            {
                clone = cachedClone;
                return true;
            }

            string cloneName = originalPrefab.name + " - Replacement Solutions Transparent";
            using (NativeArray<Entity> prefabs = m_AllPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefabEntity in prefabs)
                {
                    if (EntityManager.HasComponent<Deleted>(prefabEntity) ||
                        !m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase registeredPrefab) ||
                        registeredPrefab == null || !string.Equals(registeredPrefab.name, cloneName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    cloneEntity = prefabEntity;
                    clone = registeredPrefab;
                    break;
                }
            }

            bool newlyCreated = clone == null;
            if (newlyCreated)
            {
                clone = originalPrefab.Clone(cloneName);
            }

            if (clone == null || !clone.TryGet<RenderedArea>(out RenderedArea cloneRendered) || cloneRendered == null)
            {
                clone = null;
                cloneEntity = Entity.Null;
                return false;
            }

            cloneRendered.m_Version = RenderedArea.Version.OverrideMaterial;
            cloneRendered.m_Material = transparentMaterial;
            // AreaBatchSystem copies these values into the runtime material when
            // it builds a batch, so clear every visual input on this private clone.
            cloneRendered.m_BaseColor = UnityEngine.Color.clear;
            cloneRendered.m_BaseColorMap = null;
            cloneRendered.m_NormalMap = null;
            cloneRendered.m_MaskMap = null;
            cloneRendered.m_Metallic = 0f;
            cloneRendered.m_Smoothness = 0f;
            cloneRendered.m_NormalOpacity = 0f;
            cloneRendered.m_MetallicOpacity = 0f;

            if (newlyCreated)
            {
                bool added = m_PrefabSystem.AddPrefab(clone);
                if (!added)
                {
                    clone = null;
                    return false;
                }

                cloneEntity = m_PrefabSystem.GetEntity(clone);
            }

            if (cloneEntity == Entity.Null || !EntityManager.Exists(cloneEntity))
            {
                clone = null;
                return false;
            }

            if (!EntityManager.HasComponent<TransparentIndustryPrefabCloneTag>(cloneEntity))
            {
                EntityManager.AddComponent<TransparentIndustryPrefabCloneTag>(cloneEntity);
            }

            m_PrefabSystem.UpdatePrefab(clone);

            m_TransparentIndustryPrefabClones[originalIndustryPrefab] = clone;
            return true;
        }

        private void QueueTransparentIndustryRootAssignment(
            Entity industryRoot,
            Entity originalIndustryPrefab,
            Entity cloneEntity,
            Entity transparentSurfaceEntity,
            EntityCommandBuffer commandBuffer)
        {
            commandBuffer.SetComponent(industryRoot, new PrefabRef { m_Prefab = cloneEntity });
            if (EntityManager.HasComponent<SpecialIndustryLotReplacement>(industryRoot))
            {
                commandBuffer.SetComponent(industryRoot, new SpecialIndustryLotReplacement
                {
                    OriginalPrefab = originalIndustryPrefab,
                    TargetSurfacePrefab = transparentSurfaceEntity,
                });
            }
            else
            {
                commandBuffer.AddComponent(industryRoot, new SpecialIndustryLotReplacement
                {
                    OriginalPrefab = originalIndustryPrefab,
                    TargetSurfacePrefab = transparentSurfaceEntity,
                });
            }
            if (!EntityManager.HasComponent<Updated>(industryRoot))
            {
                commandBuffer.AddComponent<Updated>(industryRoot);
            }

        }

        /// <summary>
        /// Root-batch extractor route for industries whose surface is rendered by
        /// the root Lot prefab (for example forestry, ore, oil, and stone extractors).
        /// Keep that root prefab and its ExtractorAreaData; create a child Surface
        /// area owned by the Lot and copy its Nodes so it follows contour edits and
        /// is deleted with the industry. Child-Lot extractors such as farms/fish use
        /// ApplySurfacePrefabReplacement on their existing field area instead.
        /// </summary>
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

            bool hasSubAreaBuffer = EntityManager.HasBuffer<Game.Areas.SubArea>(industryRoot);
            // Apply the transparent root-prefab clone only after the replacement
            // overlay has completed creation. Changing PrefabRef in the same ECB
            // playback that creates the owned area can corrupt extractor setup.
            // Some extractor root areas (notably Forestry Lot) have no child
            // sub-areas until we add a replacement overlay. Give the owner the
            // buffer needed by SubAreaReferencesSystem before creating the owned
            // surface; leave its ExtractorAreaData and root prefab untouched.
            if (!hasSubAreaBuffer)
            {
                commandBuffer.AddBuffer<Game.Areas.SubArea>(industryRoot);
                Mod.Log.InfoFormat(
                    "Special-industry root SubArea buffer creation queued without prefab swap: operation={0}, root={1}, prefab='{2}'",
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

        private bool TryQueueIndustrySurfaceOverlay(IndustryOverlayReplacementContext context)
        {
            return TryQueueIndustrySurfaceOverlay(
                context.IndustryRoot,
                context.IndustryPrefab,
                context.TargetSurfacePrefab,
                context.CommandBuffer,
                context.OperationId);
        }

        private void SynchronizeIndustrySurfaceOverlay(Entity industryRoot, EntityCommandBuffer commandBuffer)
        {
            SpecialIndustrySurfaceOverlayState state = EntityManager.GetComponentData<SpecialIndustrySurfaceOverlayState>(industryRoot);
            Entity overlay = FindIndustrySurfaceOverlay(industryRoot, state.TargetSurfacePrefab);
            if (overlay == Entity.Null)
            {
                TryQueueIndustrySurfaceOverlay(new IndustryOverlayReplacementContext(
                    industryRoot,
                    state.OriginalIndustryPrefab,
                    state.TargetSurfacePrefab,
                    commandBuffer,
                    ++m_NextOperationId));
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


    }
}
