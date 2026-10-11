using UnityEngine;

/// <summary>Client side: the primary and optic the player wants on the next deployment, remembered between sessions.</summary>
public static class LoadoutChoice {

    private const string PrimaryKey = "loadout.primary";
    private const string OpticKey = "loadout.optic";

    public static WeaponId Primary { get; private set; }
    public static OpticId Optic { get; private set; }

    static LoadoutChoice() {
        Set((WeaponId)PlayerPrefs.GetInt(PrimaryKey, (int)Weapons.Primaries[0]),
            (OpticId)PlayerPrefs.GetInt(OpticKey, (int)Weapons.Get(Weapons.Primaries[0]).Optics[0]), false);
    }

    /// <summary>Pick a gun; its optic stays if it fits, otherwise the gun's default.</summary>
    public static void Set(WeaponId gun, OpticId optic, bool tellServer = true) {
        if (!Weapons.IsPrimary(gun)) gun = Weapons.Primaries[0];
        WeaponSpec spec = Weapons.Get(gun);
        if (!spec.Fits(optic)) optic = spec.Optics[0];
        Primary = gun;
        Optic = optic;
        PlayerPrefs.SetInt(PrimaryKey, (int)gun);
        PlayerPrefs.SetInt(OpticKey, (int)optic);
        if (tellServer && NetPlayer.Local != null) NetPlayer.Local.ChooseLoadout(gun, optic);
    }

    public static void NextOptic() {
        OpticId[] fits = Weapons.Get(Primary).Optics;
        int i = System.Array.IndexOf(fits, Optic);
        Set(Primary, fits[(i + 1) % fits.Length]);
    }

}
