using System.Collections.Generic;
using UnityEngine;

/// <summary>Bullet impact effects (Resources/impact&lt;Surface&gt;), spawned locally on every machine that sees the shot.</summary>
public static class Impacts {

    private static readonly Dictionary<string, GameObject> cache = new Dictionary<string, GameObject>();

    public static void Spawn(string surface, Vector3 point, Vector3 normal) {
        GameObject prefab = Load(surface);
        if (prefab == null) prefab = Load("Concrete");
        if (prefab == null) return;
        Object.Instantiate(prefab, point, Quaternion.Euler(normal.x - 90, normal.y, normal.z));
    }

    private static GameObject Load(string surface) {
        if (string.IsNullOrEmpty(surface)) return null;
        if (!cache.TryGetValue(surface, out GameObject prefab)) {
            prefab = Resources.Load<GameObject>("impact" + surface);
            cache[surface] = prefab;
        }
        return prefab;
    }

}
