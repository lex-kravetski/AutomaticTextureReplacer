using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Entities;

namespace AutomaticTextureReplacer
{
    internal static class ReplacementRuleCatalog
    {
        public const string TransparentSurfacePrefabName = "Automatic Texture Replacer - Transparent Surface";
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
            foreach (SurfaceReplacementSystem.SurfaceReplacementRule rule in rules ?? Enumerable.Empty<SurfaceReplacementSystem.SurfaceReplacementRule>())
            {
                if (rule == null || rule.sourceSurfaceIds == null || rule.targetSurfaceIds == null ||
                    (selectedRuleId.HasValue ? rule.id != selectedRuleId.Value : enabledOnly && !rule.enabled))
                {
                    continue;
                }

                foreach (string sourceId in rule.sourceSurfaceIds)
                {
                    if (string.IsNullOrEmpty(sourceId) || !surfacesById.ContainsKey(sourceId))
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
                        if (!string.IsNullOrEmpty(targetId) && surfacesById.ContainsKey(targetId) && targetId != sourceId)
                        {
                            targets.Add(targetId);
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
                        if (!string.IsNullOrEmpty(targetId) && surfacesById.ContainsKey(targetId))
                        {
                            targets.Add(targetId);
                        }
                    }
                }
            }

            return result;
        }
    }
}
