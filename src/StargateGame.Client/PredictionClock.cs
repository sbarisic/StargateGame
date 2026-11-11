namespace StargateGame.Client;

// Monotonic client time, gently steered by server anchors. Packet arrival and
// ping jitter must not repeatedly restart the simulation/render tick phase.
sealed class PredictionClock
{
    double tick, time, error, lead = 2, leadTarget = 2, leadAt;
    bool initialized;
    public double Estimate(double now) => tick + (now - time) * 60 + Math.Clamp(error, -Math.Max(0, now - time) * 3, Math.Max(0, now - time) * 3);
    double Lead(double now) => lead + Math.Clamp(leadTarget - lead, -Math.Max(0, now - leadAt) * 3, Math.Max(0, now - leadAt) * 3);
    public double PredictionTime(double now) => Estimate(now) + Lead(now);
    public void Anchor(double incoming, double now, bool reset = false)
    {
        double current = Estimate(now);
        if (!initialized || reset || Math.Abs(incoming - current) > 12)
        {
            tick = incoming;
            error = 0;
            initialized = true;
        }
        else
        {
            tick = current;
            error = incoming - current;
        }
        time = now;
    }
    public void SetRoundTrip(double seconds, double now, bool initialize = false)
    {
        lead = Lead(now);
        leadAt = now;
        leadTarget = Math.Clamp(Math.Ceiling(seconds * 30) + 2, 2, 12);
        if (initialize)
            lead = leadTarget;
    }
}
