using System.Collections.Generic;
using Colossal.IO.AssetDatabase;
using UnityEngine;

namespace AutomaticTextureReplacer
{
    internal enum RoadAtlasScaleMode
    {
        FitRegionWidth,
        SurfacePrefab,
    }

    internal sealed class RoadAtlasRegion
    {
        public string Id { get; }
        public string Name { get; }
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public bool UsesFullTextureHeight { get; }
        public RoadAtlasScaleMode ScaleMode { get; }
        public string ColorTextureProperty { get; }
        public string NormalTextureProperty { get; }
        public string MaskTextureProperty { get; }

        public RoadAtlasRegion(string id, string name, int x, int y, int width, int height = 0,
            bool usesFullTextureHeight = false, RoadAtlasScaleMode scaleMode = RoadAtlasScaleMode.SurfacePrefab,
            string colorTextureProperty = "_BaseColorMap", string normalTextureProperty = "_NormalMap",
            string maskTextureProperty = "_MaskMap")
        {
            Id = id;
            Name = name;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            UsesFullTextureHeight = usesFullTextureHeight;
            ScaleMode = scaleMode;
            ColorTextureProperty = colorTextureProperty;
            NormalTextureProperty = normalTextureProperty;
            MaskTextureProperty = maskTextureProperty;
        }

        public int GetHeight(int textureHeight) => UsesFullTextureHeight ? textureHeight : Height;

        public string GetDestinationProperty(string sourceProperty)
        {
            if (sourceProperty == "_BaseColorMap") return ColorTextureProperty;
            if (sourceProperty == "_NormalMap") return NormalTextureProperty;
            if (sourceProperty == "_MaskMap") return MaskTextureProperty;
            return sourceProperty;
        }
    }

    internal sealed class RoadAtlasTextureLayer
    {
        public string MaterialProperty { get; }
        public string TextureAssetName { get; }
        public bool IsSrgb { get; }
        public float NormalHueRotationDegrees { get; }

        public RoadAtlasTextureLayer(string materialProperty, string textureAssetName, bool isSrgb,
            float normalHueRotationDegrees = 0f)
        {
            MaterialProperty = materialProperty;
            TextureAssetName = textureAssetName;
            IsSrgb = isSrgb;
            NormalHueRotationDegrees = normalHueRotationDegrees;
        }
    }

    internal static class RoadAtlasRegions
    {
        public const int AtlasWidth = 2048;
        public const int AtlasHeight = 4096;
        public const string RoadEuCatalogId = "RoadAreaSet:RoadEU";
        public const string DefiningTexturePrefix = "RoadEU_BaseColor_";

        // Small Road inventory: include only TextureAssets whose names begin with RoadEU_.
        public static readonly IReadOnlyList<RoadAtlasTextureLayer> RoadEuTextureLayers = new List<RoadAtlasTextureLayer>
        {
            new RoadAtlasTextureLayer("_BaseColorMap", "RoadEU_BaseColor_fcd6e988046753d4682cb5472360a801", true),
            new RoadAtlasTextureLayer("_NormalMap", "RoadEU_Normal_0f7d11c19a1d7dc4aba9ef85b90f7f8c", false,
                normalHueRotationDegrees: 90f),
            // TEMP disabled while checking road and sidewalk color/normal routing.
            // The generated mask was nearly black and previously produced fragmented shading.
            // new RoadAtlasTextureLayer("_MaskMap", "RoadEU_MaskMap_2c3140860fed2b34da0146470995c6c6", false),
        };

        public static readonly IReadOnlyList<RoadAtlasRegion> RoadEu = new List<RoadAtlasRegion>
        {
            new RoadAtlasRegion("roadway", "Roadway", 0, 0, 546, usesFullTextureHeight: true,
                scaleMode: RoadAtlasScaleMode.FitRegionWidth,
                colorTextureProperty: "_WorldspaceAlbedo", normalTextureProperty: "_WorldspaceNormalMap",
                maskTextureProperty: "_WorldspaceMaskMap"),
            new RoadAtlasRegion("inner-curb", "Inner curb", 546, 0, 89, usesFullTextureHeight: true),
            new RoadAtlasRegion("sidewalk", "Sidewalk", 638, 0, 882, usesFullTextureHeight: true,
                scaleMode: RoadAtlasScaleMode.FitRegionWidth),
            new RoadAtlasRegion("outer-curb", "Outer curb", 1520, 0, 96, usesFullTextureHeight: true),
            new RoadAtlasRegion("median", "Median", 1616, 4, 207, 932),
        };
    }

