using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Json;
using Game;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace AutomaticTextureReplacer
{
    /// <summary>
    /// Replaces surface prefabs in creation definitions before the game creates building areas.
    /// </summary>
    public sealed partial class NewBuildingSurfaceReplacementSystem : GameSystemBase
    {
        public sealed class ReplacementRule
        {
            public int id { get; set; }
            public bool enabled { get; set; }
            public List<string> sourceSurfaceIds { get; set; } = new List<string>();
            public List<string> targetSurfaceIds { get; set; } = new List<string>();
        }

        private EntityQuery m_SurfacePrefabQuery;
        private EntityQuery m_CreationDefinitionQuery;
        private PrefabSystem m_PrefabSystem;
        private ToolSystem m_ToolSystem;
        private AreaToolSystem m_AreaToolSystem;
        private string m_CachedRulesJson;
        private int m_CachedSurfacePrefabCount = -1;
        private Dictionary<Entity, List<Entity>> m_TargetsBySource = new Dictionary<Entity, List<Entity>>();
        private System.Random m_Random;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_AreaToolSystem = World.GetOrCreateSystemManaged<AreaToolSystem>();
            m_Random = new System.Random();

            m_SurfacePrefabQuery = GetEntityQuery(
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<SurfaceData>(),
                    },
                    None = new[] { ComponentType.ReadOnly<Deleted>() },
                },
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadOnly<PrefabData>(),
                        ComponentType.ReadOnly<ExtractorAreaData>(),
                    },
                    None = new[] { ComponentType.ReadOnly<Deleted>() },
                });
            m_CreationDefinitionQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<CreationDefinition>(),
                    ComponentType.ReadOnly<Updated>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Overridden>(),
                },
            });

        }

        protected override void OnUpdate()
        {
            if (Mod.Settings == null)
            {
                return;
            }

            if (!Mod.Settings.AlsoNewBuildings)
            {
                if (m_CachedRulesJson != null)
                {
                    m_CachedRulesJson = null;
                    m_TargetsBySource.Clear();
                    SurfaceReplacementSystem.ResetConstructionBatchRefreshes();
                }
                return;
            }

            string rulesJson = Mod.Settings.RulesJson ?? "[]";
            int surfacePrefabCount = m_SurfacePrefabQuery.CalculateEntityCount();
            bool rulesChanged = !string.Equals(rulesJson, m_CachedRulesJson, StringComparison.Ordinal);
            if (rulesChanged)
            {
                RebuildReplacementMap(rulesJson, true);
            }
            else if (surfacePrefabCount != m_CachedSurfacePrefabCount)
            {
                // Prefabs can register after this system first sees settings (for
                // example, after mod assets finish loading). Rebuild once the catalog
                // changes so valid rules are not silently missing from construction.
                RebuildReplacementMap(rulesJson, false);
            }

            if (m_TargetsBySource.Count == 0 || m_CreationDefinitionQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            int replaced = 0;
            using (NativeArray<Entity> definitions = m_CreationDefinitionQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity definitionEntity in definitions)
                {
                    CreationDefinition definition = EntityManager.GetComponentData<CreationDefinition>(definitionEntity);
                    Entity specialIndustryOwner = FindSpecialIndustryOwner(definition.m_Owner);
                    bool isSurfacePrefab = definition.m_Prefab != Entity.Null && EntityManager.HasComponent<SurfaceData>(definition.m_Prefab);
                    bool isExtractorAreaPrefab = definition.m_Prefab != Entity.Null && EntityManager.HasComponent<ExtractorAreaData>(definition.m_Prefab);
                    if (specialIndustryOwner != Entity.Null || isExtractorAreaPrefab)
                    {
                        string mapFeature = isExtractorAreaPrefab
                            ? EntityManager.GetComponentData<ExtractorAreaData>(definition.m_Prefab).m_MapFeature.ToString()
                            : "<none>";
                        Mod.Log.InfoFormat(
                            "Special-industry creation definition: definition={0}, original={1}, owner={2}, industryOwner={3}, prefab='{4}', hasSurfaceData={5}, hasExtractorAreaData={6}, mapFeature={7}",
                            definitionEntity,
                            definition.m_Original,
                            definition.m_Owner,
                            specialIndustryOwner,
                            definition.m_Prefab != Entity.Null ? m_PrefabSystem.GetPrefabName(definition.m_Prefab) : "<null>",
                            isSurfacePrefab,
                            isExtractorAreaPrefab,
                            mapFeature);
                    }

                    if (definition.m_Original == Entity.Null && isExtractorAreaPrefab)
                    {
                        string lotPrefabName = m_PrefabSystem.GetPrefabName(definition.m_Prefab);
                        foreach (KeyValuePair<Entity, List<Entity>> sourceMapping in m_TargetsBySource)
                        {
                            if (sourceMapping.Value.Count == 0 ||
                                !AutomaticTextureReplacerUISystem.IsSpecialIndustrySurface(m_PrefabSystem, sourceMapping.Key) ||
                                !SurfaceReplacementSystem.MatchesSpecialIndustryLot(
                                    m_PrefabSystem.GetPrefabName(sourceMapping.Key),
                                    lotPrefabName))
                            {
                                continue;
                            }

                            Entity fieldTarget = sourceMapping.Value[m_Random.Next(sourceMapping.Value.Count)];
                            SurfaceReplacementSystem.QueueConstructionBatchRefresh(sourceMapping.Key, fieldTarget);
                            Mod.Log.InfoFormat(
                                "Queued automatic special-industry lot mapping: lotDefinition={0}, lotPrefab='{1}', source='{2}', target='{3}'",
                                definitionEntity,
                                lotPrefabName,
                                m_PrefabSystem.GetPrefabName(sourceMapping.Key),
                                m_PrefabSystem.GetPrefabName(fieldTarget));
                        }
                    }

                    // GenerateAreasSystem restores the prefab from the original area when
                    // m_Original is set, so changing that definition would not take effect.
                    // Restrict this feature to newly created areas belonging to new buildings.
                    if (definition.m_Original != Entity.Null ||
                        definition.m_Prefab == Entity.Null ||
                        !isSurfacePrefab ||
                        specialIndustryOwner != Entity.Null ||
                        (m_ToolSystem.activeTool == m_AreaToolSystem && specialIndustryOwner == Entity.Null && !isExtractorAreaPrefab) ||
                        !m_TargetsBySource.TryGetValue(definition.m_Prefab, out List<Entity> targets) ||
                        targets.Count == 0)
                    {
                        continue;
                    }

                    Entity source = definition.m_Prefab;
                    Entity target = targets[m_Random.Next(targets.Count)];
                    definition.m_Prefab = target;
                    EntityManager.SetComponentData(definitionEntity, definition);
                    SurfaceReplacementSystem.QueueConstructionBatchRefresh(source, target);
                    replaced++;

                    Mod.Log.InfoFormat(
                        "New building surface replacement: definition={0}, owner={1}, source='{2}', target='{3}', candidates={4}",
                        definitionEntity,
                        definition.m_Owner,
                        m_PrefabSystem.GetPrefabName(source),
                        m_PrefabSystem.GetPrefabName(target),
                        targets.Count);
                }
            }

            if (replaced > 0)
            {
                Mod.Log.InfoFormat("New building surface replacements applied before area creation: count={0}", replaced);
            }
        }

        private void RebuildReplacementMap(string rulesJson, bool resetPendingConstructionRefreshes)
        {
            if (resetPendingConstructionRefreshes)
            {
                SurfaceReplacementSystem.ResetConstructionBatchRefreshes();
            }
            m_CachedRulesJson = rulesJson;
            m_CachedSurfacePrefabCount = m_SurfacePrefabQuery.CalculateEntityCount();
            m_TargetsBySource.Clear();

            List<ReplacementRule> rules;
            try
            {
                rules = JSON.MakeInto<List<ReplacementRule>>(JSON.Load(rulesJson)) ?? new List<ReplacementRule>();
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Failed to read rules for new building surface replacement");
                return;
            }

            Dictionary<string, Entity> surfacesById = new Dictionary<string, Entity>(StringComparer.Ordinal);
            using (NativeArray<Entity> prefabs = m_SurfacePrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefabEntity in prefabs)
                {
                    if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase prefab) || prefab == null)
                    {
                        continue;
                    }

                    bool isSurface = EntityManager.HasComponent<SurfaceData>(prefabEntity);
                    bool isFishAreaSource = EntityManager.HasComponent<ExtractorAreaData>(prefabEntity) &&
                                            EntityManager.GetComponentData<ExtractorAreaData>(prefabEntity).m_MapFeature == MapFeature.Fish &&
                                            string.Equals(prefab.name, "Inland Fish Farm Lot", StringComparison.OrdinalIgnoreCase);
                    if ((!isSurface && !isFishAreaSource) || (isSurface && IsExcludedSurfaceName(prefab.name)))
                    {
                        continue;
                    }

                    string id = prefab.GetPrefabID().ToString();
                    if (!surfacesById.ContainsKey(id))
                    {
                        surfacesById.Add(id, prefabEntity);
                    }
                }
            }

            Dictionary<Entity, HashSet<Entity>> distinctTargetsBySource = new Dictionary<Entity, HashSet<Entity>>();
            foreach (ReplacementRule rule in rules)
            {
                if (rule == null || !rule.enabled || rule.sourceSurfaceIds == null || rule.targetSurfaceIds == null)
                {
                    continue;
                }

                foreach (string sourceId in rule.sourceSurfaceIds)
                {
                    if (string.IsNullOrEmpty(sourceId) || !surfacesById.TryGetValue(sourceId, out Entity source))
                    {
                        continue;
                    }

                    if (!distinctTargetsBySource.TryGetValue(source, out HashSet<Entity> targets))
                    {
                        targets = new HashSet<Entity>();
                        distinctTargetsBySource.Add(source, targets);
                    }

                    foreach (string targetId in rule.targetSurfaceIds)
                    {
                        if (!string.IsNullOrEmpty(targetId) && surfacesById.TryGetValue(targetId, out Entity target) &&
                            target != source && EntityManager.HasComponent<SurfaceData>(target))
                        {
                            targets.Add(target);
                        }
                    }
                }
            }

            foreach (KeyValuePair<Entity, HashSet<Entity>> pair in distinctTargetsBySource)
            {
                if (pair.Value.Count > 0)
                {
                    m_TargetsBySource.Add(pair.Key, pair.Value.ToList());
                }
            }

            Mod.Log.InfoFormat(
                "New building replacement map cached: prefabCount={0}, enabledRules={1}, sourceSurfaces={2}, mappings={3}, sources=[{4}]",
                m_CachedSurfacePrefabCount,
                rules.Count(rule => rule != null && rule.enabled),
                m_TargetsBySource.Count,
                m_TargetsBySource.Sum(pair => pair.Value.Count),
                string.Join(", ", m_TargetsBySource.Keys.Select(m_PrefabSystem.GetPrefabName)));
        }

        private static bool IsExcludedSurfaceName(string name)
        {
            return string.IsNullOrEmpty(name) ||
                   name.EndsWith("Placeholder", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Surface Area", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Missing Area", StringComparison.OrdinalIgnoreCase);
        }

        private Entity FindSpecialIndustryOwner(Entity owner)
        {
            Entity current = owner;
            HashSet<Entity> visited = new HashSet<Entity>();
            for (int depth = 0; depth < 16 && current != Entity.Null && visited.Add(current); depth++)
            {
                if (EntityManager.HasComponent<PrefabRef>(current))
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(current).m_Prefab;
                    if (prefab != Entity.Null && EntityManager.HasComponent<ExtractorAreaData>(prefab))
                    {
                        return current;
                    }
                }

                if (!EntityManager.HasComponent<Owner>(current))
                {
                    break;
                }

                current = EntityManager.GetComponentData<Owner>(current).m_Owner;
            }

            return Entity.Null;
        }
    }
}
