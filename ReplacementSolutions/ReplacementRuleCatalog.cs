using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;

namespace AutomaticTextureReplacer
{
    internal static class ReplacementRuleCatalog
    {
        public const string TransparentSurfacePrefabName = "Replacement Solutions - Transparent Surface";
        public const string TransparentSurfaceTemplateName = "Missing Area";
        public const string TransparentSurfaceDisplayName = "Transparent Surface (Pseudo-delete)";

        public static bool IsTransparentSurface(string name)
        {
            return string.Equals(name, TransparentSurfacePrefabName, StringComparison.OrdinalIgnoreCase);
        }

        public static Dictionary<string, HashSet<string>> BuildSurfaceTargetIds(
            IEnumerable<SurfaceReplacementSystem.SurfaceReplacementRule> rules,
            IReadOnlyDictionary<string, Entity> surfacesById,
            bool enabledOnly,
            int? selectedRuleId = null)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            Dictionary<string, List<string>> surfaceIdsByStableName = CreateStableSurfaceIdLookup(surfacesById.Keys);
            foreach (SurfaceReplacementSystem.SurfaceReplacementRule rule in rules ?? Enumerable.Empty<SurfaceReplacementSystem.SurfaceReplacementRule>())
            {
                if (rule == null || rule.sourceSurfaceIds == null || rule.targetSurfaceIds == null ||
                    (selectedRuleId.HasValue ? rule.id != selectedRuleId.Value : enabledOnly && !rule.enabled))
                {
                    continue;
                }

                foreach (string sourceId in rule.sourceSurfaceIds)
                {
                    string resolvedSourceId = ResolveSurfaceId(sourceId, surfacesById, surfaceIdsByStableName);
                    if (resolvedSourceId == null)
                    {
                        continue;
                    }

                    if (!result.TryGetValue(resolvedSourceId, out HashSet<string> targets))
                    {
                        targets = new HashSet<string>(StringComparer.Ordinal);
                        result.Add(resolvedSourceId, targets);
                    }

                    foreach (string targetId in rule.targetSurfaceIds)
                    {
                        string resolvedTargetId = ResolveSurfaceId(targetId, surfacesById, surfaceIdsByStableName);
                        if (resolvedTargetId != null && resolvedTargetId != resolvedSourceId)
                        {
                            targets.Add(resolvedTargetId);
                        }
                    }
                }
            }

            return result;
        }

        public static Dictionary<string, HashSet<string>> BuildIndustryTargetIds(
            IEnumerable<SurfaceReplacementSystem.AreaReplacementRule> rules,
            IReadOnlyDictionary<string, Entity> industriesById,
            IReadOnlyDictionary<string, Entity> surfacesById,
            bool enabledOnly,
            int? selectedRuleId = null)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            Dictionary<string, List<string>> surfaceIdsByStableName = CreateStableSurfaceIdLookup(surfacesById.Keys);
            foreach (SurfaceReplacementSystem.AreaReplacementRule rule in rules ?? Enumerable.Empty<SurfaceReplacementSystem.AreaReplacementRule>())
            {
                if (rule == null || rule.sourceAreaIds == null || rule.targetSurfaceIds == null ||
                    (selectedRuleId.HasValue ? rule.id != selectedRuleId.Value : enabledOnly && !rule.enabled))
                {
                    continue;
                }

                foreach (string sourceId in rule.sourceAreaIds)
                {
                    if (string.IsNullOrEmpty(sourceId) || !industriesById.ContainsKey(sourceId))
                    {
                        continue;
                    }

                    if (!result.TryGetValue(sourceId, out HashSet<string> targets))
                    {
                        targets = new HashSet<string>(StringComparer.Ordinal);
                        result.Add(sourceId, targets);
                    }

                    foreach (string targetId in rule.targetSurfaceIds)
                    {
                        string resolvedTargetId = ResolveSurfaceId(targetId, surfacesById, surfaceIdsByStableName);
                        if (resolvedTargetId != null)
                        {
                            targets.Add(resolvedTargetId);
                        }
                    }
                }
            }

            return result;
        }

        private static Dictionary<string, List<string>> CreateStableSurfaceIdLookup(IEnumerable<string> surfaceIds)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string surfaceId in surfaceIds ?? Enumerable.Empty<string>())
            {
                string stableId = GetStableSurfaceId(surfaceId);
                if (string.IsNullOrEmpty(stableId))
                {
                    continue;
                }

                if (!result.TryGetValue(stableId, out List<string> matches))
                {
                    matches = new List<string>();
                    result.Add(stableId, matches);
                }
                matches.Add(surfaceId);
            }
            return result;
        }

        private static string ResolveSurfaceId(
            string requestedId,
            IReadOnlyDictionary<string, Entity> surfacesById,
            IReadOnlyDictionary<string, List<string>> surfaceIdsByStableName)
        {
            if (string.IsNullOrEmpty(requestedId))
            {
                return null;
            }
            if (surfacesById.ContainsKey(requestedId))
            {
                return requestedId;
            }

            string stableId = GetStableSurfaceId(requestedId);
            if (stableId != null && surfaceIdsByStableName.TryGetValue(stableId, out List<string> matches) && matches.Count == 1)
            {
                return matches[0];
            }
            return null;
        }

        private static string GetStableSurfaceId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            int hashSeparator = id.LastIndexOf(" (", StringComparison.Ordinal);
            return hashSeparator > 0 && id.EndsWith(")", StringComparison.Ordinal)
                ? id.Substring(0, hashSeparator)
                : id;
        }
    }
}
