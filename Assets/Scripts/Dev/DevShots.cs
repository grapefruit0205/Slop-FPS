using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FishNet;
using UnityEngine;
using SlopFPS.Input;

/// <summary>
/// Screenshot tour for checking the UI without a second machine or the lobby API:
///   FPS.x86_64 -uishot /path/to/output
/// Hosts a local server in-process and joins it like a real client, fills the screens with sample data, saves PNGs, quits.
///   FPS.x86_64 -boundsshot /path/to/output -map outpost
/// Loads the map without any network and photographs its play area: from above with the outline and spawn points drawn
/// in (debug lines, only in this mode), and at eye level along the edge, to show the invisible walls really are invisible.
///   FPS.x86_64 -elimshot /path/to/output -connect host:port -autojoin
/// Joins an elimination server like a player and photographs the round HUD as it comes up: freeze countdown, live
/// round, killed (out of the round), spectating a teammate, scoreboard with the dead, round result. Quits when done.
///   FPS.x86_64 -grenadeshot /path/to/output -connect host:port -autojoin
/// Joins like a player and goes through the grenades: the HUD slots, each grenade in hand, a frag cooking with its arc,
/// the frag's blast (own view and from the side), a flashbang whiteout from the thrower's own eyes, a smoke cloud from
/// outside, from the side and from inside it, someone else holding a grenade, and the killfeed. Quits when done.
/// </summary>
public class DevShots : MonoBehaviour {

    private static bool parsed;
    private static string outDir;
    private static bool stanceTour;
    private static bool boundsTour;
    private static bool elimTour;
    private static bool grenadeTour;

    public static bool Enabled {
        get {
            if (!parsed) {
                parsed = true;
                string[] args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-uishot");
                if (i >= 0 && i + 1 < args.Length) outDir = args[i + 1];
                int j = Array.IndexOf(args, "-stanceshot");
                if (j >= 0 && j + 1 < args.Length) {
                    outDir = args[j + 1];
                    stanceTour = true;
                }
                int e = Array.IndexOf(args, "-elimshot");
                if (e >= 0 && e + 1 < args.Length) {
                    outDir = args[e + 1];
                    elimTour = true;
                }
                int g = Array.IndexOf(args, "-grenadeshot");
                if (g >= 0 && g + 1 < args.Length) {
                    outDir = args[g + 1];
                    grenadeTour = true;
                }
                int k = Array.IndexOf(args, "-boundsshot");
                if (k >= 0 && k + 1 < args.Length) {
                    outDir = args[k + 1];
                    boundsTour = true;
                }
            }
            return outDir != null;
        }
    }

    /// <summary>The stance tour joins a running server the normal way (with -autojoin).</summary>
    public static bool UsesLobby => Enabled && (stanceTour || elimTour || grenadeTour);

    public void Begin(GameNetwork network, GameUI ui) {
        Application.runInBackground = true;
        ui.IgnoreKeys = true;
        Directory.CreateDirectory(outDir);
        StartCoroutine(boundsTour ? BoundsTour(ui) : elimTour ? ElimTour(ui) : grenadeTour ? GrenadeTour(ui) : stanceTour ? StanceTour() : Tour(network, ui));
    }