    internal static class RoadTextureSourceRegistry
    {
        private static readonly object s_Lock = new object();
        private static readonly Dictionary<string, (TextureAsset Asset, Texture Texture)> s_Textures =
            new Dictionary<string, (TextureAsset, Texture)>(System.StringComparer.Ordinal);

        public static void ClearRule(int ruleId)
        {
            string prefix = ruleId + "|";
            lock (s_Lock)
            {
                var stale = new List<string>();
                foreach (string key in s_Textures.Keys)
                    if (key.StartsWith(prefix, System.StringComparison.Ordinal)) stale.Add(key);
                foreach (string key in stale) s_Textures.Remove(key);
            }
        }

        public static void Register(int ruleId, string regionId, string property, TextureAsset asset, Texture texture)
        {
            lock (s_Lock) s_Textures[ruleId + "|" + regionId + "|" + property] = (asset, texture);
        }

        public static bool TryGet(int ruleId, string regionId, string property, out TextureAsset asset, out Texture texture)
        {
            lock (s_Lock)
            {
                if (s_Textures.TryGetValue(ruleId + "|" + regionId + "|" + property, out var source))
                {
                    asset = source.Asset;
                    texture = source.Texture;
                    return true;
                }
            }
            asset = null;
            texture = null;
            return false;
        }
    }

    internal static class RoadNormalHueTransform
    {
        // Photoshop's Hue adjustment is an HSL hue rotation; it is not a tangent-vector rotation.
        public static Color32 ShiftHue(Color32 pixel, float degrees)
        {
            float r = pixel.r / 255f;
            float g = pixel.g / 255f;
            float b = pixel.b / 255f;
            float max = Mathf.Max(r, Mathf.Max(g, b));
            float min = Mathf.Min(r, Mathf.Min(g, b));
            float delta = max - min;
            if (delta <= 0.000001f) return pixel;

            float hue;
            if (max == r) hue = 60f * (((g - b) / delta) % 6f);
            else if (max == g) hue = 60f * ((b - r) / delta + 2f);
            else hue = 60f * ((r - g) / delta + 4f);
            if (hue < 0f) hue += 360f;

            float lightness = (max + min) * 0.5f;
            float saturation = delta / (1f - Mathf.Abs(2f * lightness - 1f));
            hue = (hue + degrees) % 360f;
            if (hue < 0f) hue += 360f;

            float chroma = (1f - Mathf.Abs(2f * lightness - 1f)) * saturation;
            float section = hue / 60f;
            float secondary = chroma * (1f - Mathf.Abs(section % 2f - 1f));
            float r1, g1, b1;
            if (section < 1f) { r1 = chroma; g1 = secondary; b1 = 0f; }
            else if (section < 2f) { r1 = secondary; g1 = chroma; b1 = 0f; }
            else if (section < 3f) { r1 = 0f; g1 = chroma; b1 = secondary; }
            else if (section < 4f) { r1 = 0f; g1 = secondary; b1 = chroma; }
            else if (section < 5f) { r1 = secondary; g1 = 0f; b1 = chroma; }
            else { r1 = chroma; g1 = 0f; b1 = secondary; }
            float match = lightness - chroma * 0.5f;
            return new Color32(ToByte(r1 + match), ToByte(g1 + match), ToByte(b1 + match), pixel.a);
        }

        private static byte ToByte(float value) => (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
    }

    internal sealed class RoadTextureRule
    {
        public int id { get; set; }
        public bool enabled { get; set; }
        public List<string> sourceRoadIds { get; set; } = new List<string>();
        public List<string> targetSurfaceIds { get; set; } = new List<string>();
        public Dictionary<string, string> roadAreaReplacements { get; set; } = new Dictionary<string, string>();
    }
}
