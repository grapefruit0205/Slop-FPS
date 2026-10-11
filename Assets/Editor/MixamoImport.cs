using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Import settings for the Mixamo soldiers and their rifle animations (Assets/Art/Military/Characters/Mixamo): the
/// characters become Humanoid with their own avatar and Standard (Specular setup) materials built from the extracted
/// textures; every clip is Humanoid on the Swat avatar (all Mixamo skeletons are the same), baked so it plays in place
/// without root motion. The settings live in the .meta files; this re-creates them. Safe to run again.
///   Unity -batchmode -quit -projectPath . -executeMethod MixamoImport.Apply
/// The character FBX files were slimmed before they went in: the textures Mixamo embeds were pulled out, scaled down to
/// 1024 px and saved next to them (Textures/), and the embedded copies blanked.
/// </summary>
public static class MixamoImport {

    public const string Root = "Assets/Art/Military/Characters/Mixamo/";
    public const string CharacterDir = Root + "Characters/";
    public const string TextureDir = Root + "Textures/";
    public const string MaterialDir = Root + "Materials/";
    public const string AnimationDir = Root + "Animations/";
    /// <summary>The avatar every clip is imported against.</summary>
    public const string SourceAvatarModel = CharacterDir + "Swat.fbx";
    public static readonly string[] Characters = { "Swat", "SwatGuy", "GasMask" };

    // FBX material name -> texture set (Textures/<set>_Albedo/_Normal/_SpecGloss/_Emission), and the smoothness scale.
    // The GasMask albedo textures bake in much darker than the other two (mean luma ~30 vs ~60), so its materials get a
    // >1 tint to bring the soldier back to the same visual range without repainting the textures.
    private static readonly Dictionary<string, (string material, string set, float gloss)[]> MaterialSets =
        new Dictionary<string, (string, string, float)[]> {
            { "Swat", new[] { ("Soldier_body1", "Swat_Body", 0.35f), ("Soldier_head6", "Swat_Head", 0.3f) } },
            { "SwatGuy", new[] { ("Ch15_body", "SwatGuy_1001", 1f), ("Ch15_body1", "SwatGuy_1002", 1f) } },
            { "GasMask", new[] { ("Ch35_body", "GasMask_1001", 1f), ("Ch35_body1", "GasMask_1002", 1f), ("Ch35_body2", "GasMask_1003", 1f) } },
        };

    /// <summary>Albedo tint per character; >1 brightens materials whose source textures bake in dark.</summary>
    private static readonly Dictionary<string, float> Tints = new Dictionary<string, float> {
        { "Swat", 1f }, { "SwatGuy", 1f }, { "GasMask", 1.7f },
    };

    // One-shot clips; everything else loops.
    private static readonly HashSet<string> OneShots = new HashSet<string> {
        "jump_up", "jump_down", "crouch_to_prone", "prone_to_crouch", "prone_death",
        "death_crouching_headshot_front", "death_from_back_headshot", "death_from_front_headshot", "death_from_right",
        "death_from_the_back", "death_from_the_front",
    };

    [MenuItem("Tools/FPS/Import Mixamo Soldiers")]
    public static void Apply() {
        AssetDatabase.Refresh();
        Textures();
        foreach (string c in Characters) Character(c, MakeMaterials(c));
        Avatar source = AssetDatabase.LoadAllAssetsAtPath(SourceAvatarModel).OfType<Avatar>().FirstOrDefault();
        if (source == null || !source.isHuman) throw new System.Exception("[MixamoImport] no humanoid avatar in " + SourceAvatarModel);
        int clips = 0;
        foreach (string path in Directory.GetFiles(AnimationDir, "*.fbx").Select(p => p.Replace('\\', '/')).OrderBy(p => p)) {
            Animation(path, source);
            clips++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[MixamoImport] " + Characters.Length + " characters, " + clips + " clips");
    }

    /// <summary>The clip imported from Animations/&lt;name&gt;.fbx.</summary>
    public static AnimationClip Clip(string name) {
        AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(AnimationDir + name + ".fbx").OfType<AnimationClip>()
            .FirstOrDefault(c => !c.name.StartsWith("__preview"));
        if (clip == null) throw new System.Exception("[MixamoImport] missing clip " + name);
        return clip;
    }

    // ------------------------------------------------------------------ textures and materials

    private static void Textures() {
        foreach (string path in Directory.GetFiles(TextureDir).Where(p => !p.EndsWith(".meta")).Select(p => p.Replace('\\', '/'))) {
            TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) continue;
            bool normal = path.Contains("_Normal");
            TextureImporterType type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (ti.textureType == type && ti.maxTextureSize == 1024 && ti.mipmapEnabled && ti.sRGBTexture == !normal) continue;
            ti.textureType = type;
            ti.maxTextureSize = 1024;
            ti.mipmapEnabled = true;
            ti.sRGBTexture = !normal;
            ti.alphaSource = path.Contains("_SpecGloss") ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            ti.alphaIsTransparency = false;
            ti.SaveAndReimport();
        }
    }

