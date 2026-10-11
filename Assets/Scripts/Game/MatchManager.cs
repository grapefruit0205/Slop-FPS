using System;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;
using SlopFPS.Input;

/// <summary>Where an elimination round stands. Other modes stay at <see cref="None"/>.</summary>
public enum RoundPhase : byte {
    None = 0,
    Waiting = 1,    // a side has nobody on it: warm up (respawns on) until both have someone
    Freeze = 2,     // everyone just deployed: look around, no moving or shooting
    Live = 3,       // no respawns; ends when a side is wiped out or the clock runs out
    RoundEnd = 4,   // result on screen, then the next round (or the match result)
}

/// <summary>
/// Round clock, rules, score and killfeed for one game session. Spawned by the server; the round end is a network
/// tick so every client counts down against the same synchronized clock. The server decides the mode (team deathmatch
/// or free-for-all) and map once per session and replicates them here; a round ends at the time limit or as soon as a
/// team (TDM) or a player (FFA) reaches the score limit.
/// Elimination plays the match as rounds instead (Waiting → Freeze → Live → RoundEnd → Freeze …): every round
/// everyone deploys fresh, the dead stay dead and watch their team, and a round goes to the side that wipes the other
/// out, or when the clock runs out to the side with more players alive (equal is a draw, which scores for nobody). The
/// team scores count round wins; the first side to <see cref="ScoreLimit"/> takes the match, then the usual
/// intermission. Everything is decided here on the server and replicated through SyncVars.
/// </summary>
public class MatchManager : NetworkBehaviour {

    public const float MatchLength = 600f;
    public const float IntermissionLength = 12f;
    public const float EliminationRoundLength = 120f;
    public const float FreezeLength = 5f;
    public const float RoundEndLength = 5f;

    public static MatchManager Instance { get; private set; }

    /// <summary>Client side: the round just ended / a new round just began.</summary>
    public event Action MatchEnded;
    public event Action MatchStarted;

    /// <summary>Server side: the intermission is over and players should be reset.</summary>
    public event Action ServerRoundRestart;

    /// <summary>
    /// Server side, elimination: a round is about to start, put everyone back at their side's spawns with full health
    /// and a fresh loadout. True for the first round of a match.
    /// </summary>
    public event Action<bool> ServerRoundBegin;

    private readonly SyncVar<uint> endTick = new SyncVar<uint>();
    private readonly SyncVar<bool> over = new SyncVar<bool>();
    private readonly SyncVar<uint> overTick = new SyncVar<uint>();
    public readonly SyncVar<string> RoomName = new SyncVar<string>("");

    private readonly SyncVar<byte> mode = new SyncVar<byte>((byte)Modes.Default);
    private readonly SyncVar<string> mapId = new SyncVar<string>("");
    private readonly SyncVar<int> scoreLimit = new SyncVar<int>(Modes.ScoreLimit(Modes.Default));
    private readonly SyncVar<float> roundLength = new SyncVar<float>(MatchLength);
    private readonly SyncVar<int> scoreA = new SyncVar<int>();
    private readonly SyncVar<int> scoreB = new SyncVar<int>();
    private readonly SyncVar<byte> winnerTeam = new SyncVar<byte>();
    private readonly SyncVar<string> winnerName = new SyncVar<string>("");

    // Elimination rounds (see RoundPhase).
    private readonly SyncVar<byte> phase = new SyncVar<byte>();
    private readonly SyncVar<uint> phaseEndTick = new SyncVar<uint>();
    private readonly SyncVar<int> round = new SyncVar<int>();
    private readonly SyncVar<byte> aliveA = new SyncVar<byte>();
    private readonly SyncVar<byte> aliveB = new SyncVar<byte>();
    private readonly SyncVar<byte> roundWinner = new SyncVar<byte>();
    private readonly SyncVar<bool> roundByTime = new SyncVar<bool>();
    private readonly SyncVar<float> roundClock = new SyncVar<float>();

    public GameMode Mode => (GameMode)mode.Value;
    public bool IsTeamMode => Modes.IsTeamMode(Mode);
    /// <summary>Map id of the session (see <see cref="MapLoader"/>).</summary>
    public string MapId => mapId.Value;
    public int ScoreLimit => scoreLimit.Value;
    public float RoundLength => roundLength.Value;

    /// <summary>Round result once it is over: the winning side (TDM; None = draw) or the top player's callsign (FFA).</summary>
    public Team WinnerTeam => (Team)winnerTeam.Value;
    public string WinnerName => winnerName.Value;

