using System.Collections;
using UnityEngine;
using SlopFPS.Input;
using SlopFPS.Input;

/// <summary>
/// First-person weapons: the primary and the pistol (1/2 or the mouse wheel), aiming down the sights (right mouse),
/// ammo, recoil and client-side hit detection. Only active on the owner's machine. Every shot is reported to the
/// server, which checks it against the same <see cref="WeaponSpec"/> before anything takes damage.
/// Grenades (4/5/6) are <see cref="GrenadeHands"/>: while one is out the gun is put away and does nothing.
/// </summary>
public class FpsGun : MonoBehaviour {

    [SerializeField]
    private AudioClip reloadClip;
    [SerializeField]
    private AudioClip dryFireClip;
    [SerializeField]
    private TpsGun tpsGun;
    [SerializeField]
    private ParticleSystem gunParticles;
    [SerializeField]
    private LineRenderer gunLine;
    [SerializeField]
    private Animator animator;
    [SerializeField]
    private Camera raycastCamera;

    private const float AimSpeed = 6f;           // full aim in 1/6 s
    private const float ScopeAt = 0.9f;          // aim progress where a scope's view takes over
    // How far in front of the eye the sight sits while aiming, in camera space (metres).
    private const float RedDotDistance = 0.17f;
    private const float ScopeDistance = 0.12f;
    private const float IronDistance = 0.6f;          // to the front sight
    private const float PistolIronDistance = 0.45f;
    private const float ClickBuffer = 0.15f;     // a semi-auto click this early still fires when the gun is ready

    private class Slot {
        public WeaponSpec Spec;
        public OpticId Optic;
        public int InMagazine;
        public int Reserve;
    }

    /// <summary>Automated tests (DevBot) hold the trigger through this.</summary>
    public static bool DevTrigger;
    /// <summary>Screenshot mode holds the right mouse button through this.</summary>
    public static bool DevAim;

    private readonly Slot[] slots = new Slot[2];
    private int current;
    private float timer;
    private float readyAt;
    private float clickedAt = -1f;
    private bool triggerHeld;
    private bool reloading;
    private Coroutine reloadRoutine;
    private float aim;
    private bool scoped;
    private float baseFov;
    private bool fovTouched;
    private WeaponMotion motion;
    private AudioSource audioSource;
    private AudioClip currentReloadClip;
    private PlayerHealth shooter;
    private Loadout loadout;
    private GunModels models;
    private GrenadeHands hands;

    public WeaponSpec Spec => slots[current].Spec;
    public OpticId Optic => slots[current].Optic;
    public int InMagazine => slots[current].InMagazine;
    public int Reserve => slots[current].Reserve;
    public bool IsReloading => reloading;
    /// <summary>0 at the hip, 1 looking down the sights.</summary>
    public float Aim => aim;
    /// <summary>Looking down the sights or shot in the last half second: others see the rifle shouldered.</summary>
    public bool Shouldered => aim > 0.3f || (timer < 0.5f && !reloading);

    void Start() {
        motion = WeaponMotion.Attach(animator != null ? animator.transform : transform);
        shooter = GetComponentInParent<PlayerHealth>();
        loadout = GetComponentInParent<Loadout>();
        models = GetComponent<GunModels>();
        hands = gameObject.AddComponent<GrenadeHands>();
        hands.Init(this, raycastCamera);
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
        baseFov = raycastCamera != null ? raycastCamera.fieldOfView : 60f;
        for (int i = 0; i < slots.Length; i++) {
            WeaponId id = loadout != null ? loadout.WeaponIn(i) : (i == 0 ? Weapons.Primaries[0] : Weapons.Secondary);
            WeaponSpec spec = Weapons.Get(id);
            slots[i] = new Slot {
                Spec = spec, Optic = loadout != null ? loadout.OpticIn(i) : OpticId.Iron,
                InMagazine = spec.Magazine, Reserve = spec.Reserve,
            };
        }
        Equip(Loadout.PrimarySlot, false);
        L10n.Changed += RefreshWeaponLabel;
    }

    private void RefreshWeaponLabel() {
        if (GameUI.Instance != null && slots[current] != null)
            GameUI.Instance.Hud.SetWeapon(Spec, Optic, slots[1 - current].Spec);
    }

    void OnDestroy() { L10n.Changed -= RefreshWeaponLabel; }

    void OnDisable() {
        // Dying (or leaving) mid-aim must not leave the zoom, the slow mouse or the scope behind.
        aim = 0f;
        if (motion != null) motion.Aim = 0f;
        scoped = false;
        FpsInputGate.SensitivityScale = 1f;
        if (fovTouched && raycastCamera != null) raycastCamera.fieldOfView = BaseFov;
        fovTouched = false;
        if (GameUI.Instance != null) GameUI.Instance.Hud.SetAim(0f, OpticId.Iron, false);
    }

