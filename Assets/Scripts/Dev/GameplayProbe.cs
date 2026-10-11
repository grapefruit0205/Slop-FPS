#if !UNITY_WEBGL
using System;
using System.Collections;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Real multiplayer regression client. Run with -gameplaycheck -connect host:port; never contacts AWS.</summary>
public class GameplayProbe : MonoBehaviour {
    private WeaponId savedPrimary;
    private OpticId savedOptic;
    private float deadline;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install() {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "-gameplaycheck") >= 0)
            new GameObject("GameplayProbe").AddComponent<GameplayProbe>();
    }

    private IEnumerator Start() {
        if (NetConfig.DirectConnect == null || NetConfig.HasDirectory) {
            Fail("A local -connect address is required");
            yield break;
        }
        savedPrimary = LoadoutChoice.Primary;
        savedOptic = LoadoutChoice.Optic;
        yield return null;
        GameUI ui = GameUI.Instance;
        if (ui == null) { Fail("UI missing"); yield break; }
        ui.IgnoreKeys = true;
        ui.Lobby.Callsign = "Probe1";
        Button primary = ui.Lobby.GetComponentInChildren<LoadoutPicker>().GetComponentsInChildren<Button>()
            .First(b => b.GetComponentInChildren<TMP_Text>().text == "MP5");
        primary.onClick.Invoke();
        GameNetwork.Instance.Connect(new ConnectInfo { Host = NetConfig.DevHost, Port = NetConfig.DevPort, Secure = false, RoomName = "Regression" });
        deadline = Time.realtimeSinceStartup + 15;
        while (PlayerNetworkMover.Local == null || MatchManager.Instance == null) {
            if (Expired("Initial spawn")) yield break;
            yield return null;
        }
        MatchManager match = MatchManager.Instance;
        float before = match.TimeLeft;
        yield return new WaitForSecondsRealtime(3);
        if (!match.WaitingForPlayers || Mathf.Abs(match.TimeLeft - before) > 0.1f) { Fail("Solo must stay in warmup with a full clock"); yield break; }
        if (PlayerNetworkMover.Local.GetComponent<Loadout>().Primary != WeaponId.SMG) { Fail("Lobby weapon not equipped"); yield break; }
        Debug.Log("[GameplayProbe] SOLO_READY: warmup clock held; lobby MP5 equipped");
        deadline = Time.realtimeSinceStartup + 25;
        while (match.WaitingForPlayers || (match.IsRoundBased && match.Phase != RoundPhase.Live) || PlayerNetworkMover.Local == null) {
            if (Expired("Two-player start")) yield break;
            yield return null;
        }
        if (NetPlayer.All.Count != 2) { Fail("Expected exactly two players"); yield break; }
        if (match.IsTeamMode && NetPlayer.All[0].Team == NetPlayer.All[1].Team) { Fail("Teams not balanced"); yield break; }
        Debug.Log("[GameplayProbe] PASS: match live with exactly 2 players");

        ui.Pause.SetVisible(true);
        Button next = ui.Pause.GetComponentsInChildren<LoadoutPicker>(true).Single().GetComponentsInChildren<Button>()
            .First(b => b.GetComponentInChildren<TMP_Text>().text == "M700");
        next.onClick.Invoke();
        deadline = Time.realtimeSinceStartup + 4;
        while (NetPlayer.Local.Primary != WeaponId.Sniper) {
            if (Expired("Next loadout synchronization")) yield break;
            yield return null;
        }
        if (PlayerNetworkMover.Local.GetComponent<Loadout>().Primary != WeaponId.SMG) { Fail("Live weapon changed before redeployment"); yield break; }
        ui.Pause.SetVisible(false);
        Debug.Log("[GameplayProbe] PASS: menu choice synchronized for next deployment");

        // The other client is a fair DevBot; its shots cause a real death and redeployment.
        deadline = Time.realtimeSinceStartup + 35;
        while (PlayerNetworkMover.Local == null || PlayerNetworkMover.Local.GetComponent<Loadout>().Primary != WeaponId.Sniper) {
            if (Expired("Selected weapon on respawn/next round")) yield break;
            yield return null;
        }
        Debug.Log("[GameplayProbe] ALL PASSED: next deployment equipped M700");
        LoadoutChoice.Set(savedPrimary, savedOptic, false);
        Application.Quit(0);
    }

    private bool Expired(string step) {
        if (Time.realtimeSinceStartup < deadline) return false;
        Fail("Timeout: " + step);
        return true;
    }

    private void Fail(string why) {
        Debug.LogError("[GameplayProbe] FAILED: " + why);
        LoadoutChoice.Set(savedPrimary, savedOptic, false);
        Application.Quit(1);
    }
}
#endif
