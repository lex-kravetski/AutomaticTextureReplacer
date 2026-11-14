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
    {        private void ProcessPendingTemporarySurfaceReplacements()
        {
            TemporaryAreaBatchReleaseQueue.Request[] requests = TemporaryAreaBatchReleaseQueue.TakePending();
            if (requests.Length == 0)
            {
                return;
            }

            List<TemporaryAreaBatchReleaseQueue.Request> releases = new List<TemporaryAreaBatchReleaseQueue.Request>();
            foreach (TemporaryAreaBatchReleaseQueue.Request request in requests)
            {
                if (!IsReplacementAreaUsable(request.Area) || !EntityManager.HasComponent<Temp>(request.Area))
                {
                    Mod.Log.InfoFormat(
                        "Temporary surface replacement skipped because the preview entity is no longer usable: area={0}, source='{1}', target='{2}'",
                        request.Area,
                        request.SourceName,
                        request.TargetName);
                    continue;
                }

                if (!EntityManager.HasComponent<Batch>(request.Area))
                {
                    continue;
                }

                Batch batch = EntityManager.GetComponentData<Batch>(request.Area);
                if (batch.m_AllocatedSize <= 0)
                {
                    continue;
                }

                batch.m_VisibleCount = Math.Max(1, batch.m_VisibleCount);
                EntityManager.SetComponentData(request.Area, batch);
                EntityManager.RemoveComponent<Temp>(request.Area);
                EntityManager.AddComponent<Deleted>(request.Area);
                releases.Add(request);
            }

            if (releases.Count > 0)
            {
                Mod.Log.InfoFormat(
                    "Running AreaBatchSystem once to release old temporary surface allocations: count={0}",
                    releases.Count);
                try
                {
                    // AreaBatchSystem's normal update query excludes Temp areas,
                    // but its Deleted path is the supported per-area cleanup:
                    // it releases m_AreaBufferAllocator and removes the area's
                    // entry from the old batch's AreaMetaData. Resetting Batch
                    // before this path leaks that allocation; keeping the old
                    // Batch while changing PrefabRef leaves its old batch and
                    // metadata indexes attached to the new prefab and can crash.
                    // Run this synchronously and complete m_DataDependencies so
                    // Deleted can be removed and the target PrefabRef assigned
                    // before the placement tool destroys its temporary entity.
                    m_AreaBatchSystem.Update();
                    CompleteAreaBatchDataDependencies();
                }
                catch (Exception exception)
                {
                    Mod.Log.Error(exception, "Synchronous AreaBatchSystem cleanup failed for temporary surfaces");
                    RestoreTemporarySurfaceMarkers(releases);
                    return;
                }
            }

            int replaced = 0;
            int releaseFailed = 0;
            foreach (TemporaryAreaBatchReleaseQueue.Request request in requests)
            {
                if (!EntityManager.Exists(request.Area))
                {
                    Mod.Log.WarnFormat(
                        "Temporary surface disappeared during synchronous batch cleanup: area={0}, source='{1}', target='{2}'",
                        request.Area,
                        request.SourceName,
                        request.TargetName);
                    continue;
                }

                bool releaseWasRequested = releases.Contains(request);
                if (releaseWasRequested &&
                    (!EntityManager.HasComponent<Batch>(request.Area) ||
                     EntityManager.GetComponentData<Batch>(request.Area).m_AllocatedSize != 0))
                {
                    releaseFailed++;
                    RestoreTemporarySurfaceMarkers(new[] { request });
                    Mod.Log.WarnFormat(
                        "AreaBatchSystem did not release the temporary area's allocation; keeping original prefab: area={0}, allocated={1}",
                        request.Area,
                        EntityManager.HasComponent<Batch>(request.Area)
                            ? EntityManager.GetComponentData<Batch>(request.Area).m_AllocatedSize
                            : -1);
                    continue;
                }

                if (EntityManager.HasComponent<Deleted>(request.Area))
                {
                    EntityManager.RemoveComponent<Deleted>(request.Area);
                }

                if (CompleteTemporarySurfaceReplacement(
                    request.Area,
                    request.SourcePrefab,
                    request.TargetPrefab,
                    request.SourceName,
                    request.TargetName,
                    request.InitialOwner,
                    request.OriginalBatchIndex,
                    request.OriginalTemp,
                    hadTemp: true))
                {
                    replaced++;
                }
            }

            Mod.Log.InfoFormat(
                "Temporary surface batch release pass finished: requested={0}, released={1}, replaced={2}, releaseFailed={3}",
                requests.Length,
                releases.Count,
                replaced,
                releaseFailed);
        }

        private void CompleteAreaBatchDataDependencies()
        {
            if (s_AreaBatchDataDependenciesField == null)
            {
                throw new InvalidOperationException("AreaBatchSystem.m_DataDependencies was not found");
            }

            object boxedHandle = s_AreaBatchDataDependenciesField.GetValue(m_AreaBatchSystem);
            if (!(boxedHandle is JobHandle handle))
            {
                throw new InvalidOperationException("AreaBatchSystem.m_DataDependencies is not a JobHandle");
            }

            handle.Complete();
        }

        private void RestoreTemporarySurfaceMarkers(IEnumerable<TemporaryAreaBatchReleaseQueue.Request> requests)
        {
            foreach (TemporaryAreaBatchReleaseQueue.Request request in requests)
            {
                if (!EntityManager.Exists(request.Area))
                {
                    continue;
                }

                if (EntityManager.HasComponent<Deleted>(request.Area))
                {
                    EntityManager.RemoveComponent<Deleted>(request.Area);
                }
                if (!EntityManager.HasComponent<Temp>(request.Area))
                {
                    EntityManager.AddComponentData(request.Area, request.OriginalTemp);
                }
            }
        }

        private void ProcessDeferredIndustryRootClones()
        {
            // The replacement overlay must finish creation before changing the
            // root PrefabRef. Doing both in one command-buffer playback can
            // corrupt extractor setup, including roots that already had SubArea.
            EntityCommandBuffer commandBuffer = default(EntityCommandBuffer);
            using (NativeArray<Entity> pendingDefinitions = m_PendingCreationDefinitionQuery.ToEntityArray(Allocator.Temp))
            using (NativeArray<Entity> roots = m_DeferredIndustryRootCloneQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity root in roots)
                {
                    if (m_IndustryAreasScheduledThisFrame.Contains(root) ||
                        m_IndustryAreasRevertedThisFrame.Contains(root))
                    {
                        continue;
                    }

                    Entity pendingDefinition = Entity.Null;
                    foreach (Entity definitionEntity in pendingDefinitions)
                    {
                        CreationDefinition pending = EntityManager.GetComponentData<CreationDefinition>(definitionEntity);
                        if (pending.m_Original == root || pending.m_Owner == root)
                        {
                            pendingDefinition = definitionEntity;
                            break;
                        }
                    }
                    if (pendingDefinition != Entity.Null)
                    {
                        if (m_LoggedDeferredIndustryRoots.Add(root))
                        {
                            Mod.Log.InfoFormat(
                                "Transparent industry root clone deferred until creation completes: root={0}, pendingDefinition={1}",
                                root,
                                pendingDefinition);
                        }
                        continue;
                    }
                    m_LoggedDeferredIndustryRoots.Remove(root);

                    SpecialIndustrySurfaceOverlayState state =
                        EntityManager.GetComponentData<SpecialIndustrySurfaceOverlayState>(root);
                    Entity overlay = FindIndustrySurfaceOverlay(root, state.TargetSurfacePrefab);
                    if (overlay == Entity.Null || EntityManager.HasComponent<Created>(overlay) ||
                        state.OriginalIndustryPrefab == Entity.Null ||
                        !EntityManager.HasComponent<RenderedAreaData>(state.OriginalIndustryPrefab))
                    {
                        continue;
                    }

                    if (!commandBuffer.IsCreated)
                    {
                        commandBuffer = m_ToolOutputBarrier.CreateCommandBuffer();
                    }
                    if (TryUseTransparentIndustryPrefabClone(root, state.OriginalIndustryPrefab,
                            commandBuffer, ++m_NextOperationId, out Entity cloneEntity))
                    {
                        QueueBatchDiagnostic(root, state.OriginalIndustryPrefab, cloneEntity,
                            m_PrefabSystem.GetPrefabName(state.OriginalIndustryPrefab),
                            m_PrefabSystem.GetPrefabName(cloneEntity));
                    }
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

        private void ProcessTemporarySurfacePreviews()
        {
            if (Mod.Settings == null || !Mod.Settings.AlsoNewBuildings || m_TempConstructionSurfaceQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NewBuildingSurfaceReplacementSystem rules = World.GetExistingSystemManaged<NewBuildingSurfaceReplacementSystem>();
            if (rules == null)
            {
                return;
            }

            using (NativeArray<Entity> areas = m_TempConstructionSurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                int staleAreas = 0;
                List<string> staleAreaSamples = new List<string>(5);
                foreach (Entity area in areas)
                {
                    // Preset tools can destroy and recreate their preview entities
                    // within a few frames. Revalidate every entity immediately
                    // before reading or changing its PrefabRef.
                    if (!EntityManager.Exists(area) || EntityManager.HasComponent<Deleted>(area) ||
                        !EntityManager.HasComponent<Temp>(area) || !EntityManager.HasComponent<PrefabRef>(area))
                    {
                        staleAreas++;
                        if (staleAreaSamples.Count < 5)
                        {
                            staleAreaSamples.Add(area.ToString());
                        }
                        continue;
                    }

                    Temp temp = EntityManager.GetComponentData<Temp>(area);
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Hidden)) != 0)
                    {
                        continue;
                    }

                    Entity source = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                    if (source == Entity.Null || !rules.TryChooseSurfaceReplacement(source, out Entity target) ||
                        target == Entity.Null || target == source || !IsReplacementAreaUsable(area))
                    {
                        continue;
                    }

                    ApplySurfacePrefabReplacement(
                        area,
                        source,
                        target,
                        default(EntityCommandBuffer),
                        m_PrefabSystem.GetPrefabName(source),
                        m_PrefabSystem.GetPrefabName(target));
                }

                if (staleAreas > 0)
                {
                    Mod.Log.WarnFormat(
                        "Temporary surface query contained entities that were already gone or no longer usable: count={0}, sample=[{1}]",
                        staleAreas,
                        string.Join(", ", staleAreaSamples));
                }
            }
        }

        private void TraceTemporaryPreviewReplacementLifetimes()
        {
            if (m_TemporaryPreviewReplacementTraces.Count == 0)
            {
                return;
            }

            int frame = UnityEngine.Time.frameCount;
            if (frame % 30 != 0)
            {
                return;
            }

            List<Entity> completed = null;
            foreach (KeyValuePair<Entity, TemporaryPreviewReplacementTrace> pair in m_TemporaryPreviewReplacementTraces)
            {
                Entity area = pair.Key;
                TemporaryPreviewReplacementTrace trace = pair.Value;
                if (!EntityManager.Exists(area))
                {
                    Mod.Log.InfoFormat(
                        "Temporary preview surface entity was destroyed after replacement: area={0}, ownerAtReplacement={1}, source='{2}', target='{3}', framesAlive={4}",
                        area,
                        trace.InitialOwner,
                        GetPrefabNameSafe(trace.OriginalPrefab),
                        GetPrefabNameSafe(trace.TargetPrefab),
                        frame - trace.ReplacedAtFrame);
                    (completed ?? (completed = new List<Entity>())).Add(area);
                    continue;
                }

                if (EntityManager.HasComponent<Deleted>(area))
                {
                    Mod.Log.InfoFormat(
                        "Temporary preview surface entity entered Deleted state after replacement: area={0}, ownerAtReplacement={1}, source='{2}', target='{3}', framesAlive={4}",
                        area,
                        trace.InitialOwner,
                        GetPrefabNameSafe(trace.OriginalPrefab),
                        GetPrefabNameSafe(trace.TargetPrefab),
                        frame - trace.ReplacedAtFrame);
                    (completed ?? (completed = new List<Entity>())).Add(area);
                    continue;
                }

                if (!EntityManager.HasComponent<Temp>(area))
                {
                    Mod.Log.InfoFormat(
                        "Temporary preview surface became non-temporary after replacement: area={0}, ownerAtReplacement={1}, source='{2}', target='{3}', framesAlive={4}",
                        area,
                        trace.InitialOwner,
                        GetPrefabNameSafe(trace.OriginalPrefab),
                        GetPrefabNameSafe(trace.TargetPrefab),
                        frame - trace.ReplacedAtFrame);
                    (completed ?? (completed = new List<Entity>())).Add(area);
                    continue;
                }

                Temp temp = EntityManager.GetComponentData<Temp>(area);
                if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Hidden)) != 0 && !trace.CleanupFlagsLogged)
                {
                    Mod.Log.InfoFormat(
                        "Temporary preview surface received cleanup flags after replacement: area={0}, ownerAtReplacement={1}, source='{2}', target='{3}', tempFlags={4}, framesAlive={5}",
                        area,
                        trace.InitialOwner,
                        GetPrefabNameSafe(trace.OriginalPrefab),
                        GetPrefabNameSafe(trace.TargetPrefab),
                        temp.m_Flags,
                        frame - trace.ReplacedAtFrame);
                    trace.CleanupFlagsLogged = true;
                }

                int framesAlive = frame - trace.ReplacedAtFrame;
                if (framesAlive < 300 || trace.LongLivedWarningLogged)
                {
                    continue;
                }

                Entity currentPrefab = EntityManager.HasComponent<PrefabRef>(area)
                    ? EntityManager.GetComponentData<PrefabRef>(area).m_Prefab
                    : Entity.Null;
                Entity currentOwner = EntityManager.HasComponent<Owner>(area)
                    ? EntityManager.GetComponentData<Owner>(area).m_Owner
                    : Entity.Null;
                bool hasBatch = EntityManager.HasComponent<Batch>(area);
                int batchIndex = hasBatch ? EntityManager.GetComponentData<Batch>(area).m_BatchIndex : -1;
                Mod.Log.WarnFormat(
                    "Temporary preview surface is still alive 300 frames after replacement: area={0}, ownerAtReplacement={1}, currentOwner={2}, originalBatch={3}, currentBatch={4}, source='{5}', target='{6}', currentPrefab='{7}', tempFlags={8}",
                    area,
                    trace.InitialOwner,
                    currentOwner,
                    trace.OriginalBatchIndex,
                    batchIndex,
                    GetPrefabNameSafe(trace.OriginalPrefab),
                    GetPrefabNameSafe(trace.TargetPrefab),
                    GetPrefabNameSafe(currentPrefab),
                    temp.m_Flags);
                trace.LongLivedWarningLogged = true;
            }

            if (completed != null)
            {
                foreach (Entity area in completed)
                {
                    m_TemporaryPreviewReplacementTraces.Remove(area);
                }
            }
        }

        private string GetPrefabNameSafe(Entity prefab)
        {
            if (prefab == Entity.Null || !EntityManager.Exists(prefab))
            {
                return "<missing>";
            }

            try
            {
                return m_PrefabSystem.GetPrefabName(prefab);
            }
            catch
            {
                return "<unavailable>";
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


    }
}