    void Update() {
        timer += Time.deltaTime;
        if (shooter != null && shooter.IsDead) {
            aim = 0f;
            animator.SetBool("Firing", false);
            return;
        }
        bool blocked = FpsInputGate.Blocked;
        if (hands != null && hands.InHand) {
            // A grenade is out: the gun is away. 1 / 2 / the wheel bring a gun back (not with the pin out).
            aim = 0f;
            animator.SetBool("Firing", false);
            if (!blocked && !hands.Busy) {
                if (Input.GetKeyDown(KeyCode.Alpha1)) ReturnFromGrenade(Loadout.PrimarySlot);
                else if (Input.GetKeyDown(KeyCode.Alpha2)) ReturnFromGrenade(Loadout.SecondarySlot);
                else if (Input.mouseScrollDelta.y != 0f) ReturnFromGrenade(current);
            }
            return;
        }
        if (!blocked) {
            if (Input.GetKeyDown(KeyCode.Alpha1)) Switch(Loadout.PrimarySlot);
            else if (Input.GetKeyDown(KeyCode.Alpha2)) Switch(Loadout.SecondarySlot);
            else if (Input.mouseScrollDelta.y != 0f) Switch(1 - current);
            if (Input.GetKeyDown(KeyCode.R)) StartReload();
        }

        Slot s = slots[current];
        // Getting down onto the ground (or back up) takes both hands.
        PlayerStance pose = PlayerStance.Local;
        bool ready = Time.time >= readyAt && !(pose != null && pose.Busy);
        bool wantAim = ready && !reloading && (DevAim || (!blocked && Input.GetMouseButton(1)));
        aim = Mathf.MoveTowards(aim, wantAim ? 1f : 0f, Time.deltaTime * AimSpeed);

        // Frozen (elimination round start): the trigger does nothing yet.
        bool down = !FpsInputGate.Frozen && (DevTrigger || (!blocked && CrossPlatformInputManager.GetButton("Fire1")));
        if (down && !triggerHeld) clickedAt = Time.time;
        triggerHeld = down;
        // Automatics fire while the trigger is held; the rest once per click.
        bool wantShot = s.Spec.Automatic || DevTrigger ? down : clickedAt >= 0f && Time.time - clickedAt <= ClickBuffer;
        bool canFire = ready && !reloading && timer >= s.Spec.Interval && Time.timeScale != 0;
        if (wantShot && canFire) {
            clickedAt = -1f;
            if (s.InMagazine > 0) {
                Shoot();
            } else {
                timer = 0f;
                PlayLocal(dryFireClip);
                if (dryFireClip == null) UISounds.DryFire();
                StartReload();
            }
        }
        animator.SetBool("Firing", s.Spec.Automatic && down && ready && !reloading && s.InMagazine > 0);
    }

    void LateUpdate() {
        ApplyAim();
    }

    // ------------------------------------------------------------------ weapons in hand

    private void Switch(int to) {
        if (to == current || slots[to] == null) return;
        CancelReload();
        current = to;
        if (loadout != null) loadout.ReportSwitch(to, AntiCheat.ClientClock);
        Equip(to, true);
    }

    private void Equip(int index, bool draw) {
        current = index;
        Slot s = slots[index];
        readyAt = Time.time + (draw ? s.Spec.DrawTime : 0f);
        timer = s.Spec.Interval;
        aim = 0f;
        if (models != null) {
            models.Show(s.Spec.Id, s.Optic);
            // Back from a grenade or a scope, the gun shows again.
            models.SetVisible(true);
        }
        scoped = false;
        if (tpsGun != null) tpsGun.Show(s.Spec.Id, s.Optic);
        AudioClip own = models != null && models.Shown != null ? models.Shown.Reload : null;
        currentReloadClip = own != null ? own : reloadClip;
        AimThroughSights();
        if (draw) motion.PlayDraw(s.Spec.DrawTime);
        if (GameUI.Instance != null) {
            WeaponSpec other = slots[1 - index] != null ? slots[1 - index].Spec : null;
            GameUI.Instance.Hud.SetWeapon(s.Spec, s.Optic, other);
        }
        PushAmmo();
    }

    /// <summary>A grenade comes out: stop reloading, lower the sights and hide the gun.</summary>
    public void HolsterForGrenade() {
        CancelReload();
        aim = 0f;
        triggerHeld = false;
        clickedAt = -1f;
        if (models != null) models.SetVisible(false);
    }

