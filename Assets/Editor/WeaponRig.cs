using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Puts every gun and optic model onto the player prefabs' first- and third-person gun objects and fills in their
/// <see cref="GunModels"/>. The art prefabs are authored in the gun objects' own space (the AK's scale, muzzle on the
/// AK's muzzle; see Assets/Art/Military/Weapons/WEAPONS.md). First person keeps that layout, since there are no
/// arms and the AK's grip is behind the camera; third person slides each gun along so its grip sits where the AK's
/// is, under the IK hand.
/// Guns or optics whose art is missing are skipped; GunModels then falls back to the AK. Safe to run repeatedly.
///   Unity -batchmode -quit -projectPath . -executeMethod WeaponRig.Apply
/// </summary>
public static class WeaponRig {

    private const string WeaponsDir = "Assets/Art/Military/Weapons/";
    private const string AkModelName = "AK47 Model";
    private static readonly string[] PlayerPrefabs = {
        "Assets/Prefabs/Resources/PolicemanController.prefab",
        "Assets/Prefabs/Resources/RobotXController.prefab",
        "Assets/Prefabs/Resources/RobotYController.prefab",
    };
    private static readonly Dictionary<WeaponId, string> GunArt = new Dictionary<WeaponId, string> {
        { WeaponId.SMG, "SMG/SMG_View.prefab" },
        { WeaponId.Sniper, "Sniper/Sniper_View.prefab" },
        { WeaponId.Shotgun, "Shotgun/Shotgun_View.prefab" },
        { WeaponId.Pistol, "Pistol/Pistol_View.prefab" },
    };
    private static readonly Dictionary<OpticId, string> OpticArt = new Dictionary<OpticId, string> {
        { OpticId.RedDot, "Optics/RedDot/RedDot_View.prefab" },
        { OpticId.Scope4x, "Optics/Scope4x/Scope4x_View.prefab" },
        // No separate 8x model: the 4x body stands in for it.
        { OpticId.Scope8x, "Optics/Scope4x/Scope4x_View.prefab" },
    };
    private static readonly Dictionary<WeaponId, string> ShotSounds = new Dictionary<WeaponId, string>();
    private static readonly Dictionary<WeaponId, string> ReloadSounds = new Dictionary<WeaponId, string>();

    // The AK's optic rail in gun space, from WEAPONS.md. Its grip and support hand come from the IK handles.
    private static readonly Vector3 akRail = new Vector3(0f, 0.1582f, -0.0914f);

    /// <summary>
    /// The soldier bodies (SoldierAvatars), network setup (adds Loadout to the avatars), the player animator and the weapon
    /// models, in one editor run. (MixamoImport.Apply, the import settings, runs on its own.)
    /// </summary>
    public static void SetupAll() {
        SoldierAvatars.Apply();
        NetSetup.Apply();
        StanceAnimator.Apply();
        Apply();
    }

