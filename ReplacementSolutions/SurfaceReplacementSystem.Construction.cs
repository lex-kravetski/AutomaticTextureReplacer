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
    {        private void ProcessPendingConstructionBatchRefreshes()
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
                            pending.RuleSourcePrefab == request.RuleSourcePrefab &&
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
                {
                    Entity mappingSource = !request.MatchIndustryType && request.RuleSourcePrefab != Entity.Null
                        ? request.RuleSourcePrefab
                        : request.SourcePrefab;
                    return !enabledMappings.Contains((request.MatchIndustryType ? "I|" : "S|") + GetPrefabIdString(mappingSource) + "|" + GetPrefabIdString(request.TargetPrefab));
                });
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

                EntityCommandBuffer commandBuffer = default(EntityCommandBuffer);
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
                        foreach (PendingConstructionBatchRefresh request in m_PendingConstructionBatchRefreshes)
                        {
                            if (request.MatchIndustryType || request.SourcePrefab != source ||
                                !MatchesConstructionSurfaceRequest(request, owner) ||
                                !m_QueuedConstructionSurfaceAreas.Add(area))
                                continue;

                            EnsureToolOutputCommandBuffer(ref commandBuffer);
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
                    foreach (Entity area in tempAreas)
                    {
                        if (!EntityManager.Exists(area) || EntityManager.HasComponent<Deleted>(area) ||
                            !EntityManager.HasComponent<Temp>(area) || !EntityManager.HasComponent<PrefabRef>(area))
                        {
                            continue;
                        }

                        Temp temp = EntityManager.GetComponentData<Temp>(area);
                        if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Hidden)) != 0)
                            continue;
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

                        EnsureToolOutputCommandBuffer(ref commandBuffer);
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

                            EnsureToolOutputCommandBuffer(ref commandBuffer);
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
                            EnsureToolOutputCommandBuffer(ref commandBuffer);
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
                if ((!request.MatchIndustryType && request.SurfaceAreaObserved) ||
                    request.SpecialIndustryFieldObserved || request.FramesChecked >= 3600)
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


    }
}
