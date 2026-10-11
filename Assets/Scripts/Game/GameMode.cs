using System.Collections.Generic;
using UnityEngine;

/// <summary>How a session is scored. The lobby backend stores it as the GameLift game property `mode`.</summary>
public enum GameMode : byte {
    TeamDeathmatch = 0,     // "tdm": 4v4, team score = kills
    FreeForAll = 1,         // "ffa": up to 8, every player for themselves
    Elimination = 2,        // "elim": 4v4 rounds without respawns, first side to win 7 rounds takes the match
}

/// <summary>Side in a team mode. Free-for-all players are all <see cref="None"/>.</summary>
public enum Team : byte {
    None = 0,
    A = 1,
    B = 2,
}

/// <summary>Keys, names and limits of the game modes.</summary>
public static class Modes {

    public const GameMode Default = GameMode.TeamDeathmatch;
    public const int TeamSize = 4;

    /// <summary>Every mode, in the order the lobby offers them.</summary>
    public static readonly GameMode[] All = { GameMode.TeamDeathmatch, GameMode.FreeForAll, GameMode.Elimination };

    /// <summary>"tdm" / "ffa" / "elim" (any case) to a mode; anything else is the default.</summary>
    public static GameMode Parse(string key) {
        switch ((key ?? "").Trim().ToLowerInvariant()) {
            case "ffa": return GameMode.FreeForAll;
            case "elim": return GameMode.Elimination;
            case "tdm": return GameMode.TeamDeathmatch;
            default: return Default;
        }
    }

    public static bool IsKnown(string key) {
        string k = (key ?? "").Trim().ToLowerInvariant();
        return k == "tdm" || k == "ffa" || k == "elim";
    }

    public static string Key(GameMode mode) {
        switch (mode) {
            case GameMode.FreeForAll: return "ffa";
            case GameMode.Elimination: return "elim";
            default: return "tdm";
        }
    }

    public static string DisplayName(GameMode mode) {
        switch (mode) {
            case GameMode.FreeForAll: return L10n.T("개인전");
            case GameMode.Elimination: return L10n.T("섬멸전");
            default: return L10n.T("팀 데스매치");
        }
    }

    public static bool IsTeamMode(GameMode mode) {
        return mode == GameMode.TeamDeathmatch || mode == GameMode.Elimination;
    }

    /// <summary>Played in rounds without respawns (elimination).</summary>
    public static bool IsRoundBased(GameMode mode) {
        return mode == GameMode.Elimination;
    }

    /// <summary>
    /// What ends the match early: team kills in TDM, one player's kills in FFA, round wins in elimination.
    /// </summary>
    public static int ScoreLimit(GameMode mode) {
        switch (mode) {
            case GameMode.FreeForAll: return 30;
            case GameMode.Elimination: return 7;
            default: return 50;
        }
    }

    /// <summary>The goal as the HUD words it: "50킬", or "7승" for rounds.</summary>
    public static string GoalText(GameMode mode, int limit) {
        return IsRoundBased(mode) ? limit + L10n.T("승") : limit + L10n.T("킬");
    }

}

/// <summary>
/// Who is on which side, from the replicated <see cref="NetPlayer.Team"/>. The server uses it to assign and balance
/// teams and to rule out friendly fire; clients use it to colour name tags, the scoreboard and the HUD.
/// </summary>
public static class Teams {

    public static readonly Color AllyColor = new Color(0.31f, 0.64f, 0.89f);      // #4FA3E3
    public static readonly Color EnemyColor = new Color(0.85f, 0.26f, 0.18f);     // UITheme.Red
    public const string AllyHex = "#4FA3E3";
    public const string EnemyHex = "#D8432E";

    public static GameMode Mode => MatchManager.Instance != null ? MatchManager.Instance.Mode : Modes.Default;
    public static bool TeamMode => Modes.IsTeamMode(Mode);

    public static string Name(Team team) {
        return team == Team.A ? L10n.T("알파") : team == Team.B ? L10n.T("브라보") : "";
    }

