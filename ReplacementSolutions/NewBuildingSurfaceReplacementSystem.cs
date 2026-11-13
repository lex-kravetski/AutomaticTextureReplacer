using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Json;
using Game;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace AutomaticTextureReplacer
{
    /// <summary>
    /// Maintains automatic replacement rules and handles special-industry placement.
    /// </summary>
    public sealed partial class NewBuildingSurfaceReplacementSystem : GameSystemBase
    {
        private EntityQuery m_SurfacePrefabQuery;
        private EntityQuery m_AllPrefabQuery;
        private EntityQuery m_CreationDefinitionQuery;
        private PrefabSystem m_PrefabSystem;
        private ToolSystem m_ToolSystem;
        private AreaToolSystem m_AreaToolSystem;
        private string m_CachedRulesJson;
        private string m_CachedAreaRulesJson;
        private int m_CachedSurfacePrefabCount = -1;
        private Dictionary<Entity, List<Entity>> m_TargetsBySource = new Dictionary<Entity, List<Entity>>();
        private Dictionary<Entity, List<Entity>> m_TargetsByIndustryType = new Dictionary<Entity, List<Entity>>();
        private Entity m_LastSpecialIndustryPrefab;
        private OwnerDefinition m_LastSpecialIndustryOwnerDefinition;
        private bool m_HasLastSpecialIndustryOwnerDefinition;
        private int m_FramesWithoutSpecialIndustryDefinitions;
        private readonly HashSet<OwnerDefinition> m_QueuedIndustryOwnerDefinitions = new HashSet<OwnerDefinition>();
        private System.Random m_Random;

        public bool TryChooseSurfaceReplacement(Entity source, out Entity target)
        {
            target = Entity.Null;
            if (Mod.Settings == null || !Mod.Settings.AlsoNewBuildings ||
                !string.Equals(Mod.Settings.RulesJson ?? "[]", m_CachedRulesJson, StringComparison.Ordinal) ||
                !m_TargetsBySource.TryGetValue(source, out List<Entity> targets) || targets.Count == 0)
            {
                return false;
            }
            target = targets[m_Random.Next(targets.Count)];
            return true;
        }

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
            m_AllPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
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
                    m_CachedAreaRulesJson = null;
                    m_TargetsBySource.Clear();
                    m_TargetsByIndustryType.Clear();
                    m_QueuedIndustryOwnerDefinitions.Clear();
                    m_HasLastSpecialIndustryOwnerDefinition = false;
                    m_LastSpecialIndustryPrefab = Entity.Null;
                    m_FramesWithoutSpecialIndustryDefinitions = 0;
                    SurfaceReplacementSystem.ResetConstructionBatchRefreshes();
                }
                return;
            }

            string rulesJson = Mod.Settings.RulesJson ?? "[]";
            string areaRulesJson = Mod.Settings.AreaRulesJson ?? "[]";
            int surfacePrefabCount = m_SurfacePrefabQuery.CalculateEntityCount();
            bool rulesChanged = !string.Equals(rulesJson, m_CachedRulesJson, StringComparison.Ordinal);
            bool areaRulesChanged = !string.Equals(areaRulesJson, m_CachedAreaRulesJson, StringComparison.Ordinal);
            if (rulesChanged || areaRulesChanged)
            {
                RebuildReplacementMap(rulesJson, areaRulesJson, true);
            }
            else if (surfacePrefabCount != m_CachedSurfacePrefabCount)
            {
                // Prefabs can register after this system first sees settings (for
                // example, after mod assets finish loading). Rebuild once the catalog
                // changes so valid rules are not silently missing from construction.
                RebuildReplacementMap(rulesJson, areaRulesJson, false);
            }

            PrefabBase activePrefab = m_ToolSystem.activePrefab;
            Entity activePrefabEntity = activePrefab != null ? m_PrefabSystem.GetEntity(activePrefab) : Entity.Null;
            if (activePrefabEntity != Entity.Null && m_TargetsByIndustryType.ContainsKey(activePrefabEntity))
            {
                if (m_LastSpecialIndustryPrefab != activePrefabEntity)
                {
                    m_HasLastSpecialIndustryOwnerDefinition = false;
                    m_QueuedIndustryOwnerDefinitions.Clear();
                }
                m_LastSpecialIndustryPrefab = activePrefabEntity;
            }

            if ((m_TargetsBySource.Count == 0 && m_TargetsByIndustryType.Count == 0) || m_CreationDefinitionQuery.IsEmptyIgnoreFilter)
            {
                if (++m_FramesWithoutSpecialIndustryDefinitions > 120)
                {
                    m_HasLastSpecialIndustryOwnerDefinition = false;
                    m_QueuedIndustryOwnerDefinitions.Clear();
                }
                return;
            }

            bool sawSpecialIndustryDefinition = false;
            using (NativeArray<Entity> definitions = m_CreationDefinitionQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity definitionEntity in definitions)
                {
                    CreationDefinition definition = EntityManager.GetComponentData<CreationDefinition>(definitionEntity);
                    Entity specialIndustryOwner = FindSpecialIndustryOwner(definition.m_Owner);
                    bool isIndustryExtractionDefinition = specialIndustryOwner != Entity.Null &&
                        EntityManager.HasComponent<Game.Areas.Lot>(specialIndustryOwner);
                    bool isSurfacePrefab = definition.m_Prefab != Entity.Null && EntityManager.HasComponent<SurfaceData>(definition.m_Prefab);
                    bool isExtractorAreaPrefab = definition.m_Prefab != Entity.Null && EntityManager.HasComponent<ExtractorAreaData>(definition.m_Prefab);
                    sawSpecialIndustryDefinition |= isExtractorAreaPrefab;
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

                    if (isExtractorAreaPrefab)
                    {
                        Entity constructionBuilding = FindOwningBuilding(definition.m_Owner);
                        Entity constructionBuildingPrefab = constructionBuilding != Entity.Null &&
                            EntityManager.HasComponent<PrefabRef>(constructionBuilding)
                                ? EntityManager.GetComponentData<PrefabRef>(constructionBuilding).m_Prefab
                                : Entity.Null;

                        bool hasOwnerDefinition = EntityManager.HasComponent<OwnerDefinition>(definitionEntity);
                        OwnerDefinition ownerDefinition = hasOwnerDefinition
                            ? EntityManager.GetComponentData<OwnerDefinition>(definitionEntity)
                            : default;
                        Entity industryPrefab = ownerDefinition.m_Prefab;
                        if (industryPrefab == Entity.Null)
                        {
                            industryPrefab = constructionBuildingPrefab;
                        }

                        if (industryPrefab == Entity.Null && m_HasLastSpecialIndustryOwnerDefinition &&
                            m_LastSpecialIndustryOwnerDefinition.m_Prefab != Entity.Null &&
                            SurfaceReplacementSystem.MatchesIndustryTypeLot(
                                EntityManager,
                                m_PrefabSystem,
                                m_LastSpecialIndustryOwnerDefinition.m_Prefab,
                                definition.m_Prefab))
                        {
                            ownerDefinition = m_LastSpecialIndustryOwnerDefinition;
                            industryPrefab = ownerDefinition.m_Prefab;
                            hasOwnerDefinition = true;
                            Mod.Log.InfoFormat(
                                "Resolved ownerless extractor definition from the current special-industry placement: lot='{0}', ownerPrefab='{1}', position={2}",
                                m_PrefabSystem.GetPrefabName(definition.m_Prefab),
                                m_PrefabSystem.GetPrefabName(industryPrefab),
                                ownerDefinition.m_Position);
                        }

                        if (industryPrefab == Entity.Null && m_LastSpecialIndustryPrefab != Entity.Null &&
                            SurfaceReplacementSystem.MatchesIndustryTypeLot(
                                EntityManager,
                                m_PrefabSystem,
                                m_LastSpecialIndustryPrefab,
                                definition.m_Prefab))
                        {
                            industryPrefab = m_LastSpecialIndustryPrefab;
                        }

                        // Some extractor definitions arrive without an owner and
                        // before the active menu prefab is available. Resolve the
                        // industry type by its map feature, which is the stable
                        // identity shared by all lots of that industry.
                        if (industryPrefab == Entity.Null &&
                            EntityManager.HasComponent<ExtractorAreaData>(definition.m_Prefab))
                        {
                            ExtractorAreaData definitionArea = EntityManager.GetComponentData<ExtractorAreaData>(definition.m_Prefab);
                            foreach (Entity candidate in m_TargetsByIndustryType.Keys)
                            {
                                if (EntityManager.HasComponent<ExtractorAreaData>(candidate) &&
                                    EntityManager.GetComponentData<ExtractorAreaData>(candidate).m_MapFeature == definitionArea.m_MapFeature)
                                {
                                    industryPrefab = candidate;
                                    Mod.Log.InfoFormat(
                                        "Resolved ownerless extractor definition by map feature: lot='{0}', mapFeature={1}, industryPrefab='{2}'",
                                        m_PrefabSystem.GetPrefabName(definition.m_Prefab),
                                        definitionArea.m_MapFeature,
                                        m_PrefabSystem.GetPrefabName(industryPrefab));
                                    break;
                                }
                            }
                        }

                        if (!hasOwnerDefinition && industryPrefab != Entity.Null &&
                            m_HasLastSpecialIndustryOwnerDefinition &&
                            m_LastSpecialIndustryOwnerDefinition.m_Prefab == industryPrefab)
                        {
                            ownerDefinition = m_LastSpecialIndustryOwnerDefinition;
                            hasOwnerDefinition = true;
                            Mod.Log.InfoFormat(
                                "Reused current special-industry placement for ownerless extractor definition: lot='{0}', industryPrefab='{1}', position={2}",
                                m_PrefabSystem.GetPrefabName(definition.m_Prefab),
                                m_PrefabSystem.GetPrefabName(industryPrefab),
                                ownerDefinition.m_Position);
                        }

                        if (industryPrefab != Entity.Null && m_TargetsByIndustryType.TryGetValue(industryPrefab, out List<Entity> areaTargets) &&
                            areaTargets.Count > 0)
                        {
                            if (!hasOwnerDefinition && constructionBuilding != Entity.Null && EntityManager.HasComponent<Game.Objects.Transform>(constructionBuilding))
                            {
                                Game.Objects.Transform transform = EntityManager.GetComponentData<Game.Objects.Transform>(constructionBuilding);
                                ownerDefinition = new OwnerDefinition
                                {
                                    m_Prefab = industryPrefab,
                                    m_Position = transform.m_Position,
                                    m_Rotation = transform.m_Rotation,
                                };
                                hasOwnerDefinition = true;
                            }

                            if (hasOwnerDefinition)
                            {
                                ownerDefinition.m_Prefab = industryPrefab;
                                m_LastSpecialIndustryPrefab = industryPrefab;
                                m_LastSpecialIndustryOwnerDefinition = ownerDefinition;
                                m_HasLastSpecialIndustryOwnerDefinition = true;

                                if (m_QueuedIndustryOwnerDefinitions.Add(ownerDefinition))
                                {
                                    Entity areaTarget = areaTargets[m_Random.Next(areaTargets.Count)];
                                    SurfaceReplacementSystem.QueueConstructionIndustryReplacement(
                                        industryPrefab,
                                        areaTarget,
                                        constructionBuilding,
                                        ownerDefinition,
                                        true);
                                    Mod.Log.InfoFormat(
                                        "Queued automatic Areas-tab replacement for special-industry placement: lot='{0}', owner={1}, type='{2}', position={3}, target='{4}'",
                                        m_PrefabSystem.GetPrefabName(definition.m_Prefab),
                                        constructionBuilding,
                                        m_PrefabSystem.GetPrefabName(industryPrefab),
                                        ownerDefinition.m_Position,
                                        m_PrefabSystem.GetPrefabName(areaTarget));
                                }
                            }
                            else
                            {
                                Mod.Log.WarnFormat(
                                    "Skipped automatic Areas-tab replacement without an instance identity: lot='{0}', activeIndustry='{1}'",
                                    m_PrefabSystem.GetPrefabName(definition.m_Prefab),
                                    m_PrefabSystem.GetPrefabName(industryPrefab));
                            }
                        }
                    }

                    // GenerateAreasSystem restores the prefab from the original area when
                    // m_Original is set, so changing that definition would not take effect.
                    // Restrict this feature to newly created areas belonging to new buildings.
                    if (definition.m_Original != Entity.Null ||
                        definition.m_Prefab == Entity.Null ||
                        !isSurfacePrefab ||
                        isIndustryExtractionDefinition ||
                        (m_ToolSystem.activeTool == m_AreaToolSystem && !isIndustryExtractionDefinition && !isExtractorAreaPrefab) ||
                        !m_TargetsBySource.TryGetValue(definition.m_Prefab, out List<Entity> targets) ||
                        targets.Count == 0)
                    {
                        continue;
                    }

                    // Keep the source prefab until the area has been created.
                    // SurfaceReplacementSystem handles the applied area itself.
                    bool hasSurfaceOwnerDefinition = EntityManager.HasComponent<OwnerDefinition>(definitionEntity);
                    OwnerDefinition surfaceOwnerDefinition = hasSurfaceOwnerDefinition
                        ? EntityManager.GetComponentData<OwnerDefinition>(definitionEntity)
                        : default;
                    SurfaceReplacementSystem.QueueConstructionBatchRefresh(
                        definition.m_Prefab,
                        targets[m_Random.Next(targets.Count)],
                        definition.m_Owner,
                        surfaceOwnerDefinition,
                        hasSurfaceOwnerDefinition);
                    Mod.Log.InfoFormat(
                        "Automatic construction replacement queued for post-creation swap: definition={0}, owner={1}, ownerDefinition={2}, source='{3}', candidates={4}",
                        definitionEntity,
                        definition.m_Owner,
                        hasSurfaceOwnerDefinition
                            ? surfaceOwnerDefinition.m_Prefab + " at " + surfaceOwnerDefinition.m_Position
                            : "<none>",
                        m_PrefabSystem.GetPrefabName(definition.m_Prefab),
                        targets.Count);
                }
            }

            if (sawSpecialIndustryDefinition)
            {
                m_FramesWithoutSpecialIndustryDefinitions = 0;
            }
            else if (++m_FramesWithoutSpecialIndustryDefinitions > 120)
            {
                m_HasLastSpecialIndustryOwnerDefinition = false;
                m_QueuedIndustryOwnerDefinitions.Clear();
            }
        }

        private void RebuildReplacementMap(string rulesJson, string areaRulesJson, bool resetPendingConstructionRefreshes)
        {
            if (resetPendingConstructionRefreshes)
            {
                SurfaceReplacementSystem.ResetConstructionBatchRefreshes();
                m_QueuedIndustryOwnerDefinitions.Clear();
                m_HasLastSpecialIndustryOwnerDefinition = false;
            }
            m_CachedRulesJson = rulesJson;
            m_CachedAreaRulesJson = areaRulesJson;
            m_CachedSurfacePrefabCount = m_SurfacePrefabQuery.CalculateEntityCount();
            m_TargetsBySource.Clear();
            m_TargetsByIndustryType.Clear();

            List<SurfaceReplacementSystem.SurfaceReplacementRule> rules;
            try
            {
                rules = JSON.MakeInto<List<SurfaceReplacementSystem.SurfaceReplacementRule>>(JSON.Load(rulesJson)) ??
                        new List<SurfaceReplacementSystem.SurfaceReplacementRule>();
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

                    if (!EntityManager.HasComponent<SurfaceData>(prefabEntity) ||
                        (IsExcludedSurfaceName(prefab.name) && !ReplacementRuleCatalog.IsTransparentSurface(prefab.name)))
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

            Dictionary<string, HashSet<string>> targetIdsBySource = ReplacementRuleCatalog.BuildSurfaceTargetIds(
                rules,
                surfacesById,
                enabledOnly: true);
            foreach (KeyValuePair<string, HashSet<string>> pair in targetIdsBySource)
            {
                if (!surfacesById.TryGetValue(pair.Key, out Entity source) || pair.Value.Count == 0)
                {
                    continue;
                }

                List<Entity> targets = pair.Value
                    .Where(surfacesById.ContainsKey)
                    .Select(targetId => surfacesById[targetId])
                    .Where(target => EntityManager.HasComponent<SurfaceData>(target))
                    .ToList();
                if (targets.Count > 0)
                {
                    m_TargetsBySource[source] = targets;
                }
            }

            var prefabsById = new Dictionary<string, Entity>(StringComparer.Ordinal);
            using (NativeArray<Entity> prefabEntities = m_AllPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity prefabEntity in prefabEntities)
                {
                    if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase prefab) || prefab == null)
                        continue;
                    string id = prefab.GetPrefabID().ToString();
                    if (!prefabsById.ContainsKey(id)) prefabsById.Add(id, prefabEntity);
                }
            }

            List<SurfaceReplacementSystem.AreaReplacementRule> areaRules;
            try
            {
                areaRules = JSON.MakeInto<List<SurfaceReplacementSystem.AreaReplacementRule>>(JSON.Load(areaRulesJson)) ??
                            new List<SurfaceReplacementSystem.AreaReplacementRule>();
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Failed to read Areas-tab rules for automatic construction replacement");
                areaRules = new List<SurfaceReplacementSystem.AreaReplacementRule>();
            }

            Dictionary<string, HashSet<string>> targetIdsByIndustry = ReplacementRuleCatalog.BuildIndustryTargetIds(
                areaRules,
                prefabsById,
                surfacesById,
                enabledOnly: true);
            foreach (KeyValuePair<string, HashSet<string>> pair in targetIdsByIndustry)
            {
                if (!prefabsById.TryGetValue(pair.Key, out Entity sourceType))
                {
                    continue;
                }

                List<Entity> targets = pair.Value
                    .Where(surfacesById.ContainsKey)
                    .Select(targetId => surfacesById[targetId])
                    .ToList();
                if (targets.Count > 0)
                {
                    m_TargetsByIndustryType[sourceType] = targets;
                }
            }

            Mod.Log.InfoFormat(
                "New construction replacement maps cached: prefabCount={0}, surfaceSources={1}, surfaceMappings={2}, industryTypes={3}, industryMappings={4}",
                m_CachedSurfacePrefabCount,
                m_TargetsBySource.Count,
                m_TargetsBySource.Sum(pair => pair.Value.Count),
                m_TargetsByIndustryType.Count,
                m_TargetsByIndustryType.Sum(pair => pair.Value.Count));
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

        private Entity FindOwningBuilding(Entity entity)
        {
            Entity current = entity;
            HashSet<Entity> visited = new HashSet<Entity>();
            for (int depth = 0; depth < 16 && current != Entity.Null && visited.Add(current); depth++)
            {
                if (EntityManager.HasComponent<Building>(current))
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

        private string GetPrefabIdString(Entity prefabEntity)
        {
            return m_PrefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out PrefabBase prefab) && prefab != null
                ? prefab.GetPrefabID().ToString()
                : string.Empty;
        }
    }
}