    public bool HasMatch => endTick.Value != 0;
    public bool IsOver => over.Value;
    public bool WaitingForPlayers => !IsOver && Phase == RoundPhase.Waiting;

    /// <summary>The clock: the match's in TDM/FFA, the round's in elimination (full while frozen or waiting).</summary>
    public float TimeLeft {
        get {
            if (!HasMatch) return RoundLength;
            if (IsOver) return 0f;
            if (WaitingForPlayers) return RoundLength;
            if (IsRoundBased) {
                if (Phase == RoundPhase.Live) return Mathf.Max(0f, SecondsUntil(endTick.Value));
                return Phase == RoundPhase.RoundEnd ? roundClock.Value : RoundLength;
            }
            return Mathf.Max(0f, SecondsUntil(endTick.Value));
        }
    }
    public float IntermissionLeft => !IsOver ? IntermissionLength : Mathf.Max(0f, SecondsUntil(overTick.Value) + IntermissionLength);

    public bool IsRoundBased => Modes.IsRoundBased(Mode);
    public RoundPhase Phase => (RoundPhase)phase.Value;
    /// <summary>Seconds left of the freeze or of the round-end pause.</summary>
    public float PhaseLeft => Mathf.Max(0f, SecondsUntil(phaseEndTick.Value));
    /// <summary>Elimination: the round being played (1-based; 0 before the first).</summary>
    public int Round => round.Value;
    /// <summary>Elimination: the last round's winner (None = draw) and whether the clock decided it.</summary>
    public Team RoundWinner => (Team)roundWinner.Value;
    public bool RoundByTime => roundByTime.Value;

    /// <summary>Elimination: players of <paramref name="team"/> still standing this round.</summary>
    public int AliveOf(Team team) {
        return team == Team.A ? aliveA.Value : team == Team.B ? aliveB.Value : 0;
    }

    /// <summary>Elimination: everyone deployed, nobody may move or shoot yet.</summary>
    public bool InFreeze => IsRoundBased && !IsOver && Phase == RoundPhase.Freeze;

    /// <summary>Do the dead come back (after the usual delay)? Not during an elimination round.</summary>
    public bool AllowsRespawn => !IsRoundBased || Phase == RoundPhase.Waiting || Phase == RoundPhase.None;

    /// <summary>Does someone joining now get an avatar straight away? In elimination only until the round goes live.</summary>
    public bool AllowsJoinSpawn => AllowsRespawn || Phase == RoundPhase.Freeze;

    /// <summary>Do hits count? Not after the match, nor in elimination's freeze or round-end pause.</summary>
    public bool AllowsDamage => !IsOver && !(IsRoundBased && (Phase == RoundPhase.Freeze || Phase == RoundPhase.RoundEnd));

    public int ScoreOf(Team team) {
        return team == Team.A ? scoreA.Value : team == Team.B ? scoreB.Value : 0;
    }

    private float SecondsUntil(uint tick) {
        return (float)(((long)tick - TimeManager.Tick) * TimeManager.TickDelta);
    }

    public override void OnStartNetwork() {
        Instance = this;
        over.OnChange += OnOverChanged;
        endTick.OnChange += OnEndTickChanged;
        mapId.OnChange += OnMapChanged;
    }

    public override void OnStopNetwork() {
        if (Instance == this) Instance = null;
        FpsInputGate.Frozen = false;
    }

    public override void OnStartClient() {
        // The dead watch their team in elimination.
        if (GetComponent<Spectator>() == null) gameObject.AddComponent<Spectator>();
    }

    public override void OnStartServer() {
        StartNewMatch();
    }

    private void OnOverChanged(bool prev, bool next, bool asServer) {
        if (asServer || prev == next) return;
        if (next) MatchEnded?.Invoke();
    }

    private void OnEndTickChanged(uint prev, uint next, bool asServer) {
        if (asServer || next == 0) return;
        // Joining mid-round also delivers a value; only announce rounds that have just begun.
        if (prev != 0 || TimeLeft > RoundLength - 5f) MatchStarted?.Invoke();
    }

    private void OnMapChanged(string prev, string next, bool asServer) {
        // Clients normally have the map from the join reply already; this covers anything that missed it.
        if (asServer || string.IsNullOrEmpty(next)) return;
        MapLoader.Load(next);
    }