    private static Dictionary<string, Material> MakeMaterials(string character) {
        Directory.CreateDirectory(MaterialDir);
        var made = new Dictionary<string, Material>();
        Shader shader = Shader.Find("Standard (Specular setup)");
        foreach ((string material, string set, float gloss) in MaterialSets[character]) {
            string path = MaterialDir + set + ".mat";
            Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = shader;
            float tint = Tints.TryGetValue(character, out float t) ? t : 1f;
            m.color = new Color(tint, tint, tint, 1f);
            m.SetTexture("_MainTex", Tex(set, "_Albedo"));
            Texture normal = Tex(set, "_Normal");
            m.SetTexture("_BumpMap", normal);
            m.SetFloat("_BumpScale", 1f);
            SetKeyword(m, "_NORMALMAP", normal != null);
            Texture spec = Tex(set, "_SpecGloss");
            m.SetTexture("_SpecGlossMap", spec);
            m.SetColor("_SpecColor", new Color(0.2f, 0.2f, 0.2f));
            m.SetFloat("_SmoothnessTextureChannel", 0f);
            m.SetFloat("_GlossMapScale", gloss);
            m.SetFloat("_Glossiness", 0.3f);
            SetKeyword(m, "_SPECGLOSSMAP", spec != null);
            Texture emission = Tex(set, "_Emission");
            m.SetTexture("_EmissionMap", emission);
            m.SetColor("_EmissionColor", emission != null ? Color.white : Color.black);
            SetKeyword(m, "_EMISSION", emission != null);
            m.globalIlluminationFlags = emission != null ? MaterialGlobalIlluminationFlags.RealtimeEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            made[material] = m;
        }
        AssetDatabase.SaveAssets();
        return made;
    }

    private static Texture Tex(string set, string kind) {
        foreach (string ext in new[] { ".jpg", ".png" }) {
            Texture t = AssetDatabase.LoadAssetAtPath<Texture>(TextureDir + set + kind + ext);
            if (t != null) return t;
        }
        return null;
    }

    private static void SetKeyword(Material m, string keyword, bool on) {
        if (on) m.EnableKeyword(keyword);
        else m.DisableKeyword(keyword);
    }

    // ------------------------------------------------------------------ models

