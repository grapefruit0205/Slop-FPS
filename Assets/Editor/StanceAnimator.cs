using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Builds the third-person player animator (Assets/Animator/PlayerController.controller) from the Mixamo rifle clips
/// (<see cref="MixamoImport"/>). The asset and its parameters keep their names, since the game drives them:
/// Vertical / Horizontal (movement input, -1..1), Running (shift), Firing (aiming or shooting), Stance (0 stand,
/// 1 crouch, 2 prone; PlayerStance), IsJumping, IsHurt, IsDead (triggers).
///   Base: stand / run / crouch / prone, each a directional blend of 8-way clips played at the controller's speeds;
///         crouch-to-prone and prone-to-crouch between them; jump; a death per stance.
///   Aim (upper body): the rifle shouldered while Firing, standing or crouched.
///   Hurt (upper body, half weight): a flinch.
/// Played without root motion: the clips are baked in place by MixamoImport. Safe to run again (rebuilds everything).
/// </summary>
public static class StanceAnimator {

    private const string ControllerPath = "Assets/Animator/PlayerController.controller";
    private const string UpperBodyMaskPath = "Assets/Masks/UpperBody.mask";
    private const string HurtMaskPath = "Assets/Masks/Hurt.mask";
    private const string HurtClipPath = "Assets/Models/Policeman/Policeman@stand_hit_reaction.fbx";

    // How fast the avatar moves (FirstPersonController speeds, world units, / the avatar's 1.65 scale) and how fast each
    // clip would travel (MixamoImport.MeasureSpeeds), so the feet keep up with the body.
    private const float AvatarScale = 1.65f;
    private const float WalkSpeed = 4f / AvatarScale;
    private const float RunSpeed = 8f / AvatarScale;
    private const float CrouchSpeed = WalkSpeed * 0.55f;
    private const float ProneSpeed = WalkSpeed * 0.25f;

    private static readonly Dictionary<string, float> ClipSpeed = new Dictionary<string, float> {
        { "walk", 1.8f }, { "run", 4.4f }, { "sprint_forward", 6.0f }, { "sprint_forward_left", 5.4f }, { "sprint_forward_right", 5.4f },
        { "sprint_left", 4.7f }, { "sprint_right", 4.7f }, { "walk_crouching", 2.0f }, { "prone_forward", 0.35f }, { "prone_backward", 0.44f },
    };

    private static readonly (string suffix, Vector2 dir)[] Directions = {
        ("forward", new Vector2(0f, 1f)), ("forward_right", new Vector2(0.7071f, 0.7071f)), ("right", new Vector2(1f, 0f)),
        ("backward_right", new Vector2(0.7071f, -0.7071f)), ("backward", new Vector2(0f, -1f)),
        ("backward_left", new Vector2(-0.7071f, -0.7071f)), ("left", new Vector2(-1f, 0f)), ("forward_left", new Vector2(-0.7071f, 0.7071f)),
    };

    private static AnimatorController ac;

    [MenuItem("Tools/FPS/Build Player Animator")]
    public static void Apply() {
        ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        Clear();
        foreach (string f in new[] { "Vertical", "Horizontal" }) ac.AddParameter(f, AnimatorControllerParameterType.Float);
        ac.AddParameter("Firing", AnimatorControllerParameterType.Bool);
        ac.AddParameter("Running", AnimatorControllerParameterType.Bool);
        foreach (string t in new[] { "IsDead", "IsHurt", "IsJumping" }) ac.AddParameter(t, AnimatorControllerParameterType.Trigger);
        ac.AddParameter("Stance", AnimatorControllerParameterType.Int);

        AvatarMask upper = UpperBodyMask();
        ac.layers = new[] {
            Layer("Base Layer", BaseLayer(), null, 1f),
            Layer("Aim", AimLayer(), upper, 1f),
            Layer("Hurt", HurtLayer(), AssetDatabase.LoadAssetAtPath<AvatarMask>(HurtMaskPath), 0.5f),
        };
        EditorUtility.SetDirty(ac);
        AssetDatabase.SaveAssets();
        Debug.Log("[StanceAnimator] " + ControllerPath + ": layers " + string.Join(", ", ac.layers.Select(l => l.name + " (" + l.stateMachine.states.Length + " states)")) +
                  ", parameters " + string.Join(", ", ac.parameters.Select(p => p.name)));
    }