    /// <summary>After a throw: the gun that was in hand comes back up.</summary>
    public void DrawAfterGrenade() {
        ReturnFromGrenade(current);
    }

    private void ReturnFromGrenade(int to) {
        if (hands != null) hands.PutAway();
        if (to != current && slots[to] != null) Switch(to);
        else Equip(current, true);
    }

    /// <summary>
    /// Tells the weapon motion what to look through: the optic's lens a comfortable distance from the eye, or the top
    /// of the front sight further out for iron sights.
    /// </summary>
    private void AimThroughSights() {
        Transform sight = models != null ? models.Eye : null;
        float distance = Weapons.IsScope(slots[current].Optic) ? ScopeDistance : RedDotDistance;
        if (sight == null) {
            distance = slots[current].Spec.Id == WeaponId.Pistol ? PistolIronDistance : IronDistance;
            GunModels.Gun shown = models != null ? models.Shown : null;
            sight = shown != null && shown.IronSight != null ? shown.IronSight
                  : models != null && models.BarrelEnd != null ? models.BarrelEnd : gunParticles.transform;
        }
        motion.SetSight(sight, distance);
    }

    private float BaseFov => FpsInputGate.FieldOfView > 0f ? FpsInputGate.FieldOfView : baseFov;

    private void ApplyAim() {
        Slot s = slots[current];
        float eased = Mathf.SmoothStep(0f, 1f, aim);
        motion.Aim = eased;
        bool scope = Weapons.IsScope(s.Optic);
        bool nowScoped = scope && aim >= ScopeAt;
        if (nowScoped != scoped) {
            scoped = nowScoped;
            if (models != null) models.SetVisible(!scoped);
        }
        if (raycastCamera != null) {
            float full = Weapons.AimFov(s.Optic, BaseFov);
            // A scope only magnifies once the eye is behind it; until then the view barely changes.
            float fov = scope ? (scoped ? full : Mathf.Lerp(BaseFov, BaseFov / 1.15f, eased)) : Mathf.Lerp(BaseFov, full, eased);
            if (aim > 0f) {
                raycastCamera.fieldOfView = fov;
                fovTouched = true;
            } else if (fovTouched) {
                raycastCamera.fieldOfView = BaseFov;
                fovTouched = false;
            }
            FpsInputGate.SensitivityScale = aim > 0f ? fov / BaseFov : 1f;
        }
        if (GameUI.Instance != null) GameUI.Instance.Hud.SetAim(aim, s.Optic, scoped, models != null && models.Shown != null && models.Shown.IronSight != null);
    }

    // ------------------------------------------------------------------ ammo

    /// <summary>Dev screenshot mode: put the gun into a given magazine state.</summary>
    public void SetMagazineForTesting(int rounds) {
        Slot s = slots[current];
        s.InMagazine = Mathf.Clamp(rounds, 0, s.Spec.Magazine);
        PushAmmo();
    }

    /// <summary>Dev screenshot mode: hold <paramref name="gun"/> (as the primary, unless it is the pistol) with an optic.</summary>
    public void ShowForTesting(WeaponId gun, OpticId optic) {
        WeaponSpec spec = Weapons.Get(gun);
        int slot = Weapons.IsPrimary(gun) ? Loadout.PrimarySlot : Loadout.SecondarySlot;
        slots[slot] = new Slot { Spec = spec, Optic = optic, InMagazine = spec.Magazine, Reserve = spec.Reserve };
        CancelReload();
        Equip(slot, false);
    }

    private void StartReload() {
        Slot s = slots[current];
        if (reloading || s.InMagazine >= s.Spec.Magazine || s.Reserve <= 0 || Time.time < readyAt) return;
        // The server keeps its own magazine count and refuses shots it has not seen a reload for.
        if (loadout != null) loadout.ReportReload(AntiCheat.ClientClock);
        reloadRoutine = StartCoroutine(ReloadCoroutine(s));
    }

    private IEnumerator ReloadCoroutine(Slot s) {
        reloading = true;
        PushAmmo();
        PlayLocal(currentReloadClip);
        motion.PlayReload(s.Spec.ReloadTime);
        yield return new WaitForSeconds(s.Spec.ReloadTime);
        int take = Mathf.Min(s.Spec.Magazine - s.InMagazine, s.Reserve);
        s.InMagazine += take;
        s.Reserve -= take;
        reloading = false;
        reloadRoutine = null;
        PushAmmo();
    }

