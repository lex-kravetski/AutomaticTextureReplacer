using System.Collections.Generic;
using Game.Areas;
using Game.Tools;
using Unity.Entities;

namespace AutomaticTextureReplacer
{
    /// <summary>
    /// Temporary surfaces queued for a same-update AreaBatchSystem release.
    /// Keep the cleanup and prefab swap in one ToolUpdate. Placement tools
    /// destroy temporary entities as soon as they observe the Deleted marker,
    /// so a deferred or next-frame cleanup would remove the entity before the
    /// replacement PrefabRef can be applied.
    /// </summary>
    internal static class TemporaryAreaBatchReleaseQueue
    {
        internal sealed class Request
        {
            public Entity Area;
            public Entity SourcePrefab;
            public Entity TargetPrefab;
            public Entity InitialOwner;
            public Temp OriginalTemp;
            public int OriginalBatchIndex;
            public string SourceName;
            public string TargetName;
        }

        private static readonly List<Request> s_Pending = new List<Request>();

        public static void Enqueue(Request request)
        {
            int existingIndex = s_Pending.FindIndex(pending => pending.Area == request.Area);
            if (existingIndex >= 0)
            {
                s_Pending[existingIndex] = request;
            }
            else
            {
                s_Pending.Add(request);
            }
        }

        public static Request[] TakePending()
        {
            Request[] requests = s_Pending.ToArray();
            s_Pending.Clear();
            return requests;
        }
    }
}
