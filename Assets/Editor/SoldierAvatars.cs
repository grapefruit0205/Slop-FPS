using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Puts the Mixamo soldiers (see <see cref="MixamoImport"/>) into the three player prefabs in place of the old robot and
/// policeman bodies. The prefab files, their GUIDs and everything on them stay; only the body under the root changes:
/// the soldier comes in as a nested model prefab, takes over the old body's Animator settings, IKControl and audio, and
/// the third-person gun moves onto a "GunHolder" that IKControl keeps in the animated right hand. Every reference to the
/// old body (PlayerNetworkMover, PlayerHealth, TpsGun, NetworkAnimator...) is pointed at the new one. Safe to run again:
/// an existing soldier is replaced the same way.
///   Unity -batchmode -quit -projectPath . -executeMethod SoldierAvatars.Apply
/// </summary>
public static class SoldierAvatars {

    public const string ModelName = "SoldierModel";
    private const string HolderName = "GunHolder";

    private static readonly (string prefab, string character)[] Avatars = {
        ("Assets/Prefabs/Resources/PolicemanController.prefab", "Swat"),
        ("Assets/Prefabs/Resources/RobotXController.prefab", "SwatGuy"),
        ("Assets/Prefabs/Resources/RobotYController.prefab", "GasMask"),
    };

    // Where the rifle's grip rests before the animation takes over: the right hand in the rifle-aiming idle (model space;
    // the Mixamo soldiers are ~1.72 m tall, the same as the avatar's capsule, so the model is not scaled).
    private static readonly Vector3 RestGrip = new Vector3(0.2f, 1.29f, 0.04f);

    [MenuItem("Tools/FPS/Soldier Avatars")]
    public static void Apply() {
        foreach ((string prefab, string character) in Avatars) Build(prefab, character);
        AssetDatabase.SaveAssets();
    }