    /// <summary>
    /// Server (also before spawning): the session's rules. The length takes effect from the next round, so set them
    /// before the first one starts.
    /// </summary>
    public void Configure(GameMode gameMode, string map, int limit, float length) {
        mode.Value = (byte)gameMode;
        mapId.Value = map;
        scoreLimit.Value = limit > 0 ? limit : Modes.ScoreLimit(gameMode);
        roundLength.Value = length > 0f ? length : Modes.IsRoundBased(gameMode) ? EliminationRoundLength : MatchLength;
    }

    [Server]
    public void StartNewMatch() {
        over.Value = false;
        overTick.Value = 0;
        scoreA.Value = 0;
        scoreB.Value = 0;
        winnerTeam.Value = (byte)Team.None;
        winnerName.Value = "";
        round.Value = 0;
        roundWinner.Value = (byte)Team.None;
        roundByTime.Value = false;
        // Initial sessions wait for two. An intermission has already reset the players; start its next match
        // directly when enough remain, rather than despawning and deploying everyone twice.
        bool ready = !IsRoundBased && ReadyToStart();
        phase.Value = (byte)(ready ? RoundPhase.None : RoundPhase.Waiting);
        phaseEndTick.Value = 0;
        endTick.Value = TimeManager.Tick + TimeManager.TimeToTicks(RoundLength);
        Debug.Log(ready ? "[Match] New " + Modes.Key(Mode) + " match with " + NetPlayer.All.Count + " players"
            : "[Match] Waiting for " + NetConfig.MinPlayersToStart + " players (" + Modes.Key(Mode) + ")");
    }

    void Update() {
        // Client: hold the local player still while the round is frozen (looking around is fine).
        if (IsClientInitialized) FpsInputGate.Frozen = InFreeze;
        if (!IsServerInitialized || !HasMatch) return;
        if (over.Value) {
            if (IntermissionLeft <= 0f) {
                ServerRoundRestart?.Invoke();
                StartNewMatch();
            }
        } else if (!IsRoundBased && WaitingForPlayers) {
            if (ReadyToStart()) {
                // Warmup kills and elapsed time must not carry into the match.
                ServerRoundRestart?.Invoke();
                phase.Value = (byte)RoundPhase.None;
                endTick.Value = TimeManager.Tick + TimeManager.TimeToTicks(RoundLength);
                Debug.Log("[Match] Started " + Modes.Key(Mode) + " with " + NetPlayer.All.Count + " players");
            }
        } else if (IsRoundBased) {
            UpdateRounds();
        } else if (TimeLeft <= 0f) {
            EndMatch();
        }
    }

    // ------------------------------------------------------------------ elimination rounds (server)

    private bool ReadyToStart() {
        int count = 0, a = 0, b = 0;
        foreach (NetPlayer p in NetPlayer.All) {
            if (p.Owner == null || !p.Owner.IsActive) continue;
            count++;
            if (p.Team == Team.A) a++;
            if (p.Team == Team.B) b++;
        }
        return count >= NetConfig.MinPlayersToStart && (!IsTeamMode || (a > 0 && b > 0));
    }

    /// <summary>Server: count who is still standing and move the round along.</summary>
    [Server]
    private void UpdateRounds() {
        int inA = 0, inB = 0, upA = 0, upB = 0;
        foreach (NetPlayer p in NetPlayer.All) {
            if (p.Team == Team.A) {
                inA++;
                if (p.Alive.Value) upA++;
            } else if (p.Team == Team.B) {
                inB++;
                if (p.Alive.Value) upB++;
            }
        }
        // A player who left is gone from NetPlayer.All, so a disconnect counts as a death.
        if (aliveA.Value != upA) aliveA.Value = (byte)upA;
        if (aliveB.Value != upB) aliveB.Value = (byte)upB;

        switch (Phase) {
            case RoundPhase.Waiting:
                if (ReadyToStart()) BeginRound();
                break;
            case RoundPhase.Freeze:
                if (upA == 0 || upB == 0) {
                    // A whole side left during the freeze.
                    EndRound(upA == upB ? Team.None : upA > 0 ? Team.A : Team.B, false);
                } else if (PhaseLeft <= 0f) {
                    phase.Value = (byte)RoundPhase.Live;
                    endTick.Value = TimeManager.Tick + TimeManager.TimeToTicks(RoundLength);
                    Debug.Log("[Match] Round " + Round + " live: A " + upA + " vs B " + upB + ", " + Mathf.RoundToInt(RoundLength) + " s on the clock");
                }
                break;
            case RoundPhase.Live:
                if (upA == 0 || upB == 0) {
                    EndRound(upA == upB ? Team.None : upA > 0 ? Team.A : Team.B, false);
                } else if (TimeLeft <= 0f) {
                    // Time: the side with more players standing takes it; level is a draw.
                    EndRound(upA == upB ? Team.None : upA > upB ? Team.A : Team.B, true);
                }
                break;
            case RoundPhase.RoundEnd:
                if (PhaseLeft > 0f) break;
                if (scoreA.Value >= ScoreLimit || scoreB.Value >= ScoreLimit) {
                    EndMatch();
                } else if (ReadyToStart()) {
                    BeginRound();
                } else {
                    phase.Value = (byte)RoundPhase.Waiting;
                    Debug.Log("[Match] Round " + Round + " cannot start: A " + inA + " / B " + inB + " players, waiting");
                    MessageLocalized("event.wait");
                }
                break;
        }
    }

