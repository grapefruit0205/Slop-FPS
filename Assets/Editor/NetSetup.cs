using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FishNet.Component.Animating;
using FishNet.Component.Transforming;
using FishNet.Managing;
using FishNet.Managing.Client;
using FishNet.Managing.Object;
using FishNet.Managing.Server;
using FishNet.Managing.Timing;
using FishNet.Managing.Transporting;
using FishNet.Object;
using FishNet.Transporting.Bayou;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using SlopFPS.Input;

/// <summary>
/// Converts the project's networking from Photon PUN to FishNet: avatar and impact prefabs, the two server-spawned
/// prefabs (NetPlayer, MatchManager), the scene's "Network" object and the prefab registry. Safe to run repeatedly.
/// Lives outside FpsTools.Editor because it needs the game scripts (Assembly-CSharp).
/// </summary>
public static class NetSetup {

    private const string ScenePath = "Assets/Scenes/Start.unity";
    private const string NetPrefabDir = "Assets/Prefabs/Net";
    private const string ResourcesDir = "Assets/Resources";
    private static readonly string[] Avatars = {
        "Assets/Prefabs/Resources/PolicemanController.prefab",
        "Assets/Prefabs/Resources/RobotXController.prefab",
        "Assets/Prefabs/Resources/RobotYController.prefab",
    };

    [MenuItem("Tools/FPS/Network Setup (FishNet)")]
    public static void Apply() {
        foreach (string path in Avatars) ConvertAvatar(path);
        foreach (string path in AssetDatabase.FindAssets("impact t:Prefab", new[] { "Assets/Prefabs/Resources" })
                     .Select(AssetDatabase.GUIDToAssetPath)) {
            StripPhoton(path);
        }
        NetworkObject netPlayer = MakeNetPrefab("NetPlayer", typeof(NetPlayer));
        NetworkObject match = MakeNetPrefab("MatchManager", typeof(MatchManager));
        WriteNetConfig();
        DefaultPrefabObjects prefabs = RegisterPrefabs();
        SetupScene(netPlayer, match, prefabs);
        SetupPlayerSettings();
        AssetDatabase.SaveAssets();
        Debug.Log("[NetSetup] Done.");
    }

    // ------------------------------------------------------------------ prefabs

    private static int RemovePhotonComponents(GameObject root) {
        int removed = 0;
        // Observed-component lists reference other components, so take the views (which reference them) out last.
        Component[] all = root.GetComponentsInChildren<Component>(true)
            .Where(c => c != null && c.GetType().Namespace != null && c.GetType().Namespace.StartsWith("Photon"))
            .OrderBy(c => c.GetType().Name == "PhotonView" ? 1 : 0)
            .ToArray();
        foreach (Component c in all) {
            UnityEngine.Object.DestroyImmediate(c, true);
            removed++;
        }
        return removed;
    }

    private static void StripPhoton(string path) {
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        int removed = RemovePhotonComponents(root);
        if (removed > 0) PrefabUtility.SaveAsPrefabAsset(root, path);
        PrefabUtility.UnloadPrefabContents(root);
        if (removed > 0) Debug.Log("[NetSetup] " + path + ": removed " + removed + " Photon component(s)");
    }

