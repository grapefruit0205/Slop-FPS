using System;
using UnityEngine;

/// <summary>
/// The gun models one gun object (first- or third-person) can show, with the points that move along with them: the
/// muzzle (where the flash and tracer start), the hand IK targets and the optic rail. Filled in by the WeaponRig
/// editor tool; any gun without a model of its own falls back to the first entry, so the game works before the art.
/// </summary>
public class GunModels : MonoBehaviour {

    [Serializable]
    public class Gun {
        public WeaponId Id;
        public GameObject Model;
        public Transform Muzzle;
        public Transform Grip;
        public Transform Handguard;
        public Transform Rail;
        [Tooltip("Top of the front sight: iron sights look along the bore at this height.")]
        public Transform IronSight;
        public AudioClip Shot;
        public AudioClip Reload;
    }

    [Serializable]
    public class Optic {
        public OpticId Id;
        public GameObject Model;
        public Transform Mount;
        public Transform Eye;
    }

    [SerializeField]
    private Gun[] guns = new Gun[0];
    [SerializeField]
    private Optic[] optics = new Optic[0];
    [Tooltip("Moves to the shown gun's muzzle; the muzzle flash and tracer hang off it.")]
    [SerializeField]
    private Transform barrelEnd;
    [Tooltip("Third person only: hand IK targets.")]
    [SerializeField]
    private Transform rightHand;
    [SerializeField]
    private Transform leftHand;

    private Vector3 rightHandRest, leftHandRest;
    private bool rested;

    public Gun Shown { get; private set; }
    public OpticId ShownOptic { get; private set; }
    /// <summary>Centre of the shown optic's lens, or null for iron sights.</summary>
    public Transform Eye { get; private set; }
    public Transform BarrelEnd => barrelEnd;

    public void Show(WeaponId id, OpticId optic) {
        if (!rested) {
            rested = true;
            if (rightHand != null) rightHandRest = rightHand.localPosition;
            if (leftHand != null) leftHandRest = leftHand.localPosition;
        }
        Gun pick = null;
        foreach (Gun g in guns) if (g.Id == id && g.Model != null) pick = g;
        if (pick == null && guns.Length > 0) pick = guns[0];
        foreach (Gun g in guns) if (g.Model != null) g.Model.SetActive(g == pick);
        Shown = pick;
        if (pick == null) return;

        if (barrelEnd != null && pick.Muzzle != null) barrelEnd.position = pick.Muzzle.position;
        if (rightHand != null) rightHand.localPosition = pick.Grip != null ? rightHand.parent.InverseTransformPoint(pick.Grip.position) : rightHandRest;
        if (leftHand != null) {
            Transform support = pick.Handguard != null ? pick.Handguard : pick.Grip;
            leftHand.localPosition = support != null ? leftHand.parent.InverseTransformPoint(support.position) : leftHandRest;
        }

        // Optics sit on the gun's rail; a gun without one (or iron sights) shows none.
        ShownOptic = OpticId.Iron;
        Eye = null;
        foreach (Optic o in optics) {
            bool on = o.Id == optic && o.Model != null && pick.Rail != null;
            if (o.Model != null) o.Model.SetActive(on);
            if (!on) continue;
            o.Model.transform.position += pick.Rail.position - o.Mount.position;
            ShownOptic = optic;
            Eye = o.Eye;
        }
    }

    public void SetVisible(bool visible) {
        if (Shown == null || Shown.Model == null) return;
        foreach (Renderer r in Shown.Model.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
        foreach (Optic o in optics) {
            if (o.Model == null || !o.Model.activeSelf) continue;
            foreach (Renderer r in o.Model.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
        }
    }

}