    /// <summary>Server: everyone back to their side's spawns, then a short freeze.</summary>
    [Server]
    private void BeginRound() {
        round.Value++;
        roundWinner.Value = (byte)Team.None;
        roundByTime.Value = false;
        // First the players (rebalanced, fresh avatars), so the counts below are of a full round.
        ServerRoundBegin?.Invoke(round.Value == 1);
        phase.Value = (byte)RoundPhase.Freeze;
        phaseEndTick.Value = TimeManager.Tick + TimeManager.TimeToTicks(FreezeLength);
        endTick.Value = phaseEndTick.Value + TimeManager.TimeToTicks(RoundLength);
        int a = 0, b = 0;
        foreach (NetPlayer p in NetPlayer.All) {
            if (p.Team == Team.A && p.Alive.Value) a++;
            else if (p.Team == Team.B && p.Alive.Value) b++;
        }
        aliveA.Value = (byte)a;
        aliveB.Value = (byte)b;
        Debug.Log("[Match] Round " + Round + " freeze: A " + a + " vs B " + b + ", rounds A " + scoreA.Value + " : " + scoreB.Value + " B");
        MessageLocalized("event.round", Round.ToString(), scoreA.Value.ToString(), scoreB.Value.ToString(), Mathf.RoundToInt(FreezeLength).ToString());
    }

    /// <summary>Server: the round is decided. <paramref name="winner"/> None is a draw.</summary>
    [Server]
    private void EndRound(Team winner, bool byTime) {
        roundClock.Value = TimeLeft;
        if (winner == Team.A) scoreA.Value++;
        else if (winner == Team.B) scoreB.Value++;
        roundWinner.Value = (byte)winner;
        roundByTime.Value = byTime;
        phase.Value = (byte)RoundPhase.RoundEnd;
        phaseEndTick.Value = TimeManager.Tick + TimeManager.TimeToTicks(RoundEndLength);
        Debug.Log("[Match] Round " + Round + " over (" + (byTime ? "time" : "elimination") + "): " + (winner == Team.None ? "draw" : "winner " + winner) +
                  ", alive A " + aliveA.Value + " / B " + aliveB.Value + ", rounds A " + scoreA.Value + " : " + scoreB.Value + " B");
        MessageLocalized("event.roundend", Round.ToString(), scoreA.Value.ToString(), scoreB.Value.ToString());
    }

    /// <summary>"알파 승리" / "브라보 승리" / "무승부".</summary>
    public static string RoundResultText(Team winner) {
        return winner == Team.None ? L10n.T("무승부") : Teams.Name(winner) + L10n.T(" 승리");
    }

    /// <summary>Server: the round is over (time limit or score limit). Works out the winner and starts the intermission.</summary>
    [Server]
    private void EndMatch() {
        if (over.Value) return;
        if (IsTeamMode) {
            Team w = scoreA.Value == scoreB.Value ? Team.None : scoreA.Value > scoreB.Value ? Team.A : Team.B;
            winnerTeam.Value = (byte)w;
        } else {
            NetPlayer top = null;
            foreach (NetPlayer p in NetPlayer.All) {
                if (top == null || p.Kills.Value > top.Kills.Value || (p.Kills.Value == top.Kills.Value && p.Score.Value > top.Score.Value)) top = p;
            }
            winnerName.Value = top != null ? top.Callsign.Value : "";
        }
        overTick.Value = TimeManager.Tick;
        over.Value = true;
        Debug.Log("[Match] " + (IsRoundBased ? "Match" : "Round") + " over (" + Modes.Key(Mode) + "): " + (IsTeamMode ? "A " + scoreA.Value + " : " + scoreB.Value + " B, winner " + WinnerTeam
                                                                                    : "winner " + winnerName.Value));
        MessageLocalized("event.end", Mathf.RoundToInt(IntermissionLength).ToString());
    }