    public static Team Other(Team team) {
        return team == Team.A ? Team.B : team == Team.B ? Team.A : Team.None;
    }

    public static Team Of(int clientId) {
        NetPlayer p = NetPlayer.ForClient(clientId);
        return p != null ? p.Team : Team.None;
    }

    /// <summary>Can <paramref name="attackerId"/> hurt <paramref name="victimId"/>? No friendly fire in team modes.</summary>
    public static bool CanDamage(int attackerId, int victimId) {
        if (attackerId == victimId) return false;
        if (!TeamMode) return true;
        Team a = Of(attackerId);
        return a == Team.None || a != Of(victimId);
    }

    private static int blockedHits;

    /// <summary>Server, before a hit is applied: false (and the hit is dropped) for friendly fire.</summary>
    public static bool AllowDamage(PlayerHealth attacker, PlayerHealth victim) {
        if (attacker == null || victim == null || CanDamage(attacker.OwnerId, victim.OwnerId)) return true;
        blockedHits++;
        if (blockedHits % 20 == 1) {
            Debug.Log("[Match] Friendly fire blocked: client " + attacker.OwnerId + " -> " + victim.OwnerId + " (team " + Of(victim.OwnerId) +
                      ", " + blockedHits + " blocked so far)");
        }
        return false;
    }

    /// <summary>Is the owner of <paramref name="clientId"/> on the other side from the local player?</summary>
    public static bool IsHostileToLocal(int clientId) {
        NetPlayer me = NetPlayer.Local;
        if (me == null) return true;
        if (clientId == me.ClientId) return false;
        return !TeamMode || me.Team == Team.None || Of(clientId) != me.Team;
    }

    public static bool IsHostileToLocal(PlayerHealth avatar) {
        return avatar == null || avatar.Owner == null || IsHostileToLocal(avatar.OwnerId);
    }

    /// <summary>Server: the side a newly joined player goes to (the smaller one; on a tie the one that is behind).</summary>
    public static Team Pick(IEnumerable<NetPlayer> others, GameMode mode) {
        if (!Modes.IsTeamMode(mode)) return Team.None;
        int a = 0, b = 0;
        foreach (NetPlayer p in others) {
            if (p.Team == Team.A) a++;
            else if (p.Team == Team.B) b++;
        }
        if (a != b) return a < b ? Team.A : Team.B;
        MatchManager mm = MatchManager.Instance;
        if (mm != null && mm.ScoreOf(Team.A) != mm.ScoreOf(Team.B)) return mm.ScoreOf(Team.A) < mm.ScoreOf(Team.B) ? Team.A : Team.B;
        return Random.value < 0.5f ? Team.A : Team.B;
    }

    /// <summary>
    /// Server, between rounds: even the sides out after people left (at most one apart), moving the lowest scorers of
    /// the bigger side. Returns the players that changed sides.
    /// </summary>
    public static List<NetPlayer> Rebalance(ICollection<NetPlayer> players, GameMode mode) {
        List<NetPlayer> moved = new List<NetPlayer>();
        if (!Modes.IsTeamMode(mode)) return moved;
        List<NetPlayer> a = new List<NetPlayer>(), b = new List<NetPlayer>();
        foreach (NetPlayer p in players) {
            if (p.Team == Team.A) a.Add(p);
            else if (p.Team == Team.B) b.Add(p);
            else {
                // Joined while the mode was something else: put them anywhere, the loop below evens it out.
                p.Team = Team.A;
                a.Add(p);
            }
        }
        while (Mathf.Abs(a.Count - b.Count) > 1) {
            List<NetPlayer> from = a.Count > b.Count ? a : b, to = from == a ? b : a;
            from.Sort((x, y) => x.Score.Value.CompareTo(y.Score.Value));
            NetPlayer p = from[0];
            from.RemoveAt(0);
            p.Team = to == a ? Team.A : Team.B;
            to.Add(p);
            moved.Add(p);
        }
        return moved;
    }

}
