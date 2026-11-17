using System.Collections.Generic;

namespace AutomaticTextureReplacer
{
    internal sealed class RoadAtlasRegion
    {
        public string Id { get; }
        public string Name { get; }
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public bool UsesFullTextureHeight { get; }

        public RoadAtlasRegion(string id, string name, int x, int y, int width, int height = 0, bool usesFullTextureHeight = false)
        {
            Id = id;
            Name = name;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            UsesFullTextureHeight = usesFullTextureHeight;
        }

        public int GetHeight(int textureHeight) => UsesFullTextureHeight ? textureHeight : Height;
    }

    internal sealed class RoadAtlasTextureLayer
    {
        public string MaterialProperty { get; }
        public string TextureAssetName { get; }
        public bool IsSrgb { get; }

        public RoadAtlasTextureLayer(string materialProperty, string textureAssetName, bool isSrgb)
        {
            MaterialProperty = materialProperty;
            TextureAssetName = textureAssetName;
            IsSrgb = isSrgb;
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
            new RoadAtlasTextureLayer("_NormalMap", "RoadEU_Normal_0f7d11c19a1d7dc4aba9ef85b90f7f8c", false),
            new RoadAtlasTextureLayer("_MaskMap", "RoadEU_MaskMap_2c3140860fed2b34da0146470995c6c6", false),
        };

        public static readonly IReadOnlyList<RoadAtlasRegion> RoadEu = new List<RoadAtlasRegion>
        {
            new RoadAtlasRegion("roadway", "Roadway", 0, 0, 546, usesFullTextureHeight: true),
            new RoadAtlasRegion("inner-curb", "Inner curb", 575, 0, 33, usesFullTextureHeight: true),
            new RoadAtlasRegion("sidewalk", "Sidewalk", 635, 0, 884, usesFullTextureHeight: true),
            new RoadAtlasRegion("outer-curb", "Outer curb", 1551, 0, 27, usesFullTextureHeight: true),
            new RoadAtlasRegion("median", "Median", 1664, 63, 83, 448),
        };
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
