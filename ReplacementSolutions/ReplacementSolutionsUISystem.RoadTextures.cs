using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Json;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Rendering;
using UnityEngine;

namespace AutomaticTextureReplacer
{
    public sealed partial class ReplacementSolutionsUISystem
    {
        private static readonly uint[] s_PngCrcTable = BuildPngCrcTable();

        private int m_RoadTextureFrameCounter;

        private sealed class RoadTextureGenerationState
        {
            public int Generation { get; set; }
            public CancellationTokenSource Cancellation { get; set; }
            public CancellationToken Token { get; set; }
            public Task Task { get; set; }
            public TaskCompletionSource<bool> Completion { get; set; }
            public List<RoadTexturePngOutput> Outputs { get; set; }
            public int PendingReadbacks { get; set; }
            public bool CaptureFinished { get; set; }
            public bool WorkerStarted { get; set; }
            public Exception CaptureError { get; set; }
            public RoadTextureRule PendingRule { get; set; }
            public int CaptureAfterFrame { get; set; }
        }

        private sealed class RoadTexturePngOutput
        {
            public int RuleId { get; set; }
            public int Generation { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public Color32[] Pixels { get; set; }
            public string Path { get; set; }
            public string MapProperty { get; set; }
            public string Replacements { get; set; }
        }

        private int m_RoadTextureGenerationCounter;

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
                Mod.Log.Info("Road startup texture pre-generation skipped: no saved road rules have area replacements");
                return;
            }

            foreach (RoadTextureRule rule in rulesToGenerate)
            {
                RoadTextureGenerationState generation = BeginRoadTextureGeneration(rule.id);
                generation.PendingRule = rule;
                generation.CaptureAfterFrame = m_RoadTextureFrameCounter + 1;
            }

            Mod.Log.InfoFormat("Road startup texture pre-generation scheduled: rules={0}, source='RoadEU', areaReplacements={1}; existing rules will be rendered without applying road replacements",
                rulesToGenerate.Length,
                rulesToGenerate.Sum(rule => rule.roadAreaReplacements.Count));
        }

        private void CompleteRoadTexturePreparation(RoadTextureGenerationState generation)
        {
            lock (generation)
            {
                if (!generation.CaptureFinished || generation.PendingReadbacks != 0 || generation.WorkerStarted) return;
                generation.WorkerStarted = true;
            }

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

            Task.Run(() => WriteRoadTexturePngs(generation.Outputs ?? new List<RoadTexturePngOutput>(), generation))
                .ContinueWith(task =>
                {
                    if (task.IsCanceled) generation.Completion.TrySetCanceled();
                    else if (task.IsFaulted) generation.Completion.TrySetException(task.Exception.InnerExceptions);
                    else generation.Completion.TrySetResult(true);
                }, TaskScheduler.Default);
        }