    private static void Character(string name, Dictionary<string, Material> materials) {
        string path = CharacterDir + name + ".fbx";
        ModelImporter mi = (ModelImporter)AssetImporter.GetAtPath(path);
        mi.animationType = ModelImporterAnimationType.Human;
        mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        mi.importAnimation = false;
        mi.importBlendShapes = false;
        mi.importCameras = false;
        mi.importLights = false;
        mi.importVisibility = false;
        mi.optimizeGameObjects = false;      // the bones stay transforms: the gun and the shot checks find them by name
        mi.meshCompression = ModelImporterMeshCompression.Medium;
        mi.isReadable = false;
        mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
        mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
        mi.materialSearch = ModelImporterMaterialSearch.Local;
        foreach (KeyValuePair<string, Material> m in materials) {
            mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), m.Key), m.Value);
        }
        mi.SaveAndReimport();
        Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
        Debug.Log("[MixamoImport] " + path + ": avatar " + (avatar != null ? avatar.name + (avatar.isHuman ? " (human)" : " (NOT human)") : "none") +
                  ", materials " + string.Join(", ", materials.Keys));
    }

    private static void Animation(string path, Avatar source) {
        string name = Path.GetFileNameWithoutExtension(path);
        ModelImporter mi = (ModelImporter)AssetImporter.GetAtPath(path);
        mi.animationType = ModelImporterAnimationType.Human;
        mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
        mi.sourceAvatar = source;
        mi.importAnimation = true;
        mi.materialImportMode = ModelImporterMaterialImportMode.None;
        mi.importBlendShapes = false;
        mi.importCameras = false;
        mi.importLights = false;
        mi.resampleCurves = true;
        mi.animationCompression = ModelImporterAnimationCompression.Optimal;
        ModelImporterClipAnimation[] defaults = mi.defaultClipAnimations;
        if (defaults.Length == 0) {
            Debug.LogWarning("[MixamoImport] " + path + ": no take");
            return;
        }
        ModelImporterClipAnimation c = defaults[0];
        bool loop = !OneShots.Contains(name);
        c.name = name;
        c.loopTime = loop;
        c.loopPose = false;
        // Played without root motion: the facing and the height stay as authored (so a crouch is low and a prone body
        // lies on the ground). Loops drop their sideways drift (it goes to root motion, which nobody applies); the
        // one-shots keep it, so a falling body falls where the animation puts it.
        c.lockRootRotation = true;
        c.keepOriginalOrientation = true;
        c.lockRootHeightY = true;
        c.keepOriginalPositionY = true;
        c.heightFromFeet = false;
        c.lockRootPositionXZ = !loop;
        c.keepOriginalPositionXZ = true;
        c.rotationOffset = 0f;
        c.heightOffset = 0f;
        c.cycleOffset = 0f;
        c.mirror = false;
        mi.clipAnimations = new[] { c };
        mi.SaveAndReimport();
    }

    // ------------------------------------------------------------------ measurements

    /// <summary>
    /// Logs where the body is in the clips that matter for hitboxes, the camera and the gun: head, hips, hands and
    /// feet in the model's own space (feet at 0, facing +z), for each character's T-pose height and each pose.
    ///   Unity -batchmode -quit -projectPath . -executeMethod MixamoImport.Measure
    /// </summary>
    public static void Measure() {
        foreach (string c in Characters) {
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterDir + c + ".fbx");
            GameObject go = Object.Instantiate(model);
            Animator a = go.GetComponent<Animator>();
            Bounds b = new Bounds(go.transform.position, Vector3.zero);
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
            Transform head = a.GetBoneTransform(HumanBodyBones.Head);
            Debug.Log("[Measure] " + c + " tpose: bounds min " + b.min.ToString("F3") + " max " + b.max.ToString("F3") +
                      " head " + head.position.ToString("F3") + " eyes " + Eye(a).ToString("F3") + " humanScale " + a.humanScale.ToString("F3"));
            Sample(a, Clip("idle"), 0f, c + " idle");
            if (c == "Swat") {
                foreach (string clip in new[] { "idle", "idle_aiming", "walk_forward", "run_forward", "sprint_forward", "idle_crouching",
                                                "idle_crouching_aiming", "walk_crouching_forward", "prone_idle", "prone_forward",
                                                "prone_backward", "prone_firing", "crouch_to_prone", "prone_to_crouch", "death_from_the_front",
                                                "prone_death", "jump_loop" }) {
                    AnimationClip ac = Clip(clip);
                    foreach (float f in new[] { 0f, 0.5f, 1f }) Sample(a, ac, f * ac.length, clip + "@" + f.ToString("F1"));
                }
            }
            Object.DestroyImmediate(go);
        }
    }

    /// <summary>
    /// How fast each in-place loop would travel: the planted foot slides backwards at the walking speed. Logged so the
    /// blend trees can play each clip at the speed the controller moves.
    ///   Unity -batchmode -quit -projectPath . -executeMethod MixamoImport.MeasureSpeeds
    /// </summary>
    public static void MeasureSpeeds() {
        GameObject go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(SourceAvatarModel));
        Animator a = go.GetComponent<Animator>();
        foreach (string clip in Directory.GetFiles(AnimationDir, "*.fbx").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n)) {
            if (OneShots.Contains(clip)) continue;
            AnimationClip ac = Clip(clip);
            const int n = 60;
            var speeds = new List<float>();
            Vector3 prevL = Vector3.zero, prevR = Vector3.zero;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i <= n; i++) {
                float t = ac.length * i / n;
                AnimationMode.StartAnimationMode();
                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(go, ac, t);
                AnimationMode.EndSampling();
                Vector3 l = go.transform.InverseTransformPoint(a.GetBoneTransform(HumanBodyBones.LeftToes).position);
                Vector3 r = go.transform.InverseTransformPoint(a.GetBoneTransform(HumanBodyBones.RightToes).position);
                AnimationMode.StopAnimationMode();
                if (i > 0) {
                    // The planted foot: the lower one, if it was also the lower one a frame ago.
                    bool left = l.y < r.y && prevL.y < prevR.y;
                    bool right = r.y < l.y && prevR.y < prevL.y;
                    Vector3 d = left ? l - prevL : right ? r - prevR : Vector3.zero;
                    d.y = 0f;
                    if (left || right) {
                        speeds.Add(d.magnitude / (ac.length / n));
                        sum += d;
                    }
                }
                prevL = l;
                prevR = r;
            }
            speeds.Sort();
            float median = speeds.Count > 0 ? speeds[speeds.Count / 2] : 0f;
            Vector3 dir = sum.sqrMagnitude > 0f ? -sum.normalized : Vector3.zero;
            Debug.Log("[Speed] " + clip + ": " + median.ToString("F2") + " m/s toward " + dir.ToString("F2") + " (" + ac.length.ToString("F2") + " s)");
        }
        Object.DestroyImmediate(go);
    }

    private static Vector3 Eye(Animator a) {
        Transform l = a.GetBoneTransform(HumanBodyBones.LeftEye), r = a.GetBoneTransform(HumanBodyBones.RightEye);
        if (l != null && r != null) return (l.position + r.position) / 2f;
        Transform head = a.GetBoneTransform(HumanBodyBones.Head);
        return head.position + Vector3.up * 0.08f + a.transform.forward * 0.1f;
    }

    private static void Sample(Animator a, AnimationClip clip, float time, string label) {
        AnimationMode.StartAnimationMode();
        AnimationMode.BeginSampling();
        AnimationMode.SampleAnimationClip(a.gameObject, clip, time);
        AnimationMode.EndSampling();
        Transform root = a.transform;
        string P(HumanBodyBones bone) {
            Transform t = a.GetBoneTransform(bone);
            return t != null ? root.InverseTransformPoint(t.position).ToString("F2") : "-";
        }
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (Transform t in root.GetComponentsInChildren<Transform>()) {
            Vector3 l = root.InverseTransformPoint(t.position);
            minY = Mathf.Min(minY, l.y);
            maxY = Mathf.Max(maxY, l.y);
        }
        // The skin itself, posed: its extent in the model's space (top of the helmet, front of the head lying down...).
        Vector3 vmin = Vector3.one * float.MaxValue, vmax = Vector3.one * float.MinValue;
        foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>()) {
            Mesh baked = new Mesh();
            smr.BakeMesh(baked, true);
            foreach (Vector3 v in baked.vertices) {
                Vector3 l = root.InverseTransformPoint(smr.transform.TransformPoint(v));
                vmin = Vector3.Min(vmin, l);
                vmax = Vector3.Max(vmax, l);
            }
            Object.DestroyImmediate(baked);
        }
        Debug.Log("[Measure] " + label + ": skin " + vmin.ToString("F2") + ".." + vmax.ToString("F2") + " head " + P(HumanBodyBones.Head) + " eyes " + root.InverseTransformPoint(Eye(a)).ToString("F2") +
                  " hips " + P(HumanBodyBones.Hips) + " chest " + P(HumanBodyBones.UpperChest) +
                  " rHand " + P(HumanBodyBones.RightHand) + " lHand " + P(HumanBodyBones.LeftHand) +
                  " rShoulder " + P(HumanBodyBones.RightUpperArm) + " lFoot " + P(HumanBodyBones.LeftFoot) + " rFoot " + P(HumanBodyBones.RightFoot) +
                  " bonesY " + minY.ToString("F2") + ".." + maxY.ToString("F2"));
        AnimationMode.StopAnimationMode();
    }

}