    private static void Build(string path, string character) {
        GameObject soldier = AssetDatabase.LoadAssetAtPath<GameObject>(MixamoImport.CharacterDir + character + ".fbx");
        if (soldier == null) throw new System.Exception("[SoldierAvatars] missing " + character + "; run MixamoImport.Apply first");
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        PlayerNetworkMover mover = root.GetComponent<PlayerNetworkMover>();
        Animator oldAnimator = new SerializedObject(mover).FindProperty("animator").objectReferenceValue as Animator;
        if (oldAnimator == null) throw new System.Exception("[SoldierAvatars] " + path + ": no body animator");
        GameObject oldModel = oldAnimator.gameObject;
        if (PrefabUtility.GetCorrespondingObjectFromOriginalSource(oldModel) == soldier) {
            PrefabUtility.UnloadPrefabContents(root);
            Debug.Log("[SoldierAvatars] " + path + ": already " + character);
            return;
        }
        IKControl oldIk = oldModel.GetComponent<IKControl>();
        TpsGun tps = root.GetComponentInChildren<TpsGun>(true);

        GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(soldier, root.transform);
        model.name = ModelName;
        model.transform.SetSiblingIndex(oldModel.transform.GetSiblingIndex());
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.identity;
        model.transform.localScale = Vector3.one;
        int layer = oldModel.layer;
        foreach (Transform t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;

        Animator animator = model.GetComponent<Animator>();
        animator.runtimeAnimatorController = oldAnimator.runtimeAnimatorController;
        animator.applyRootMotion = false;
        animator.cullingMode = oldAnimator.cullingMode;
        animator.updateMode = oldAnimator.updateMode;

        foreach (SkinnedMeshRenderer smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
            smr.updateWhenOffscreen = false;
            // Bounds around the hips that still hold the body lying down (head 1.2 m in front of the hips).
            smr.localBounds = new Bounds(Vector3.zero, Vector3.one * 2.8f);
            smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            smr.receiveShadows = true;
            smr.skinnedMotionVectors = false;
        }

        // The gun hangs off a plain child of the body; IKControl moves it into the animated right hand every frame.
        Transform holder = new GameObject(HolderName).transform;
        holder.gameObject.layer = layer;
        holder.SetParent(model.transform, false);
        if (tps != null) {
            Transform gun = tps.transform;
            Vector3 scale = gun.localScale;
            gun.SetParent(holder, false);
            gun.localRotation = Quaternion.identity;
            gun.localScale = scale;
            Transform grip = gun.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "RightHandHandle");
            gun.localPosition = Vector3.zero;
            Vector3 gripLocal = grip != null ? holder.InverseTransformPoint(grip.position) : Vector3.zero;
            gun.localPosition = RestGrip - gripLocal;
        }

        // Whatever else hung off the old body (its audio source) comes along.
        foreach (Transform child in oldModel.transform.Cast<Transform>().ToList()) {
            if (child.GetComponentInChildren<Renderer>(true) != null || child.name.StartsWith("mixamorig") || IsBone(child)) continue;
            child.SetParent(model.transform, false);
        }

        IKControl ik = model.AddComponent<IKControl>();
        if (oldIk != null) EditorUtility.CopySerialized(oldIk, ik);
        SerializedObject iks = new SerializedObject(ik);
        iks.FindProperty("ikActive").boolValue = true;
        iks.FindProperty("gunHolder").objectReferenceValue = holder;
        iks.ApplyModifiedPropertiesWithoutUndo();

        // Anything else on the old body (besides the transform, animator and IK) is copied over.
        foreach (Component c in oldModel.GetComponents<Component>()) {
            if (c is Transform || c is Animator || c is IKControl || c == null) continue;
            Component copy = model.AddComponent(c.GetType());
            EditorUtility.CopySerialized(c, copy);
            Debug.Log("[SoldierAvatars] " + path + ": copied " + c.GetType().Name + " to the soldier");
        }

        var map = new Dictionary<Object, Object> {
            { oldModel, model }, { oldModel.transform, model.transform }, { oldAnimator, animator },
        };
        if (oldIk != null) map[oldIk] = ik;
        int remapped = 0;
        var unmapped = new List<string>();
        foreach (Component c in root.GetComponentsInChildren<Component>(true)) {
            if (c == null || c.transform.IsChildOf(oldModel.transform)) continue;
            SerializedObject so = new SerializedObject(c);
            SerializedProperty p = so.GetIterator();
            bool changed = false;
            while (p.Next(true)) {
                if (p.propertyType != SerializedPropertyType.ObjectReference || p.objectReferenceValue == null) continue;
                Object v = p.objectReferenceValue;
                GameObject owner = v is Component vc ? vc.gameObject : v as GameObject;
                if (owner == null || !owner.transform.IsChildOf(oldModel.transform)) continue;
                if (map.TryGetValue(v, out Object to)) {
                    p.objectReferenceValue = to;
                    changed = true;
                    remapped++;
                } else {
                    unmapped.Add(c.GetType().Name + "." + p.propertyPath + " -> " + v.name);
                }
            }
            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
        }
        string oldName = oldModel.name;
        Object.DestroyImmediate(oldModel);

        PrefabUtility.SaveAsPrefabAsset(root, path);
        PrefabUtility.UnloadPrefabContents(root);
        Debug.Log("[SoldierAvatars] " + path + ": " + oldName + " -> " + character + ", " + remapped + " reference(s) moved" +
                  (unmapped.Count > 0 ? "; UNMAPPED: " + string.Join("; ", unmapped) : ""));
    }

    private static bool IsBone(Transform t) {
        // A skeleton root: no renderer anywhere below, but bones (transforms with more transforms below) of a skinned mesh.
        return t.GetComponentsInChildren<Transform>(true).Length > 8 && t.GetComponentInChildren<Renderer>(true) == null &&
               t.GetComponentInChildren<AudioSource>(true) == null;
    }

}
