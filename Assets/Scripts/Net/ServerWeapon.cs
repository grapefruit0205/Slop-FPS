/// <summary>
/// The server's copy of one avatar's gun: magazine, reserve, reload timer and fire rate. Every shot a client reports
/// has to fit this model, so modified clients cannot fire faster, skip reloads or shoot forever.
///
/// Shots and reloads are timed on the shooter's own clock (sent with each report), because a hitch on the server or
/// a stalled connection delivers several legitimate shots in one frame. <see cref="AntiCheat.CheckClock"/> keeps
/// that clock honest, and a real-time token bucket caps the average rate whatever the timestamps say.
/// </summary>
public class ServerWeapon {

    private const double ReloadTolerance = 0.9;   // reload may finish this fraction of the real time early
    private const double SpacingTolerance = 0.9;  // shots may be this fraction of the interval apart
    private const float RateTolerance = 1.1f;     // average rate, in real time
    private const float BurstSeconds = 1.5f;      // shots that may arrive bunched together after a stall

    private readonly int magazineSize;
    private readonly double reloadTime;
    private readonly double interval;
    private readonly float shotsPerSecond;
    private readonly float burst;

    private int magazine;
    private int reserve;
    private bool reloading;
    private double reloadEnd;
    private double lastShot = double.NegativeInfinity;
    private float tokens;
    private float lastRefill;

    public ServerWeapon(int magazineSize, int reserve, float reloadTime, float timeBetweenBullets) {
        this.magazineSize = magazineSize;
        this.reserve = reserve;
        this.reloadTime = reloadTime;
        interval = timeBetweenBullets;
        shotsPerSecond = RateTolerance / timeBetweenBullets;
        burst = 1f + BurstSeconds / timeBetweenBullets;
        tokens = burst;
        magazine = magazineSize;
        lastRefill = AntiCheat.Now;
    }

    public int Magazine => magazine;
    public int Reserve => reserve;

    /// <param name="clientTime">The shooter's clock when the reload started.</param>
    public void BeginReload(double clientTime) {
        Settle(clientTime);
        if (reloading || magazine >= magazineSize || reserve <= 0) return;
        reloading = true;
        reloadEnd = clientTime + reloadTime * ReloadTolerance;
    }

    /// <summary>Putting the gun away stops a reload that has not finished by <paramref name="clientTime"/>.</summary>
    public void CancelReload(double clientTime) {
        Settle(clientTime);
        reloading = false;
    }

    /// <summary>Consumes a round. Returns null when the shot is possible, otherwise why it is not.</summary>
    /// <param name="clientTime">The shooter's clock when the shot was fired.</param>
    public string TryFire(double clientTime) {
        Settle(clientTime);
        float now = AntiCheat.Now;
        tokens = System.Math.Min(burst, tokens + (now - lastRefill) * shotsPerSecond);
        lastRefill = now;
        if (reloading) return "fired while reloading";
        if (magazine <= 0) return "fired with an empty magazine";
        double gap = clientTime - lastShot;
        if (gap < interval * SpacingTolerance) return "fire rate (" + (gap * 1000).ToString("F0") + " ms after the last shot)";
        if (tokens < 1f) return "fire rate (sustained)";
        tokens -= 1f;
        lastShot = clientTime;
        magazine--;
        return null;
    }

    private void Settle(double clientTime) {
        if (!reloading || clientTime < reloadEnd) return;
        int take = System.Math.Min(magazineSize - magazine, reserve);
        magazine += take;
        reserve -= take;
        reloading = false;
    }

}