    private IEnumerator Tour(GameNetwork network, GameUI ui) {
        ui.Lobby.Callsign = "Grapefruit";
        ui.Lobby.SetStatus(LobbyScreen.NetState.Online,
            "REGION <color=#ECEAE3>ASIA</color>    PING <color=#ECEAE3>42ms</color>    접속 <color=#ECEAE3>31</color>");
        ui.Lobby.SetRooms(new List<LobbyScreen.RoomEntry> {
            Room("Alpha Squad", 6, true), Room("초보 환영 · 매너겜", 2, true, "outpost", GameMode.FreeForAll),
            Room("Night Ops", 8, true, "warehouse", GameMode.Elimination), Room("클랜전 연습", 4, true, "outpost", GameMode.Elimination),
        });
        yield return new WaitForSeconds(2f);
        yield return Shot("01_lobby");

        const ushort port = 7779;
        InstanceFinder.NetworkManager.GetComponent<GameServer>().Run(network, InstanceFinder.NetworkManager, port);
        yield return new WaitForSeconds(0.5f);
        network.Connect(new ConnectInfo { Host = "127.0.0.1", Port = port, Secure = false, RoomName = "Alpha Squad" });
        float waited = 0f;
        while (PlayerNetworkMover.Local == null && waited < 10f) {
            waited += Time.deltaTime;
            yield return null;
        }
        Debug.Log("[DevShots] local avatar " + (PlayerNetworkMover.Local != null ? "spawned" : "MISSING") + " after " + waited.ToString("F1") + "s");
        yield return new WaitForSeconds(1.5f);

        FpsGun gun = FindObjectOfType<FpsGun>();
        PlayerHealth me = FindObjectOfType<PlayerHealth>();
        ui.Hud.AddKill("Ghost77", "Policeman_Lee", "AK-47", false, false, false);
        ui.Hud.AddKill("Sgt_Park", "Ghost77", "AK-47", true, false, false);
        ui.Hud.AddKill("Grapefruit", "RobotX_Kim", "AK-47", false, true, false);
        ui.Hud.AddMessage("Night_Owl 님이 작전에서 이탈했습니다");
        ui.Hud.AddMessage("<color=#E3A72F>Grapefruit</color> 님이 RobotX_Kim 님을 처치했습니다  <color=#E3A72F>+100</color>");
        ui.Hud.SetHealth(72, 100);
        if (gun != null) gun.SetMagazineForTesting(24);
        if (me != null) ui.Hud.DamageFrom(me.transform.position - me.transform.right * 6f + me.transform.forward * 2f);
        yield return new WaitForSeconds(0.3f);
        ui.Hud.Hitmarker(false, false);
        yield return null;
        yield return Shot("02_hud");

        if (gun != null) {
            gun.SetMagazineForTesting(3);
            gun.SendMessage("StartReload");
            yield return new WaitForSeconds(0.9f);
        }
        ui.Hud.SetHealth(22, 100);
        yield return new WaitForSeconds(0.6f);
        yield return Shot("03_reload_lowhp");

        ui.Scoreboard.OverrideRows = new List<ScoreboardScreen.Row> {
            Row("Sgt_Park", 11, 4, 1240, 35), Row("Grapefruit", 7, 3, 860, 42, me: true),
            Row("Ghost77", 6, 6, 700, 58), Row("RobotX_Kim", 4, 7, 480, 71, dead: true),
            Row("Policeman_Lee", 3, 5, 390, 44), Row("Rookie_01", 0, 6, 40, 132),
        };
        ui.ForceScoreboard = true;
        yield return new WaitForSeconds(1f);
        yield return Shot("04_scoreboard");
        ui.ForceScoreboard = false;
        ui.Scoreboard.OverrideRows = null;
        yield return null;

        ui.Hud.SetAlive(false);
        ui.Death.Show("Sgt_Park", "AK-47", true, 32f, 64, 8f);
        yield return new WaitForSeconds(2.2f);
        yield return Shot("05_death");
        ui.Death.Hide();
        ui.Hud.SetAlive(true);

        ui.Pause.SetVisible(true);
        yield return new WaitForSeconds(0.8f);
        yield return Shot("06_pause");

        ui.Pause.SetVisible(false);
        yield return WeaponTour(gun, ui);
        yield return WorldTour(network, ui);

        Debug.Log("[DevShots] done → " + outDir);
        Application.Quit();
    }

    /// <summary>Each gun at the hip and aimed, with its optics.</summary>
    private IEnumerator WeaponTour(FpsGun gun, GameUI ui) {
        if (gun == null) yield break;
        ui.Hud.SetHealth(100, 100);
        var views = new (WeaponId, OpticId)[] {
            (WeaponId.AK47, OpticId.RedDot), (WeaponId.AK47, OpticId.Scope4x), (WeaponId.AK47, OpticId.Iron),
            (WeaponId.SMG, OpticId.RedDot), (WeaponId.SMG, OpticId.Iron), (WeaponId.Sniper, OpticId.Scope8x), (WeaponId.Shotgun, OpticId.Iron), (WeaponId.Shotgun, OpticId.RedDot),
            (WeaponId.Pistol, OpticId.Iron),
        };
        foreach ((WeaponId id, OpticId optic) in views) {
            gun.ShowForTesting(id, optic);
            FpsGun.DevAim = false;
            yield return new WaitForSeconds(0.6f);
            yield return Shot("2_" + id + "_" + optic + "_hip");
            FpsGun.DevAim = true;
            yield return new WaitForSeconds(0.6f);
            yield return Shot("2_" + id + "_" + optic + "_aim");
        }
        FpsGun.DevAim = false;
        gun.ShowForTesting(WeaponId.AK47, OpticId.RedDot);
        yield return new WaitForSeconds(0.4f);
    }

