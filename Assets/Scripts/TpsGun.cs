using FishNet.Object;
using UnityEngine;

/// <summary>
/// The gun other players see. For the owner it follows the first-person gun's aim (its transform is replicated by a
/// client-authoritative NetworkTransform on this object); shots are relayed so others hear and see them, and it shows
/// whichever gun the avatar's <see cref="Loadout"/> has in hand, or the grenade instead of it while one is out.
/// </summary>
public class TpsGun : NetworkBehaviour {

    [Tooltip("The scaling number for changing the local postion Y of TpsGun when aiming angle changes.")]
    [SerializeField]
    private float localPositionYScale = 0.007f;
    [SerializeField]
    private ParticleSystem gunParticles;
    [SerializeField]
    private AudioSource gunAudio;
    [SerializeField]
    private FpsGun fpsGun;
    [SerializeField]
    private Animator animator;

    private float defaultLocalPositionY;
    private Loadout loadout;
    private GunModels models;
    private AudioClip defaultShot;
    private GameObject grenade;
    private GrenadeId grenadeShown;

    void Awake() {
        defaultLocalPositionY = transform.localPosition.y;
        loadout = GetComponentInParent<Loadout>();
        models = GetComponent<GunModels>();
        defaultShot = gunAudio.clip;
        if (loadout != null) {
            loadout.Changed += ShowLoadout;
            loadout.GrenadesChanged += ShowLoadout;
        }
    }

    public override void OnStartClient() {
        ShowLoadout();
    }

    void OnDestroy() {
        if (loadout != null) {
            loadout.Changed -= ShowLoadout;
            loadout.GrenadesChanged -= ShowLoadout;
        }
    }

    /// <summary>Draw the gun in hand. The owner calls it too, since its own switches show before the server echoes them.</summary>
    public void Show(WeaponId id, OpticId optic) {
        if (models != null) models.Show(id, optic);
        AudioClip shot = models != null && models.Shown != null ? models.Shown.Shot : null;
        gunAudio.clip = shot != null ? shot : defaultShot;
    }

    private void ShowLoadout() {
        if (loadout == null || IsOwner) return;
        Show(loadout.InHand, loadout.OpticIn(loadout.Slot));
        ShowGrenade(loadout.Held);
    }

    /// <summary>Others see the grenade in the right hand (where the gun's grip was) while it is out, and no gun.</summary>
    private void ShowGrenade(GrenadeId id) {
        if (models != null) models.SetVisible(id == GrenadeId.None);
        if (id == grenadeShown) return;
        grenadeShown = id;
        if (grenade != null) Destroy(grenade);
        grenade = null;
        if (id == GrenadeId.None) return;
        grenade = GrenadeFx.Model(id, gameObject.layer);
        Transform t = grenade.transform;
        // In the right hand, riding along with the hand bone.
        Animator body = animator != null && animator.isHuman ? animator : null;
        if (body == null && loadout != null) {
            foreach (Animator a in loadout.GetComponentsInChildren<Animator>()) if (a.isHuman) body = a;
        }
        Transform hand = body != null ? body.GetBoneTransform(HumanBodyBones.RightHand) : null;
        Transform finger = body != null ? body.GetBoneTransform(HumanBodyBones.RightMiddleProximal) : null;
        Transform grip = models != null && models.Shown != null ? models.Shown.Grip : null;
        Transform parent = hand != null ? hand : transform;
        t.SetParent(parent, false);
        t.localScale = Vector3.one * Grenades.M(1f) / Mathf.Max(0.0001f, parent.lossyScale.x);
        // Held out at the fingertips, a little in front of the hand, so the glove doesn't swallow it from the front.
        if (hand != null) t.position = (finger != null ? Vector3.Lerp(hand.position, finger.position, 1.1f) : hand.position) + transform.forward * Grenades.M(0.05f);
        else t.position = grip != null ? grip.position : transform.position;
        t.rotation = transform.rotation;
    }

    void Update() {
        if (IsOwner) {
            transform.rotation = fpsGun.transform.rotation;
        }
    }

    void LateUpdate() {
        if (!IsOwner) return;
        float deltaEulerAngle = transform.eulerAngles.x > 180 ? 360 - transform.eulerAngles.x : -transform.eulerAngles.x;
        transform.localPosition = new Vector3(
            transform.localPosition.x,
            defaultLocalPositionY + deltaEulerAngle * localPositionYScale,
            transform.localPosition.z
        );
    }

    /// <summary>Owner: the shot is heard straight away; everyone else hears it once the server accepts it.</summary>
    public void PlayLocalShot() {
        gunAudio.Play();
    }

    /// <summary>Server: relay an accepted shot (with the impacts of the bullets that hit something) to the other players.</summary>
    [Server]
    public void RelayShot(Impact[] impacts) {
        RpcShoot(impacts);
    }

    [ObserversRpc(ExcludeOwner = true)]
    private void RpcShoot(Impact[] impacts) {
        gunAudio.Play();
        if (gunParticles.isPlaying) {
            gunParticles.Stop();
        }
        gunParticles.Play();
        if (impacts == null) return;
        foreach (Impact i in impacts) Impacts.Spawn(i.Surface, i.Point, i.Normal);
    }

}
