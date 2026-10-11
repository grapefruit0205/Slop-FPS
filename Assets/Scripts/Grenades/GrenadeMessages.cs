using FishNet.Broadcast;
using UnityEngine;

/// <summary>
/// Server to every client: a grenade left someone's hand. Each client flies its own copy from here with
/// <see cref="GrenadeFlight"/>; the thrower already has one in the air and only learns its id.
/// </summary>
public struct GrenadeThrown : IBroadcast {
    public ushort Id;
    public byte Type;
    public int Thrower;         // client id
    public ushort Seq;          // the thrower's own number for it
    public Vector3 Position;
    public Vector3 Velocity;
}

/// <summary>
/// Server to every client: grenade <see cref="Id"/> went off at <see cref="Position"/> (the server's, which the flying
/// copies snap to). For smoke, <see cref="Elapsed"/> is how long the cloud has already stood (late joiners).
/// </summary>
public struct GrenadeBlast : IBroadcast {
    public ushort Id;
    public byte Type;
    public int Thrower;
    public Vector3 Position;
    public float Elapsed;
}

/// <summary>Server to every client: a new elimination round, so every grenade and cloud of the last one is gone.</summary>
public struct GrenadeClear : IBroadcast {
    public int Round;
}
