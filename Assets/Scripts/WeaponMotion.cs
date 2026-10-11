using System.Collections;
using UnityEngine;

/// <summary>
/// Procedural first-person weapon motion (recoil kick, reload dip). It inserts a pivot
/// above the animated gun so the keyframed shooting animation keeps working untouched.
/// </summary>
public class WeaponMotion : MonoBehaviour {

    [SerializeField]
    private float kickBack = 0.035f;
    [SerializeField]
    private float kickPitch = 2.2f;
    [SerializeField]
    private float returnSpeed = 14f;

    private Vector3 basePosition;
    private Quaternion baseRotation = Quaternion.identity;
    private Vector3 baseScale = Vector3.one;
    private Transform gun;
    private Transform sight;
    private float sightDistance;
    private Vector3 kickOffset;
    private float kickAngle;
    private Vector3 reloadOffset;
    private Vector3 reloadEuler;
    private Coroutine reloadRoutine;
    private Vector3 drawOffset;
    private Vector3 drawEuler;
    private Coroutine drawRoutine;

    /// <summary>0 at the hip, 1 looking down the sights. Set every frame by the gun.</summary>
    public float Aim { get; set; }

    /// <summary>
    /// What to look through when aiming: the gun turns square to the camera and moves so <paramref name="point"/>
    /// sits on the view axis <paramref name="distance"/> metres ahead of the eye.
    /// </summary>
    public void SetSight(Transform point, float distance) {
        sight = point;
        sightDistance = distance;
    }

    /// <summary>Wrap <paramref name="gun"/> in a new pivot and return the motion driver on it.</summary>
    public static WeaponMotion Attach(Transform gun) {
        // The pivot sits at the parent's origin so the gun keeps its own local pose, which
        // matters if the gun's animation keyframes its root transform.
        GameObject pivot = new GameObject("WeaponPivot");
        pivot.layer = gun.gameObject.layer;
        Transform p = pivot.transform;
        p.SetParent(gun.parent, false);
        p.SetSiblingIndex(gun.GetSiblingIndex());
        gun.SetParent(p, false);
        WeaponMotion m = pivot.AddComponent<WeaponMotion>();
        m.gun = gun;
        m.basePosition = gun.localPosition;
        m.baseRotation = gun.localRotation;
        m.baseScale = gun.localScale;
        return m;
    }

    public void Kick(float scale = 1f) {
        kickOffset += Vector3.back * kickBack * scale;
        kickAngle += kickPitch * scale;
    }

    /// <summary>Bring a freshly selected gun up from below.</summary>
    public void PlayDraw(float duration) {
        if (drawRoutine != null) StopCoroutine(drawRoutine);
        drawRoutine = StartCoroutine(Draw(duration));
    }

    private IEnumerator Draw(float duration) {
        float t = 0f;
        while (t < duration) {
            t += Time.deltaTime;
            float w = 1f - Mathf.SmoothStep(0f, 1f, t / duration);
            drawOffset = new Vector3(0f, -0.12f, 0f) * w;
            drawEuler = new Vector3(25f, 0f, -10f) * w;
            yield return null;
        }
        drawOffset = Vector3.zero;
        drawEuler = Vector3.zero;
        drawRoutine = null;
    }

    public void CancelReload() {
        if (reloadRoutine != null) StopCoroutine(reloadRoutine);
        reloadRoutine = null;
        reloadOffset = Vector3.zero;
        reloadEuler = Vector3.zero;
    }

    public void PlayReload(float duration) {
        if (reloadRoutine != null) StopCoroutine(reloadRoutine);
        reloadRoutine = StartCoroutine(Reload(duration));
    }

    private IEnumerator Reload(float duration) {
        // Dip and roll the rifle (mag out), hold, then bring it back up (mag in, bolt).
        float t = 0f;
        while (t < duration) {
            t += Time.deltaTime;
            float n = t / duration;
            float w = n < 0.25f ? Mathf.SmoothStep(0f, 1f, n / 0.25f)
                    : n > 0.75f ? Mathf.SmoothStep(1f, 0f, (n - 0.75f) / 0.25f) : 1f;
            float jiggle = n > 0.45f && n < 0.6f ? Mathf.Sin((n - 0.45f) / 0.15f * Mathf.PI) * 0.4f : 0f;
            reloadOffset = new Vector3(0f, -0.035f - jiggle * 0.015f, 0.01f) * w;
            reloadEuler = new Vector3(12f + jiggle * 4f, 0f, -16f) * w;
            yield return null;
        }
        reloadOffset = Vector3.zero;
        reloadEuler = Vector3.zero;
        reloadRoutine = null;
    }

    void LateUpdate() {
        float k = 1f - Mathf.Exp(-returnSpeed * Time.deltaTime);
        kickOffset = Vector3.Lerp(kickOffset, Vector3.zero, k);
        kickAngle = Mathf.Lerp(kickAngle, 0f, k);
        // Rotate about the gun's rest position, in the parent's (camera) axes, since the
        // model's own axes are arbitrary.
        Quaternion r = Quaternion.Euler(reloadEuler + drawEuler + new Vector3(-kickAngle, 0f, 0f));

        // Aiming: undo the gun's canted hip pose so the bore runs along the view, and put the sight on the view axis.
        // Worked out from the gun's rest pose so its own animation still plays on top.
        Quaternion aimRotation = Quaternion.identity;
        Vector3 aimPosition = Vector3.zero;
        if (sight != null && gun != null && Aim > 0f) {
            Vector3 inGun = gun.InverseTransformPoint(sight.position);
            Vector3 atRest = basePosition + baseRotation * Vector3.Scale(baseScale, inGun);
            Quaternion square = Quaternion.Inverse(baseRotation);
            aimRotation = Quaternion.Slerp(Quaternion.identity, square, Aim);
            aimPosition = Vector3.Lerp(Vector3.zero, new Vector3(0f, 0f, sightDistance) - square * atRest, Aim);
        }

        // The motion turns about the gun's rest position, in the parent's (camera) axes, since the model's own axes
        // are arbitrary.
        Vector3 rest = aimPosition + aimRotation * basePosition;
        transform.localRotation = r * aimRotation;
        transform.localPosition = rest + r * (aimPosition - rest) + kickOffset + reloadOffset + drawOffset;
    }

}
