using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace AutomaticTextureReplacer
{
    /// <summary>Inventories texture assets referenced by Small Road prefabs without inspecting mesh geometry.</summary>
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
