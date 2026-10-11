using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Where and how to connect to a game server, as handed out by the lobby API.</summary>
public class ConnectInfo {
    public string Host;
    public ushort Port;
    public bool Secure;
    public string PlayerSessionId;
    public string RoomId;
    public string RoomName;
}

/// <summary>
/// Client for the lobby API (Server/backend): lists rooms and turns "join / create / quick join" into a
/// GameLift player session. Without an API configured it offers a single local dev server instead.
/// </summary>
public class RoomDirectory : MonoBehaviour {

    private const int TimeoutSeconds = 15;
    private const string LocalId = "local";

    // JsonUtility mirrors of the API payloads.
    [Serializable] private class RoomJson { public string id; public string name; public int players; public int maxPlayers; public string status; public string mode; public string map; }
    [Serializable] private class RoomsJson { public RoomJson[] rooms; }
    [Serializable] private class ConnectJson { public string host; public int port; public string playerSessionId; }
    [Serializable] private class JoinJson { public RoomJson room; public ConnectJson connect; }
    [Serializable] private class ErrorJson { public string error; public string message; }
    [Serializable] private class CreateBody { public string name; public string playerName; public string mode; public string map; }
    [Serializable] private class JoinBody { public string roomId; public string playerName; }
    [Serializable] private class QuickBody { public string playerName; }

    public bool IsLocal => !NetConfig.HasDirectory;

    public IEnumerator List(Action<List<LobbyScreen.RoomEntry>> done, Action<string> fail) {
        if (IsLocal) {
            done(new List<LobbyScreen.RoomEntry> {
                new LobbyScreen.RoomEntry {
                    Id = LocalId, Name = L10n.T("로컬 서버  ") + NetConfig.DevHost + ":" + NetConfig.DevPort,
                    Players = 0, MaxPlayers = NetConfig.MaxPlayersPerRoom, IsOpen = true, Map = L10n.T("서버 설정"),
                },
            });
            yield break;
        }
        using (UnityWebRequest req = UnityWebRequest.Get(NetConfig.ApiBase + "/rooms")) {
            req.timeout = TimeoutSeconds;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) {
                fail(Describe(req));
                yield break;
            }
            RoomsJson body = JsonUtility.FromJson<RoomsJson>(req.downloadHandler.text);
            List<LobbyScreen.RoomEntry> rooms = new List<LobbyScreen.RoomEntry>();
            if (body != null && body.rooms != null) {
                foreach (RoomJson r in body.rooms) rooms.Add(ToEntry(r));
            }
            done(rooms);
        }
    }

    /// <param name="mode">Lobby mode key ("tdm" / "ffa" / "elim"); <paramref name="map"/> a map id. A local dev server ignores both.</param>
    public IEnumerator Create(string roomName, string playerName, string mode, string map, Action<ConnectInfo> done, Action<string> fail) {
        if (IsLocal) { done(LocalConnect()); yield break; }
        CreateBody body = new CreateBody { name = roomName, playerName = playerName, mode = mode, map = map };
        yield return Post("/rooms", JsonUtility.ToJson(body), done, fail);
    }

    public IEnumerator Join(string roomId, string playerName, Action<ConnectInfo> done, Action<string> fail) {
        if (IsLocal || roomId == LocalId) { done(LocalConnect()); yield break; }
        yield return Post("/rooms/join", JsonUtility.ToJson(new JoinBody { roomId = roomId, playerName = playerName }), done, fail);
    }

    public IEnumerator QuickJoin(string playerName, Action<ConnectInfo> done, Action<string> fail) {
        if (IsLocal) { done(LocalConnect()); yield break; }
        yield return Post("/quickjoin", JsonUtility.ToJson(new QuickBody { playerName = playerName }), done, fail);
    }

    private IEnumerator Post(string path, string json, Action<ConnectInfo> done, Action<string> fail) {
        using (UnityWebRequest req = new UnityWebRequest(NetConfig.ApiBase + path, "POST")) {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = TimeoutSeconds;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) {
                fail(Describe(req));
                yield break;
            }
            JoinJson body = JsonUtility.FromJson<JoinJson>(req.downloadHandler.text);
            if (body == null || body.connect == null || string.IsNullOrEmpty(body.connect.host)) {
                fail(L10n.T("서버 응답을 이해할 수 없습니다."));
                yield break;
            }
            done(new ConnectInfo {
                Host = body.connect.host,
                Port = (ushort)body.connect.port,
                // GameLift fleets generate TLS certificates; pages served over https can only open wss:// anyway.
                Secure = true,
                PlayerSessionId = body.connect.playerSessionId,
                RoomId = body.room != null ? body.room.id : null,
                RoomName = body.room != null ? body.room.name : null,
            });
        }
    }

    private static ConnectInfo LocalConnect() {
        return new ConnectInfo {
            Host = NetConfig.DevHost, Port = NetConfig.DevPort, Secure = false,
            RoomId = LocalId, RoomName = L10n.T("로컬 서버"),
        };
    }

    private static LobbyScreen.RoomEntry ToEntry(RoomJson r) {
        int max = r.maxPlayers > 0 ? r.maxPlayers : NetConfig.MaxPlayersPerRoom;
        return new LobbyScreen.RoomEntry {
            Id = r.id, Name = string.IsNullOrEmpty(r.name) ? L10n.T("이름 없는 작전") : r.name,
            Players = r.players, MaxPlayers = max,
            IsOpen = r.status == "ACTIVE" && r.players < max,
            // "창고 · 섬멸전"; rooms from before modes existed have neither and are the defaults.
            Map = MapLoader.DisplayName(r.map) + "  ·  " + Modes.DisplayName(Modes.Parse(r.mode)),
        };
    }

    /// <summary>Korean, player-facing explanation of a failed request.</summary>
    private static string Describe(UnityWebRequest req) {
        string code = null;
        if (req.downloadHandler != null && !string.IsNullOrEmpty(req.downloadHandler.text)) {
            try {
                code = JsonUtility.FromJson<ErrorJson>(req.downloadHandler.text)?.error;
            } catch (ArgumentException) {
                // Not JSON (e.g. a gateway error page).
            }
        }
        switch (code) {
            case "ROOM_FULL": return L10n.T("방이 가득 찼습니다. 다른 방을 골라 주세요.");
            case "NOT_FOUND": return L10n.T("이미 종료된 방입니다. 목록을 새로 고칩니다.");
            case "FLEET_FULL": return L10n.T("지금은 모든 서버가 가득 찼습니다. 잠시 후 다시 시도해 주세요.");
            case "SERVER_STARTING": return L10n.T("서버를 준비하는 중입니다. 몇 초 후 다시 시도해 주세요.");
            case "BAD_REQUEST": return L10n.T("요청이 올바르지 않습니다. 이름을 확인해 주세요.");
        }
        if (req.result == UnityWebRequest.Result.ConnectionError) return L10n.T("로비 서버에 연결할 수 없습니다. 인터넷 연결을 확인해 주세요.");
        if (req.responseCode == 429) return L10n.T("요청이 너무 많습니다. 잠시 후 다시 시도해 주세요.");
        return L10n.T("로비 서버 오류가 발생했습니다 (") + req.responseCode + ").";
    }

}
