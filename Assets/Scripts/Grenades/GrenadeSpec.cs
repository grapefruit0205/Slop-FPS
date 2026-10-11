using UnityEngine;

/// <summary>The three throwables. Bytes on the wire; None means "a gun is in hand".</summary>
public enum GrenadeId : byte { None = 0, Frag = 1, Flash = 2, Smoke = 3 }

/// <summary>
/// One kind of hand grenade. Like <see cref="WeaponSpec"/>, the thrower's client and the server read the same numbers:
/// the client to draw the arc and time its throw, the server to fly, time and set off every grenade itself.
/// Distances are in real metres; <see cref="Grenades.M"/> turns them into world units.
/// </summary>
public sealed class GrenadeSpec {

    public GrenadeId Id;
    public string Name;                 // killfeed, death screen, weapon panel
    public string Short;                // HUD slot
    public KeyCode Key;
    public int PerLife = 1;             // issued with every avatar (every life; every round in elimination)
    public float Fuse;                  // seconds from the pin (cookable) or the throw, or after coming to rest
    public bool Cookable;               // the fuse runs from the pin: hold it too long and it goes off in the hand
    public bool FuseAfterRest;          // smoke: counts down once it has stopped rolling...
    public float MaxFlight;             // ...but goes off this long after the throw whatever it is doing
    public float Radius;                // metres: damage (frag), blinding (flash), cloud (smoke)
    public Color Body;                  // model colour

}

/// <summary>
/// The grenade table and every tuning constant of throwing, flight and effects, in one place.
///
///   key  grenade   fuse                        effect
///   4    수류탄     3.0 s from the pin (cookable)  100 dmg at the body, falling to 0 at 6 m; needs line of sight
///   5    섬광탄     1.5 s from the throw          blinds within 25 m in line of sight: up to ~3.5 s white facing it
///   6    연막탄     1.5 s after it stops rolling  4 m cloud for 15 s that hides players and name tags
///
/// One of each per life (per round in elimination). Hold left click to cook/aim and release for the long overhand
/// throw, right click for a short underhand lob; G cycles, 1/2 (or the wheel) goes back to a gun.
/// </summary>
public static class Grenades {

    // ---------------------------------------------------------------- world
    /// <summary>Metres to world units (maps and avatars are scaled x1.65).</summary>
    public static float M(float metres) {
        return metres * MapDefinition.MetresToWorld;
    }

    public const float Gravity = 9.81f;          // m/s²
    public const float BodyRadius = 0.05f;       // m, for bouncing off things
    public const float Restitution = 0.38f;      // share of the speed into a surface that bounces back
    public const float BounceFriction = 0.25f;   // share of the sliding speed lost on a real bounce
    public const float RollDrag = 3.5f;          // m/s² while rolling along the floor
    public const float RestSpeed = 0.45f;        // m/s: slower than this on the floor and it lies still
    public const float SimStep = 1f / 60f;       // fixed step every machine flies grenades with

    // ---------------------------------------------------------------- throwing
    public const float OverhandSpeed = 15f;      // m/s, left click
    public const float UnderhandSpeed = 6.5f;    // m/s, right click
    public const float OverhandLift = 6f;        // degrees the throw leaves above the crosshair
    public const float UnderhandLift = 10f;
    public const float LaunchAhead = 0.35f;      // m in front of the eye where it leaves the hand
    public const float DrawTime = 0.35f;         // grenade out, ready to throw
    public const float RecoverTime = 0.4f;       // after the throw, before the gun comes back up
    public const float MinThrowInterval = 0.5f;  // server: throws closer together than this are refused
    public const float CookGrace = 0.3f;         // server: a cooked frag still counts as thrown this late (latency)

    // ---------------------------------------------------------------- frag
    public const float FragDamage = 100f;        // touching the body...
    public const float FragFullRadius = 0.75f;   // ...up to here (m)
    public const float CoveredScale = 0.5f;      // a body only partly in sight takes at least half
    public const float ShakeRadius = 18f;        // m: cameras this close shake

    // ---------------------------------------------------------------- flash
    public const float FlashFullDistance = 6f;   // m: as strong as it gets up to here
    public const float FlashFarScale = 0.25f;    // strength left at the edge of the radius
    public const float FlashFullAngle = 50f;     // degrees off the view axis still looking straight at it
    public const float FlashAwayAngle = 140f;    // ...and beyond this, looking away
    public const float FlashAwayScale = 0.15f;   // strength left looking away
    public const float FlashMaxWhite = 3.2f;     // s of full white at strength 1 (then it fades)
    public const float FlashFade = 1.6f;         // s of fade at strength 1 (plus a short floor)

    // ---------------------------------------------------------------- smoke
    public const float SmokeDuration = 15f;      // s the cloud stands
    public const float SmokeHeight = 3.2f;       // m, from the floor
    public const float SmokeBloom = 1.6f;        // s to grow to full size
    public const float SmokeFadeOut = 2.5f;      // s of thinning at the end
    public const int SmokePuffs = 110;           // billboards per cloud (WebGL budget: <= 200 on screen)

    public static readonly GrenadeId[] All = { GrenadeId.Frag, GrenadeId.Flash, GrenadeId.Smoke };

    private static readonly GrenadeSpec[] specs = {
        null,
        new GrenadeSpec {
            Id = GrenadeId.Frag, Name = "수류탄", Short = "수류탄", Key = KeyCode.Alpha4,
            Fuse = 3.0f, Cookable = true, Radius = 6f, Body = new Color(0.27f, 0.31f, 0.2f),
        },
        new GrenadeSpec {
            Id = GrenadeId.Flash, Name = "섬광탄", Short = "섬광", Key = KeyCode.Alpha5,
            Fuse = 1.5f, Radius = 25f, Body = new Color(0.32f, 0.33f, 0.33f),
        },
        new GrenadeSpec {
            Id = GrenadeId.Smoke, Name = "연막탄", Short = "연막", Key = KeyCode.Alpha6,
            Fuse = 1.5f, FuseAfterRest = true, MaxFlight = 6f, Radius = 4f, Body = new Color(0.22f, 0.27f, 0.22f),
        },
    };

    public static GrenadeSpec Get(GrenadeId id) {
        int i = (int)id;
        return i > 0 && i < specs.Length ? specs[i] : null;
    }

    public static bool IsValid(byte id) {
        return id >= (byte)GrenadeId.Frag && id <= (byte)GrenadeId.Smoke;
    }

    /// <summary>
    /// How hard a flash hits an eye <paramref name="metres"/> away looking <paramref name="degrees"/> off it (0..1).
    /// Line of sight is the caller's business.
    /// </summary>
    public static float FlashStrength(float metres, float degrees) {
        GrenadeSpec flash = Get(GrenadeId.Flash);
        if (metres > flash.Radius) return 0f;
        float near = metres <= FlashFullDistance ? 1f
                   : Mathf.Lerp(1f, FlashFarScale, (metres - FlashFullDistance) / (flash.Radius - FlashFullDistance));
        float facing = degrees <= FlashFullAngle ? 1f
                     : Mathf.Lerp(1f, FlashAwayScale, (degrees - FlashFullAngle) / (FlashAwayAngle - FlashFullAngle));
        return Mathf.Clamp01(near * facing);
    }

    /// <summary>Frag damage at <paramref name="metres"/> from the body, before cover.</summary>
    public static float FragDamageAt(float metres) {
        float r = Get(GrenadeId.Frag).Radius;
        if (metres >= r) return 0f;
        return FragDamage * (1f - Mathf.Clamp01((metres - FragFullRadius) / (r - FragFullRadius)));
    }

}