        private RoadTextureGenerationState BeginRoadTextureGeneration(int ruleId)
        {
            lock (m_RoadTextureGenerationLock)
            {
                if (m_RoadTextureGenerationStates.TryGetValue(ruleId, out RoadTextureGenerationState previous))
                    CancelRoadTextureGenerationState(previous);
                var state = new RoadTextureGenerationState
                {
                    Generation = Interlocked.Increment(ref m_RoadTextureGenerationCounter),
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
                        List<RoadTexturePngOutput> outputs = GenerateRoadRuleTextures(rule, state);
                        state.Outputs = outputs;
                        DeleteObsoleteGeneratedRoadMaps(rule.id, outputs, state);
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
                    Mod.Log.Error(state.Task.Exception, "Background road texture preparation failed for rule " + entry.Key);
                else if (succeeded)
                {
                    foreach (RoadTexturePngOutput output in state.Outputs ?? new List<RoadTexturePngOutput>())
                        Mod.Log.InfoFormat("Road texture generated: source='RoadEU', map='{0}', resolution={1}x{2}, replacements=[{3}], file='{4}'",
                            output.MapProperty, output.Width, output.Height, output.Replacements, output.Path);
                    Mod.Log.InfoFormat("Background road texture preparation completed: ruleId='{0}'", entry.Key);
                }

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

        private void DeleteObsoleteGeneratedRoadMaps(int ruleId, List<RoadTexturePngOutput> outputs, RoadTextureGenerationState generation)
        {
            string directory = Path.Combine(GetModsDataRoot(), "Replacement Solutions", "textures");
            var outputPaths = new HashSet<string>(outputs.Select(output => output.Path), StringComparer.OrdinalIgnoreCase);
            lock (m_RoadTextureGenerationLock)
            {
                if (!m_RoadTextureGenerationStates.TryGetValue(ruleId, out RoadTextureGenerationState current) ||
                    !ReferenceEquals(current, generation)) return;
                foreach (RoadAtlasTextureLayer layer in RoadAtlasRegions.RoadEuTextureLayers)
                {
                    string fileName = SanitizePrefabName("RoadEU_Rule_" + ruleId + "_" + layer.MaterialProperty.Trim('_')) + ".png";
                    string path = Path.Combine(directory, fileName);
                    if (outputPaths.Contains(path) || !File.Exists(path)) continue;
                    try { File.Delete(path); }
                    catch (Exception exception) { Mod.Log.Warn(exception, "Could not remove an obsolete generated road map: " + path); }
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

        private List<RoadTexturePngOutput> GenerateRoadRuleTextures(RoadTextureRule rule, RoadTextureGenerationState generation)
        {
            var outputs = new List<RoadTexturePngOutput>();
            if (m_RoadDefiningTextures.Count == 0)
            {
                Mod.Log.Warn("Road texture generation skipped because the RoadEU source maps are not available yet");
                return outputs;
            }

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

            string directory = Path.Combine(GetModsDataRoot(), "Replacement Solutions", "textures");
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception exception)
            {
                Mod.Log.Error(exception, "Could not create the temporary road texture directory: " + directory);
                return outputs;
            }
            DeleteObsoleteStandaloneRegionTextures(directory);

            foreach (RoadAtlasTextureLayer mapLayer in RoadAtlasRegions.RoadEuTextureLayers)
            {
                string sourceProperty = mapLayer.MaterialProperty;
                if (!m_RoadDefiningTextures.TryGetValue(sourceProperty, out TextureAsset sourceMap) || sourceMap == null ||
                    !string.Equals(sourceMap.name, mapLayer.TextureAssetName, StringComparison.Ordinal))
                    continue;

                string fileName = SanitizePrefabName("RoadEU_Rule_" + rule.id + "_" + sourceProperty.Trim('_')) + ".png";
                string path = Path.Combine(directory, fileName);
                string[] replacements = rule.roadAreaReplacements
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                    .Select(entry => entry.Key + "=" + entry.Value)
                    .ToArray();
                if (replacements.Length == 0) continue;

                try
                {
                    RenderTexture atlas = null;
                    var changedAreas = new List<string>();
                    try
                    {
                        Texture2D sourceTexture = sourceMap.Load<Texture2D>(0, TextureAsset.KeepOnCPU.Object);
                        bool sourceLoaded = true;
                        if (sourceTexture == null) throw new InvalidOperationException("Source TextureAsset returned no Texture2D");
                        try
                        {
                            atlas = RenderTexture.GetTemporary(sourceTexture.width, sourceTexture.height, 0,
                                RenderTextureFormat.ARGB32, mapLayer.IsSrgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                            Graphics.Blit(sourceTexture, atlas);
                        }
                        finally
                        {
                            if (sourceLoaded) sourceMap.Unload();
                        }

                        foreach (KeyValuePair<string, string> replacement in rule.roadAreaReplacements)
                        {
                            RoadAtlasRegion region = RoadAtlasRegions.RoadEu.FirstOrDefault(item => item.Id == replacement.Key);
                            if (region == null)
                            {
                                Mod.Log.WarnFormat("Road texture region is not defined: area='{0}'", replacement.Key);
                                continue;
                            }
                            if (!replacementSurfaces.TryGetValue(replacement.Value, out RoadSurfaceInfo replacementInfo))
                            {
                                Mod.Log.WarnFormat("Road texture replacement surface was not found: surfaceId='{0}', area='{1}'", replacement.Value, region.Name);
                                continue;
                            }

                            if (!TryGetRenderedAreaTexture(replacementInfo.RenderedArea, replacementInfo.RuntimeMaterial,
                                sourceProperty, out TextureAsset overlayAsset, out Texture overlayTexture))
                            {
                                Mod.Log.InfoFormat("RoadEU map layer kept unchanged for area='{0}', replacementSurface='{1}', missingMap='{2}'",
                                    region.Name, replacementInfo.Prefab.name, sourceProperty);
                                continue;
                            }

                            GetScaledRegion(region, atlas.width, atlas.height, out int x, out int y, out int width, out int height);
                            float surfaceUvScale = Mathf.Abs(GetOriginalRoadSurfaceUvScale(replacementInfo));
                            if (surfaceUvScale < 0.0001f) surfaceUvScale = 1f;
                            ApplyTiledRegion(atlas, overlayAsset, overlayTexture, region.ScaleMode, surfaceUvScale,
                                x, y, width, height, mapLayer.IsSrgb);
                            changedAreas.Add(region.Name + " <- " + replacementInfo.Prefab.name);
                            Mod.Log.InfoFormat("RoadEU region texture applied on GPU: area='{0}' ({1},{2},{3},{4}), surface='{5}', map='{6}', textureSource='{7}'",
                                region.Name, x, y, width, height, replacementInfo.Prefab.name, sourceProperty,
                                overlayAsset != null ? "RenderedArea asset reference" : "RenderedArea material");
                        }

                        if (changedAreas.Count == 0) continue;
                        var output = new RoadTexturePngOutput
                        {
                            RuleId = rule.id,
                            Generation = generation.Generation,
                            Width = atlas.width,
                            Height = atlas.height,
                            Path = path,
                            MapProperty = sourceProperty,
                            Replacements = string.Join("; ", changedAreas),
                        };
                        outputs.Add(output);
                        if (SystemInfo.supportsAsyncGPUReadback)
                        {
                            lock (generation) generation.PendingReadbacks++;
                            RenderTexture readbackAtlas = atlas;
                            atlas = null;
                            try
                            {
                                AsyncGPUReadback.Request(readbackAtlas, 0, TextureFormat.RGBA32, request =>
                                {
                                    try
                                    {
                                        if (request.hasError)
                                            throw new InvalidOperationException("AsyncGPUReadback failed for " + output.MapProperty);
                                        if (!generation.Token.IsCancellationRequested)
                                            output.Pixels = request.GetData<Color32>().ToArray();
                                    }
                                    catch (Exception exception)
                                    {
                                        lock (generation)
                                            if (generation.CaptureError == null) generation.CaptureError = exception;
                                    }
                                    finally
                                    {
                                        RenderTexture.ReleaseTemporary(readbackAtlas);
                                        lock (generation) generation.PendingReadbacks--;
                                        CompleteRoadTexturePreparation(generation);
                                    }
                                });
                            }
                            catch (Exception exception)
                            {
                                atlas = readbackAtlas;
                                lock (generation) generation.PendingReadbacks--;
                                if (generation.CaptureError == null) generation.CaptureError = exception;
                                throw;
                            }
                        }
                        else
                        {
                            output.Pixels = CaptureRenderTexturePixels(atlas, mapLayer.IsSrgb);
                        }
                    }
                    finally
                    {
                        if (atlas != null) RenderTexture.ReleaseTemporary(atlas);
                    }
                }
                catch (Exception exception)
                {
                    if (generation.CaptureError == null) generation.CaptureError = exception;
                    Mod.Log.Error(exception, "Could not generate the full RoadEU texture map " + sourceProperty + " for rule " + rule.id);
                }
            }
            return outputs;
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

        private float GetOriginalRoadSurfaceUvScale(RoadSurfaceInfo surface)
        {
            string surfaceId = surface.Prefab.GetPrefabID().ToString();
            if (m_SurfaceScaleBaseValues.TryGetValue(surfaceId, out float originalScale))
                return originalScale;
            return surface.RenderedArea.m_UVScale;
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

        private static void GetScaledRegion(RoadAtlasRegion region, int textureWidth, int textureHeight,
            out int x, out int y, out int width, out int height)
        {
            float scaleX = (float)textureWidth / RoadAtlasRegions.AtlasWidth;
            float scaleY = (float)textureHeight / RoadAtlasRegions.AtlasHeight;
            x = Mathf.RoundToInt(region.X * scaleX);
            y = Mathf.RoundToInt(region.Y * scaleY);
            width = Mathf.RoundToInt(region.Width * scaleX);
            height = region.UsesFullTextureHeight
                ? textureHeight
                : Mathf.RoundToInt(region.Height * scaleY);
            x = Mathf.Clamp(x, 0, textureWidth - 1);
            y = Mathf.Clamp(y, 0, textureHeight - 1);
            width = Mathf.Clamp(width, 1, textureWidth - x);
            height = Mathf.Clamp(height, 1, textureHeight - y);
        }

        private static void ApplyTiledRegion(RenderTexture atlas, TextureAsset sourceAsset, Texture sourceTexture,
            RoadAtlasScaleMode scaleMode, float surfaceUvScale, int x, int topY, int width, int height, bool isSrgb)
        {
            RenderTexture target = null;
            Texture texture = sourceTexture;
            bool loaded = false;
            bool ownsTexture = false;
            TextureWrapMode oldWrapU = TextureWrapMode.Repeat;
            TextureWrapMode oldWrapV = TextureWrapMode.Repeat;
            try
            {
                if (texture == null && sourceAsset != null)
                {
                    texture = sourceAsset.Load<Texture2D>(0, TextureAsset.KeepOnCPU.Object);
                    loaded = true;
                    ownsTexture = true;
                }
                if (texture == null) throw new InvalidOperationException("Replacement surface returned no texture");
                oldWrapU = texture.wrapModeU;
                oldWrapV = texture.wrapModeV;
                texture.wrapModeU = TextureWrapMode.Repeat;
                texture.wrapModeV = TextureWrapMode.Repeat;
                target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                    isSrgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                Vector2 uvScale;
                float verticalRepeats;
                if (scaleMode == RoadAtlasScaleMode.FitRegionWidth)
                {
                    uvScale.x = 1f;
                    verticalRepeats = (float)height / width * texture.width / texture.height;
                }
                else
                {
                    uvScale.x = (float)width / texture.width * surfaceUvScale;
                    verticalRepeats = (float)height / texture.height * surfaceUvScale;
                }
                // Keep the texture seamless at the top and bottom edges by using a whole number of vertical tiles.
                uvScale.y = Mathf.Max(1, Mathf.RoundToInt(verticalRepeats));
                Graphics.Blit(texture, target, uvScale, Vector2.zero);
                int destinationY = atlas.height - topY - height;
                Graphics.CopyTexture(target, 0, 0, 0, 0, width, height, atlas, 0, 0, x, destinationY);
            }
            finally
            {
                if (texture != null)
                {
                    texture.wrapModeU = oldWrapU;
                    texture.wrapModeV = oldWrapV;
                }
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (loaded && ownsTexture) sourceAsset.Unload();
            }
        }

        private static Color32[] CaptureRenderTexturePixels(RenderTexture source, bool isSrgb)
        {
            Texture2D readback = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                readback = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, !isSrgb);
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                if (readback != null) UnityEngine.Object.Destroy(readback);
            }
        }

        private void WriteRoadTexturePngs(List<RoadTexturePngOutput> outputs, RoadTextureGenerationState generation)
        {
            CancellationToken cancellationToken = generation.Token;
            foreach (RoadTexturePngOutput output in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string temporaryPath = output.Path + "." + output.Generation + ".tmp";
                try
                {
                    byte[] png = EncodePng(output.Pixels, output.Width, output.Height, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.WriteAllBytes(temporaryPath, png);
                    cancellationToken.ThrowIfCancellationRequested();

                    lock (m_RoadTextureGenerationLock)
                    {
                        if (cancellationToken.IsCancellationRequested ||
                            !m_RoadTextureGenerationStates.TryGetValue(output.RuleId, out RoadTextureGenerationState current) ||
                            !ReferenceEquals(current, generation) || current.Generation != output.Generation)
                            throw new OperationCanceledException(cancellationToken);

                        if (File.Exists(output.Path)) File.Delete(output.Path);
                        File.Move(temporaryPath, output.Path);
                    }
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
        }

        private static byte[] EncodePng(Color32[] pixels, int width, int height, CancellationToken cancellationToken)
        {
            int rowBytes = checked(width * 4 + 1);
            byte[] scanlines = new byte[checked(rowBytes * height)];
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                int sourceRow = height - 1 - y;
                int sourceIndex = sourceRow * width;
                int destinationIndex = y * rowBytes;
                scanlines[destinationIndex++] = 0;
                for (int x = 0; x < width; x++)
                {
                    Color32 pixel = pixels[sourceIndex + x];
                    scanlines[destinationIndex++] = pixel.r;
                    scanlines[destinationIndex++] = pixel.g;
                    scanlines[destinationIndex++] = pixel.b;
                    scanlines[destinationIndex++] = pixel.a;
                }
            }

            byte[] deflateBytes;
            using (var compressed = new MemoryStream())
            {
                using (var deflate = new DeflateStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true))
                {
                    for (int offset = 0; offset < scanlines.Length; offset += 65536)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int count = Math.Min(65536, scanlines.Length - offset);
                        deflate.Write(scanlines, offset, count);
                    }
                }
                deflateBytes = compressed.ToArray();
            }

            var zlib = new byte[2 + deflateBytes.Length + 4];
            zlib[0] = 0x78;
            zlib[1] = 0x9C;
            Buffer.BlockCopy(deflateBytes, 0, zlib, 2, deflateBytes.Length);
            WriteUInt32BigEndian(zlib, zlib.Length - 4, ComputeAdler32(scanlines));

            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var header = new byte[13];
                WriteUInt32BigEndian(header, 0, (uint)width);
                WriteUInt32BigEndian(header, 4, (uint)height);
                header[8] = 8;
                header[9] = 6;
                WritePngChunk(png, "IHDR", header);
                WritePngChunk(png, "IDAT", zlib);
                WritePngChunk(png, "IEND", Array.Empty<byte>());
                return png.ToArray();
            }
        }

        private static void WritePngChunk(Stream stream, string type, byte[] data)
        {
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            WriteUInt32BigEndian(stream, (uint)data.Length);
            stream.Write(typeBytes, 0, typeBytes.Length);
            stream.Write(data, 0, data.Length);
            uint crc = 0xFFFFFFFF;
            foreach (byte value in typeBytes) crc = UpdateCrc32(crc, value);
            foreach (byte value in data) crc = UpdateCrc32(crc, value);
            WriteUInt32BigEndian(stream, crc ^ 0xFFFFFFFF);
        }

        private static uint UpdateCrc32(uint crc, byte value)
        {
            return (crc >> 8) ^ s_PngCrcTable[(int)((crc ^ value) & 0xFF)];
        }

        private static uint[] BuildPngCrcTable()
        {
            var table = new uint[256];
            for (int i = 0; i < table.Length; i++)
            {
                uint value = (uint)i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
                table[i] = value;
            }
            return table;
        }

        private static uint ComputeAdler32(byte[] data)
        {
            const uint modulus = 65521;
            uint a = 1;
            uint b = 0;
            int offset = 0;
            while (offset < data.Length)
            {
                int end = Math.Min(offset + 5552, data.Length);
                for (; offset < end; offset++)
                {
                    a += data[offset];
                    b += a;
                }
                a %= modulus;
                b %= modulus;
            }
            return (b << 16) | a;
        }

        private static void WriteUInt32BigEndian(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
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

        private static void DeleteObsoleteStandaloneRegionTextures(string directory)
        {
            string[] mapNames = { "BaseColorMap", "NormalMap", "MaskMap" };
            foreach (RoadAtlasRegion region in RoadAtlasRegions.RoadEu)
                foreach (string mapName in mapNames)
                    foreach (string file in Directory.GetFiles(directory, "RoadEU_" + region.Id + "_" + mapName + "_*.png", SearchOption.TopDirectoryOnly))
                    {
                        try { File.Delete(file); }
                        catch (Exception exception) { Mod.Log.Warn(exception, "Could not remove an obsolete standalone road region texture: " + file); }
                    }
        }
    }
}