    private void CancelReload() {
        if (!reloading) return;
        if (reloadRoutine != null) StopCoroutine(reloadRoutine);
        reloadRoutine = null;
        reloading = false;
        audioSource.Stop();
        motion.CancelReload();
    }

    // ------------------------------------------------------------------ firing

    /// <summary>
    /// Fire once: every pellet is traced here, and the whole shot is reported to the server, which checks it and
    /// relays it to everyone else's third-person gun.
    /// </summary>
    void Shoot() {
        Slot s = slots[current];
        WeaponSpec spec = s.Spec;
        timer = 0.0f;
        s.InMagazine--;
        PushAmmo();
        gunLine.enabled = true;
        StartCoroutine(DisableShootingEffect());
        if (gunParticles.isPlaying) {
            gunParticles.Stop();
        }
        gunParticles.Play();
        float steady = PlayerStance.Local != null ? PlayerStance.Local.AccuracyScale : 1f;
        motion.Kick(spec.Kick * (aim > 0.5f ? 0.6f : 1f) * steady);
        if (GameUI.Instance != null) GameUI.Instance.Hud.Shot();
        tpsGun.PlayLocalShot();

        double now = AntiCheat.ClientClock;
        Ray center = raycastCamera.ScreenPointToRay(new Vector3(Screen.width / 2, Screen.height / 2, 0f));
        float spread = Mathf.Lerp(spec.HipSpread, spec.AimSpread, aim) * steady;
        int mask = LayerMask.GetMask("Shootable");
        Pellet[] pellets = new Pellet[spec.Pellets];
        bool hitPlayer = false, headshotAny = false;
        for (int i = 0; i < pellets.Length; i++) {
            Vector3 dir = Scatter(center.direction, spread);
            Pellet p = new Pellet { Direction = dir };
            if (Physics.Raycast(center.origin, dir, out RaycastHit hit, spec.Range, mask)) {
                string surface = hit.transform.gameObject.tag;
                if (surface == "Player") {
                    surface = "Flesh";
                    PlayerHealth victim = hit.collider.GetComponentInParent<PlayerHealth>();
                    if (victim != null && victim != shooter && !victim.IsDead) {
                        p.Target = victim;
                        p.Headshot = IsHeadshot(victim, new Ray(center.origin, dir));
                        hitPlayer |= Teams.IsHostileToLocal(victim);   // teammates take no damage, so no hitmarker
                        headshotAny |= p.Headshot;
                    }
                }
                p.Hit = true;
                p.Point = hit.point;
                p.Normal = hit.normal;
                p.Surface = surface;
                Impacts.Spawn(surface, hit.point, hit.normal);
            }
            pellets[i] = p;
        }
        if (hitPlayer && GameUI.Instance != null) GameUI.Instance.Hud.Hitmarker(headshotAny, false);
        if (shooter != null) shooter.ReportShot(now, spec.Id, center.origin, center.direction, pellets);
    }

    /// <summary>A direction within <paramref name="degrees"/> of <paramref name="dir"/>, evenly over the cone's disc.</summary>
    private static Vector3 Scatter(Vector3 dir, float degrees) {
        if (degrees <= 0f) return dir;
        Vector2 r = Random.insideUnitCircle * Mathf.Tan(degrees * Mathf.Deg2Rad);
        return (Quaternion.LookRotation(dir) * new Vector3(r.x, r.y, 1f)).normalized;
    }

    /// <summary>
    /// The player hitbox is a single collider, so judge headshots by how close the
    /// shot line passes to the victim's head bone.
    /// </summary>
    private static bool IsHeadshot(PlayerHealth target, Ray ray) {
        Animator a = target.GetComponentInChildren<Animator>();
        if (a == null || !a.isHuman) return false;
        Transform head = a.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) return false;
        Vector3 center = head.position + Vector3.up * 0.09f;
        Vector3 toHead = center - ray.origin;
        float along = Vector3.Dot(toHead, ray.direction);
        if (along < 0f) return false;
        float missDistance = (toHead - ray.direction * along).magnitude;
        return missDistance < 0.16f;
    }

    private void PushAmmo() {
        Slot s = slots[current];
        if (GameUI.Instance != null) GameUI.Instance.Hud.SetAmmo(s.InMagazine, s.Spec.Magazine, s.Reserve, reloading);
    }

    private void PlayLocal(AudioClip clip) {
        if (clip != null) audioSource.PlayOneShot(clip);
    }

    /// <summary>
    /// Coroutine function to disable shooting effect.
    /// <summary>
    public IEnumerator DisableShootingEffect() {
        yield return new WaitForSeconds(0.05f);
        gunLine.enabled = false;
    }

}
