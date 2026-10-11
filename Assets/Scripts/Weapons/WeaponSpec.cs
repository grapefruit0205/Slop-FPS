using UnityEngine;

public enum WeaponId : byte { AK47, SMG, Sniper, Shotgun, Pistol }

public enum OpticId : byte { Iron, RedDot, Scope4x, Scope8x }

/// <summary>
/// Handling and ballistics of one gun. The shooter's client fires with these numbers and the server checks every
/// reported shot against the same ones, so they live in code that both builds share.
/// </summary>
public sealed class WeaponSpec {

    public WeaponId Id;
    public string Name;               // HUD, killfeed
    public string Role;               // loadout card
    public int Damage;                // per bullet (per pellet for the shotgun)
    public float HeadshotMultiplier;
    public float Interval;            // seconds between shots
    public bool Automatic;
    public float Range;               // metres; hits further away don't count
    public float FalloffStart;        // full damage up to here...
    public float FalloffEnd;          // ...down to MinDamageScale here
    public float MinDamageScale = 1f;
    public int Magazine;
    public int Reserve;
    public float ReloadTime;
    public int Pellets = 1;
    public float HipSpread;           // cone half-angle in degrees, hip fire
    public float AimSpread;           // ... aiming down the sights
    public float DrawTime;            // switching to this gun
    public float Kick = 1f;           // view kick scale
    public OpticId[] Optics;          // what fits on it; the first is the default

    public int DamageAt(float distance, bool headshot) {
        float scale = 1f;
        if (distance > FalloffStart && FalloffEnd > FalloffStart) {
            scale = Mathf.Lerp(1f, MinDamageScale, (distance - FalloffStart) / (FalloffEnd - FalloffStart));
        }
        float d = Damage * scale * (headshot ? HeadshotMultiplier : 1f);
        return Mathf.Max(1, Mathf.RoundToInt(d));
    }

    public bool Fits(OpticId optic) {
        return System.Array.IndexOf(Optics, optic) >= 0;
    }

    /// <summary>The widest cone a legitimate bullet can leave the barrel in.</summary>
    public float MaxSpread => Mathf.Max(HipSpread, AimSpread);

}

/// <summary>The armoury. Players pick one primary; everyone carries the pistol.</summary>
public static class Weapons {

    public const WeaponId Secondary = WeaponId.Pistol;
    public static readonly WeaponId[] Primaries = { WeaponId.AK47, WeaponId.SMG, WeaponId.Sniper, WeaponId.Shotgun };

    private static readonly WeaponSpec[] specs = {
        new WeaponSpec {
            Id = WeaponId.AK47, Name = "AK-47", Role = "돌격소총",
            Damage = 20, HeadshotMultiplier = 2.5f, Interval = 0.2f, Automatic = true, Range = 100f,
            FalloffStart = 50f, FalloffEnd = 100f, MinDamageScale = 0.75f,
            Magazine = 30, Reserve = 90, ReloadTime = 2.2f, HipSpread = 1.2f, AimSpread = 0.15f, DrawTime = 0.6f, Kick = 1f,
            Optics = new[] { OpticId.RedDot, OpticId.Scope4x, OpticId.Iron },
        },
        new WeaponSpec {
            Id = WeaponId.SMG, Name = "MP5", Role = "기관단총",
            Damage = 15, HeadshotMultiplier = 2f, Interval = 0.085f, Automatic = true, Range = 70f,
            FalloffStart = 15f, FalloffEnd = 45f, MinDamageScale = 0.6f,
            Magazine = 30, Reserve = 120, ReloadTime = 1.9f, HipSpread = 1.6f, AimSpread = 0.5f, DrawTime = 0.45f, Kick = 0.55f,
            Optics = new[] { OpticId.RedDot, OpticId.Scope4x, OpticId.Iron },
        },
        new WeaponSpec {
            Id = WeaponId.Sniper, Name = "M700", Role = "저격소총",
            Damage = 95, HeadshotMultiplier = 2.5f, Interval = 1.3f, Automatic = false, Range = 300f,
            FalloffStart = 300f, FalloffEnd = 300f,
            Magazine = 5, Reserve = 20, ReloadTime = 3.0f, HipSpread = 7f, AimSpread = 0f, DrawTime = 0.9f, Kick = 3f,
            Optics = new[] { OpticId.Scope8x, OpticId.Scope4x },
        },
        new WeaponSpec {
            Id = WeaponId.Shotgun, Name = "M870", Role = "산탄총",
            Damage = 13, HeadshotMultiplier = 1.5f, Interval = 0.9f, Automatic = false, Range = 40f,
            FalloffStart = 8f, FalloffEnd = 30f, MinDamageScale = 0.25f,
            Magazine = 6, Reserve = 24, ReloadTime = 3.0f, Pellets = 8, HipSpread = 4.5f, AimSpread = 3.5f, DrawTime = 0.7f, Kick = 2.5f,
            Optics = new[] { OpticId.RedDot, OpticId.Iron },
        },
        new WeaponSpec {
            Id = WeaponId.Pistol, Name = "M1911", Role = "권총",
            Damage = 18, HeadshotMultiplier = 2f, Interval = 0.2f, Automatic = false, Range = 60f,
            FalloffStart = 15f, FalloffEnd = 45f, MinDamageScale = 0.7f,
            Magazine = 12, Reserve = 36, ReloadTime = 1.6f, HipSpread = 1.2f, AimSpread = 0.35f, DrawTime = 0.35f, Kick = 0.8f,
            Optics = new[] { OpticId.Iron },
        },
    };

    public static WeaponSpec Get(WeaponId id) {
        int i = (int)id;
        return i >= 0 && i < specs.Length ? specs[i] : specs[0];
    }

    public static bool IsPrimary(WeaponId id) {
        return System.Array.IndexOf(Primaries, id) >= 0;
    }

    public static string OpticName(OpticId optic) {
        switch (optic) {
            case OpticId.RedDot: return L10n.T("레드닷");
            case OpticId.Scope4x: return L10n.T("4배율");
            case OpticId.Scope8x: return L10n.T("8배율");
            default: return L10n.T("기본 조준기");
        }
    }

    /// <summary>Field of view while aiming through <paramref name="optic"/>, from the normal one.</summary>
    public static float AimFov(OpticId optic, float baseFov) {
        switch (optic) {
            case OpticId.RedDot: return baseFov / 1.25f;
            case OpticId.Scope4x: return baseFov / 4f;
            case OpticId.Scope8x: return baseFov / 8f;
            default: return baseFov / 1.15f;
        }
    }

    public static bool IsScope(OpticId optic) {
        return optic == OpticId.Scope4x || optic == OpticId.Scope8x;
    }

}
