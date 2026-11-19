using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Json;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace AutomaticTextureReplacer
{
    public sealed partial class ReplacementSolutionsUISystem
    {
        private int m_RoadTextureFrameCounter;

        private sealed class RoadTextureGenerationState
        {
            public CancellationTokenSource Cancellation { get; set; }
            public CancellationToken Token { get; set; }
            public Task Task { get; set; }
            public TaskCompletionSource<bool> Completion { get; set; }
            public bool CaptureFinished { get; set; }
            public Exception CaptureError { get; set; }
            public RoadTextureRule PendingRule { get; set; }
            public int CaptureAfterFrame { get; set; }
        }

        private void GenerateChangedRoadAreaTextures(string oldJson, string newJson)
        {
            List<RoadTextureRule> oldRules = ParseRoadTextureRules(oldJson);
            List<RoadTextureRule> newRules = ParseRoadTextureRules(newJson);
            var newRuleIds = new HashSet<int>(newRules.Select(item => item.id));
            foreach (RoadTextureRule oldRule in oldRules)
                if (!newRuleIds.Contains(oldRule.id))
                {
                    CancelRoadTextureGeneration(oldRule.id, removeState: true);
                    m_PendingRoadReplacementRules.Remove(oldRule.id);
                    DeleteGeneratedRoadTextures(oldRule.id);
                }
            foreach (RoadTextureRule rule in newRules)
            {
                RoadTextureRule oldRule = oldRules.FirstOrDefault(item => item.id == rule.id);
                if (RoadAreaMappingsEqual(oldRule?.roadAreaReplacements, rule.roadAreaReplacements))
                    continue;

                RoadTextureGenerationState generation = BeginRoadTextureGeneration(rule.id);
                if (rule.roadAreaReplacements == null || rule.roadAreaReplacements.Count == 0)
                {
                    DeleteGeneratedRoadTextures(rule.id);
                    generation.CaptureFinished = true;
                    CompleteRoadTexturePreparation(generation);
                    continue;
                }
                generation.PendingRule = rule;
                generation.CaptureAfterFrame = m_RoadTextureFrameCounter + 1;
            }
        }

        private void TryScheduleRoadTexturePreGeneration()
        {
            if (m_RoadStartupTexturePreGenerationScheduled || !m_RoadCatalogBuildCompleted || !m_SurfaceCatalogBuildStarted)
                return;
            if (!m_RoadDefiningTextures.TryGetValue("_BaseColorMap", out TextureAsset baseColor) || baseColor == null)
                return;

            m_RoadStartupTexturePreGenerationScheduled = true;
            List<RoadTextureRule> rules = ParseRoadTextureRules(Mod.Settings?.RoadRulesJson ?? "[]");
            RoadTextureRule[] rulesToGenerate = rules
                .Where(rule => rule != null && rule.roadAreaReplacements != null && rule.roadAreaReplacements.Count != 0)
                .ToArray();
            if (rulesToGenerate.Length == 0)
            {
                Mod.Log.Info("Road startup texture capture skipped: no saved road rules have area replacements");
                return;
            }

            foreach (RoadTextureRule rule in rulesToGenerate)
            {
                RoadTextureGenerationState generation = BeginRoadTextureGeneration(rule.id);
                generation.PendingRule = rule;
                generation.CaptureAfterFrame = m_RoadTextureFrameCounter + 1;
            }

            Mod.Log.InfoFormat("Road startup texture source capture scheduled: rules={0}, areaReplacements={1}",
                rulesToGenerate.Length,
                rulesToGenerate.Sum(rule => rule.roadAreaReplacements.Count));
        }

        private void CompleteRoadTexturePreparation(RoadTextureGenerationState generation)
        {
            if (!generation.CaptureFinished) return;

            if (generation.Token.IsCancellationRequested)
            {
                generation.Completion.TrySetCanceled();
                return;
            }
            if (generation.CaptureError != null)
            {
                generation.Completion.TrySetException(generation.CaptureError);
                return;
            }

            generation.Completion.TrySetResult(true);
        }

        private RoadTextureGenerationState BeginRoadTextureGeneration(int ruleId)
        {
            lock (m_RoadTextureGenerationLock)
            {
                if (m_RoadTextureGenerationStates.TryGetValue(ruleId, out RoadTextureGenerationState previous))
                    CancelRoadTextureGenerationState(previous);
                var state = new RoadTextureGenerationState
                {
                    Cancellation = new CancellationTokenSource(),
                    Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                };
                state.Token = state.Cancellation.Token;
                state.Task = state.Completion.Task;
                m_RoadTextureGenerationStates[ruleId] = state;
                return state;
            }
        }

        private void CancelRoadTextureGeneration(int ruleId, bool removeState)
        {
            lock (m_RoadTextureGenerationLock)
            {
                if (!m_RoadTextureGenerationStates.TryGetValue(ruleId, out RoadTextureGenerationState state)) return;
                CancelRoadTextureGenerationState(state);
                if (removeState) m_RoadTextureGenerationStates.Remove(ruleId);
            }
        }

        private static void CancelRoadTextureGenerationState(RoadTextureGenerationState state)
        {
            if (state?.Cancellation == null) return;
            state.Cancellation.Cancel();
            state.Completion?.TrySetCanceled();
            if (state.Task == null)
                state.Cancellation.Dispose();
            else
                state.Task.ContinueWith(_ => state.Cancellation.Dispose(), TaskScheduler.Default);
        }

        private void ProcessRoadTextureGenerationTasks()
        {
            m_RoadTextureFrameCounter++;
            foreach (KeyValuePair<int, RoadTextureGenerationState> entry in m_RoadTextureGenerationStates.ToArray())
            {
                RoadTextureGenerationState state = entry.Value;
                if (state.PendingRule != null && m_RoadTextureFrameCounter >= state.CaptureAfterFrame)
                {
                    RoadTextureRule rule = state.PendingRule;
                    state.PendingRule = null;
                    try
                    {
                        GenerateRoadRuleTextures(rule, state);
                    }
                    catch (Exception exception)
                    {
                        state.CaptureError = exception;
                        Mod.Log.Error(exception, "Could not prepare RoadEU textures for rule " + rule.id);
                    }
                    state.CaptureFinished = true;
                    CompleteRoadTexturePreparation(state);
                }
                if (state.Task == null || !state.Task.IsCompleted) continue;

                lock (m_RoadTextureGenerationLock)
                {
                    if (m_RoadTextureGenerationStates.TryGetValue(entry.Key, out RoadTextureGenerationState current) &&
                        ReferenceEquals(current, state))
                        m_RoadTextureGenerationStates.Remove(entry.Key);
                    else
                        continue;
                }

                bool succeeded = state.Task.Status == TaskStatus.RanToCompletion;
                if (state.Task.IsFaulted)
                    Mod.Log.Error(state.Task.Exception, "Road texture source capture failed for rule " + entry.Key);
                else if (succeeded)
                    Mod.Log.InfoFormat("Road texture source capture completed: ruleId='{0}'", entry.Key);

                state.Cancellation.Dispose();
                if (m_PendingRoadReplacementRules.Remove(entry.Key))
                {
                    if (succeeded)
                    {
                        RoadReplacementSystem.QueueReplacement(entry.Key.ToString(), Mod.Settings?.RoadRulesJson ?? "[]");
                        Mod.Log.InfoFormat("Road replacement queued after texture preparation: ruleId='{0}'", entry.Key);
                    }
                    else
                        Mod.Log.WarnFormat("Road replacement was not started because texture preparation did not complete: ruleId='{0}'", entry.Key);
                }
            }
        }

        private static bool RoadAreaMappingsEqual(Dictionary<string, string> first, Dictionary<string, string> second)
        {
            first = first ?? new Dictionary<string, string>();
            second = second ?? new Dictionary<string, string>();
            if (first.Count != second.Count) return false;
            foreach (KeyValuePair<string, string> entry in first)
                if (!second.TryGetValue(entry.Key, out string other) || !string.Equals(entry.Value, other, StringComparison.Ordinal))
                    return false;
            return true;
        }

        private static List<RoadTextureRule> ParseRoadTextureRules(string json)
        {
            try
            {
                return JSON.MakeInto<List<RoadTextureRule>>(JSON.Load(json ?? "[]")) ?? new List<RoadTextureRule>();
            }
            catch (Exception exception)
            {
                Mod.Log.Warn(exception, "Could not read road texture replacement rules");
                return new List<RoadTextureRule>();
            }
        }

        private void GenerateRoadRuleTextures(RoadTextureRule rule, RoadTextureGenerationState generation)
        {
            RoadTextureSourceRegistry.ClearRule(rule.id);

            var replacementSurfaces = new Dictionary<string, RoadSurfaceInfo>(StringComparer.Ordinal);
            using (NativeArray<Entity> entities = m_SurfaceQuery.ToEntityArray(Allocator.Temp))
            {
                PrefabSystem prefabSystem = World.GetExistingSystemManaged<PrefabSystem>();
                foreach (Entity entity in entities)
                {
                    if (!prefabSystem.TryGetPrefab<SurfacePrefab>(entity, out SurfacePrefab surface) || surface == null ||
                        !surface.TryGet<RenderedArea>(out RenderedArea renderedArea) || renderedArea == null)
                        continue;
                    Material runtimeMaterial = null;
                    if (m_AreaBatchSystem != null && EntityManager.HasComponent<RenderedAreaData>(entity))
                    {
                        int batchIndex = EntityManager.GetComponentData<RenderedAreaData>(entity).m_BatchIndex;
                        if (batchIndex >= 0 && batchIndex < m_AreaBatchSystem.GetBatchCount())
                            m_AreaBatchSystem.GetAreaBatch(batchIndex, out _, out _, out _, out runtimeMaterial, out _, out _, out _);
                    }
                    replacementSurfaces[surface.GetPrefabID().ToString()] = new RoadSurfaceInfo(surface, renderedArea, runtimeMaterial);
                }
            }

            DeleteGeneratedRoadTextures(rule.id);
            foreach (KeyValuePair<string, string> replacement in rule.roadAreaReplacements ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(replacement.Value)) continue;
                RoadAtlasRegion region = RoadAtlasRegions.RoadEu.FirstOrDefault(item => item.Id == replacement.Key);
                if (region == null)
                {
                    Mod.Log.WarnFormat("Road texture replacement region is not defined: area='{0}'", replacement.Key);
                    continue;
                }
                if (!replacementSurfaces.TryGetValue(replacement.Value, out RoadSurfaceInfo replacementInfo))
                {
                    Mod.Log.WarnFormat("Road texture replacement surface was not found: surfaceId='{0}', area='{1}'", replacement.Value, region.Name);
                    continue;
                }

                string colorProperty = region.Id == "roadway" ? "_WorldspaceAlbedo" : "_BaseColorMap";
                string normalProperty = region.Id == "roadway" ? "_WorldspaceNormalMap" : "_NormalMap";
                CaptureRoadSurfaceTexture(rule.id, region, replacementInfo, "_BaseColorMap", colorProperty);
                CaptureRoadSurfaceTexture(rule.id, region, replacementInfo, "_NormalMap", normalProperty);
            }

            Mod.Log.InfoFormat("Road texture inputs captured without atlas baking: ruleId='{0}', mappedRegions={1}",
                rule.id, (rule.roadAreaReplacements ?? new Dictionary<string, string>()).Count);
        }

        private static void CaptureRoadSurfaceTexture(int ruleId, RoadAtlasRegion region, RoadSurfaceInfo replacement,
            string sourceProperty, string destinationProperty)
        {
            if (!TryGetRenderedAreaTexture(replacement.RenderedArea, replacement.RuntimeMaterial,
                sourceProperty, out TextureAsset asset, out Texture materialTexture))
            {
                Mod.Log.WarnFormat("Road replacement map not found: ruleId='{0}', area='{1}', surface='{2}', map='{3}'",
                    ruleId, region.Name, replacement.Prefab.name, sourceProperty);
                return;
            }

            RoadTextureSourceRegistry.Register(ruleId, region.Id, destinationProperty, asset, materialTexture);
            Mod.Log.InfoFormat("Road texture input captured: ruleId='{0}', area='{1}', surface='{2}', source='{3}', destination='{4}', asset='{5}', texture='{6}'",
                ruleId, region.Name, replacement.Prefab.name, sourceProperty, destinationProperty,
                asset != null ? asset.name : "<material texture>",
                materialTexture != null ? materialTexture.name + " " + materialTexture.width + "x" + materialTexture.height : "<asset>");
        }

        private static bool TryGetRenderedAreaTexture(RenderedArea renderedArea, Material fallbackMaterial, string property,
            out TextureAsset asset, out Texture materialTexture)
        {
            asset = null;
            if (property == "_BaseColorMap") asset = ResolveTextureAsset(renderedArea.m_BaseColorMap);
            else if (property == "_NormalMap") asset = ResolveTextureAsset(renderedArea.m_NormalMap);
            else if (property == "_MaskMap") asset = ResolveTextureAsset(renderedArea.m_MaskMap);
            materialTexture = null;
            if (asset != null) return true;

            int propertyId = Shader.PropertyToID(property);
            Material material = renderedArea.m_Material;
            if (material != null && material.HasProperty(propertyId))
                materialTexture = material.GetTexture(propertyId);
            if (materialTexture == null && fallbackMaterial != null && fallbackMaterial.HasProperty(propertyId))
                materialTexture = fallbackMaterial.GetTexture(propertyId);
            return materialTexture != null;
        }

        private sealed class RoadSurfaceInfo
        {
            public SurfacePrefab Prefab { get; }
            public RenderedArea RenderedArea { get; }
            public Material RuntimeMaterial { get; }

            public RoadSurfaceInfo(SurfacePrefab prefab, RenderedArea renderedArea, Material runtimeMaterial)
            {
                Prefab = prefab;
                RenderedArea = renderedArea;
                RuntimeMaterial = runtimeMaterial;
            }
        }

        private static TextureAsset ResolveTextureAsset(AssetReference<TextureAsset> reference)
        {
            return reference != null && reference.guid.isValid
                ? AssetDatabase.global.GetAsset<TextureAsset>(reference.guid)
                : null;
        }

        private static void DeleteGeneratedRoadTextures(int ruleId)
        {
            string directory = Path.Combine(GetModsDataRoot(), "Replacement Solutions", "textures");
            if (!Directory.Exists(directory)) return;
            string prefix = "RoadEU_Rule_" + ruleId + "_";
            foreach (string file in Directory.GetFiles(directory, prefix + "*.png", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(file); }
                catch (Exception exception) { Mod.Log.Warn(exception, "Could not remove an obsolete temporary road texture: " + file); }
            }
        }


    }
}
