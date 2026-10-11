using System;
using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using SlopFPS.Input;

/// <summary>
/// Health, death and shot validation. The shooter's machine detects hits and reports every shot; the server checks
/// each one (ammo, fire rate, where it was fired from, line of sight, where the target was) before applying damage,
/// owns the health value and decides kills.
/// </summary>
[RequireComponent(typeof(FirstPersonController))]
[RequireComponent(typeof(Rigidbody))]
public class PlayerHealth : NetworkBehaviour {

    /// <summary>Server side: an avatar just died (GameServer schedules the respawn).</summary>
    public static event Action<PlayerHealth> ServerDied;

    [SerializeField]
    private int startingHealth = 100;
    [SerializeField]
    private float sinkSpeed = 0.12f;
    [SerializeField]
    private float sinkTime = 2.5f;
    [SerializeField]
    private float respawnTime = 8.0f;
    [SerializeField]
    private AudioClip deathClip;
    [SerializeField]
    private AudioClip hurtClip;
    [SerializeField]
    private AudioSource playerAudio;
    [SerializeField]
    private NameTag nameTag;
    [SerializeField]
    private Animator animator;

    private readonly SyncVar<int> health = new SyncVar<int>(100);
    private readonly SyncVar<bool> dead = new SyncVar<bool>();

    private FirstPersonController fpController;
    private IKControl ikControl;
    private TpsGun tpsGun;
    private MovementGuard guard;
    private Loadout loadout;
    private bool isSinking;
    
    public int CurrentHealth => health.Value;
    public bool IsDead => dead.Value;
    public float RespawnTime => respawnTime;

    void Awake() {
        fpController = GetComponent<FirstPersonController>();
        ikControl = GetComponentInChildren<IKControl>();
        loadout = GetComponent<Loadout>();
        tpsGun = GetComponentInChildren<TpsGun>(true);
        guard = GetComponent<MovementGuard>();
        health.OnChange += OnHealthChanged;
    }

    public override void OnStartServer() {
        health.Value = startingHealth;
        dead.Value = false;
    }

    public override void OnStartClient() {
        if (!IsOwner) return;
        gameObject.layer = LayerMask.NameToLayer("FPSPlayer");
        if (GameUI.Instance != null) GameUI.Instance.Hud.SetHealth(health.Value, startingHealth);
    }

    private void OnHealthChanged(int prev, int next, bool asServer) {
        if (asServer || !IsOwner) return;
        if (GameUI.Instance != null) GameUI.Instance.Hud.SetHealth(next, startingHealth);
    }

    void Update() {
        if (isSinking) {
            transform.Translate(Vector3.down * sinkSpeed * Time.deltaTime);
        }
    }

    /// <summary>
    /// Shooter's machine: this avatar fired <paramref name="weapon"/> once. <paramref name="pellets"/> holds one entry per
    /// bullet (eight for the shotgun) with what it hit, if anything.
    /// </summary>
    /// <param name="time">The shooter's clock (<see cref="AntiCheat.ClientClock"/>) when the shot was fired.</param>
    public void ReportShot(double time, WeaponId weapon, Vector3 origin, Vector3 aim, Pellet[] pellets) {
        if (IsOwner) ServerShot(time, weapon, origin, aim, pellets);
    }