    // ------------------------------------------------------------------ layers

    private static AnimatorStateMachine BaseLayer() {
        AnimatorStateMachine sm = Machine("Base Layer");
        AnimatorState stand = sm.AddState("Stand", new Vector3(300, 0));
        stand.motion = Directional("Stand Move", "idle", s => "walk_" + s, s => Speed("walk", WalkSpeed));
        AnimatorState run = sm.AddState("Run", new Vector3(300, 120));
        // Sprinting forward and to the sides; backing off fast is the rifle jog.
        run.motion = Directional("Run Move", "idle", s => s.StartsWith("backward") ? "run_" + s : "sprint_" + s,
                                 s => s.StartsWith("backward") ? Speed("run", RunSpeed) : Speed("sprint_" + s, RunSpeed));
        AnimatorState crouch = sm.AddState("Crouch", new Vector3(600, 0));
        crouch.motion = Directional("Crouch Move", "idle_crouching", s => "walk_crouching_" + s, s => Speed("walk_crouching", CrouchSpeed));
        AnimatorState prone = sm.AddState("Prone", new Vector3(900, 0));
        prone.motion = ProneMove();
        AnimatorState down = sm.AddState("Crouch To Prone", new Vector3(750, 120));
        down.motion = MixamoImport.Clip("crouch_to_prone");
        down.speed = 2.4f;
        AnimatorState up = sm.AddState("Prone To Crouch", new Vector3(750, -120));
        up.motion = MixamoImport.Clip("prone_to_crouch");
        up.speed = 2.4f;
        AnimatorState jumpUp = sm.AddState("Jump Up", new Vector3(0, 0));
        jumpUp.motion = MixamoImport.Clip("jump_up");
        jumpUp.speed = 1.3f;
        AnimatorState jumpDown = sm.AddState("Jump Down", new Vector3(0, 120));
        jumpDown.motion = MixamoImport.Clip("jump_down");
        jumpDown.speed = 1.3f;
        foreach (AnimatorState s in new[] { stand, run, crouch }) s.iKOnFeet = true;
        sm.defaultState = stand;

        // Standing <-> running <-> crouching.
        To(stand, run, 0.2f, If("Running"), Is(0));
        To(run, stand, 0.2f, IfNot("Running"));
        foreach (AnimatorState from in new[] { stand, run }) {
            To(from, crouch, 0.25f, Is(1));
            To(from, down, 0.25f, Is(2));
            To(from, jumpUp, 0.1f, Trigger("IsJumping"));
        }
        To(crouch, stand, 0.25f, Is(0));
        To(crouch, down, 0.15f, Is(2));
        // Lying down and getting up go through the transition clips; changing your mind halfway turns straight back.
        ExitTo(down, prone, 0.9f, 0.15f);
        To(down, crouch, 0.25f, Is(1));
        To(down, stand, 0.3f, Is(0));
        To(prone, up, 0.15f, Not(2));
        ExitTo(up, crouch, 0.85f, 0.2f, Is(1));
        ExitTo(up, stand, 0.85f, 0.3f, Is(0));
        To(up, down, 0.2f, Is(2));
        // A jump: the take-off and the landing, then back to the feet.
        ExitTo(jumpUp, jumpDown, 0.85f, 0.15f);
        ExitTo(jumpDown, stand, 0.8f, 0.2f, IfNot("Running"));
        ExitTo(jumpDown, run, 0.8f, 0.2f, If("Running"));
        To(jumpDown, crouch, 0.2f, Is(1));

        // Death from wherever the body is; nothing leaves it (the body sinks and is replaced on respawn).
        Death(sm, "Death", "death_from_the_front", 0, new Vector3(300, 300));
        Death(sm, "Death Crouching", "death_crouching_headshot_front", 1, new Vector3(600, 300));
        Death(sm, "Death Prone", "prone_death", 2, new Vector3(900, 300));
        return sm;
    }