    /// <summary>Server: count a kill towards the round (team score in TDM) and end the round at the score limit.</summary>
    [Server]
    private void ScoreKill(int killerId, int victimId) {
        if (over.Value || WaitingForPlayers || killerId == victimId) return;
        NetPlayer killer = NetPlayer.ForClient(killerId);
        if (killer == null) return;
        // Elimination scores rounds, not kills (UpdateRounds sees the death through NetPlayer.Alive).
        if (IsRoundBased) return;
        if (IsTeamMode) {
            Team side = killer.Team;
            if (side == Team.None || side == Teams.Of(victimId)) return;
            if (side == Team.A) scoreA.Value++;
            else scoreB.Value++;
            Debug.Log("[Match] Score A " + scoreA.Value + " : " + scoreB.Value + " B (" + killer.Callsign.Value + ", team " + side + ")");
            if (ScoreOf(side) >= ScoreLimit) EndMatch();
        } else {
            Debug.Log("[Match] " + killer.Callsign.Value + " has " + killer.Kills.Value + "/" + ScoreLimit + " kills");
            if (killer.Kills.Value >= ScoreLimit) EndMatch();
        }
    }

    /// <summary>Server: announce a kill to everyone (killfeed + event log) and score it.</summary>
    [Server]
    public void AnnounceKill(int killerId, string killer, int victimId, string victim, string weapon, bool headshot) {
        RpcKill(killerId, killer, victimId, victim, weapon, headshot);
        ScoreKill(killerId, victimId);
    }

    [ObserversRpc]
    private void RpcKill(int killerId, string killer, int victimId, string victim, string weapon, bool headshot) {
        GameUI ui = GameUI.Instance;
        if (ui == null) return;
        int me = ClientManager.Connection != null ? ClientManager.Connection.ClientId : -1;
        ui.Hud.AddKill(killer, victim, weapon, headshot, killerId == me, victimId == me);
        if (killerId == me && victimId != me) ui.Hud.Hitmarker(headshot, true);
    }

    /// <summary>Server: a line in everyone's event log. Callers escape player names with UITheme.Safe.</summary>
    [Server]
    public void Message(string text) {
        RpcMessage(text);
    }

    [ObserversRpc]
    private void RpcMessage(string text) {
        if (GameUI.Instance != null) GameUI.Instance.Hud.AddMessage(text);
    }

    [Server]
    public void MessageLocalized(string key, params string[] args) {
        RpcLocalizedMessage(key, args);
    }

    [ObserversRpc]
    private void RpcLocalizedMessage(string key, string[] args) {
        if (GameUI.Instance != null) GameUI.Instance.Hud.AddMessage(L10n.Format(key, args));
    }

    // ------------------------------------------------------------------ client-side presentation

    /// <summary>Client: the big line over the results screen, from the local player's point of view.</summary>
    public string ResultHeadline() {
        if (!IsOver) return L10n.T("작전 종료");
        NetPlayer me = NetPlayer.Local;
        if (IsTeamMode) {
            Team mine = me != null ? me.Team : Team.None;
            if (WinnerTeam == Team.None) return L10n.T("무승부");
            if (mine == Team.None) return Teams.Name(WinnerTeam) + L10n.T(" 팀 승리");
            return WinnerTeam == mine ? "<color=" + Teams.AllyHex + L10n.T(">승리</color>") : "<color=" + Teams.EnemyHex + L10n.T(">패배</color>");
        }
        return me != null && WinnerName == me.Callsign.Value ? L10n.T("1위 달성") : L10n.T("작전 종료");
    }

    /// <summary>Client: the final team scores or the leading player's name.</summary>
    public string ResultDetail() {
        if (IsTeamMode) {
            return "<color=" + SideHex(Team.A) + ">" + Teams.Name(Team.A) + " " + scoreA.Value + "</color>  :  <color=" + SideHex(Team.B) + ">" +
                   scoreB.Value + " " + Teams.Name(Team.B) + "</color>";
        }
        return string.IsNullOrEmpty(WinnerName) ? "" : "<color=#E3A72F>" + UITheme.Safe(WinnerName) + L10n.T("</color> 님이 이번 작전 1위입니다.");
    }

    /// <summary>Client: blue for the local player's side, red for the other.</summary>
    public static string SideHex(Team team) {
        NetPlayer me = NetPlayer.Local;
        bool mine = me != null && me.Team == team;
        return mine ? Teams.AllyHex : Teams.EnemyHex;
    }

}