    [ServerRpc]
    private void ServerShot(double time, WeaponId weapon, Vector3 origin, Vector3 aim, Pellet[] pellets) {
        if (dead.Value || loadout == null) return;
        string why = AntiCheat.CheckClock(Owner, time);
        if (why != null) {
            AntiCheat.Strike(Owner, "weapon: " + why);
            return;
        }
        // Ammo and fire rate first, so the server's magazine stays in step with the client's.
        why = loadout.TryFire(weapon, time);
        if (why != null) {
            AntiCheat.Strike(Owner, "weapon: " + why);
            return;
        }
        WeaponSpec spec = Weapons.Get(weapon);
        if (!Unit(ref aim) || pellets == null || pellets.Length > spec.Pellets) {
            AntiCheat.Strike(Owner, "shot: malformed report", 2);
            return;
        }
        float window = LagWindow();
        // Fired from where the body is (or very recently was)?
        if (guard != null && !guard.WasNear(origin, window, OriginSlack, out _, out float off)) {
            AntiCheat.Strike(Owner, "shot: fired " + off.ToString("F1") + " m away from the body", 2);
            return;
        }
        // Shooting behind your own back is what silent-aim cheats do. Fast flicks are fine: the server's view of the
        // body lags a little, so only shots far outside any recent facing count.
        if (!FacedRecently(aim, window)) {
            AntiCheat.Strike(Owner, "shot: aimed away from where the player was looking");
            return;
        }
        float cone = spec.MaxSpread * 1.5f + 0.5f;
        List<Impact> impacts = new List<Impact>(pellets.Length);
        for (int i = 0; i < pellets.Length; i++) {
            Pellet p = pellets[i];
            if (!Unit(ref p.Direction) || Vector3.Angle(p.Direction, aim) > cone) {
                AntiCheat.Strike(Owner, "shot: bullet outside the weapon's spread", 2);
                return;
            }
            if (!p.Hit) continue;
            Vector3 toPoint = p.Point - origin;
            float along = Vector3.Dot(toPoint, p.Direction);
            if (along < 0f || (toPoint - p.Direction * along).magnitude > 0.3f) {
                AntiCheat.Strike(Owner, "shot: impact not on the line of fire", 2);
                return;
            }
            if (along > spec.Range + 1f) {
                AntiCheat.Strike(Owner, "shot: hit at " + along.ToString("F0") + " m, beyond the " + spec.Name + "'s range", 2);
                return;
            }
            if (WorldBetween(origin, p.Point)) {
                AntiCheat.Strike(Owner, "shot: through solid geometry", 2);
                return;
            }
            pellets[i] = p;
            impacts.Add(new Impact { Point = p.Point, Normal = p.Normal, Surface = p.Surface });
        }
        if (tpsGun != null) tpsGun.RelayShot(impacts.ToArray());
        if (MatchManager.Instance != null && !MatchManager.Instance.AllowsDamage) return;

        // Damage, summed per target so a shotgun blast is one hit (and one kill) rather than eight.
        Dictionary<PlayerHealth, int> damage = null;
        HashSet<PlayerHealth> headshots = null;
        foreach (Pellet p in pellets) {
            PlayerHealth target = p.Target;
            if (!p.Hit || target == null || target == this || target.dead.Value) continue;
            bool headshot = p.Headshot;
            MovementGuard targetGuard = target.guard;
            if (targetGuard != null) {
                if (!targetGuard.WasNear(p.Point, window, HitSlack, out bool atHead, out float miss)) {
                    AntiCheat.Strike(Owner, "shot: hit a player who was " + miss.ToString("F1") + " m from the impact (target at " +
                                            target.transform.position.ToString("F1") + ", impact " + p.Point.ToString("F1") +
                                            ", window " + window.ToString("F2") + " s)");
                    continue;
                }
                // Only the head end of the body (wherever the stance put it) counts as a headshot, whatever the client says.
                if (headshot && !atHead) headshot = false;
            }
            if (damage == null) {
                damage = new Dictionary<PlayerHealth, int>();
                headshots = new HashSet<PlayerHealth>();
            }
            damage.TryGetValue(target, out int sum);
            damage[target] = sum + spec.DamageAt(Vector3.Distance(origin, p.Point), headshot);
            if (headshot) headshots.Add(target);
        }
        if (damage == null) return;
        foreach (KeyValuePair<PlayerHealth, int> kv in damage) {
            kv.Key.ApplyDamage(kv.Value, this, headshots.Contains(kv.Key), origin, weapon);
        }
    }

    private static bool Unit(ref Vector3 v) {
        float sq = v.sqrMagnitude;
        if (float.IsNaN(sq) || sq < 0.5f || sq > 1.5f) return false;
        v.Normalize();
        return true;
    }

    public const float OriginSlack = 2.5f;
    private const float HitSlack = 0.9f;
    private const float MaxAimError = 100f;

    /// <summary>How far back to look in position history: the shooter's latency plus interpolation, capped.</summary>
    public float LagWindow() {
        NetPlayer p = NetPlayer.ForClient(OwnerId);
        float ping = p != null ? p.Ping.Value / 1000f : 0.2f;
        return Mathf.Clamp(ping + 0.35f, 0.35f, 1.0f);
    }

    public bool FacedRecently(Vector3 direction, float window) {
        Vector3 flat = new Vector3(direction.x, 0f, direction.z);
        if (flat.sqrMagnitude < 0.01f) return true;  // straight up or down
        float now = AntiCheat.Now;
        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (Vector3.Angle(flat, forward) <= MaxAimError) return true;
        foreach (FacingSample f in facing) {
            if (now - f.Time <= window && Vector3.Angle(flat, f.Forward) <= MaxAimError) return true;
        }
        return false;
    }