    /// <summary>Environment views from a few spawn points through a copy of the scene camera, UI hidden.</summary>
    private IEnumerator WorldTour(GameNetwork network, GameUI ui) {
        Transform[] spawns = network.SpawnPoints;
        if (spawns == null || spawns.Length == 0 || network.SceneCamera == null) yield break;
        Camera cam = Instantiate(network.SceneCamera.gameObject).GetComponent<Camera>();
        cam.gameObject.SetActive(true);
        cam.enabled = true;
        cam.depth = 50;
        cam.fieldOfView = 70;
        Canvas canvas = ui.GetComponent<Canvas>();
        canvas.enabled = false;
        float[] yaw = { 0f, 120f, 240f, 60f, 180f, 300f };
        int views = Mathf.Min(6, spawns.Length);
        for (int i = 0; i < views; i++) {
            Transform s = spawns[i * spawns.Length / views];
            Vector3 eye = s.position + Vector3.up * 1.7f;
            Quaternion look = Quaternion.Euler(4f, s.eulerAngles.y + yaw[i], 0);
            // Odd views frame one of the scattered props from a clear spot a few metres away.
            GameObject props = GameObject.Find("Military Props");
            if (props != null && i % 2 == 1 && props.transform.childCount > 0) {
                Transform prop = props.transform.GetChild((i * 7) % props.transform.childCount);
                Vector3 target = prop.position + Vector3.up * 0.5f;
                for (int d = 0; d < 8; d++) {
                    Vector3 dir = Quaternion.Euler(0, d * 45f, 0) * Vector3.forward;
                    if (Physics.Raycast(target + dir * 1.2f, dir, 4f)) continue;
                    eye = target + dir * 4.5f + Vector3.up * 1.1f;
                    look = Quaternion.LookRotation(target - eye);
                    break;
                }
            }
            cam.transform.SetPositionAndRotation(eye, look);
            yield return new WaitForSeconds(0.5f);
            yield return Shot("1" + i + "_world");
        }
        canvas.enabled = true;
        Destroy(cam.gameObject);
    }