    private static AnimatorStateMachine AimLayer() {
        AnimatorStateMachine sm = Machine("Aim");
        AnimatorState none = sm.AddState("Empty", new Vector3(300, 0));
        AnimatorState stand = sm.AddState("Aim Standing", new Vector3(600, -80));
        stand.motion = MixamoImport.Clip("idle_aiming");
        AnimatorState crouch = sm.AddState("Aim Crouching", new Vector3(600, 80));
        crouch.motion = MixamoImport.Clip("idle_crouching_aiming");
        sm.defaultState = none;
        To(none, stand, 0.2f, If("Firing"), Is(0));
        To(none, crouch, 0.2f, If("Firing"), Is(1));
        To(stand, none, 0.25f, IfNot("Firing"));
        To(stand, none, 0.15f, Not(0));
        To(crouch, none, 0.25f, IfNot("Firing"));
        To(crouch, none, 0.15f, Not(1));
        return sm;
    }

    private static AnimatorStateMachine HurtLayer() {
        AnimatorStateMachine sm = Machine("Hurt");
        AnimatorState none = sm.AddState("Empty", new Vector3(300, 0));
        AnimatorState hurt = sm.AddState("Hurt", new Vector3(600, 0));
        hurt.motion = AssetDatabase.LoadAllAssetsAtPath(HurtClipPath).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
        hurt.speed = 3f;
        sm.defaultState = none;
        // Standing or crouched; a flinch would twist a body lying down, so there the trigger is just used up.
        AnimatorStateTransition t = sm.AddAnyStateTransition(hurt);
        Conditions(t, Trigger("IsHurt"), Less(2));
        t.duration = 0.05f;
        t.hasExitTime = false;
        t.canTransitionToSelf = true;
        AnimatorStateTransition skip = sm.AddAnyStateTransition(none);
        Conditions(skip, Trigger("IsHurt"), Is(2));
        skip.duration = 0f;
        skip.hasExitTime = false;
        skip.canTransitionToSelf = true;
        ExitTo(hurt, none, 0.8f, 0.15f);
        return sm;
    }

    // ------------------------------------------------------------------ pieces

    private static BlendTree Directional(string name, string idle, System.Func<string, string> clip, System.Func<string, float> speed) {
        BlendTree tree = Tree(name);
        tree.AddChild(MixamoImport.Clip(idle), Vector2.zero);
        foreach ((string suffix, Vector2 dir) in Directions) tree.AddChild(MixamoImport.Clip(clip(suffix)), dir);
        ChildMotion[] children = tree.children;
        for (int i = 1; i < children.Length; i++) children[i].timeScale = speed(Directions[i - 1].suffix);
        tree.children = children;
        return tree;
    }

    private static BlendTree ProneMove() {
        BlendTree tree = Tree("Prone Move");
        float forward = Speed("prone_forward", ProneSpeed), back = Speed("prone_backward", ProneSpeed);
        tree.AddChild(MixamoImport.Clip("prone_idle"), Vector2.zero);
        tree.AddChild(MixamoImport.Clip("prone_forward"), new Vector2(0f, 1f));
        tree.AddChild(MixamoImport.Clip("prone_backward"), new Vector2(0f, -1f));
        // No sideways crawl: shuffling left or right uses the forward crawl.
        tree.AddChild(MixamoImport.Clip("prone_forward"), new Vector2(1f, 0f));
        tree.AddChild(MixamoImport.Clip("prone_forward"), new Vector2(-1f, 0f));
        ChildMotion[] children = tree.children;
        children[1].timeScale = forward;
        children[2].timeScale = back;
        children[3].timeScale = forward;
        children[4].timeScale = forward;
        tree.children = children;
        return tree;
    }

