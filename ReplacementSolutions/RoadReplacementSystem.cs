using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Colossal.IO.AssetDatabase;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace AutomaticTextureReplacer
{
    /// <summary>
    /// Applies road texture rules by assigning mod-owned replacement road prefabs
    /// to existing road instances. The game then builds Composition and batches
    /// through its normal prefab pipeline.
    /// </summary>
    public sealed partial class RoadReplacementSystem : GameSystemBase
    {
        internal struct RoadReplacementState : IComponentData
        {
            public int RuleId;
            public Entity OriginalRoadPrefab;
        }

        private sealed class Request
        {
            public bool RevertAll;
            public string RuleId;
            public string RulesJson;
        }

        private static readonly object s_RequestLock = new object();
        private static readonly Queue<Request> s_Requests = new Queue<Request>();

        private readonly Dictionary<string, Entity> m_ReplacementPieces = new Dictionary<string, Entity>(StringComparer.Ordinal);
        private readonly Dictionary<string, NetPiecePrefab> m_ReplacementPiecePrefabs = new Dictionary<string, NetPiecePrefab>(StringComparer.Ordinal);
        private readonly Dictionary<string, RenderPrefab> m_ReplacementLodPrefabs = new Dictionary<string, RenderPrefab>(StringComparer.Ordinal);
        private readonly Dictionary<string, Entity> m_ReplacementSections = new Dictionary<string, Entity>(StringComparer.Ordinal);
        private readonly Dictionary<string, NetGeometryPrefab> m_ReplacementRoadPrefabs = new Dictionary<string, NetGeometryPrefab>(StringComparer.Ordinal);
        private readonly Dictionary<string, SurfaceAsset> m_ReplacementMaterials = new Dictionary<string, SurfaceAsset>(StringComparer.Ordinal);
        private readonly Dictionary<string, TextureAsset> m_ReplacementTextures = new Dictionary<string, TextureAsset>(StringComparer.Ordinal);
        private bool m_SmallRoadReplacementTreeLogged;
        private bool m_RuntimePrefabsPendingUpdate;
        private int m_RoadReplacementWatchFrames;
        private readonly Dictionary<string, string> m_WatchedMaterialStates = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<Entity, string> m_WatchedRoadCompositions = new Dictionary<Entity, string>();
        private EntityQuery m_RoadPrefabQuery;
        private EntityQuery m_RoadInstanceQuery;
        private EntityQuery m_NewRoadInstanceQuery;
        private PrefabSystem m_PrefabSystem;
        private ILocalAssetDatabase m_RuntimeAssetDatabase;
        private Task m_RuntimeAssetDatabaseRegistration;
        private bool m_RuntimeAssetDatabaseReady;

        public static void QueueReplacement(string ruleId, string rulesJson)
        {
            QueueReplacementInternal(ruleId, rulesJson, "external-trigger");
        }

        private static void QueueReplacementInternal(string ruleId, string rulesJson, string origin)
        {
            lock (s_RequestLock)
                s_Requests.Enqueue(new Request { RuleId = ruleId ?? string.Empty, RulesJson = rulesJson ?? "[]" });
            Mod.Log.InfoFormat("Road replacement request queued: ruleId='{0}', origin='{1}'", ruleId ?? string.Empty, origin);
        }

        public static void QueueRevertAll()
        {
            lock (s_RequestLock)
                s_Requests.Enqueue(new Request { RevertAll = true });
            Mod.Log.Info("Road Revert All request queued");
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_RoadPrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<NetGeometryData>() },
                Any = new[] { ComponentType.ReadOnly<RoadData>(), ComponentType.ReadOnly<PathwayData>(), ComponentType.ReadOnly<TrackData>() },
            });
            m_RoadInstanceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadWrite<Composition>() },
                None = new[] { ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_NewRoadInstanceQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadWrite<Composition>(), ComponentType.ReadOnly<Created>() },
                None = new[] { ComponentType.ReadOnly<PrefabData>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<RoadReplacementState>() },
            });
            m_RuntimeAssetDatabase = AssetDatabase.GetTransient();
            m_RuntimeAssetDatabaseRegistration = AssetDatabase.global.RegisterDatabase(m_RuntimeAssetDatabase);
        }

        protected override void OnUpdate()
        {
            // PrefabSystem and systems such as FindIt's prefab indexer must not
            // be updated recursively while the game is still loading.
            if (GameManager.instance != null && GameManager.instance.isGameLoading)
                return;

            // Runtime prefab additions made in the previous PrefabUpdate pass
            // have now been processed by the normal PrefabUpdate phase.
            m_RuntimePrefabsPendingUpdate = false;

            MonitorRoadReplacementInputs();
            if (!EnsureRuntimeAssetDatabaseReady()) return;

            Request[] requests;
            lock (s_RequestLock)
            {
                requests = s_Requests.ToArray();
                s_Requests.Clear();
            }

            foreach (Request request in requests)
            {
                if (request.RevertAll)
                    RevertAll();
                else
                    ApplyRequestedRule(request);
            }

            if (Mod.Settings?.AlsoNewRoads == true)
                ApplyRulesToNewRoads(Mod.Settings.RoadRulesJson);
        }

        private bool EnsureRuntimeAssetDatabaseReady()
        {
            if (m_RuntimeAssetDatabaseReady) return true;
            if (m_RuntimeAssetDatabaseRegistration == null || !m_RuntimeAssetDatabaseRegistration.IsCompleted) return false;
            if (m_RuntimeAssetDatabaseRegistration.Status != TaskStatus.RanToCompletion)
            {
                Mod.Log.Error(m_RuntimeAssetDatabaseRegistration.Exception, "Road replacement runtime asset database registration failed");
                m_RuntimeAssetDatabaseRegistration = null;
                return false;
            }
            m_RuntimeAssetDatabaseReady = true;
            m_RuntimeAssetDatabaseRegistration = null;
            return true;
        }

        private void ApplyRequestedRule(Request request)
        {
            if (!int.TryParse(request.RuleId, out int ruleId))
            {
                Mod.Log.WarnFormat("Road replacement ignored invalid rule id '{0}'", request.RuleId);
                return;
            }

            List<RoadTextureRule> rules = ParseRules(request.RulesJson);
            RoadTextureRule rule = rules.FirstOrDefault(item => item != null && item.id == ruleId);
            if (rule == null)
            {
                Mod.Log.WarnFormat("Road replacement rule not found: ruleId='{0}'", ruleId);
                return;
            }

            ApplyRule(rule, null);
        }

        private static List<RoadTextureRule> ParseRules(string json)
        {
            try
            {
                return Colossal.Json.JSON.MakeInto<List<RoadTextureRule>>(Colossal.Json.JSON.Load(json ?? "[]")) ?? new List<RoadTextureRule>();
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not parse road replacement rules");
                return new List<RoadTextureRule>();
            }
        }

        private void ApplyRulesToNewRoads(string json)
        {
            List<RoadTextureRule> rules = ParseRules(json);
            if (rules.Count == 0) return;

            using (NativeArray<Entity> roads = m_NewRoadInstanceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity road in roads)
                {
                    if (!EntityManager.Exists(road) || !EntityManager.HasComponent<PrefabRef>(road)) continue;
                    Entity roadPrefab = EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
                    RoadTextureRule rule = rules.FirstOrDefault(candidate => candidate != null && candidate.enabled &&
                        ResolveSourceRoadPrefabs(candidate).Contains(roadPrefab));
                    if (rule != null)
                        ApplyRule(rule, new HashSet<Entity> { road });
                }
            }
        }

        private HashSet<Entity> ResolveSourceRoadPrefabs(RoadTextureRule rule)
        {
            var sourceIds = new HashSet<string>(rule.sourceRoadIds ?? new List<string>(), StringComparer.Ordinal);
            var result = new HashSet<Entity>();
            if (sourceIds.Count == 0) return result;

            using (NativeArray<Entity> prefabs = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in prefabs)
                {
                    if (!m_PrefabSystem.TryGetPrefab<NetGeometryPrefab>(entity, out NetGeometryPrefab geometry) || geometry == null) continue;
                    string prefabId = geometry.GetPrefabID().ToString();
                    bool matches = sourceIds.Contains(prefabId) ||
                        (sourceIds.Contains(RoadAtlasRegions.RoadEuCatalogId) && GeometryUsesRoadEu(geometry));
                    if (matches) result.Add(entity);
                }
            }
            return result;
        }

        private bool GeometryUsesRoadEu(NetGeometryPrefab geometry)
        {
            var pieces = new HashSet<Entity>();
            var visited = new HashSet<NetSectionPrefab>();
            foreach (NetSectionInfo section in geometry.m_Sections ?? Array.Empty<NetSectionInfo>())
                if (section?.m_Section != null) CollectSectionPieces(section.m_Section, visited, pieces);
            foreach (Entity entity in pieces)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetPiecePrefab>(entity, out NetPiecePrefab piece) || piece == null) continue;
                foreach (SurfaceAsset surface in piece.surfaceAssets ?? Enumerable.Empty<SurfaceAsset>())
                {
                    if (surface == null) continue;
                    bool loaded = false;
                    try
                    {
                        surface.LoadProperties(useVT: false);
                        loaded = true;
                        if (RoadAtlasRegions.RoadEuTextureLayers.Any(layer =>
                            surface.textures.TryGetValue(layer.MaterialProperty, out TextureAsset texture) &&
                            texture != null && string.Equals(texture.name, layer.TextureAssetName, StringComparison.Ordinal))) return true;
                    }
                    finally { if (loaded) surface.UnloadProperties(); }
                }
            }
            return false;
        }

        private void ApplyRule(RoadTextureRule rule, HashSet<Entity> onlyRoads)
        {
            HashSet<Entity> sourceRoads = ResolveSourceRoadPrefabs(rule);
            if (sourceRoads.Count == 0)
            {
                Mod.Log.WarnFormat("Road rule has no resolved source prefab: ruleId='{0}', sourceIds=[{1}], geometryPrefabEntities={2}; no road prefab clones or instance changes were made",
                    rule.id, string.Join(",", rule.sourceRoadIds ?? new List<string>()), m_RoadPrefabQuery.CalculateEntityCount());
                return;
            }

            string variantKey = GetRuleVariantKey(rule);
            Dictionary<Entity, Entity> replacementPieces = BuildReplacementPieces(rule, sourceRoads, variantKey);
            if (replacementPieces.Count == 0)
            {
                Mod.Log.WarnFormat("Road rule produced no replacement piece prefabs: ruleId='{0}'", rule.id);
                return;
            }
            DumpSmallRoadReplacementTree(replacementPieces);

            Dictionary<Entity, Entity> replacementRoads = BuildReplacementRoadPrefabs(rule, sourceRoads, variantKey, replacementPieces);
            if (m_RuntimePrefabsPendingUpdate)
            {
                QueueReplacementInternal(rule.id.ToString(), Mod.Settings?.RoadRulesJson ?? "[]", "prefab-update-retry");
                Mod.Log.InfoFormat("Road replacement deferred one frame: ruleId='{0}'; runtime prefab additions/updates will be processed by the normal PrefabUpdate phase before PrefabRef changes",
                    rule.id);
                return;
            }
            if (replacementRoads.Count == 0)
            {
                Mod.Log.WarnFormat("Road rule produced no replacement road prefabs: ruleId='{0}'", rule.id);
                return;
            }

            foreach (KeyValuePair<Entity, Entity> pair in replacementRoads)
            {
                if (!EntityManager.HasBuffer<NetGeometrySection>(pair.Value) ||
                    !EntityManager.HasBuffer<NetGeometrySection>(pair.Key) ||
                    EntityManager.GetBuffer<NetGeometrySection>(pair.Value, true).Length !=
                    EntityManager.GetBuffer<NetGeometrySection>(pair.Key, true).Length)
                {
                    int sourceCount = EntityManager.HasBuffer<NetGeometrySection>(pair.Key) ? EntityManager.GetBuffer<NetGeometrySection>(pair.Key, true).Length : -1;
                    int replacementCount = EntityManager.HasBuffer<NetGeometrySection>(pair.Value) ? EntityManager.GetBuffer<NetGeometrySection>(pair.Value, true).Length : -1;
                    Mod.Log.WarnFormat("Road replacement aborted: prefab geometry sections not initialized or do not match source; source={0}, sourceSections={1}, replacement={2}, replacementSections={3}, replacementCreated={4}; no road PrefabRefs changed", pair.Key, sourceCount, pair.Value, replacementCount, EntityManager.HasComponent<Created>(pair.Value));
                    return;
                }
            }

            Mod.Log.InfoFormat(
                "Road pipeline stage 1/4: rule data resolved; sourcePrefabs={0}, replacementRoadPrefabs={1}, replacementPieces={2}, replacementLodPrefabs={3}, clonedMaterials={4}, generatedTextures={5}; road instances will receive PrefabRef changes and the game will rebuild Composition and batches",
                sourceRoads.Count, replacementRoads.Count, replacementPieces.Count, m_ReplacementLodPrefabs.Count,
                m_ReplacementMaterials.Count, m_ReplacementTextures.Count);

            int roadsScanned = 0;
            int roadsChanged = 0;
            if (onlyRoads == null)
            {
                using NativeArray<Entity> roads = m_RoadInstanceQuery.ToEntityArray(Allocator.Temp);
                foreach (Entity road in roads)
                {
                    if (TryApplyToMatchingRoad(rule.id, road, sourceRoads, replacementRoads, ref roadsScanned)) roadsChanged++;
                }
            }
            else
                foreach (Entity road in onlyRoads)
                    if (TryApplyToMatchingRoad(rule.id, road, sourceRoads, replacementRoads, ref roadsScanned)) roadsChanged++;

            string mappings = string.Join("; ", (rule.roadAreaReplacements ?? new Dictionary<string, string>())
                .Select(entry => entry.Key + "=" + entry.Value));
            Mod.Log.InfoFormat("Road replacement applied: ruleId='{0}', mappings=[{1}], sourcePrefabs={2}, replacementRoadPrefabs={3}, clonedPieces={4}, roadsScanned={5}, roadsChanged={6}; Composition and batches are delegated to the game pipeline",
                rule.id, mappings, sourceRoads.Count, replacementRoads.Count, replacementPieces.Count, roadsScanned, roadsChanged);
            StartRoadReplacementInputWatch();
        }

        private void StartRoadReplacementInputWatch()
        {
            m_WatchedMaterialStates.Clear();
            foreach (KeyValuePair<string, SurfaceAsset> entry in m_ReplacementMaterials)
                m_WatchedMaterialStates[entry.Key] = DescribeMaterialState(entry.Value);
            m_WatchedRoadCompositions.Clear();
            using (NativeArray<Entity> roads = m_RoadInstanceQuery.ToEntityArray(Allocator.Temp))
                foreach (Entity road in roads)
                    if (EntityManager.HasComponent<RoadReplacementState>(road))
                        m_WatchedRoadCompositions[road] = DescribeRoadPrefab(road);
            m_RoadReplacementWatchFrames = 600;
            Mod.Log.InfoFormat("Road replacement input watch started: frames=600, materials={0}, smallRoadInstances={1}",
                m_WatchedMaterialStates.Count, m_WatchedRoadCompositions.Count);
        }

        private void MonitorRoadReplacementInputs()
        {
            if (m_RoadReplacementWatchFrames <= 0) return;
            m_RoadReplacementWatchFrames--;
            foreach (KeyValuePair<string, SurfaceAsset> entry in m_ReplacementMaterials.ToArray())
            {
                string current = DescribeMaterialState(entry.Value);
                if (!m_WatchedMaterialStates.TryGetValue(entry.Key, out string previous))
                {
                    m_WatchedMaterialStates[entry.Key] = current;
                    continue;
                }
                if (string.Equals(previous, current, StringComparison.Ordinal)) continue;
                Mod.Log.WarnFormat("Road replacement input changed after Apply: kind=material, framesRemaining={0}, key='{1}'\n  before: {2}\n  after:  {3}",
                    m_RoadReplacementWatchFrames, entry.Key, previous, current);
                m_WatchedMaterialStates[entry.Key] = current;
            }
            foreach (Entity road in m_WatchedRoadCompositions.Keys.ToArray())
            {
                if (!EntityManager.Exists(road) || !EntityManager.HasComponent<PrefabRef>(road)) continue;
                string current = DescribeRoadPrefab(road);
                string previous = m_WatchedRoadCompositions[road];
                if (string.Equals(previous, current, StringComparison.Ordinal)) continue;
                Mod.Log.WarnFormat("Road replacement input changed after Apply: kind=road PrefabRef, framesRemaining={0}, road={1}, before='{2}', after='{3}'",
                    m_RoadReplacementWatchFrames, road, previous, current);
                m_WatchedRoadCompositions[road] = current;
            }
            if (m_RoadReplacementWatchFrames == 0)
                Mod.Log.Info("Road replacement input watch completed: no further input-state changes will be recorded");
        }

        private static string DescribeMaterialState(SurfaceAsset material)
        {
            if (material == null) return "<null>";
            bool loaded = false;
            try
            {
                material.LoadProperties(false);
                loaded = true;
                return "name=" + material.name + ";template=" + material.materialTemplateHash + ";keywords=" +
                    string.Join(",", material.keywords.OrderBy(value => value, StringComparer.Ordinal)) + ";textures=" +
                    string.Join(",", material.textures.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => pair.Key + "=" + (pair.Value?.id.ToString() ?? "<null>")));
            }
            catch (Exception exception) { return "<unavailable: " + exception.Message + ">"; }
            finally { if (loaded) material.UnloadProperties(); }
        }

        private string DescribeRoadPrefab(Entity road)
        {
            if (!EntityManager.HasComponent<PrefabRef>(road)) return "<no PrefabRef>";
            return "prefab=" + EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
        }

        private bool TryApplyToMatchingRoad(int ruleId, Entity road, HashSet<Entity> sourceRoads,
            Dictionary<Entity, Entity> replacementRoads, ref int roadsScanned)
        {
            if (!EntityManager.Exists(road) || !EntityManager.HasComponent<PrefabRef>(road) ||
                !EntityManager.HasComponent<Composition>(road)) return false;

            PrefabRef currentPrefabRef = EntityManager.GetComponentData<PrefabRef>(road);
            Entity sourceRoadPrefab = currentPrefabRef.m_Prefab;
            if (EntityManager.HasComponent<RoadReplacementState>(road))
                sourceRoadPrefab = EntityManager.GetComponentData<RoadReplacementState>(road).OriginalRoadPrefab;
            if (!sourceRoads.Contains(sourceRoadPrefab) || !replacementRoads.TryGetValue(sourceRoadPrefab, out Entity replacementRoadPrefab))
                return false;

            roadsScanned++;
            if (currentPrefabRef.m_Prefab == replacementRoadPrefab) return false;

            EntityManager.SetComponentData(road, new PrefabRef(replacementRoadPrefab));
            var state = new RoadReplacementState
            {
                RuleId = ruleId,
                OriginalRoadPrefab = sourceRoadPrefab,
            };
            if (EntityManager.HasComponent<RoadReplacementState>(road))
                EntityManager.SetComponentData(road, state);
            else
                EntityManager.AddComponentData(road, state);

            // CompositionSelectSystem is the authoritative owner of road
            // Composition. Updated makes it select the composition belonging
            // to the new PrefabRef, after which the normal net and batch
            // systems rebuild their derived data.
            EnsureComponent<Updated>(road);
            Mod.Log.InfoFormat("Road PrefabRef replaced: ruleId='{0}', road={1}, sourcePrefab={2}, replacementPrefab={3}; Updated added for CompositionSelectSystem",
                ruleId, road, sourceRoadPrefab, replacementRoadPrefab);
            return true;
        }

        private string GetRuleVariantKey(RoadTextureRule rule)
        {
            var key = new StringBuilder().Append("rule=").Append(rule.id);
            foreach (KeyValuePair<string, string> replacement in (rule.roadAreaReplacements ?? new Dictionary<string, string>())
                         .OrderBy(item => item.Key, StringComparer.Ordinal))
                key.Append('|').Append(replacement.Key).Append('=').Append(replacement.Value);
            foreach (RoadAtlasTextureLayer layer in RoadAtlasRegions.RoadEuTextureLayers)
            {
                string path = Path.Combine(GetRoadTextureDirectory(), Sanitize("RoadEU_Rule_" + rule.id + "_" + layer.MaterialProperty.Trim('_')) + ".png");
                if (!File.Exists(path)) continue;
                FileInfo info = new FileInfo(path);
                key.Append('|').Append(layer.MaterialProperty).Append('@').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
            }
            return key.ToString();
        }

        private Dictionary<Entity, Entity> BuildReplacementPieces(RoadTextureRule rule, HashSet<Entity> sourceRoads, string variantKey)
        {
            var sourcePieces = new HashSet<Entity>();
            foreach (Entity roadPrefab in sourceRoads)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetGeometryPrefab>(roadPrefab, out NetGeometryPrefab geometry) || geometry == null) continue;
                var visited = new HashSet<NetSectionPrefab>();
                foreach (NetSectionInfo section in geometry.m_Sections ?? Array.Empty<NetSectionInfo>())
                    if (section?.m_Section != null) CollectSectionPieces(section.m_Section, visited, sourcePieces);
            }

            var replacements = new Dictionary<Entity, Entity>();
            bool addedPrefab = false;
            foreach (Entity pieceEntity in sourcePieces)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetPiecePrefab>(pieceEntity, out NetPiecePrefab sourcePiece) || sourcePiece == null) continue;
                string cacheKey = variantKey + "|" + sourcePiece.GetPrefabID();
                if (m_ReplacementPieces.TryGetValue(cacheKey, out Entity cached) && EntityManager.Exists(cached))
                {
                    if (m_ReplacementPiecePrefabs.TryGetValue(cacheKey, out NetPiecePrefab cachedPrefab) && cachedPrefab != null)
                    {
                        SurfaceAsset[] refreshedMaterials = ClonePieceMaterials(rule.id, variantKey, sourcePiece) ??
                            (sourcePiece.surfaceAssets?.ToArray() ?? Array.Empty<SurfaceAsset>());
                        if (!cachedPrefab.surfaceAssets.SequenceEqual(refreshedMaterials))
                        {
                            cachedPrefab.surfaceAssets = refreshedMaterials;
                            m_PrefabSystem.UpdatePrefab(cachedPrefab);
                            m_RuntimePrefabsPendingUpdate = true;
                            addedPrefab = true;
                            Mod.Log.InfoFormat("Road piece clone refreshed: ruleId='{0}', source='{1}', clone='{2}'",
                                rule.id, sourcePiece.name, cachedPrefab.name);
                        }
                    }
                    replacements[pieceEntity] = cached;
                    continue;
                }

                SurfaceAsset[] clonedMaterials = ClonePieceMaterials(rule.id, variantKey, sourcePiece);
                if (clonedMaterials == null) continue;
                var clone = (NetPiecePrefab)sourcePiece.Clone(sourcePiece.name + " - Replacement Solutions Rule " + rule.id + " " + ShortHash(cacheKey));
                CopyRenderGeometry(sourcePiece, clone);
                clone.surfaceAssets = clonedMaterials;
                int lodMeshesChanged = CloneLodMeshes(rule.id, variantKey, sourcePiece, clone, ref addedPrefab);
                if (!m_PrefabSystem.AddPrefab(clone))
                {
                    Mod.Log.WarnFormat("Could not register road piece clone: ruleId='{0}', source='{1}'", rule.id, sourcePiece.name);
                    continue;
                }
                Entity cloneEntity = m_PrefabSystem.GetEntity(clone);
                if (cloneEntity == Entity.Null || !EntityManager.Exists(cloneEntity)) continue;
                m_RuntimePrefabsPendingUpdate = true;
                m_ReplacementPieces[cacheKey] = cloneEntity;
                m_ReplacementPiecePrefabs[cacheKey] = clone;
                replacements[pieceEntity] = cloneEntity;
                addedPrefab = true;
                Mod.Log.InfoFormat("Road piece clone created: ruleId='{0}', source='{1}', sourceId='{2}', clone='{3}', cloneId='{4}', lodMeshesChanged={5}",
                    rule.id, sourcePiece.name, sourcePiece.GetPrefabID(), clone.name, clone.GetPrefabID(), lodMeshesChanged);
            }

            return replacements;
        }

        private int CloneLodMeshes(int ruleId, string variantKey, RenderPrefab source, RenderPrefab clone, ref bool addedPrefab)
        {
            if (!source.TryGet<LodProperties>(out LodProperties sourceLod) || sourceLod?.m_LodMeshes == null ||
                !clone.TryGet<LodProperties>(out LodProperties cloneLod) || cloneLod == null)
                return 0;

            int changed = 0;
            for (int index = 0; index < sourceLod.m_LodMeshes.Length; index++)
            {
                RenderPrefab sourceLodMesh = sourceLod.m_LodMeshes[index];
                if (sourceLodMesh == null) continue;

                RenderPrefab replacement = GetOrCreateReplacementLodPrefab(ruleId, variantKey, sourceLodMesh, ref addedPrefab);
                if (replacement == null || replacement == sourceLodMesh) continue;
                cloneLod.m_LodMeshes[index] = replacement;
                changed++;
            }
            return changed;
        }

        private RenderPrefab GetOrCreateReplacementLodPrefab(int ruleId, string variantKey, RenderPrefab source, ref bool addedPrefab)
        {
            string cacheKey = variantKey + "|" + source.GetPrefabID();
            if (m_ReplacementLodPrefabs.TryGetValue(cacheKey, out RenderPrefab cached) && cached != null)
                return cached;

            SurfaceAsset[] clonedMaterials = ClonePieceMaterials(ruleId, variantKey, source);
            var clone = (RenderPrefab)source.Clone(source.name + " - Replacement Solutions Rule " + ruleId + " " + ShortHash(cacheKey));
            CopyRenderGeometry(source, clone);
            bool changed = clonedMaterials != null;
            if (changed) clone.surfaceAssets = clonedMaterials;

            int nestedChanged = CloneLodMeshes(ruleId, variantKey, source, clone, ref addedPrefab);
            changed |= nestedChanged != 0;
            if (!changed)
            {
                UnityEngine.Object.Destroy(clone);
                return source;
            }

            if (!m_PrefabSystem.AddPrefab(clone))
            {
                Mod.Log.WarnFormat("Could not register road LOD prefab clone: ruleId='{0}', source='{1}'", ruleId, source.name);
                UnityEngine.Object.Destroy(clone);
                return source;
            }

            m_ReplacementLodPrefabs[cacheKey] = clone;
            m_RuntimePrefabsPendingUpdate = true;
            addedPrefab = true;
            Mod.Log.InfoFormat("Road LOD prefab clone created: ruleId='{0}', source='{1}', clone='{2}', nestedLodMeshesChanged={3}",
                ruleId, source.name, clone.name, nestedChanged);
            return clone;
        }

        private static void CopyRenderGeometry(RenderPrefab source, RenderPrefab clone)
        {
            clone.geometryAsset = source.geometryAsset;
            clone.bounds = source.bounds;
            clone.surfaceArea = source.surfaceArea;
            clone.indexCount = source.indexCount;
            clone.vertexCount = source.vertexCount;
            clone.meshCount = source.meshCount;
            clone.isImpostor = source.isImpostor;
            clone.manualVTRequired = source.manualVTRequired;
        }

        private void RefreshReplacementPieceEntities()
        {
            foreach (KeyValuePair<string, NetPiecePrefab> entry in m_ReplacementPiecePrefabs.ToArray())
            {
                NetPiecePrefab prefab = entry.Value;
                if (prefab == null) continue;

                Entity refreshed = m_PrefabSystem.GetEntity(prefab);
                if (refreshed == Entity.Null || !EntityManager.Exists(refreshed))
                {
                    Mod.Log.WarnFormat(
                        "Road replacement piece entity refresh failed: key='{0}', prefab='{1}'",
                        entry.Key, prefab.name);
                    continue;
                }

                if (!m_ReplacementPieces.TryGetValue(entry.Key, out Entity previous) || previous == refreshed) continue;
                m_ReplacementPieces[entry.Key] = refreshed;
                Mod.Log.InfoFormat(
                    "Road replacement piece entity refreshed after PrefabSystem.Update: prefab='{0}', old={1}, current={2}",
                    prefab.name, previous, refreshed);
            }
        }

        private Dictionary<Entity, Entity> BuildReplacementRoadPrefabs(RoadTextureRule rule,
            HashSet<Entity> sourceRoads, string variantKey, Dictionary<Entity, Entity> replacementPieces)
        {
            var result = new Dictionary<Entity, Entity>();
            bool addedPrefab = false;
            foreach (Entity sourceRoadEntity in sourceRoads)
            {
                if (!m_PrefabSystem.TryGetPrefab<NetGeometryPrefab>(sourceRoadEntity, out NetGeometryPrefab sourceRoad) ||
                    sourceRoad == null) continue;

                NetGeometryPrefab replacementRoad = GetOrCreateReplacementRoadPrefab(
                    rule.id, variantKey, sourceRoad, replacementPieces, ref addedPrefab);
                if (replacementRoad == null || replacementRoad == sourceRoad) continue;

                Entity replacementEntity = m_PrefabSystem.GetEntity(replacementRoad);
                if (replacementEntity != Entity.Null && EntityManager.Exists(replacementEntity))
                    result[sourceRoadEntity] = replacementEntity;
            }

            return result;
        }

        private NetGeometryPrefab GetOrCreateReplacementRoadPrefab(int ruleId, string variantKey,
            NetGeometryPrefab source, Dictionary<Entity, Entity> replacementPieces, ref bool addedPrefab)
        {
            string cacheKey = variantKey + "|road|" + source.GetPrefabID();
            if (m_ReplacementRoadPrefabs.TryGetValue(cacheKey, out NetGeometryPrefab cached) && cached != null)
                return cached;

            var replacementSections = new NetSectionInfo[source.m_Sections?.Length ?? 0];
            bool changed = false;
            for (int index = 0; index < replacementSections.Length; index++)
            {
                NetSectionInfo sourceInfo = source.m_Sections[index];
                if (sourceInfo?.m_Section == null)
                {
                    replacementSections[index] = sourceInfo;
                    continue;
                }

                NetSectionPrefab replacementSection = GetOrCreateReplacementSection(
                    ruleId, variantKey, sourceInfo.m_Section, replacementPieces, ref addedPrefab);
                replacementSections[index] = new NetSectionInfo
                {
                    m_Section = replacementSection,
                    m_RequireAll = sourceInfo.m_RequireAll,
                    m_RequireAny = sourceInfo.m_RequireAny,
                    m_RequireNone = sourceInfo.m_RequireNone,
                    m_HiddenLayers = sourceInfo.m_HiddenLayers,
                    m_Invert = sourceInfo.m_Invert,
                    m_Flip = sourceInfo.m_Flip,
                    m_Median = sourceInfo.m_Median,
                    m_HalfLength = sourceInfo.m_HalfLength,
                    m_Offset = sourceInfo.m_Offset,
                };
                changed |= replacementSection != sourceInfo.m_Section;
            }

            if (!changed)
                return source;

            var clone = (NetGeometryPrefab)source.Clone(
                source.name + " - Replacement Solutions Rule " + ruleId + " " + ShortHash(cacheKey));
            clone.m_Sections = replacementSections;
            if (!m_PrefabSystem.AddPrefab(clone))
            {
                Mod.Log.WarnFormat("Could not register replacement road prefab: ruleId='{0}', source='{1}'", ruleId, source.name);
                UnityEngine.Object.Destroy(clone);
                return source;
            }

            m_ReplacementRoadPrefabs[cacheKey] = clone;
            m_RuntimePrefabsPendingUpdate = true;
            addedPrefab = true;
            Mod.Log.InfoFormat("Road replacement prefab created: ruleId='{0}', source='{1}', sourceId='{2}', clone='{3}', cloneId='{4}', sections={5}",
                ruleId, source.name, source.GetPrefabID(), clone.name, clone.GetPrefabID(), replacementSections.Length);
            return clone;
        }

        private NetSectionPrefab GetOrCreateReplacementSection(int ruleId, string variantKey,
            NetSectionPrefab source, Dictionary<Entity, Entity> replacementPieces, ref bool addedPrefab)
        {
            if (source == null || !m_PrefabSystem.TryGetEntity(source, out Entity sourceEntity))
                return source;

            string cacheKey = variantKey + "|section|" + source.GetPrefabID();
            if (m_ReplacementSections.TryGetValue(cacheKey, out Entity cachedEntity) &&
                EntityManager.Exists(cachedEntity) &&
                m_PrefabSystem.TryGetPrefab<NetSectionPrefab>(cachedEntity, out NetSectionPrefab cached))
                return cached;

            NetPieceInfo[] sourcePieces = source.m_Pieces ?? Array.Empty<NetPieceInfo>();
            var replacementPiecesInfo = new NetPieceInfo[sourcePieces.Length];
            bool changed = false;
            for (int index = 0; index < sourcePieces.Length; index++)
            {
                NetPieceInfo sourceInfo = sourcePieces[index];
                if (sourceInfo == null) continue;
                replacementPiecesInfo[index] = new NetPieceInfo
                {
                    m_Piece = sourceInfo.m_Piece,
                    m_RequireAll = sourceInfo.m_RequireAll,
                    m_RequireAny = sourceInfo.m_RequireAny,
                    m_RequireNone = sourceInfo.m_RequireNone,
                    m_Offset = sourceInfo.m_Offset,
                };
                if (sourceInfo.m_Piece == null || !m_PrefabSystem.TryGetEntity(sourceInfo.m_Piece, out Entity pieceEntity) ||
                    !replacementPieces.TryGetValue(pieceEntity, out Entity replacementEntity) ||
                    !m_PrefabSystem.TryGetPrefab<NetPiecePrefab>(replacementEntity, out NetPiecePrefab replacementPiece)) continue;

                replacementPiecesInfo[index].m_Piece = replacementPiece;
                changed = true;
            }

            NetSubSectionInfo[] sourceSubSections = source.m_SubSections ?? Array.Empty<NetSubSectionInfo>();
            var replacementSubSections = new NetSubSectionInfo[sourceSubSections.Length];
            for (int index = 0; index < sourceSubSections.Length; index++)
            {
                NetSubSectionInfo sourceInfo = sourceSubSections[index];
                if (sourceInfo?.m_Section == null)
                {
                    replacementSubSections[index] = sourceInfo;
                    continue;
                }

                NetSectionPrefab replacementSection = GetOrCreateReplacementSection(
                    ruleId, variantKey, sourceInfo.m_Section, replacementPieces, ref addedPrefab);
                replacementSubSections[index] = new NetSubSectionInfo
                {
                    m_Section = replacementSection,
                    m_RequireAll = sourceInfo.m_RequireAll,
                    m_RequireAny = sourceInfo.m_RequireAny,
                    m_RequireNone = sourceInfo.m_RequireNone,
                };
                changed |= replacementSection != sourceInfo.m_Section;
            }

            if (!changed)
                return source;

            string cloneName = source.name + " - Replacement Solutions Rule " + ruleId + " " + ShortHash(cacheKey);
            var clone = (NetSectionPrefab)source.Clone(cloneName);
            clone.m_Pieces = replacementPiecesInfo;
            clone.m_SubSections = replacementSubSections;
            if (!m_PrefabSystem.AddPrefab(clone))
            {
                Mod.Log.WarnFormat("Could not register replacement road section: ruleId='{0}', source='{1}'", ruleId, source.name);
                UnityEngine.Object.Destroy(clone);
                return source;
            }

            Entity cloneEntity = m_PrefabSystem.GetEntity(clone);
            m_ReplacementSections[cacheKey] = cloneEntity;
            m_RuntimePrefabsPendingUpdate = true;
            addedPrefab = true;
            Mod.Log.InfoFormat("Road replacement section created: ruleId='{0}', source='{1}', clone='{2}', replacedPieces={3}, nestedSections={4}",
                ruleId, source.name, clone.name, replacementPiecesInfo.Count(info => info?.m_Piece != null), replacementSubSections.Length);
            return clone;
        }

        private void CollectSectionPieces(NetSectionPrefab section, HashSet<NetSectionPrefab> visited, HashSet<Entity> pieces)
        {
            if (section == null || !visited.Add(section)) return;
            if (section.m_SubSections != null)
                foreach (NetSubSectionInfo subsection in section.m_SubSections)
                    if (subsection?.m_Section != null) CollectSectionPieces(subsection.m_Section, visited, pieces);
            if (section.m_Pieces != null)
                foreach (NetPieceInfo info in section.m_Pieces)
                    if (info?.m_Piece != null && m_PrefabSystem.TryGetEntity(info.m_Piece, out Entity pieceEntity)) pieces.Add(pieceEntity);
        }

        private void DumpSmallRoadReplacementTree(Dictionary<Entity, Entity> replacements)
        {
            if (m_SmallRoadReplacementTreeLogged) return;
            using (NativeArray<Entity> prefabs = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in prefabs)
                {
                    if (!m_PrefabSystem.TryGetPrefab<NetGeometryPrefab>(entity, out NetGeometryPrefab road) || road == null ||
                        !string.Equals(road.name, "Small Road", StringComparison.OrdinalIgnoreCase)) continue;
                    var lines = new List<string>
                    {
                        "Small Road replacement tree: source prefab='" + road.name + "' [" + road.GetPrefabID() + "]",
                        "Each piece shows source -> cloned replacement and both material slots."
                    };
                    var visited = new HashSet<NetSectionPrefab>();
                    foreach (NetSectionInfo section in road.m_Sections ?? Array.Empty<NetSectionInfo>())
                        if (section?.m_Section != null) AppendSectionTree(lines, section.m_Section, replacements, visited, "", section.m_Offset);
                    Mod.Log.Info("\n" + string.Join("\n", lines));
                    m_SmallRoadReplacementTreeLogged = true;
                    return;
                }
            }
        }

        private void AppendSectionTree(List<string> lines, NetSectionPrefab section, Dictionary<Entity, Entity> replacements,
            HashSet<NetSectionPrefab> visited, string indent, Unity.Mathematics.float3 sectionOffset)
        {
            if (!visited.Add(section))
            {
                lines.Add(indent + "└─ Section '" + section.name + "' <already shown>");
                return;
            }
            lines.Add(indent + "└─ Section '" + section.name + "' [" + section.GetPrefabID() + "]; offset=" + sectionOffset);
            string childIndent = indent + "   ";
            foreach (NetPieceInfo info in section.m_Pieces ?? Array.Empty<NetPieceInfo>())
            {
                if (info?.m_Piece == null || !m_PrefabSystem.TryGetEntity(info.m_Piece, out Entity sourceEntity)) continue;
                replacements.TryGetValue(sourceEntity, out Entity cloneEntity);
                NetPiecePrefab clone = cloneEntity != Entity.Null && m_PrefabSystem.TryGetPrefab<NetPiecePrefab>(cloneEntity, out NetPiecePrefab resolved) ? resolved : null;
                lines.Add(childIndent + "├─ Piece '" + info.m_Piece.name + "' [" + info.m_Piece.GetPrefabID() + "]; offset=" + info.m_Offset +
                    (clone == null ? "\n" + childIndent + "│  └─ replacement: <none>" : "\n" + childIndent + "│  └─ replacement: '" + clone.name + "' [" + clone.GetPrefabID() + "]"));
                AppendPieceMaterials(lines, childIndent + "│     ", info.m_Piece, clone);
            }
            foreach (NetSubSectionInfo sub in section.m_SubSections ?? Array.Empty<NetSubSectionInfo>())
                if (sub?.m_Section != null) AppendSectionTree(lines, sub.m_Section, replacements, visited, childIndent, default);
        }

        private static void AppendPieceMaterials(List<string> lines, string indent, NetPiecePrefab source, NetPiecePrefab clone)
        {
            SurfaceAsset[] sourceMaterials = source.surfaceAssets?.ToArray() ?? Array.Empty<SurfaceAsset>();
            SurfaceAsset[] cloneMaterials = clone?.surfaceAssets?.ToArray() ?? Array.Empty<SurfaceAsset>();
            int length = Math.Max(sourceMaterials.Length, cloneMaterials.Length);
            for (int index = 0; index < length; index++)
            {
                SurfaceAsset original = index < sourceMaterials.Length ? sourceMaterials[index] : null;
                SurfaceAsset replacement = index < cloneMaterials.Length ? cloneMaterials[index] : null;
                lines.Add(indent + "├─ Material slot " + index + ": " + DescribeMaterial(original) + " => " + DescribeMaterial(replacement));
            }
        }

        private static string DescribeMaterial(SurfaceAsset material)
        {
            if (material == null) return "<none>";
            bool loaded = false;
            try
            {
                material.LoadProperties(false);
                loaded = true;
                string maps = string.Join(", ", material.textures.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => pair.Key + "=" + (pair.Value?.name ?? "<null>")));
                return "'" + material.name + "'; template=" + material.materialTemplateHash + "; keywords=[" +
                    string.Join(",", material.keywords.OrderBy(value => value, StringComparer.Ordinal)) + "]; maps=[" + maps + "]";
            }
            catch (Exception exception) { return "'" + material.name + "'; <unavailable: " + exception.Message + ">"; }
            finally { if (loaded) material.UnloadProperties(); }
        }

        private SurfaceAsset[] ClonePieceMaterials(int ruleId, string variantKey, RenderPrefab sourcePiece)
        {
            SurfaceAsset[] sourceMaterials = sourcePiece.surfaceAssets?.ToArray() ?? Array.Empty<SurfaceAsset>();
            var clones = (SurfaceAsset[])sourceMaterials.Clone();
            bool changed = false;
            for (int index = 0; index < sourceMaterials.Length; index++)
            {
                SurfaceAsset source = sourceMaterials[index];
                if (source == null) continue;
                SurfaceAsset replacement = GetOrCreateReplacementMaterial(ruleId, variantKey, source);
                if (replacement == null || replacement == source) continue;
                clones[index] = replacement;
                changed = true;
            }
            return changed ? clones : null;
        }

        private SurfaceAsset GetOrCreateReplacementMaterial(int ruleId, string variantKey, SurfaceAsset source)
        {
            string materialKey = variantKey + "|" + source.id;
            m_ReplacementMaterials.TryGetValue(materialKey, out SurfaceAsset cachedMaterial);

            bool loaded = false;
            try
            {
                source.LoadProperties(useVT: false);
                loaded = true;
                var textures = source.textures.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
                int changedMaps = 0;
                foreach (RoadAtlasTextureLayer layer in RoadAtlasRegions.RoadEuTextureLayers)
                {
                    if (!textures.TryGetValue(layer.MaterialProperty, out TextureAsset original) || original == null ||
                        !string.Equals(original.name, layer.TextureAssetName, StringComparison.Ordinal)) continue;
                    TextureAsset generated = GetOrCreateGeneratedTexture(ruleId, variantKey, layer);
                    if (generated == null) continue;
                    textures[layer.MaterialProperty] = generated;
                    changedMaps++;
                }
                if (cachedMaterial != null)
                {
                    cachedMaterial.UpdateTextures(textures);
                    cachedMaterial.UpdateKeywords(source.keywords);
                    Mod.Log.InfoFormat("Road material clone refreshed: ruleId='{0}', source='{1}', clone='{2}', replacedMaps={3}",
                        ruleId, source.name, cachedMaterial.name, changedMaps);
                    return cachedMaterial;
                }
                if (changedMaps == 0) return source;

                string assetName = "RoadMaterial_Rule_" + ruleId + "_" + ShortHash(materialKey);
                SurfaceAsset clone = m_RuntimeAssetDatabase.AddAsset<SurfaceAsset>(
                    AssetDataPath.Create("Replacement Solutions Runtime", assetName), CreateRuntimeGuid("Surface/" + materialKey));
                if (clone == null) return null;
                clone.CopyFrom(source);
                clone.UpdateTextures(textures);
                m_ReplacementMaterials[materialKey] = clone;
                LogRoadMaterialComparison(source, clone);
                Mod.Log.InfoFormat("Road material clone created: ruleId='{0}', source='{1}', clone='{2}', replacedMaps={3}",
                    ruleId, source.name, clone.name, changedMaps);
                return clone;
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Could not clone road material " + source.name);
                return null;
            }
            finally
            {
                if (loaded) source.UnloadProperties();
            }
        }

        private static void LogRoadMaterialComparison(SurfaceAsset source, SurfaceAsset clone)
        {
            try
            {
                string Describe(SurfaceAsset material)
                {
                    string textures = string.Join("; ", material.textures.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => pair.Key + "=" + (pair.Value == null ? "<null>" : pair.Value.name + " [" + pair.Value.id + "]") +
                            ", handledByVT=" + material.IsHandledByVirtualTexturing(pair)));
                    return "name=" + material.name + ", template=" + material.materialTemplateHash +
                        ", isVT=" + typeof(SurfaceAsset).GetField("m_IsVTMaterial", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(material) + ", usingVT=" + material.isCurrentlyUsingVT +
                        ", vtAsset=" + material.hasVTSurfaceAsset + ", stacks=" + material.stackCount +
                        ", atlasInfos=" + (material.VTAtlassingInfos == null ? "null" : string.Join(",", material.VTAtlassingInfos.Select(info => info.ToString()))) +
                        ", reservedInfos=" + (material.PreReservedAtlassingInfos == null ? "null" : string.Join(",", material.PreReservedAtlassingInfos.Select(info => info.ToString()))) +
                        "\n    keywords: " + string.Join(", ", material.keywords.OrderBy(value => value, StringComparer.Ordinal)) +
                        "\n    floats: " + string.Join("; ", material.floats.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)) +
                        "\n    ints: " + string.Join("; ", material.ints.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)) +
                        "\n    vectors: " + string.Join("; ", material.vectors.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)) +
                        "\n    colors: " + string.Join("; ", material.colors.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)) +
                        "\n    textures: " + textures;
                }
                Mod.Log.Info("Road material comparison (source loaded without VT; clone after CopyFrom):\n  SOURCE: " + Describe(source) + "\n  CLONE: " + Describe(clone));
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Road material comparison failed");
            }
        }

        private TextureAsset GetOrCreateGeneratedTexture(int ruleId, string variantKey, RoadAtlasTextureLayer layer)
        {
            string file = Path.Combine(GetRoadTextureDirectory(), Sanitize("RoadEU_Rule_" + ruleId + "_" + layer.MaterialProperty.Trim('_')) + ".png");
            if (!File.Exists(file)) return null;

            string key = variantKey + "|" + layer.MaterialProperty;
            if (m_ReplacementTextures.TryGetValue(key, out TextureAsset cachedTexture)) return cachedTexture;

            Texture2D texture = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(file);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, !layer.IsSrgb);
                if (!ImageConversion.LoadImage(texture, bytes, false))
                    throw new InvalidDataException("Generated road texture is not a readable image: " + file);
                texture.name = "RoadEU_Rule_" + ruleId + "_" + layer.MaterialProperty.Trim('_');
                string assetName = "RoadTexture_Rule_" + ruleId + "_" + layer.MaterialProperty.Trim('_') + "_" + ShortHash(variantKey);
                TextureAsset asset = m_RuntimeAssetDatabase.AddAsset<TextureAsset, Texture>(
                    AssetDataPath.Create("Replacement Solutions Runtime", assetName), texture,
                    CreateRuntimeGuid("Texture/" + key));
                if (asset == null) throw new InvalidOperationException("Transient TextureAsset registration returned null");
                m_ReplacementTextures[key] = asset;
                Mod.Log.InfoFormat("Road generated atlas loaded: ruleId='{0}', map='{1}', resolution={2}x{3}, lastWrite='{4:O}', asset='{5}'",
                    ruleId, layer.MaterialProperty, texture.width, texture.height, File.GetLastWriteTimeUtc(file), asset.id);
                texture = null;
                return asset;
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Could not load generated road atlas " + file);
                return null;
            }
            finally
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }

        private void RevertAll()
        {
            int restored = 0;
            using (NativeArray<Entity> roads = m_RoadInstanceQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity road in roads)
                {
                    if (!EntityManager.Exists(road) || !EntityManager.HasComponent<RoadReplacementState>(road) ||
                        !EntityManager.HasComponent<PrefabRef>(road)) continue;
                    RoadReplacementState state = EntityManager.GetComponentData<RoadReplacementState>(road);
                    Entity replacedPrefab = EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
                    EntityManager.SetComponentData(road, new PrefabRef(state.OriginalRoadPrefab));
                    EntityManager.RemoveComponent<RoadReplacementState>(road);
                    EnsureComponent<Updated>(road);
                    Mod.Log.InfoFormat("Road PrefabRef restored: road={0}, replacementPrefab={1}, originalPrefab={2}; Updated added for CompositionSelectSystem",
                        road, replacedPrefab, state.OriginalRoadPrefab);
                    restored++;
                }
            }
            Mod.Log.InfoFormat("Road Revert All completed: roadsRestored={0}; game pipeline will rebuild original Composition and batches", restored);
        }

        private void EnsureComponent<T>(Entity entity) where T : unmanaged, IComponentData
        {
            if (!EntityManager.HasComponent<T>(entity)) EntityManager.AddComponent<T>(entity);
        }

        private static Colossal.Hash128 CreateRuntimeGuid(string value)
        {
            Colossal.Hash128 guid = default;
            guid.Calculate("Replacement Solutions Runtime Asset/" + value);
            return guid;
        }

        private static string ShortHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).Substring(0, 16);
        }

        private static string Sanitize(string value)
        {
            var result = new StringBuilder(value.Length);
            foreach (char character in value)
                result.Append(char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '_');
            return result.ToString();
        }

        private static string GetRoadTextureDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow",
                "Colossal Order", "Cities Skylines II", "ModsData", "Replacement Solutions", "textures");
        }
    }
}