    [MenuItem("Tools/FPS/Rig Weapon Models")]
    public static void Apply() {
        FindSounds();
        foreach (string path in PlayerPrefabs) {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            Transform tps = all.FirstOrDefault(t => t.name == "TpsGun_AK47");
            Transform fps = all.FirstOrDefault(t => t.name == "FpsGun_AK47");
            // Both gun objects share one local layout, so the third-person IK handles locate the AK's grip for both.
            Transform rightHandle = tps != null ? tps.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "RightHandHandle") : null;
            Transform leftHandle = tps != null ? tps.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "LeftHandHandle") : null;
            Vector3 grip = rightHandle != null ? tps.InverseTransformPoint(rightHandle.position) : new Vector3(0.044f, -0.081f, -0.559f);
            Vector3 support = leftHandle != null ? tps.InverseTransformPoint(leftHandle.position) : new Vector3(-0.118f, -0.019f, 0.131f);
            string report = "";
            if (fps != null) report += Rig(fps, grip, support, null, null, false) + " ";
            if (tps != null) report += Rig(tps, grip, support, rightHandle, leftHandle, true);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
            Debug.Log("[WeaponRig] " + Path.GetFileNameWithoutExtension(path) + ": " + report);
        }
        AssetDatabase.SaveAssets();
    }

    private static string Rig(Transform gun, Vector3 grip, Vector3 support, Transform rightHandle, Transform leftHandle,
                              bool alignGrips) {
        Transform ak = gun.Find(AkModelName);
        Transform barrel = gun.Find("GunBarrelEnd");
        if (ak == null || barrel == null) return gun.name + " (no AK model)";
        int layer = gun.gameObject.layer;
        MeshRenderer akRenderer = ak.GetComponent<MeshRenderer>();

        // Start clean: earlier runs' copies and points go.
        foreach (Transform c in gun.Cast<Transform>().ToList()) {
            if (c.name.StartsWith("Gun_") || c.name.StartsWith("Optic_")) Object.DestroyImmediate(c.gameObject);
        }
        foreach (Transform c in ak.Cast<Transform>().ToList()) Object.DestroyImmediate(c.gameObject);

        var guns = new List<GunModels.Gun>();
        guns.Add(new GunModels.Gun {
            Id = WeaponId.AK47, Model = ak.gameObject,
            Muzzle = Point(ak, "Muzzle", gun.TransformPoint(barrel.localPosition)),
            Grip = Point(ak, "Grip", gun.TransformPoint(grip)),
            Handguard = Point(ak, "Handguard", gun.TransformPoint(support)),
            Rail = Point(ak, "Rail", gun.TransformPoint(akRail)),
            IronSight = Point(ak, "IronSight", FrontSight(gun, ak, gun.TransformPoint(barrel.localPosition))),
            Shot = Sound(ShotSounds, WeaponId.AK47), Reload = Sound(ReloadSounds, WeaponId.AK47),
        });
        string made = "AK";
        foreach (KeyValuePair<WeaponId, string> art in GunArt) {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(WeaponsDir + art.Value);
            if (prefab == null) continue;
            GameObject copy = Copy(prefab, gun, "Gun_" + art.Key, layer, akRenderer);
            Transform g = Find(copy.transform, "Grip");
            if (alignGrips && g != null) copy.transform.position += gun.TransformPoint(grip) - g.position;
            copy.SetActive(false);
            Transform muzzle = Find(copy.transform, "Muzzle");
            guns.Add(new GunModels.Gun {
                Id = art.Key, Model = copy, Muzzle = muzzle, Grip = g,
                IronSight = Point(copy.transform, "IronSight", FrontSight(gun, copy.transform, muzzle.position)),
                Handguard = Find(copy.transform, "Handguard"), Rail = Find(copy.transform, "Rail"),
                Shot = Sound(ShotSounds, art.Key), Reload = Sound(ReloadSounds, art.Key),
            });
            made += " " + art.Key;
        }

        var optics = new List<GunModels.Optic>();
        foreach (KeyValuePair<OpticId, string> art in OpticArt) {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(WeaponsDir + art.Value);
            if (prefab == null) continue;
            GameObject copy = Copy(prefab, gun, "Optic_" + art.Key, layer, akRenderer);
            copy.SetActive(false);
            // The HUD draws the red dot exactly where bullets go; the model's own dot would sit beside it.
            Transform dot = Find(copy.transform, "Dot");
            if (dot != null) dot.gameObject.SetActive(false);
            // A full-length tube reads as a tunnel this close to the eye; a short housing leaves a wide window.
            if (art.Key == OpticId.RedDot) copy.transform.localScale = Vector3.Scale(copy.transform.localScale, new Vector3(1f, 1f, 0.5f));
            optics.Add(new GunModels.Optic {
                Id = art.Key, Model = copy, Mount = Find(copy.transform, "Mount"), Eye = Find(copy.transform, "Eye"),
            });
            made += " " + art.Key;
        }

        GunModels models = gun.GetComponent<GunModels>();
        if (models == null) models = gun.gameObject.AddComponent<GunModels>();
        SerializedObject so = new SerializedObject(models);
        so.FindProperty("barrelEnd").objectReferenceValue = barrel;
        so.FindProperty("rightHand").objectReferenceValue = rightHandle;
        so.FindProperty("leftHand").objectReferenceValue = leftHandle;
        so.ApplyModifiedPropertiesWithoutUndo();
        // Arrays of plain classes are simplest to set through the component itself.
        typeof(GunModels).GetField("guns", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(models, guns.ToArray());
        typeof(GunModels).GetField("optics", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(models, optics.ToArray());
        EditorUtility.SetDirty(models);
        return gun.name + " [" + made + "]";
    }

    private static Transform Point(Transform parent, string name, Vector3 world) {
        Transform t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.position = world;
        t.gameObject.layer = parent.gameObject.layer;
        return t;
    }

    /// <summary>A plain (unpacked) copy of an art prefab under <paramref name="gun"/>, at the gun's origin.</summary>
    private static GameObject Copy(GameObject prefab, Transform gun, string name, int layer, MeshRenderer template) {
        GameObject copy = Object.Instantiate(prefab, gun, false);
        copy.name = name;
        copy.transform.localPosition = Vector3.zero;
        copy.transform.localRotation = Quaternion.identity;
        copy.transform.localScale = prefab.transform.localScale;
        foreach (Transform t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        foreach (Collider c in copy.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        foreach (MeshRenderer r in copy.GetComponentsInChildren<MeshRenderer>(true)) {
            if (template == null) continue;
            r.shadowCastingMode = template.shadowCastingMode;
            r.receiveShadows = template.receiveShadows;
        }
        return copy;
    }

    /// <summary>
    /// The top of the front sight: the highest vertex near the muzzle, close to the bore's vertical plane. Iron sights
    /// line the eye up with it, looking parallel to the bore.
    /// </summary>
    private static Vector3 FrontSight(Transform gun, Transform model, Vector3 muzzleWorld) {
        Vector3 muzzle = gun.InverseTransformPoint(muzzleWorld);
        float minZ = float.MaxValue;
        var points = new List<Vector3>();
        foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>(true)) {
            if (mf.sharedMesh == null) continue;
            foreach (Vector3 v in mf.sharedMesh.vertices) {
                Vector3 p = gun.InverseTransformPoint(mf.transform.TransformPoint(v));
                points.Add(p);
                minZ = Mathf.Min(minZ, p.z);
            }
        }
        float length = muzzle.z - minZ;
        Vector3 best = muzzle;
        foreach (Vector3 p in points) {
            if (p.z < muzzle.z - length * 0.15f || p.z > muzzle.z + 0.01f || Mathf.Abs(p.x - muzzle.x) > 0.03f) continue;
            if (p.y > best.y) best = p;
        }
        return gun.TransformPoint(new Vector3(muzzle.x, best.y, best.z));
    }

    private static Transform Find(Transform root, string name) {
        return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
    }

    /// <summary>Assets/Audio/Weapons/&lt;Name&gt;/: a clip with "reload" in its name reloads, any other one fires.</summary>
    private static void FindSounds() {
        ShotSounds.Clear();
        ReloadSounds.Clear();
        foreach (WeaponId id in System.Enum.GetValues(typeof(WeaponId))) {
            string dir = "Assets/Audio/Weapons/" + id;
            if (!AssetDatabase.IsValidFolder(dir)) continue;
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { dir })) {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                bool reload = Path.GetFileName(p).ToLowerInvariant().Contains("reload");
                (reload ? ReloadSounds : ShotSounds)[id] = p;
            }
        }
    }

    private static AudioClip Sound(Dictionary<WeaponId, string> table, WeaponId id) {
        return table.TryGetValue(id, out string p) ? AssetDatabase.LoadAssetAtPath<AudioClip>(p) : null;
    }

}
