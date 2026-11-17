using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private void GenerateChangedRoadAreaTextures(string oldJson, string newJson)
        {
            List<RoadTextureRule> oldRules = ParseRoadTextureRules(oldJson);
            List<RoadTextureRule> newRules = ParseRoadTextureRules(newJson);
            var newRuleIds = new HashSet<int>(newRules.Select(item => item.id));
            foreach (RoadTextureRule oldRule in oldRules)
                if (!newRuleIds.Contains(oldRule.id)) DeleteGeneratedRoadTextures(oldRule.id);
            foreach (RoadTextureRule rule in newRules)
            {
                RoadTextureRule oldRule = oldRules.FirstOrDefault(item => item.id == rule.id);
                if (RoadAreaMappingsEqual(oldRule?.roadAreaReplacements, rule.roadAreaReplacements))
                    continue;

                if (rule.roadAreaReplacements == null || rule.roadAreaReplacements.Count == 0)
                {
                    DeleteGeneratedRoadTextures(rule.id);
                    continue;
                }
                GenerateRoadRuleTextures(rule);
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

        private void GenerateRoadRuleTextures(RoadTextureRule rule)
        {
            if (m_RoadDefiningTextures.Count == 0)
            {
                Mod.Log.Warn("Road texture generation skipped because the RoadEU source maps are not available yet");
                return;
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
                return;
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
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception exception)
                {
                    Mod.Log.Warn(exception, "Could not remove a stale generated road map before rebuilding: " + path);
                }

                string[] replacements = rule.roadAreaReplacements
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                    .Select(entry => entry.Key + "=" + entry.Value)
                    .ToArray();
                if (replacements.Length == 0) continue;

                try
                {
                    Color32[] atlasPixels = ReadTexturePixels(sourceMap, mapLayer.IsSrgb, out int atlasWidth, out int atlasHeight);
                    var changedAreas = new List<string>();
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

                        GetScaledRegion(region, atlasWidth, atlasHeight, out int x, out int y, out int width, out int height);
                        Color32[] overlayPixels = CreateTiledPixels(overlayAsset, overlayTexture, width, height, mapLayer.IsSrgb);
                        CopyRegion(overlayPixels, width, height, atlasPixels, atlasWidth, atlasHeight, x, y);
                        changedAreas.Add(region.Name + " <- " + replacementInfo.Prefab.name);
                        Mod.Log.InfoFormat("RoadEU region texture applied in memory: area='{0}' ({1},{2},{3},{4}), surface='{5}', map='{6}', textureSource='{7}'",
                            region.Name, x, y, width, height, replacementInfo.Prefab.name, sourceProperty,
                            overlayAsset != null ? "RenderedArea asset reference" : "RenderedArea material");
                    }

                    if (changedAreas.Count == 0) continue;
                    WriteTexturePixels(atlasPixels, atlasWidth, atlasHeight, path, mapLayer.IsSrgb);
                    Mod.Log.InfoFormat("Road texture generated: source='RoadEU', map='{0}', resolution={1}x{2}, replacements=[{3}], file='{4}'",
                        sourceProperty, atlasWidth, atlasHeight, string.Join("; ", changedAreas), path);
                }
                catch (Exception exception)
                {
                    Mod.Log.Error(exception, "Could not generate the full RoadEU texture map " + sourceProperty + " for rule " + rule.id);
                }
            }
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

        private static Color32[] ReadTexturePixels(TextureAsset source, bool isSrgb, out int width, out int height)
        {
            Texture2D texture = null;
            RenderTexture target = null;
            Texture2D readback = null;
            RenderTexture previous = RenderTexture.active;
            bool loaded = false;
            try
            {
                texture = source.Load<Texture2D>(0, TextureAsset.KeepOnCPU.Object);
                loaded = true;
                if (texture == null) throw new InvalidOperationException("Source TextureAsset returned no Texture2D");
                width = texture.width;
                height = texture.height;
                target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                    isSrgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                readback = new Texture2D(width, height, TextureFormat.RGBA32, false, !isSrgb);
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                if (readback != null) UnityEngine.Object.Destroy(readback);
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (loaded) source.Unload();
            }
        }

        private static Color32[] CreateTiledPixels(TextureAsset sourceAsset, Texture sourceTexture, int width, int height, bool isSrgb)
        {
            Texture texture = sourceTexture;
            RenderTexture target = null;
            Texture2D readback = null;
            RenderTexture previous = RenderTexture.active;
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
                Graphics.Blit(texture, target, new Vector2((float)width / texture.width, (float)height / texture.height), Vector2.zero);
                RenderTexture.active = target;
                readback = new Texture2D(width, height, TextureFormat.RGBA32, false, !isSrgb);
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                if (texture != null)
                {
                    texture.wrapModeU = oldWrapU;
                    texture.wrapModeV = oldWrapV;
                }
                if (readback != null) UnityEngine.Object.Destroy(readback);
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (loaded && ownsTexture) sourceAsset.Unload();
            }
        }

        private static void CopyRegion(Color32[] source, int sourceWidth, int sourceHeight,
            Color32[] destination, int destinationWidth, int destinationHeight, int x, int topY)
        {
            int width = Math.Min(sourceWidth, destinationWidth - x);
            int height = Math.Min(sourceHeight, destinationHeight - topY);
            int destinationY = destinationHeight - topY - height;
            for (int row = 0; row < height; row++)
            {
                int sourceIndex = row * sourceWidth;
                int destinationIndex = (destinationY + row) * destinationWidth + x;
                Array.Copy(source, sourceIndex, destination, destinationIndex, width);
            }
        }

        private static void WriteTexturePixels(Color32[] pixels, int width, int height, string path, bool isSrgb)
        {
            Texture2D output = null;
            try
            {
                output = new Texture2D(width, height, TextureFormat.RGBA32, false, !isSrgb);
                output.SetPixels32(pixels);
                output.Apply(false, false);
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(output));
            }
            finally
            {
                if (output != null) UnityEngine.Object.Destroy(output);
            }
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
