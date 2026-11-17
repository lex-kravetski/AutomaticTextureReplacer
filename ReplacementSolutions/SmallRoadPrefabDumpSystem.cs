using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private EntityQuery m_RoadPrefabQuery;
        private PrefabSystem m_PrefabSystem;
        private bool m_Completed;
        private bool m_LoggedRoadPrefabQueryReady;
        private int m_MenuLookupFrameCounter;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_RoadPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<NetGeometryData>());
            Mod.Log.Info("TEMP road texture inventory scanner initialized; waiting for Small Road prefab registration");
        }

        protected override void OnUpdate()
        {
            if (m_Completed || m_RoadPrefabQuery.IsEmptyIgnoreFilter)
                return;
            if (!m_LoggedRoadPrefabQueryReady)
            {
                m_LoggedRoadPrefabQueryReady = true;
                Mod.Log.InfoFormat("TEMP road geometry prefab query is ready: prefabCount={0}", m_RoadPrefabQuery.CalculateEntityCount());
            }

            if (++m_MenuLookupFrameCounter >= 30)
            {
                m_MenuLookupFrameCounter = 0;
                // TEMP: query road geometry prefabs directly; do not wait for the
                // build-menu ECS or for a map world to start updating.
                m_Completed = InspectRoadPrefabs();
            }
        }

        private bool InspectRoadPrefabs()
        {
            using (NativeArray<Entity> roadEntities = m_RoadPrefabQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity roadEntity in roadEntities)
                {
                    if (!m_PrefabSystem.TryGetPrefab<NetGeometryPrefab>(roadEntity, out NetGeometryPrefab road) || road == null ||
                        !string.Equals(road.name, "Small Road", StringComparison.OrdinalIgnoreCase))
                        continue;
                    return DumpTextureInventory(road);
                }
            }
            return false;
        }

        private static bool DumpTextureInventory(NetGeometryPrefab geometry)
        {
            var inventory = new Dictionary<string, RoadTextureMapInventoryEntry>(StringComparer.Ordinal);
            var visitedSections = new HashSet<NetSectionPrefab>();
            var visitedPieces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (geometry.m_Sections != null)
                foreach (NetSectionInfo info in geometry.m_Sections)
                    if (info?.m_Section != null)
                        CollectTextureInventory(info.m_Section, inventory, visitedSections, visitedPieces, 0);

            var entries = inventory.Values.OrderBy(item => item.Property, StringComparer.Ordinal)
                .ThenBy(item => item.Texture.name, StringComparer.OrdinalIgnoreCase).ToList();
            if (entries.Count == 0) return false;
            var lines = new List<string>
            {
                "TEMP Small Road texture layer inventory; source='" + geometry.name + "' [" + geometry.GetPrefabID() + "]",
                "Unique material-property / TextureAsset pairs: " + entries.Count,
            };
            for (int i = 0; i < entries.Count; i++)
            {
                RoadTextureMapInventoryEntry item = entries[i];
                List<string> pieces = item.Pieces.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
                string dimensions = ReadTextureDimensions(item.Texture);
                lines.Add((i == entries.Count - 1 ? "└─ " : "├─ ") + item.Property + " => " + item.Texture.name +
                    " [" + item.Texture.id + "]; size=" + dimensions + "; pieces=" + pieces.Count);
                for (int pieceIndex = 0; pieceIndex < pieces.Count; pieceIndex++)
                    lines.Add((i == entries.Count - 1 ? "   " : "│  ") +
                        (pieceIndex == pieces.Count - 1 ? "└─ " : "├─ ") + pieces[pieceIndex]);
            }
            Mod.Log.Info("\n" + string.Join("\n", lines));
            return entries.Count > 0;
        }

        private static void CollectTextureInventory(NetSectionPrefab section,
            Dictionary<string, RoadTextureMapInventoryEntry> inventory, HashSet<NetSectionPrefab> visitedSections,
            HashSet<string> visitedPieces, int depth)
        {
            if (section == null || depth > 16 || !visitedSections.Add(section)) return;
            if (section.m_SubSections != null)
                foreach (NetSubSectionInfo subSection in section.m_SubSections)
                    if (subSection?.m_Section != null)
                        CollectTextureInventory(subSection.m_Section, inventory, visitedSections, visitedPieces, depth + 1);

            if (section.m_Pieces == null) return;
            foreach (NetPieceInfo pieceInfo in section.m_Pieces)
            {
                NetPiecePrefab piece = pieceInfo?.m_Piece;
                if (piece == null || !visitedPieces.Add(piece.GetPrefabID().ToString()) || piece.surfaceAssets == null) continue;
                foreach (SurfaceAsset surface in piece.surfaceAssets)
                {
                    if (surface == null) continue;
                    bool loaded = false;
                    try
                    {
                        surface.LoadProperties(false);
                        loaded = true;
                        if (surface.textures == null) continue;
                        foreach (KeyValuePair<string, TextureAsset> texture in surface.textures)
                        {
                            if (texture.Value == null) continue;
                            string key = texture.Key + "|" + texture.Value.id;
                            if (!inventory.TryGetValue(key, out RoadTextureMapInventoryEntry entry))
                            {
                                entry = new RoadTextureMapInventoryEntry(texture.Key, texture.Value);
                                inventory.Add(key, entry);
                            }
                            entry.Pieces.Add(piece.name);
                        }
                    }
                    catch (Exception exception)
                    {
                        Mod.Log.Warn(exception, "TEMP road texture layer scan failed for piece " + piece.name);
                    }
                    finally
                    {
                        if (loaded) surface.Unload();
                    }
                }
            }
        }

        private static string ReadTextureDimensions(TextureAsset asset)
        {
            Texture2D texture = null;
            bool loaded = false;
            try
            {
                texture = asset.Load<Texture2D>(0, TextureAsset.KeepOnCPU.Object);
                loaded = true;
                return texture == null ? "<unavailable>" : texture.width + "x" + texture.height;
            }
            catch (Exception exception)
            {
                return "<unavailable: " + exception.Message + ">";
            }
            finally
            {
                if (loaded) asset.Unload();
            }
        }

        private sealed class RoadTextureMapInventoryEntry
        {
            public string Property { get; }
            public TextureAsset Texture { get; }
            public HashSet<string> Pieces { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public RoadTextureMapInventoryEntry(string property, TextureAsset texture)
            {
                Property = property;
                Texture = texture;
            }
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
            var missed = new List<MissedPieceReport>();
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
            AddMissedPieceLines(lines, missed);
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

        private static void AddMissedPieceLines(List<string> lines, List<MissedPieceReport> pieces)
        {
            if (pieces.Count == 0)
            {
                lines.Add("  └─ <none>");
                return;
            }

            for (int i = 0; i < pieces.Count; i++)
            {
                MissedPieceReport piece = pieces[i];
                lines.Add("  " + (i == pieces.Count - 1 ? "└─ " : "├─ ") + piece.Label +
                    (piece.RoadAtlasUv == null ? "; RoadEU UV unavailable" : "; RoadEU " + piece.RoadAtlasUv));
                lines.Add("      Prefab placement: width=" + F(piece.Width) + " m, length=" + F(piece.Length) + " m, widthOffset=" + F(piece.WidthOffset) +
                    " m, sideConnectionOffset=" + F(piece.SideConnectionOffset) + " m, nodeOffset=" + F(piece.NodeOffset) + " m");
                foreach (string uvChannel in piece.UvChannels)
                    lines.Add("      " + uvChannel);
                if (piece.Slots.Count == 0)
                {
                    lines.Add("      └─ <no SurfaceAsset slots>");
                    continue;
                }

                for (int slotIndex = 0; slotIndex < piece.Slots.Count; slotIndex++)
                {
                    SurfaceSlotReport slot = piece.Slots[slotIndex];
                    bool lastSlot = slotIndex == piece.Slots.Count - 1;
                    lines.Add("      " + (lastSlot ? "└─ " : "├─ ") + slot.Label);
                    string slotIndent = "      " + (lastSlot ? "   " : "│  ");
                    int childCount = slot.Textures.Count + (slot.RoadAtlasUv == null ? 0 : 1);
                    if (childCount == 0)
                    {
                        lines.Add(slotIndent + "└─ <no resolved texture maps>");
                        continue;
                    }

                    int childIndex = 0;
                    foreach (string texture in slot.Textures)
                    {
                        childIndex++;
                        lines.Add(slotIndent + (childIndex == childCount ? "└─ " : "├─ ") + texture);
                    }
                    if (slot.RoadAtlasUv != null)
                        lines.Add(slotIndent + "└─ RoadEU " + slot.RoadAtlasUv);
                }
            }
        }

        private static void CollectPieces(NetSectionPrefab section, List<PieceGroup> groups, List<MissedPieceReport> missed,
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
                    var uvChannels = new List<string>();
                    var slotReports = new List<SurfaceSlotReport>();
                    if (piece.surfaceAssets != null)
                    {
                        int slot = 0;
                        foreach (SurfaceAsset surface in piece.surfaceAssets)
                        {
                            int currentSlot = slot++;
                            var slotReport = new SurfaceSlotReport
                            {
                                Label = "SurfaceAsset slot[" + currentSlot + "]: " +
                                    (surface == null ? "<null>" : surface.name + " [" + surface.id + "]")
                            };
                            slotReports.Add(slotReport);
                            if (surface == null) continue;
                            bool loaded = false;
                            try
                            {
                                Material material = surface.Load(-1, loadTextures: false, mode: TextureAsset.KeepOnCPU.Dont, useVT: true);
                                loaded = true;
                                if (surface.textures == null) continue;
                                var textureTransforms = new Dictionary<string, TextureTransform>(StringComparer.Ordinal);
                                if (material != null)
                                    foreach (string property in surface.textures.Keys)
                                        if (material.HasProperty(property))
                                            textureTransforms[property] = new TextureTransform(material.GetTextureScale(property), material.GetTextureOffset(property));
                                foreach (KeyValuePair<string, UnityEngine.Vector4> vector in surface.vectors)
                                    slotReport.Textures.Add("material vector " + vector.Key + "=" + FormatVector(vector.Value));
                                TextureWrapModes wrapModes = default;
                                bool thisSlotUsesDefiningTexture = false;
                                foreach (KeyValuePair<string, TextureAsset> texture in surface.textures)
                                {
                                    if (texture.Value == null)
                                    {
                                        slotReport.Textures.Add(texture.Key + ": <null>");
                                        continue;
                                    }

                                    textureTransforms.TryGetValue(texture.Key, out TextureTransform transform);
                                    slotReport.Textures.Add(texture.Key + ": " + texture.Value.name + " [" + texture.Value.id + "]; scale=" + FormatVector(transform.Scale) + ", offset=" + FormatVector(transform.Offset));
                                    if (IsDefiningRoadTexture(texture.Value.name))
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
                                }
                                if (!thisSlotUsesDefiningTexture) continue;

                                UvBounds bounds;
                                uvChannels.Clear();
                                TextureTransform roadTransform = default;
                                string roadProperty = null;
                                foreach (KeyValuePair<string, TextureAsset> texture in surface.textures)
                                    if (texture.Value != null && IsDefiningRoadTexture(texture.Value.name))
                                    {
                                        roadProperty = texture.Key;
                                        textureTransforms.TryGetValue(roadProperty, out roadTransform);
                                        break;
                                    }
                                if (!TryGetUvBounds(piece, currentSlot, wrapModes, roadTransform, out bounds, uvChannels)) continue;
                                observedUv = FormatTexturePixels(bounds);
                                slotReport.RoadAtlasUv = observedUv + (roadProperty == null ? "" : "; map=" + roadProperty + ", scale=" + FormatVector(roadTransform.Scale) + ", offset=" + FormatVector(roadTransform.Offset));
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
                                slotReport.Textures.Add("<inspection failed: " + exception.Message + ">");
                                Mod.Log.Warn("Could not inspect Four-Lane Divided Road piece " + label + " texture slot " + currentSlot + ": " + exception.Message);
                            }
                            finally { if (loaded) surface.Unload(); }
                            if (placed) break;
                        }
                    }

                    if (placed) continue;
                    if (hasDefiningTexture)
                        missed.Add(new MissedPieceReport
                        {
                            Label = label,
                            RoadAtlasUv = observedUv,
                            Slots = slotReports,
                            Width = piece.m_Width,
                            Length = piece.m_Length,
                            WidthOffset = piece.m_WidthOffset,
                            SideConnectionOffset = piece.m_SideConnectionOffset,
                            NodeOffset = piece.m_NodeOffset,
                            UvChannels = uvChannels
                        });
                    else unclassified.Add(label);
                }
        }

        private sealed class MissedPieceReport
        {
            public string Label;
            public string RoadAtlasUv;
            public List<SurfaceSlotReport> Slots;
            public float Width;
            public float Length;
            public float WidthOffset;
            public float SideConnectionOffset;
            public float NodeOffset;
            public List<string> UvChannels;
        }

        private sealed class SurfaceSlotReport
        {
            public string Label;
            public List<string> Textures { get; } = new List<string>();
            public string RoadAtlasUv;
        }

        private static bool IsDefiningRoadTexture(string textureName)
        {
            return textureName != null && textureName.StartsWith("RoadEU_BaseColor_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetUvBounds(NetPiecePrefab piece, int requestedSlot, TextureWrapModes wrapModes, TextureTransform transform,
            out UvBounds wrappedBounds, List<string> uvChannelReports)
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
                        int[] indices = mesh.GetIndices(requestedSlot);
                        Vector2[] uv = mesh.uv;
                        if (uv == null || uv.Length == 0) continue;
                        foreach (int vertexIndex in indices)
                        {
                            if (vertexIndex < 0 || vertexIndex >= uv.Length) continue;
                            Vector2 p = uv[vertexIndex];
                            float wrappedU = WrapCoordinate(p.x * transform.Scale.x + transform.Offset.x, wrapModes.U);
                            float wrappedV = WrapCoordinate(p.y * transform.Scale.y + transform.Offset.y, wrapModes.V);
                            count++;
                            wrappedMinU = Mathf.Min(wrappedMinU, wrappedU); wrappedMinV = Mathf.Min(wrappedMinV, wrappedV);
                            wrappedMaxU = Mathf.Max(wrappedMaxU, wrappedU); wrappedMaxV = Mathf.Max(wrappedMaxV, wrappedV);
                        }
                        for (int channel = 0; channel < 8; channel++)
                        {
                            var channelValues = new List<Vector4>();
                            mesh.GetUVs(channel, channelValues);
                            if (channelValues.Count == 0) continue;
                            Vector4 min = new Vector4(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                            Vector4 max = new Vector4(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                            int channelCount = 0;
                            foreach (int vertexIndex in indices)
                            {
                                if (vertexIndex < 0 || vertexIndex >= channelValues.Count) continue;
                                Vector4 value = channelValues[vertexIndex];
                                min = Vector4.Min(min, value);
                                max = Vector4.Max(max, value);
                                channelCount++;
                            }
                            if (channelCount > 0)
                                uvChannelReports.Add("Mesh UV" + channel + " bounds: X=[" + F(min.x) + ", " + F(max.x) + "], Y=[" + F(min.y) + ", " + F(max.y) + "], Z=[" + F(min.z) + ", " + F(max.z) + "], W=[" + F(min.w) + ", " + F(max.w) + "]");
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

        private struct TextureTransform
        {
            private readonly Vector2 m_Scale;
            private readonly Vector2 m_Offset;
            private readonly bool m_IsSet;
            public Vector2 Scale => m_IsSet ? m_Scale : Vector2.one;
            public Vector2 Offset => m_IsSet ? m_Offset : Vector2.zero;
            public TextureTransform(Vector2 scale, Vector2 offset) { m_Scale = scale; m_Offset = offset; m_IsSet = true; }
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
        private static string FormatVector(Vector2 value) => "(" + F(value.x) + ", " + F(value.y) + ")";
        private static string FormatVector(UnityEngine.Vector4 value) => "(" + F(value.x) + ", " + F(value.y) + ", " + F(value.z) + ", " + F(value.w) + ")";
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
