using UnityEngine;

/// <summary>One bullet (or shotgun pellet) as the shooter's machine saw it; sent to the server for checking.</summary>
public struct Pellet {
    public Vector3 Direction;
    public bool Hit;
    public Vector3 Point;
    public Vector3 Normal;
    public string Surface;
    public PlayerHealth Target;
    public bool Headshot;
}

/// <summary>Where an accepted bullet struck, for everyone else's impact effects.</summary>
public struct Impact {
    public Vector3 Point;
    public Vector3 Normal;
    public string Surface;
}