    private static T Ensure<T>(GameObject go) where T : Component {
        T c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    private static void SetField(object target, string name, object value) {
        Type t = target.GetType();
        FieldInfo f = null;
        while (t != null && f == null) {
            f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            t = t.BaseType;
        }
        if (f == null) throw new MissingFieldException(target.GetType().Name, name);
        f.SetValue(target, value);
    }

    private static string PathOf(Transform t, Transform root) {
        string p = t.name;
        while (t.parent != null && t != root) {
            t = t.parent;
            p = t.name + "/" + p;
        }
        return p;
    }

    private static void ConvertAvatar(string path) {
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        int removed = RemovePhotonComponents(root);

        Ensure<NetworkObject>(root);
        // Server-side movement checks and the position history used to validate hits.
        Ensure<MovementGuard>(root);
        // Which guns the avatar carries and holds, and the server's model of each.
        Ensure<Loadout>(root);
        // Crouch / prone / lean: the owner's choice, checked and relayed by the server.
        Ensure<PlayerStance>(root);

        // Movement: the owner simulates and the server relays (client-authoritative, as with PUN before).
        NetworkTransform rootNt = Ensure<NetworkTransform>(root);
        SetField(rootNt, "_clientAuthoritative", true);
        SetField(rootNt, "_sendToOwner", false);
        SetField(rootNt, "_synchronizeScale", false);

        // Unity's "fake null" defeats ??, so look the animator up the way the movers reference it.
        Animator animator = new SerializedObject(root.GetComponent<PlayerNetworkMover>()).FindProperty("animator").objectReferenceValue as Animator;
        if (animator == null) animator = root.GetComponentInChildren<Animator>(true);
        NetworkAnimator na = Ensure<NetworkAnimator>(root);
        SetField(na, "_animator", animator);
        SetField(na, "_clientAuthoritative", true);
        SetField(na, "_sendToOwner", false);

        // The third-person gun pitches with the owner's aim.
        TpsGun tps = root.GetComponentInChildren<TpsGun>(true);
        if (tps != null) {
            NetworkTransform gunNt = Ensure<NetworkTransform>(tps.gameObject);
            SetField(gunNt, "_clientAuthoritative", true);
            SetField(gunNt, "_sendToOwner", false);
            SetField(gunNt, "_synchronizeScale", false);
        }

        // Spawned bodies start as "someone else"; PlayerNetworkMover switches the owner's into first person.
        FirstPersonController fpc = root.GetComponent<FirstPersonController>();
        if (fpc != null) fpc.enabled = false;
        PlayerNetworkMover mover = root.GetComponent<PlayerNetworkMover>();
        SerializedObject so = new SerializedObject(mover);
        GameObject cam = so.FindProperty("cameraObject").objectReferenceValue as GameObject;
        if (cam != null) cam.SetActive(false);

        FpsGun fpsGun = root.GetComponentInChildren<FpsGun>(true);
        Debug.Log("[NetSetup] " + path + ": removed " + removed + " Photon component(s); animator=" +
                  (animator != null ? PathOf(animator.transform, root.transform) : "none") +
                  " tpsGun=" + (tps != null ? PathOf(tps.transform, root.transform) : "none") +
                  " fpsGun=" + (fpsGun != null ? PathOf(fpsGun.transform, root.transform) : "none") +
                  " camera=" + (cam != null ? PathOf(cam.transform, root.transform) : "none") +
                  " behaviours=" + root.GetComponentsInChildren<NetworkBehaviour>(true).Length);

        PrefabUtility.SaveAsPrefabAsset(root, path);
        PrefabUtility.UnloadPrefabContents(root);
    }

    private static NetworkObject MakeNetPrefab(string name, Type behaviour) {
        Directory.CreateDirectory(NetPrefabDir);
        string path = NetPrefabDir + "/" + name + ".prefab";
        GameObject go = new GameObject(name);
        go.AddComponent<NetworkObject>();
        go.AddComponent(behaviour);
        PrefabUtility.SaveAsPrefabAsset(go, path);
        UnityEngine.Object.DestroyImmediate(go);
        return AssetDatabase.LoadAssetAtPath<NetworkObject>(path);
    }

    private static void WriteNetConfig() {
        Directory.CreateDirectory(ResourcesDir);
        string path = ResourcesDir + "/netconfig.json";
        if (!File.Exists(path)) {
            File.WriteAllText(path, "{\n  \"apiBase\": \"\",\n  \"devHost\": \"127.0.0.1\",\n  \"devPort\": 7770\n}\n");
            AssetDatabase.ImportAsset(path);
        }
    }

    private static DefaultPrefabObjects RegisterPrefabs() {
        AssetDatabase.Refresh();
        Type generator = typeof(NetworkManager).Assembly.GetType("FishNet.Editing.PrefabCollectionGenerator.Generator");
        MethodInfo full = generator.GetMethod("GenerateFull", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        full.Invoke(null, new object[] { null, true, true });
        DefaultPrefabObjects dpo = AssetDatabase.LoadAssetAtPath<DefaultPrefabObjects>("Assets/DefaultPrefabObjects.asset");
        if (dpo == null) throw new Exception("DefaultPrefabObjects.asset was not generated");
        Debug.Log("[NetSetup] Registered " + dpo.GetObjectCount() + " network prefab(s): " +
                  string.Join(", ", dpo.Prefabs.Select(p => p != null ? p.name : "null")));
        return dpo;
    }

    // ------------------------------------------------------------------ scene

    private static void SetupScene(NetworkObject netPlayer, NetworkObject match, DefaultPrefabObjects prefabs) {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int removed = 0;
        foreach (GameObject go in scene.GetRootGameObjects()) removed += RemovePhotonComponents(go);

        GameObject netGo = scene.GetRootGameObjects().FirstOrDefault(g => g.GetComponent<NetworkManager>() != null);
        if (netGo == null) netGo = new GameObject("Network");
        netGo.name = "Network";

        NetworkManager nm = Ensure<NetworkManager>(netGo);
        nm.SpawnablePrefabs = prefabs;
        Ensure<TimeManager>(netGo);
        Ensure<ClientManager>(netGo);
        TransportManager tm = Ensure<TransportManager>(netGo);
        Bayou bayou = Ensure<Bayou>(netGo);
        tm.Transport = bayou;
        FpsAuthenticator auth = Ensure<FpsAuthenticator>(netGo);
        ServerManager sm = Ensure<ServerManager>(netGo);
        sm.SetAuthenticator(auth);
        // GameServer configures the port and TLS first, then starts the server itself.
        sm.SetStartOnHeadless(false);

        GameServer server = Ensure<GameServer>(netGo);
        SerializedObject so = new SerializedObject(server);
        so.FindProperty("netPlayerPrefab").objectReferenceValue = netPlayer;
        so.FindProperty("matchPrefab").objectReferenceValue = match;
        so.ApplyModifiedPropertiesWithoutUndo();

        foreach (Component c in netGo.GetComponents<Component>()) EditorUtility.SetDirty(c);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[NetSetup] Scene: removed " + removed + " Photon component(s), Network object ready");
    }

    private static void SetupPlayerSettings() {
        PlayerSettings.runInBackground = true;
        PlayerSettings.assemblyVersionValidation = false;
        // Drop PUN's symbols everywhere; FishNet only adds its own for the active target, and the dedicated server
        // target starts with none at all.
        string standalone = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone);
        foreach (NamedBuildTarget target in new[] { NamedBuildTarget.Standalone, NamedBuildTarget.WebGL, NamedBuildTarget.Server,
                                                    NamedBuildTarget.Android }) {
            string current = target == NamedBuildTarget.Server ? standalone : PlayerSettings.GetScriptingDefineSymbols(target);
            var symbols = current.Split(';').Where(d => d.Length > 0 && !d.StartsWith("PHOTON") && !d.StartsWith("PUN_")).ToList();
            foreach (string d in new[] { "FISHNET", "FISHNET_V4" }) {
                if (!symbols.Contains(d)) symbols.Add(d);
            }
            PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", symbols));
        }
    }

}
