using FishNet.Object;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Floating callsign above other players, always turned toward the local camera. Blue for teammates, red for enemies
/// (everyone is an enemy in free-for-all). Hidden while a smoke cloud stands between it and the camera.
/// </summary>
public class NameTag : NetworkBehaviour {

    [SerializeField]
    private Text nameText;

    private NetPlayer player;
    private bool smoked;

    public override void OnStartClient() {
        // The default font has no Hangul glyphs; Korean callsigns need Noto Sans KR.
        if (UITheme.LegacyFont != null) {
            nameText.font = UITheme.LegacyFont;
        }
        nameText.text = "";
    }

    void Update() {
        if (!IsClientInitialized) return;
        // The owner's NetPlayer may arrive after the avatar; keep looking until it does.
        if (player == null && Owner != null) {
            player = NetPlayer.ForClient(Owner.ClientId);
        }
        if (player != null && nameText.text != player.Callsign.Value) {
            nameText.text = player.Callsign.Value;
        }
        // Teams can change between rounds (rebalancing), and the local player's NetPlayer may arrive late too.
        if (Owner != null) {
            Color c = Teams.IsHostileToLocal(OwnerId) ? Teams.EnemyColor : Teams.AllyColor;
            if (nameText.color != c) nameText.color = c;
        }
    }

    void LateUpdate() {
        Camera cam = Camera.main;
        if (cam == null) return;
        Vector3 away = transform.position - cam.transform.position;
        if (away.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        // Through smoke (or inside it) nobody can read a name. Alpha, not enabled: the spectator hides tags that way.
        bool hide = SmokeCloud.All.Count > 0 && GrenadeWorld.BlocksSight(cam.transform.position, transform.position);
        if (hide != smoked) {
            smoked = hide;
            nameText.canvasRenderer.SetAlpha(hide ? 0f : 1f);
        }
    }

}
