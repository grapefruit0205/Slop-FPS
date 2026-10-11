using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Static batching for map prefabs, which the build cannot batch: they are instantiated at runtime and scaled by
/// <see cref="MapDefinition.worldScale"/>. (The warehouse in Start.unity is batched by the build already.)
/// Without it every crate part, barrel and wall piece is its own draw call in each pass (depth, shadow cascades, colour),
/// which is what a laptop GPU in a browser chokes on. Combining bakes the renderers' world transforms into shared
/// vertex buffers so same-material neighbours draw together; GameObjects, colliders and LOD groups stay as they are.
/// Clients only (no graphics device, nothing to draw). -nomapbatch / ?nomapbatch turns it off for comparisons.
/// </summary>
public static class MapBatching {

    /// <summary>Big meshes are a draw call each anyway; copying them would only cost memory (the WebGL heap is small).</summary>
    private const int MaxVertices = 4000;

    /// <summary>Batch the renderers under <paramref name="root"/>, which must not move afterwards.</summary>
    public static void Combine(GameObject root) {
        if (root == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || NetConfig.NoMapBatching) return;
        List<GameObject> batch = new List<GameObject>();
        int[] skipped = new int[4];
        foreach (MeshRenderer r in root.GetComponentsInChildren<MeshRenderer>(true)) {
            int why = Skip(r);
            if (why < 0) batch.Add(r.gameObject);
            else skipped[why]++;
        }
        if (batch.Count < 2) return;
        StaticBatchingUtility.Combine(batch.ToArray(), root);
        Debug.Log("[Map] Batched " + batch.Count + " renderers under " + root.name + "; left alone: " + skipped[0] + " already batched, " +
                  skipped[1] + " not readable, " + skipped[2] + " large, " + skipped[3] + " movable");
    }

    /// <summary>-1 to batch, else why not (an index into the log line's counts).</summary>
    private static int Skip(MeshRenderer r) {
        if (r.isPartOfStaticBatch) return 0;
        MeshFilter f = r.GetComponent<MeshFilter>();
        Mesh m = f != null ? f.sharedMesh : null;
        if (m == null || !m.isReadable) return 1;
        if (m.vertexCount > MaxVertices) return 2;
        // Anything that can move: physics, animation, networked objects.
        if (r.GetComponentInParent<Rigidbody>() != null || r.GetComponentInParent<Animator>() != null ||
            r.GetComponentInParent<Animation>() != null || r.GetComponentInParent<FishNet.Object.NetworkObject>() != null) return 3;
        return -1;
    }

}