    private static BlendTree Tree(string name) {
        BlendTree tree = new BlendTree {
            name = name, blendType = BlendTreeType.FreeformDirectional2D, blendParameter = "Horizontal", blendParameterY = "Vertical",
            useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(tree, ac);
        return tree;
    }

    /// <summary>Play rate for a clip that travels at <paramref name="clipSpeed"/> when the body moves at <paramref name="speed"/>.</summary>
    private static float Speed(string clip, float speed) {
        float native = ClipSpeed.TryGetValue(clip, out float v) ? v : ClipSpeed[clip.Split('_')[0]];
        return Mathf.Clamp(speed / native, 0.6f, 1.8f);
    }

    private static void Death(AnimatorStateMachine sm, string name, string clip, int stance, Vector3 at) {
        AnimatorState s = sm.AddState(name, at);
        s.motion = MixamoImport.Clip(clip);
        s.writeDefaultValues = true;
        AnimatorStateTransition t = sm.AddAnyStateTransition(s);
        Conditions(t, Trigger("IsDead"), Is(stance));
        t.duration = 0.15f;
        t.hasExitTime = false;
        t.canTransitionToSelf = false;
    }

    private static AnimatorStateMachine Machine(string name) {
        AnimatorStateMachine sm = new AnimatorStateMachine { name = name, hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(sm, ac);
        return sm;
    }

    private static AnimatorControllerLayer Layer(string name, AnimatorStateMachine sm, AvatarMask mask, float weight) {
        return new AnimatorControllerLayer {
            name = name, stateMachine = sm, avatarMask = mask, defaultWeight = weight,
            blendingMode = AnimatorLayerBlendingMode.Override, iKPass = true,
        };
    }

    private struct Cond {
        public AnimatorConditionMode Mode;
        public float Threshold;
        public string Parameter;
    }

    private static Cond Is(int stance) => new Cond { Mode = AnimatorConditionMode.Equals, Threshold = stance, Parameter = "Stance" };
    private static Cond Not(int stance) => new Cond { Mode = AnimatorConditionMode.NotEqual, Threshold = stance, Parameter = "Stance" };
    private static Cond Less(int stance) => new Cond { Mode = AnimatorConditionMode.Less, Threshold = stance, Parameter = "Stance" };
    private static Cond If(string p) => new Cond { Mode = AnimatorConditionMode.If, Parameter = p };
    private static Cond IfNot(string p) => new Cond { Mode = AnimatorConditionMode.IfNot, Parameter = p };
    private static Cond Trigger(string p) => new Cond { Mode = AnimatorConditionMode.If, Parameter = p };

    private static void Conditions(AnimatorStateTransition t, params Cond[] conds) {
        foreach (Cond c in conds) t.AddCondition(c.Mode, c.Threshold, c.Parameter);
    }

    private static void To(AnimatorState from, AnimatorState to, float duration, params Cond[] conds) {
        AnimatorStateTransition t = from.AddTransition(to);
        t.hasExitTime = false;
        t.duration = duration;
        Conditions(t, conds);
    }

    private static void ExitTo(AnimatorState from, AnimatorState to, float exitTime, float duration, params Cond[] conds) {
        AnimatorStateTransition t = from.AddTransition(to);
        t.hasExitTime = true;
        t.exitTime = exitTime;
        t.duration = duration;
        Conditions(t, conds);
    }

    /// <summary>Everything above the hips: the spine, head and both arms (hand IK included).</summary>
    private static AvatarMask UpperBodyMask() {
        AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(UpperBodyMaskPath);
        if (mask == null) {
            mask = new AvatarMask();
            AssetDatabase.CreateAsset(mask, UpperBodyMaskPath);
        }
        for (AvatarMaskBodyPart p = 0; p < AvatarMaskBodyPart.LastBodyPart; p++) {
            bool on = p == AvatarMaskBodyPart.Body || p == AvatarMaskBodyPart.Head || p == AvatarMaskBodyPart.LeftArm ||
                      p == AvatarMaskBodyPart.RightArm || p == AvatarMaskBodyPart.LeftFingers || p == AvatarMaskBodyPart.RightFingers ||
                      p == AvatarMaskBodyPart.LeftHandIK || p == AvatarMaskBodyPart.RightHandIK;
            mask.SetHumanoidBodyPartActive(p, on);
        }
        EditorUtility.SetDirty(mask);
        return mask;
    }

    /// <summary>Empties the controller (layers, parameters and every sub-asset) but keeps the asset and its GUID.</summary>
    private static void Clear() {
        ac.layers = new AnimatorControllerLayer[0];
        ac.parameters = new AnimatorControllerParameter[0];
        foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(ControllerPath)) {
            if (o != null && o != ac) AssetDatabase.RemoveObjectFromAsset(o);
        }
    }

}