    /// <summary>
    /// -stanceshot DIR (with -autojoin, next to a bot running -stance cycle): this player's own view standing, crouched,
    /// lying down and leaning, then the other player from a free camera in each pose it takes.
    /// </summary>
    private IEnumerator StanceTour() {
        while (PlayerNetworkMover.Local == null) yield return null;
        yield return new WaitForSeconds(2f);
        var own = new (Stance pose, float lean, string name)[] {
            (Stance.Stand, 0f, "self_stand"), (Stance.Crouch, 0f, "self_crouch"), (Stance.Prone, 0f, "self_prone"),
            (Stance.Stand, -1f, "self_stand_leanL"), (Stance.Crouch, 1f, "self_crouch_leanR"),
        };
        foreach (var step in own) {
            PlayerStance.DevStance = step.pose;
            PlayerStance.DevLean = step.lean;
            yield return new WaitForSeconds(1.5f);
            yield return Shot(step.name);
        }
        PlayerStance.DevStance = Stance.Stand;
        PlayerStance.DevLean = 0f;

        Camera free = new GameObject("StanceShotCamera").AddComponent<Camera>();
        free.enabled = false;
        free.fieldOfView = 45f;
        free.cullingMask = ~LayerMask.GetMask("Hidden", "FPSGun", "UI");   // not this player's own (hidden) body
        HashSet<string> taken = new HashSet<string>();
        string seen = null;
        float seenSince = 0f, until = Time.time + 45f;
        while (Time.time < until && taken.Count < 6) {
            yield return null;
            PlayerStance other = null;
            foreach (PlayerStance p in FindObjectsOfType<PlayerStance>()) if (p != PlayerStance.Local) other = p;
            if (other == null) continue;
            // Nothing live looks at the other body, so its animator and skinning would sleep; the free camera needs
            // them running.
            foreach (Animator a in other.GetComponentsInChildren<Animator>()) {
                if (a.cullingMode != AnimatorCullingMode.AlwaysAnimate) a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            foreach (SkinnedMeshRenderer r in other.GetComponentsInChildren<SkinnedMeshRenderer>()) {
                if (!r.updateWhenOffscreen) r.updateWhenOffscreen = true;
            }
            string key = other.Current + (other.Lean < -0.5f ? "_leanL" : other.Lean > 0.5f ? "_leanR" : "");
            if (key != seen) {
                seen = key;
                seenSince = Time.time;
                continue;
            }
            if (taken.Contains(key) || Time.time - seenSince < 1.2f) continue;
            taken.Add(key);
            Transform t = other.transform;
            float scale = t.lossyScale.y;
            Vector3 look = t.position + Vector3.up * (other.Current == Stance.Prone ? 0.3f : other.Current == Stance.Crouch ? 0.7f : 1.0f) * scale;
            foreach (var view in new[] { ("front", new Vector3(-0.45f, 0f, 1f)), ("side", new Vector3(1f, 0f, -0.1f)), ("back", new Vector3(0.5f, 0f, -1f)) }) {
                // Pulled in front of any wall between the camera and the body.
                Vector3 offset = t.rotation * view.Item2.normalized * 3.2f * scale + Vector3.up * 0.25f * scale;
                float reach = offset.magnitude;
                foreach (RaycastHit h in Physics.RaycastAll(look, offset / reach, reach, ~LayerMask.GetMask("Hidden", "FPSGun", "UI", "TPSPlayer", "TPSGun", "Shootable", "FPSPlayer"),
                                                           QueryTriggerInteraction.Ignore)) {
                    reach = Mathf.Min(reach, Mathf.Max(0.6f * scale, h.distance - 0.2f));
                }
                free.transform.position = look + offset.normalized * reach;
                free.transform.LookAt(look);
                RenderOffscreen(Path.Combine(outDir, "other_" + key + "_" + view.Item1 + ".png"), free);
                yield return null;
            }
            Debug.Log("[DevShots] other player " + key);
        }
        Debug.Log("[DevShots] done → " + outDir);
        yield return new WaitForSeconds(0.5f);
        Application.Quit();
    }

    /// <summary>-boundsshot DIR [-map ID]: the play area from above (outline drawn in) and from inside along its edge.</summary>
    private IEnumerator BoundsTour(GameUI ui) {
        MapDefinition map = MapLoader.Load(NetConfig.MapArg ?? MapLoader.DefaultMap);
        ui.GetComponent<Canvas>().enabled = false;
        yield return new WaitForSeconds(1f);
        if (map == null || !map.HasBoundary) {
            Debug.LogWarning("[DevShots] map has no play area");
            Application.Quit();
            yield break;
        }
        Vector3[] corners = new Vector3[map.boundary.Length];
        Bounds b = new Bounds(map.transform.TransformPoint(new Vector3(map.boundary[0].x, map.boundaryFloor, map.boundary[0].y)), Vector3.zero);
        for (int i = 0; i < corners.Length; i++) {
            corners[i] = map.transform.TransformPoint(new Vector3(map.boundary[i].x, map.boundaryFloor, map.boundary[i].y));
            b.Encapsulate(corners[i]);
        }

        // Debug overlay: the outline in red, FFA spawns yellow, team A blue, team B orange-red.
        GameObject overlay = new GameObject("PlayAreaOverlay");
        float scale = Mathf.Max(b.size.x, b.size.z);
        Vector3 lift = Vector3.up * 40f;
        Line(overlay.transform, corners, lift, new Color(1f, 0.1f, 0.1f), scale * 0.006f, true);
        Mark(overlay.transform, map.spawnPoints, lift, new Color(1f, 0.85f, 0.1f), scale * 0.012f);
        Mark(overlay.transform, map.teamASpawns, lift + Vector3.up * 0.5f, new Color(0.25f, 0.55f, 1f), scale * 0.008f);
        Mark(overlay.transform, map.teamBSpawns, lift + Vector3.up * 0.5f, new Color(1f, 0.4f, 0.2f), scale * 0.008f);

        Camera cam = new GameObject("BoundsShotCamera").AddComponent<Camera>();
        cam.enabled = false;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.farClipPlane = 2000f;
        cam.orthographic = true;
        cam.orthographicSize = Mathf.Max(b.extents.z, b.extents.x * ShotHeight / ShotWidth) * 1.25f;
        cam.transform.SetPositionAndRotation(b.center + Vector3.up * 400f, Quaternion.Euler(90f, 0f, 0f));
        bool fog = RenderSettings.fog;
        RenderSettings.fog = false;          // from 400 up it would grey everything out
        RenderOffscreen(Path.Combine(outDir, map.id + "_01_topdown.png"), cam);
        yield return null;
        cam.orthographic = false;
        cam.fieldOfView = 50f;
        Vector3 eyeHigh = b.center + new Vector3(-0.9f * b.extents.x, 1.1f * scale, -1.0f * b.extents.z);
        cam.transform.SetPositionAndRotation(eyeHigh, Quaternion.LookRotation(b.center - eyeHigh));
        RenderOffscreen(Path.Combine(outDir, map.id + "_02_oblique.png"), cam);
        yield return null;

        RenderSettings.fog = fog;

        // Eye level, just inside the edge, looking out and along it: nothing new should be visible.
        overlay.SetActive(false);
        cam.fieldOfView = 70f;
        float eye = 1.59f * MapDefinition.MetresToWorld;     // the avatars' eye, in world units
        Vector3 centre = b.center;
        int shots = 0;
        for (int i = 0; i < corners.Length && shots < 4; i += Mathf.Max(1, corners.Length / 4)) {
            Vector3 a = corners[i], c = corners[(i + 1) % corners.Length];
            Vector3 mid = (a + c) * 0.5f;
            Vector3 inward = centre - mid;
            inward.y = 0f;
            inward.Normalize();
            Vector3 spot = mid + inward * 4f;
            // Stand on whatever floor is there.
            if (Physics.Raycast(spot + Vector3.up * 30f, Vector3.down, out RaycastHit floor, 60f, LayerMask.GetMask("Shootable", "Default"),
                                QueryTriggerInteraction.Ignore)) spot = floor.point;
            cam.transform.SetPositionAndRotation(spot + Vector3.up * eye, Quaternion.LookRotation(-inward + (c - a).normalized * 0.6f));
            RenderOffscreen(Path.Combine(outDir, map.id + "_1" + shots + "_edge.png"), cam);
            shots++;
            yield return null;
        }
        Debug.Log("[DevShots] play area of " + map.id + ": " + corners.Length + " corners, bounds " + b.min.ToString("F0") + " .. " + b.max.ToString("F0"));
        Debug.Log("[DevShots] done → " + outDir);
        yield return new WaitForSeconds(0.5f);
        Application.Quit();
    }

    /// <summary>-elimshot DIR: each screen of an elimination round, as this (idle) player lives through it.</summary>
    private IEnumerator ElimTour(GameUI ui) {
        ui.IgnoreKeys = true;
        HashSet<string> taken = new HashSet<string>();
        float deadline = Time.time + 420f;
        float diedAt = -1f, watchingSince = -1f;
        while (Time.time < deadline && taken.Count < 6) {
            yield return null;
            MatchManager mm = MatchManager.Instance;
            NetPlayer me = NetPlayer.Local;
            if (mm == null || me == null || mm.IsOver) continue;
            RoundPhase phase = mm.Phase;
            bool alive = me.Alive.Value && PlayerNetworkMover.Local != null;
            if (alive) diedAt = -1f;
            else if (diedAt < 0f) diedAt = Time.time;
            bool watching = Spectator.Instance != null && Spectator.Instance.Active && Spectator.Instance.Target != null;
            if (!watching) watchingSince = -1f;
            else if (watchingSince < 0f) watchingSince = Time.time;
            string shot = null;
            if (phase == RoundPhase.Freeze && alive && mm.PhaseLeft < 3.5f && !taken.Contains("01")) shot = "01";
            else if (phase == RoundPhase.Live && alive && mm.TimeLeft < mm.RoundLength - 3f && !taken.Contains("02")) shot = "02";
            else if (phase == RoundPhase.Live && !alive && diedAt > 0f && Time.time - diedAt > 1.2f && !taken.Contains("03")) shot = "03";
            // -devspawn rounds can be over in seconds: after a while, take the spectator view under the result banner too.
            else if ((phase == RoundPhase.Live || phase == RoundPhase.RoundEnd && Time.time > deadline - 300f) &&
                     watching && Time.time - watchingSince > 0.6f && !taken.Contains("04")) shot = "04";
            else if (phase == RoundPhase.RoundEnd && mm.PhaseLeft < 4f && !taken.Contains("06")) shot = "06";
            if (shot == null) continue;
            taken.Add(shot);
            string[] names = { "", "elim_01_freeze", "elim_02_live", "elim_03_killed", "elim_04_spectate", "", "elim_06_roundend" };
            Debug.Log("[DevShots] elimination: round " + mm.Round + " " + phase + ", alive A " + mm.AliveOf(Team.A) + " / B " + mm.AliveOf(Team.B));
            yield return Shot(names[int.Parse(shot)]);
            if (shot == "04") {
                ui.ForceScoreboard = true;
                yield return new WaitForSeconds(0.8f);
                yield return Shot("elim_05_scoreboard");
                ui.ForceScoreboard = false;
                taken.Add("05");
            }
        }
        Debug.Log("[DevShots] elimination tour: " + taken.Count + " of 6 screens → " + outDir);
        Application.Quit();
    }

    // ------------------------------------------------------------------ -grenadeshot

    private bool steer;
    private float steerYaw, steerPitch;

    /// <summary>The grenade tour holds the body on the floor and points the view itself (mouse look is off).</summary>
    void LateUpdate() {
        PlayerNetworkMover me = PlayerNetworkMover.Local;
        if (!steer || me == null) return;
        CharacterController cc = me.GetComponent<CharacterController>();
        if (cc != null && cc.enabled) cc.Move(Vector3.down * 9.8f * Time.deltaTime);
        me.transform.rotation = Quaternion.Euler(0f, steerYaw, 0f);
        Camera cam = me.GetComponentInChildren<Camera>();
        if (cam != null) cam.transform.rotation = Quaternion.Euler(-steerPitch, steerYaw, 0f);
    }

    private IEnumerator GrenadeTour(GameUI ui) {
        ui.IgnoreKeys = true;
        float m = Grenades.M(1f);
        while (PlayerNetworkMover.Local == null) yield return null;
        yield return new WaitForSeconds(2.5f);
        PlayerNetworkMover me = PlayerNetworkMover.Local;
        me.GetComponent<FirstPersonController>().enabled = false;
        Vector3 lane = OpenLane(me.transform.position + Vector3.up * m, out float room);
        steerYaw = Quaternion.LookRotation(lane).eulerAngles.y;
        steerPitch = -4f;
        steer = true;
        Debug.Log("[DevShots] grenades: lane " + lane.ToString("F2") + ", " + (room / m).ToString("F0") + " m of room");
        yield return new WaitForSeconds(1.2f);
        yield return Shot("g01_hud_slots");

        GrenadeHands hands = GrenadeHands.Local;
        foreach ((GrenadeId id, string name) in new[] { (GrenadeId.Frag, "g02_frag_in_hand"), (GrenadeId.Flash, "g03_flash_in_hand"), (GrenadeId.Smoke, "g04_smoke_in_hand") }) {
            hands.Select(id);
            yield return new WaitForSeconds(0.8f);
            yield return Shot(name);
        }

        Camera free = new GameObject("GrenadeShotCamera").AddComponent<Camera>();
        free.enabled = false;
        if (Camera.main != null) free.CopyFrom(Camera.main);
        free.cullingMask = ~LayerMask.GetMask("Hidden", "FPSGun", "UI");
        free.fieldOfView = 60f;
        free.nearClipPlane = 0.05f;
        GrenadeId blasted = GrenadeId.None;
        Vector3 blastAt = Vector3.zero;
        Action<GrenadeId, Vector3> onBlast = (type, at) => {
            blasted = type;
            blastAt = at;
        };
        GrenadeWorld.Blasted += onBlast;

        // Frag: cook it with the arc showing, then throw it down the lane and watch it go off.
        float reach = Mathf.Clamp(room / m - 4f, 6f, 14f);
        steerPitch = DevBot.LobPitch(reach, true);
        hands.DevThrow(GrenadeId.Frag, true, 999f);
        yield return new WaitForSeconds(Grenades.DrawTime + 1.1f);
        yield return Shot("g05_frag_cooking_arc");
        hands.DevRelease();
        blasted = GrenadeId.None;
        for (float t = 0f; t < 6f && blasted != GrenadeId.Frag; t += Time.deltaTime) yield return null;
        steerPitch = -6f;
        yield return new WaitForSeconds(0.05f);
        yield return Shot("g06_frag_own_view");
        Vector3 side = Vector3.Cross(Vector3.up, lane).normalized;
        AimClear(free, blastAt + side * 7f * m + Vector3.up * 2.5f * m - lane * 3f * m, blastAt + Vector3.up * 0.8f * m);
        RenderOffscreen(Path.Combine(outDir, "g07_frag_side_a.png"), free);
        yield return new WaitForSeconds(0.3f);
        RenderOffscreen(Path.Combine(outDir, "g07_frag_side_b.png"), free);
        yield return new WaitForSeconds(1.2f);
        RenderOffscreen(Path.Combine(outDir, "g07_frag_side_c.png"), free);
        Debug.Log("[DevShots] frag went off at " + blastAt.ToString("F1"));
        yield return new WaitForSeconds(1.5f);

        // Flashbang: thrown ahead and watched going off, from the thrower's own eyes.
        steerPitch = DevBot.LobPitch(Mathf.Min(reach, 10f), true);
        hands.DevThrow(GrenadeId.Flash, true, 0.15f);
        blasted = GrenadeId.None;
        for (float t = 0f; t < 6f && blasted != GrenadeId.Flash; t += Time.deltaTime) yield return null;
        steerPitch = -4f;
        yield return new WaitForSeconds(0.1f);
        yield return Shot("g08_flash_white");
        yield return new WaitForSeconds(2.6f);
        yield return Shot("g09_flash_fading");
        yield return new WaitForSeconds(2.2f);
        yield return Shot("g10_flash_over");

        // Smoke: a short throw, then the cloud from here, from the side and from the middle of it.
        steerPitch = DevBot.LobPitch(7f, true);
        hands.DevThrow(GrenadeId.Smoke, true, 0.15f);
        blasted = GrenadeId.None;
        for (float t = 0f; t < 9f && blasted != GrenadeId.Smoke; t += Time.deltaTime) yield return null;
        steerPitch = -2f;
        yield return new WaitForSeconds(2.4f);
        yield return Shot("g11_smoke_outside");
        AimClear(free, blastAt - lane * 12f * m + side * 4f * m + Vector3.up * 2.2f * m, blastAt + Vector3.up * 1.2f * m);
        RenderOffscreen(Path.Combine(outDir, "g12_smoke_side.png"), free);
        yield return null;
        AimAt(free, blastAt + Vector3.up * 1.6f * m + side * 0.5f * m, blastAt + Vector3.up * 1.5f * m + lane * 6f * m);
        GrenadeWorld.ViewOverride = free.transform;
        yield return new WaitForSeconds(0.8f);
        RenderOffscreen(Path.Combine(outDir, "g13_smoke_inside.png"), free, true);
        GrenadeWorld.ViewOverride = null;
        GrenadeWorld.Blasted -= onBlast;
        yield return new WaitForSeconds(0.5f);

        // Someone else with a grenade out (a bot running -grenades).
        steer = false;
        float until = Time.time + 40f;
        bool caught = false;
        while (Time.time < until && !caught) {
            yield return null;
            foreach (Loadout other in FindObjectsOfType<Loadout>()) {
                if (other.IsOwner || other.Held == GrenadeId.None) continue;
                foreach (Animator a in other.GetComponentsInChildren<Animator>()) a.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (SkinnedMeshRenderer r in other.GetComponentsInChildren<SkinnedMeshRenderer>()) r.updateWhenOffscreen = true;
                yield return new WaitForSeconds(0.15f);
                if (other == null || other.Held == GrenadeId.None) break;
                Transform t = other.transform;
                Vector3 look = t.position + Vector3.up * 1.1f * m;
                AimAt(free, look + t.forward * 2.6f * m + t.right * 1.1f * m + Vector3.up * 0.3f * m, look);
                RenderOffscreen(Path.Combine(outDir, "g14_tps_grenade.png"), free);
                Debug.Log("[DevShots] client " + other.OwnerId + " holding a " + Grenades.Get(other.Held).Name);
                foreach (Transform g in other.GetComponentsInChildren<Transform>(true)) {
                    if (!g.name.StartsWith("Grenade_")) continue;
                    Renderer r = g.GetComponentInChildren<Renderer>(true);
                    Debug.Log("[DevShots]   model under " + g.parent.name + " at " + g.position.ToString("F2") + " (body " + t.position.ToString("F2") +
                              "), layer " + LayerMask.LayerToName(g.gameObject.layer) + ", size " + (r != null ? r.bounds.size.ToString("F3") : "-") +
                              ", shown " + (r != null && r.enabled && r.gameObject.activeInHierarchy));
                }
                caught = true;
                break;
            }
        }
        if (!caught) Debug.LogWarning("[DevShots] nobody else took a grenade out");

        ui.Hud.AddKill("Bot4", "Bot7", Grenades.Get(GrenadeId.Frag).Name, false, false, false);
        ui.Hud.AddKill(ui.Lobby.Callsign, "Bot2", Grenades.Get(GrenadeId.Frag).Name, false, true, false);
        yield return new WaitForSeconds(0.5f);
        yield return Shot("g15_killfeed");

        // The pause menu's controls page, with the grenade keys.
        ui.Pause.SetVisible(true);
        ui.Pause.SendMessage("ShowPage", 2);
        yield return new WaitForSeconds(0.8f);
        yield return Shot("g16_pause_controls");
        ui.Pause.SetVisible(false);
        Debug.Log("[DevShots] done → " + outDir);
        yield return new WaitForSeconds(0.5f);
        Application.Quit();
    }

    /// <summary><see cref="AimAt"/>, but brought in front of any wall between the subject and the wanted spot.</summary>
    private static void AimClear(Camera cam, Vector3 from, Vector3 to) {
        Vector3 d = from - to;
        float len = d.magnitude;
        if (Physics.SphereCast(to, Grenades.M(0.2f), d / len, out RaycastHit hit, len, GrenadeFlight.WorldMask, QueryTriggerInteraction.Ignore)) {
            from = to + d / len * Mathf.Max(Grenades.M(2f), hit.distance - Grenades.M(0.3f));
        }
        AimAt(cam, from, to);
    }

    private static void AimAt(Camera cam, Vector3 from, Vector3 to) {
        cam.transform.position = from;
        cam.transform.rotation = Quaternion.LookRotation(to - from);
    }

    /// <summary>The level direction with the most open floor ahead (for throwing down), and how much there is.</summary>
    private static Vector3 OpenLane(Vector3 from, out float room) {
        Vector3 best = Vector3.forward;
        room = 0f;
        int mask = LayerMask.GetMask("Default", "Shootable", "Ignore Raycast");
        for (int i = 0; i < 24; i++) {
            Vector3 dir = Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward;
            float free = Physics.Raycast(from, dir, out RaycastHit hit, 80f, mask, QueryTriggerInteraction.Ignore) ? hit.distance : 80f;
            if (free > room) {
                room = free;
                best = dir;
            }
        }
        return best;
    }

    private static Material lineMaterial;

    private static LineRenderer Line(Transform parent, Vector3[] points, Vector3 lift, Color color, float width, bool loop) {
        if (lineMaterial == null) lineMaterial = new Material(Shader.Find("Sprites/Default"));
        LineRenderer lr = new GameObject("Line").AddComponent<LineRenderer>();
        lr.transform.SetParent(parent, false);
        lr.sharedMaterial = lineMaterial;
        lr.startColor = lr.endColor = color;
        lr.startWidth = lr.endWidth = width;
        lr.loop = loop;
        lr.alignment = LineAlignment.TransformZ;
        lr.transform.rotation = Quaternion.Euler(90f, 0f, 0f);    // flat on the ground, facing up
        lr.positionCount = points.Length;
        for (int i = 0; i < points.Length; i++) lr.SetPosition(i, points[i] + lift);
        return lr;
    }

    /// <summary>A small diamond at each spawn point.</summary>
    private static void Mark(Transform parent, Transform[] points, Vector3 lift, Color color, float size) {
        if (points == null) return;
        foreach (Transform t in points) {
            if (t == null) continue;
            Vector3 p = t.position;
            Vector3[] d = { p + Vector3.forward * size, p + Vector3.right * size, p - Vector3.forward * size, p - Vector3.right * size };
            Line(parent, d, lift, color, size * 0.6f, true);
        }
    }

    private IEnumerator Shot(string name) {
        string path = Path.Combine(outDir, name + ".png");
        if (Application.isBatchMode) {
            yield return null;
            RenderOffscreen(path);
        } else {
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
        }
        yield return new WaitForSeconds(0.3f);
        Debug.Log("[DevShots] " + path);
    }

    private const int ShotWidth = 1920, ShotHeight = 1080;
    private RenderTexture target;
    private Camera uiCamera;

    /// <summary>
    /// Batch mode has no screen to capture (it comes out black), so draw every live camera into a texture by hand,
    /// with the UI moved onto a camera of its own for the purpose.
    /// </summary>
    /// <param name="ui">With <paramref name="only"/>: draw the HUD over it too.</param>
    private void RenderOffscreen(string path, Camera only = null, bool ui = false) {
        if (target == null) {
            target = new RenderTexture(ShotWidth, ShotHeight, 24, RenderTextureFormat.ARGB32);
            uiCamera = new GameObject("ShotUICamera").AddComponent<Camera>();
            uiCamera.enabled = false;
            uiCamera.clearFlags = CameraClearFlags.Depth;
            uiCamera.cullingMask = LayerMask.GetMask("UI");
            uiCamera.orthographic = true;
            uiCamera.targetTexture = target;
        }
        List<Canvas> moved = new List<Canvas>();
        foreach (Canvas c in FindObjectsOfType<Canvas>()) {
            if (!c.isRootCanvas || c.renderMode != RenderMode.ScreenSpaceOverlay || !c.enabled) continue;
            c.renderMode = RenderMode.ScreenSpaceCamera;
            c.worldCamera = uiCamera;
            c.planeDistance = 1f;
            moved.Add(c);
        }
        Canvas.ForceUpdateCanvases();
        List<Camera> cams = only != null ? new List<Camera> { only } : new List<Camera>(Camera.allCameras);
        cams.Remove(uiCamera);
        if (only != null && !ui) moved.ForEach(c => c.renderMode = RenderMode.ScreenSpaceOverlay);
        if (only != null && !ui) moved.Clear();
        cams.Sort((a, b) => a.depth.CompareTo(b.depth));
        // Post-processing smears hand-driven renders (it expects the normal frame loop), so it sits these out.
        List<Behaviour> post = new List<Behaviour>();
        foreach (Camera cam in cams) {
            var layer = cam.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
            if (layer != null && layer.enabled) {
                layer.enabled = false;
                post.Add(layer);
            }
        }
        foreach (Camera cam in cams) {
            RenderTexture old = cam.targetTexture;
            cam.targetTexture = target;
            cam.Render();
            cam.targetTexture = old;
        }
        foreach (Behaviour b in post) b.enabled = true;
        if (moved.Count > 0) uiCamera.Render();
        foreach (Canvas c in moved) c.renderMode = RenderMode.ScreenSpaceOverlay;

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = target;
        Texture2D tex = new Texture2D(ShotWidth, ShotHeight, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Destroy(tex);
    }

    private static LobbyScreen.RoomEntry Room(string name, int players, bool open, string map = MapLoader.SceneMapId,
                                              GameMode mode = Modes.Default) {
        return new LobbyScreen.RoomEntry { Name = name, Players = players, MaxPlayers = 8, IsOpen = open,
                                           Map = MapLoader.DisplayName(map) + "  ·  " + Modes.DisplayName(mode) };
    }

    private static ScoreboardScreen.Row Row(string name, int k, int d, int score, int ping, bool me = false, bool dead = false) {
        return new ScoreboardScreen.Row { Name = name, Kills = k, Deaths = d, Score = score, Ping = ping, IsMe = me, IsDead = dead };
    }

}
