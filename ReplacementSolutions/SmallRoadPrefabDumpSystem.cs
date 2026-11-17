using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Prefabs;
using Game.SceneFlow;
using Game.UI;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace AutomaticTextureReplacer
{
    /// <summary>Classifies Four-Lane Divided Road pieces by their UV bounds in the road base-color atlas after map load.</summary>
    public sealed partial class SmallRoadPrefabDumpSystem : GameSystemBase
    {
        private const float RoadAtlasWidth = 2048f;
        private const float RoadAtlasHeight = 4096f;
        private EntityQuery m_BuildMenuQuery;
        private PrefabSystem m_PrefabSystem;
        private bool m_Completed;
        private int m_MenuLookupFrameCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_BuildMenuQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<UIAssetMenuData>(),
                ComponentType.ReadOnly<UIGroupElement>());
        }

        protected override void OnUpdate()
        {
            if (m_Completed || m_BuildMenuQuery.IsEmptyIgnoreFilter)
                return;

            GameManager gameManager = GameManager.instance;
            if (gameManager == null || gameManager.isGameLoading)
                return;

            bool mapReady = gameManager != null &&
                gameManager.shouldUpdateWorld &&
                (string.Equals(gameManager.gameMode.ToString(), "Game", StringComparison.Ordinal) ||
                 string.Equals(gameManager.gameMode.ToString(), "Editor", StringComparison.Ordinal));
            if (mapReady)
            {
                InspectRoadBuildMenu(logWhenNotFound: true);
                m_Completed = true;
                return;
            }

            // Main-menu prefab registration can finish after the menu entity first
            // appears, so retry periodically until the requested road is available.
            if (++m_MenuLookupFrameCounter >= 30)
            {
                m_MenuLookupFrameCounter = 0;
                m_Completed = InspectRoadBuildMenu(logWhenNotFound: false);
            }
        }

        private bool InspectRoadBuildMenu(bool logWhenNotFound)
        {
            EntityManager entityManager = World.EntityManager;
            using (NativeArray<Entity> menuEntities = m_BuildMenuQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity menuEntity in menuEntities)
                {
                    if (!m_PrefabSystem.TryGetPrefab<UIAssetMenuPrefab>(menuEntity, out UIAssetMenuPrefab menu) || menu == null)
                        continue;

                    DynamicBuffer<UIGroupElement> categories = entityManager.GetBuffer<UIGroupElement>(menuEntity, true);
                    foreach (UIGroupElement categoryEntry in categories)
                    {
                        if (!m_PrefabSystem.TryGetPrefab<UIAssetCategoryPrefab>(categoryEntry.m_Prefab, out UIAssetCategoryPrefab category) || category == null)
                            continue;

                        DynamicBuffer<UIGroupElement> entries = entityManager.GetBuffer<UIGroupElement>(categoryEntry.m_Prefab, true);
                        foreach (UIGroupElement entry in entries)
                        {
                            if (!m_PrefabSystem.TryGetPrefab<PrefabBase>(entry.m_Prefab, out PrefabBase prefab) || prefab == null)
                                continue;

                            string displayName = GetMenuDisplayName(prefab);
                            bool isTarget = NormalizePrefabName(displayName) == NormalizePrefabName("Four-Lane Divided Road");
                            if (!isTarget) continue;

                            Mod.Log.InfoFormat("Matched build-menu item: menu='{0}', category='{1}', displayName='{2}', asset='{3}', type='{4}', id='{5}'",
                                menu.name, category.name, displayName, prefab.name, prefab.GetType().Name, prefab.GetPrefabID());
                            if (prefab is NetGeometryPrefab geometry)
                            {
                                Mod.Log.Info("Build-menu item resolves directly to NetGeometryPrefab; classifying its piece tree.");
                                Dump(geometry, entry.m_Prefab, m_PrefabSystem);
                                return true;
                            }
                            else
                            {
                                Mod.Log.WarnFormat("Build-menu item matched by display name but is not a NetGeometryPrefab; type='{0}', id='{1}'",
                                    prefab.GetType().FullName, prefab.GetPrefabID());
                                return true;
                            }
                        }
                    }
                }
            }

            if (logWhenNotFound)
                Mod.Log.Warn("Road build-menu item 'Four-Lane Divided Road' was not found by its displayed name.");
            return false;
        }

        private static string GetMenuDisplayName(PrefabBase prefab)
        {
            string key = "Assets.NAME[" + prefab.name + "]";
            GameManager gameManager = GameManager.instance;
            if (gameManager?.localizationManager?.activeDictionary != null &&
                gameManager.localizationManager.activeDictionary.TryGetValue(key, out string localizedName) &&
                !string.IsNullOrWhiteSpace(localizedName) && !string.Equals(localizedName, key, StringComparison.Ordinal))
                return localizedName.Replace("Placeholder", string.Empty).Trim();
            return prefab.name;
        }

        private static string NormalizePrefabName(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var normalized = new System.Text.StringBuilder(value.Length);
            foreach (char character in value)
                if (char.IsLetterOrDigit(character)) normalized.Append(char.ToLowerInvariant(character));
            return normalized.ToString();
        }

        private static void Dump(NetGeometryPrefab geometry, Entity geometryEntity, PrefabSystem prefabSystem)
        {
            var groups = new List<PieceGroup>
            {
                new PieceGroup("Roadway", new UvBounds(0f, 0f, 546f / RoadAtlasWidth, 1f)),
                new PieceGroup("Inner Curb", new UvBounds(546f / RoadAtlasWidth, 0f, 635f / RoadAtlasWidth, 1f)),
                new PieceGroup("Sidewalk", new UvBounds(638f / RoadAtlasWidth, 0f, 1520f / RoadAtlasWidth, 1f)),
                new PieceGroup("Outer Curb", new UvBounds(1520f / RoadAtlasWidth, 0f, 1616f / RoadAtlasWidth, 1f)),
                new PieceGroup("Median", new UvBounds(1616f / RoadAtlasWidth, 1f - 936f / RoadAtlasHeight, 1823f / RoadAtlasWidth, 1f - 4f / RoadAtlasHeight))
            };
            var missed = new List<string>();
            var unclassified = new List<string>();
            var textureWrapModes = new Dictionary<string, TextureWrapModes>(StringComparer.OrdinalIgnoreCase);
            var visitedSections = new HashSet<NetSectionPrefab>();
            var visitedPieces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (geometry.m_Sections != null)
                foreach (NetSectionInfo info in geometry.m_Sections)
                    if (info?.m_Section != null)
                        CollectPieces(info.m_Section, groups, missed, unclassified, textureWrapModes, visitedSections, visitedPieces, prefabSystem, 0);

            var lines = new List<string> { "Four-Lane Divided Road piece UV classification", $"Defining texture: RoadEU_BaseColor_*; atlas: {RoadAtlasWidth:0}x{RoadAtlasHeight:0} px" };
            foreach (PieceGroup group in groups)
            {
                lines.Add($"{group.Name} {FormatTexturePixels(group.Bounds)} ({group.Pieces.Count})");
                AddPieceLines(lines, group.Pieces);
            }
            lines.Add($"Missed piece (defining texture found, UV bounds outside all zones) ({missed.Count})");
            AddPieceLines(lines, missed);
            lines.Add($"Unclassified (defining texture not found) ({unclassified.Count})");
            AddPieceLines(lines, unclassified);
            Mod.Log.Info("\n" + string.Join("\n", lines));
        }

        private static void AddPieceLines(List<string> lines, List<string> pieces)
        {
            if (pieces.Count == 0)
            {
                lines.Add("  └─ <none>");
                return;
            }
            for (int i = 0; i < pieces.Count; i++)
                lines.Add("  " + (i == pieces.Count - 1 ? "└─ " : "├─ ") + pieces[i]);
        }

        private static void CollectPieces(NetSectionPrefab section, List<PieceGroup> groups, List<string> missed,
            List<string> unclassified, Dictionary<string, TextureWrapModes> textureWrapModes,
            HashSet<NetSectionPrefab> visitedSections, HashSet<string> visitedPieces,
            PrefabSystem prefabSystem, int depth)
        {
            if (depth > 16 || !visitedSections.Add(section)) return;
            if (section.m_SubSections != null)
                foreach (NetSubSectionInfo sub in section.m_SubSections)
                    if (sub?.m_Section != null)
                        CollectPieces(sub.m_Section, groups, missed, unclassified, textureWrapModes, visitedSections, visitedPieces, prefabSystem, depth + 1);

            if (section.m_Pieces != null)
                foreach (NetPieceInfo pieceInfo in section.m_Pieces)
                {
                    if (pieceInfo?.m_Piece == null) continue;
                    NetPiecePrefab piece = pieceInfo.m_Piece;
                    string prefabId = piece.GetPrefabID().ToString();
                    if (!visitedPieces.Add(prefabId)) continue;
                    string label = piece.name + (string.Equals(prefabId, piece.name, StringComparison.OrdinalIgnoreCase) ? "" : " [" + prefabId + "]");
                    bool hasDefiningTexture = false;
                    bool placed = false;
                    string observedUv = null;
                    if (piece.surfaceAssets != null)
                    {
                        int slot = 0;
                        foreach (SurfaceAsset surface in piece.surfaceAssets)
                        {
                            int currentSlot = slot++;
                            if (surface == null) continue;
                            bool loaded = false;
                            try
                            {
                                surface.LoadProperties(false);
                                loaded = true;
                                if (surface.textures == null) continue;
                                TextureWrapModes wrapModes = default;
                                bool thisSlotUsesDefiningTexture = false;
                                foreach (KeyValuePair<string, TextureAsset> texture in surface.textures)
                                    if (texture.Value != null && IsDefiningRoadTexture(texture.Value.name))
                                    {
                                        hasDefiningTexture = true;
                                        thisSlotUsesDefiningTexture = true;
                                        string textureId = texture.Value.id.ToString();
                                        if (!textureWrapModes.TryGetValue(textureId, out wrapModes))
                                        {
                                            wrapModes = ReadTextureWrapModes(texture.Value, label);
                                            textureWrapModes[textureId] = wrapModes;
                                        }
                                    }
                                if (!thisSlotUsesDefiningTexture) continue;

                                UvBounds bounds;
                                if (!TryGetUvBounds(piece, currentSlot, wrapModes, out bounds)) continue;
                                observedUv = FormatTexturePixels(bounds);
                                foreach (PieceGroup group in groups)
                                    if (group.Bounds.Contains(bounds))
                                    {
                                        group.Pieces.Add(label + "; " + observedUv);
                                        placed = true;
                                        break;
                                    }
                            }
                            catch (Exception exception)
                            {
                                Mod.Log.Warn("Could not inspect Four-Lane Divided Road piece " + label + " texture slot " + currentSlot + ": " + exception.Message);
                            }
                            finally { if (loaded) surface.Unload(); }
                            if (placed) break;
                        }
                    }

                    if (placed) continue;
                    if (hasDefiningTexture) missed.Add(label + (observedUv == null ? "; UV unavailable" : "; " + observedUv));
                    else unclassified.Add(label);
                }
        }

        private static bool IsDefiningRoadTexture(string textureName)
        {
            return textureName != null && textureName.StartsWith("RoadEU_BaseColor_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetUvBounds(NetPiecePrefab piece, int requestedSlot, TextureWrapModes wrapModes,
            out UvBounds wrappedBounds)
        {
            Mesh[] meshes = null;
            float wrappedMinU = float.PositiveInfinity, wrappedMinV = float.PositiveInfinity;
            float wrappedMaxU = float.NegativeInfinity, wrappedMaxV = float.NegativeInfinity;
            int count = 0;
            try
            {
                meshes = piece.ObtainMeshes();
                if (meshes != null)
                    foreach (Mesh mesh in meshes)
                    {
                        if (mesh == null || requestedSlot < 0 || requestedSlot >= mesh.subMeshCount) continue;
                        Vector2[] uv = mesh.uv;
                        if (uv == null || uv.Length == 0) continue;
                        foreach (int vertexIndex in mesh.GetIndices(requestedSlot))
                        {
                            if (vertexIndex < 0 || vertexIndex >= uv.Length) continue;
                            Vector2 p = uv[vertexIndex];
                            float wrappedU = WrapCoordinate(p.x, wrapModes.U);
                            float wrappedV = WrapCoordinate(p.y, wrapModes.V);
                            count++;
                            wrappedMinU = Mathf.Min(wrappedMinU, wrappedU); wrappedMinV = Mathf.Min(wrappedMinV, wrappedV);
                            wrappedMaxU = Mathf.Max(wrappedMaxU, wrappedU); wrappedMaxV = Mathf.Max(wrappedMaxV, wrappedV);
                        }
                    }
                wrappedBounds = count == 0 ? default : new UvBounds(wrappedMinU, wrappedMinV, wrappedMaxU, wrappedMaxV);
                return count > 0;
            }
            catch
            {
                wrappedBounds = default;
                return false;
            }
            finally { if (meshes != null) piece.ReleaseMeshes(); }
        }

        private static string FormatTexturePixels(UvBounds bounds)
        {
            float minX = bounds.MinU * RoadAtlasWidth;
            float maxX = bounds.MaxU * RoadAtlasWidth;
            float minY = (1f - bounds.MaxV) * RoadAtlasHeight;
            float maxY = (1f - bounds.MinV) * RoadAtlasHeight;
            return $"Texture pixels X=[{F(minX)}, {F(maxX)}], Y=[{F(minY)}, {F(maxY)}]";
        }

        private static TextureWrapModes ReadTextureWrapModes(TextureAsset textureAsset, string pieceLabel)
        {
            Texture2D texture = null;
            bool loaded = false;
            try
            {
                texture = textureAsset.Load<Texture2D>(0, TextureAsset.KeepOnCPU.Object);
                loaded = true;
                if (texture == null) throw new InvalidOperationException("TextureAsset returned no Texture2D");
                var modes = new TextureWrapModes(texture.wrapModeU, texture.wrapModeV);
                Mod.Log.InfoFormat("Road atlas address mode: piece='{0}', texture='{1}', wrapU={2}, wrapV={3}",
                    pieceLabel, textureAsset.name, modes.U, modes.V);
                return modes;
            }
            catch (Exception exception)
            {
                Mod.Log.Warn("Could not read road atlas wrap mode for " + textureAsset.name + "; using Clamp: " + exception.Message);
                return new TextureWrapModes(TextureWrapMode.Clamp, TextureWrapMode.Clamp);
            }
            finally
            {
                if (loaded) textureAsset.Unload();
            }
        }

        private static float WrapCoordinate(float coordinate, TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Repeat:
                    return Mathf.Repeat(coordinate, 1f);
                case TextureWrapMode.Mirror:
                    float whole = Mathf.Floor(coordinate);
                    float fraction = coordinate - whole;
                    return Mathf.Abs(whole % 2f) >= 1f ? 1f - fraction : fraction;
                case TextureWrapMode.MirrorOnce:
                    return Mathf.Clamp01(Mathf.Abs(coordinate));
                default:
                    return Mathf.Clamp01(coordinate);
            }
        }

        private struct TextureWrapModes
        {
            public TextureWrapMode U { get; }
            public TextureWrapMode V { get; }
            public TextureWrapModes(TextureWrapMode u, TextureWrapMode v) { U = u; V = v; }
        }

        private sealed class PieceGroup
        {
            public string Name { get; }
            public UvBounds Bounds { get; }
            public List<string> Pieces { get; } = new List<string>();
            public PieceGroup(string name, UvBounds bounds) { Name = name; Bounds = bounds; }
        }

        private struct UvBounds
        {
            public float MinU { get; }
            public float MinV { get; }
            public float MaxU { get; }
            public float MaxV { get; }
            public UvBounds(float minU, float minV, float maxU, float maxV)
            { MinU = minU; MinV = minV; MaxU = maxU; MaxV = maxV; }
            public bool Contains(UvBounds other)
            {
                const float epsilon = 0.00001f;
                return other.MinU >= MinU - epsilon && other.MaxU <= MaxU + epsilon &&
                    other.MinV >= MinV - epsilon && other.MaxV <= MaxV + epsilon;
            }
        }

        private static string Describe(PrefabBase prefab, Entity entity)
        {
            string id = prefab.GetPrefabID().ToString();
            string entityText = entity == Entity.Null ? "" : "; entity=" + entity;
            return prefab.name + " [PrefabID=" + id + entityText + "]";
        }

        private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        private static string F(Unity.Mathematics.float3 value) => "(" + F(value.x) + ", " + F(value.y) + ", " + F(value.z) + ")";

        private static void ExportTextures(Dictionary<string, TextureAsset> textures)
        {
            string root = Environment.GetEnvironmentVariable("CSII_USERDATAPATH");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Colossal Order", "Cities Skylines II");
            string directory = Path.Combine(root, "ModsData", "Replacement Solutions", "textures");
            try
            {
                Directory.CreateDirectory(directory);
                int exported = 0;
                foreach (KeyValuePair<string, TextureAsset> entry in textures)
                {
                    string file = Path.Combine(directory, Sanitize(entry.Key) + ".png");
                    Texture2D texture = null;
                    Texture2D readback = null;
                    RenderTexture renderTarget = null;
                    RenderTexture previous = RenderTexture.active;
                    bool loaded = false;
                    try
                    {
                        texture = entry.Value.Load<Texture2D>(0, TextureAsset.KeepOnCPU.Object);
                        loaded = true;
                        if (texture == null) throw new InvalidOperationException("TextureAsset returned no Texture2D");
                        int width = texture.width;
                        int height = texture.height;
                        renderTarget = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                        Graphics.Blit(texture, renderTarget);
                        RenderTexture.active = renderTarget;
                        readback = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                        readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                        readback.Apply(false, false);
                        File.WriteAllBytes(file, ImageConversion.EncodeToPNG(readback));
                        exported++;
                        Mod.Log.InfoFormat("Exported road texture: {0} ({1}x{2}) -> {3}", entry.Value.name, width, height, file);
                    }
                    catch (Exception exception) { Mod.Log.Error(exception, "Could not export road texture " + entry.Value.name + " [" + entry.Key + "]"); }
                    finally
                    {
                        RenderTexture.active = previous;
                        if (renderTarget != null) RenderTexture.ReleaseTemporary(renderTarget);
                        if (readback != null) UnityEngine.Object.Destroy(readback);
                        if (loaded) entry.Value.Unload();
                        if (texture != null) UnityEngine.Object.Destroy(texture);
                    }
                }
                Mod.Log.InfoFormat("Four-Lane Divided Road texture export complete: uniqueTextures={0}, exported={1}, directory='{2}'", textures.Count, exported, directory);
            }
            catch (Exception exception) { Mod.Log.Error(exception, "Could not create the Four-Lane Divided Road texture export directory: " + directory); }
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "texture";
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value.Replace(' ', '_');
        }
    }
}