    /// <summary>Is anything a bullet would stop at between the muzzle and the reported impact? Players excluded.</summary>
    private static bool WorldBetween(Vector3 from, Vector3 to) {
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.3f) return false;
        foreach (RaycastHit h in Physics.RaycastAll(from, d / len, len - 0.25f, LayerMask.GetMask("Shootable"),
                                                   QueryTriggerInteraction.Ignore)) {
            if (h.collider.GetComponentInParent<PlayerHealth>() == null) return true;
        }
        return false;
    }

    private struct FacingSample {
        public float Time;
        public Vector3 Forward;
    }

    private readonly Queue<FacingSample> facing = new Queue<FacingSample>();

    void LateUpdate() {
        if (!IsServerInitialized) return;
        Vector3 f = transform.forward;
        f.y = 0f;
        float now = AntiCheat.Now;
        facing.Enqueue(new FacingSample { Time = now, Forward = f });
        while (facing.Count > 0 && now - facing.Peek().Time > 1.0f) facing.Dequeue();
    }

    [Server]
    private void ApplyDamage(int amount, PlayerHealth attacker, bool headshot, Vector3 from, WeaponId weapon) {
        if (!Teams.AllowDamage(attacker, this)) return;
        Hurt(amount, attacker.OwnerId, headshot, from, Weapons.Get(weapon).Name);
    }

    /// <summary>
    /// Server: a grenade blast from <paramref name="throwerId"/>'s grenade. Unlike a bullet it can hurt its own
    /// thrower; teammates are spared as usual. The kill goes to the thrower's connection, even when the body that threw
    /// it has since died. Returns what happened, for the log.
    /// </summary>
    [Server]
    public string ApplyBlast(int amount, int throwerId, Vector3 from, string weaponName) {
        bool self = throwerId == OwnerId;
        if (!self && !Teams.CanDamage(throwerId, OwnerId)) {
            Debug.Log("[Match] Friendly fire blocked: " + weaponName + " of client " + throwerId + " -> " + OwnerId + " (team " + Teams.Of(OwnerId) + ")");
            return "teammate, friendly fire blocked";
        }
        Hurt(amount, throwerId, false, from, weaponName);
        return (self ? "own grenade " : "") + "-" + amount + (dead.Value ? ", killed" : " (" + health.Value + " left)");
    }

    /// <summary>Server: take <paramref name="amount"/> from <paramref name="attackerId"/>'s weapon; at zero, die and credit the kill.</summary>
    [Server]
    private void Hurt(int amount, int attackerId, bool headshot, Vector3 from, string weaponName) {
        if (dead.Value) return;
        health.Value = Mathf.Max(0, health.Value - amount);
        RpcHurt(from);
        if (health.Value > 0) return;

        dead.Value = true;
        bool suicide = attackerId == OwnerId;
        NetPlayer killer = NetPlayer.ForClient(attackerId);
        NetPlayer victim = NetPlayer.ForClient(OwnerId);
        // Your own grenade is a death, not a kill.
        if (killer != null && !suicide) killer.AddKill(headshot);
        if (victim != null) {
            victim.AddDeath();
            victim.Alive.Value = false;
        }
        string killerName = killer != null ? killer.Callsign.Value : "알 수 없음";
        string victimName = victim != null ? victim.Callsign.Value : "?";
        if (MatchManager.Instance != null) {
            MatchManager.Instance.AnnounceKill(attackerId, killerName, OwnerId, victimName, weaponName, headshot);
        }
        Debug.Log("[Match] " + killerName + " killed " + victimName + (headshot ? " (headshot)" : "") + " with " + weaponName);
        RpcDeath(killerName, weaponName, headshot, Vector3.Distance(transform.position, from), AttackerHealth(attackerId));
        ServerDied?.Invoke(this);
    }

    /// <summary>The attacker's health for the death screen (0 if their body is gone).</summary>
    private static int AttackerHealth(int clientId) {
        foreach (PlayerHealth h in FindObjectsOfType<PlayerHealth>()) {
            if (h.OwnerId == clientId && !h.IsDead) return h.health.Value;
        }
        return 0;
    }

    [ObserversRpc]
    private void RpcHurt(Vector3 from) {
        playerAudio.clip = hurtClip;
        playerAudio.Play();
        if (!IsOwner) return;
        animator.SetTrigger("IsHurt");
        if (GameUI.Instance != null) GameUI.Instance.Hud.DamageFrom(from);
    }

    [ObserversRpc]
    private void RpcDeath(string killerName, string weaponName, bool headshot, float distance, int killerHealth) {
        ikControl.enabled = false;
        nameTag.gameObject.SetActive(false);
        playerAudio.clip = deathClip;
        playerAudio.Play();
        StartCoroutine(StartSinking(sinkTime));
        if (!IsOwner) return;
        fpController.enabled = false;
        animator.SetTrigger("IsDead");
        if (GameNetwork.Instance != null) GameNetwork.Instance.OnLocalDeath();
        if (GameUI.Instance != null) {
            // Elimination: no respawn this round (negative = "until the next round").
            MatchManager mm = MatchManager.Instance;
            float wait = mm != null && !mm.AllowsRespawn ? -1f : respawnTime;
            GameUI.Instance.Death.Show(killerName, weaponName, headshot, distance, killerHealth, wait);
        }
    }

    private IEnumerator StartSinking(float delayTime) {
        yield return new WaitForSeconds(delayTime);
        Rigidbody body = GetComponent<Rigidbody>();
        body.useGravity = false;
        body.isKinematic = false;
        isSinking = true;
    }

}
