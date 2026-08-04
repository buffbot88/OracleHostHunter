namespace OracleHost.Models;

/// <summary>
/// Tracks the current state of the instance hunting loop.
/// </summary>
public class HuntStatus
{
    public HuntState State { get; set; } = HuntState.Idle;
    public int Attempts { get; set; }
    public int CapacityHits { get; set; }
    public string? CurrentAd { get; set; }
    public string? LastError { get; set; }
    public string? SuccessInstanceId { get; set; }
    public string? PublicIp { get; set; }
    public DateTime StartTime { get; set; }
    public double NextRetryIn { get; set; }
    public string? ImageName { get; set; }

    public TimeSpan Elapsed =>
        StartTime == default || State == HuntState.Idle
            ? TimeSpan.Zero
            : DateTime.UtcNow - StartTime;

    public string ElapsedFormatted =>
        Elapsed.TotalHours >= 1
            ? $"{(int)Elapsed.TotalHours:D2}:{Elapsed.Minutes:D2}:{Elapsed.Seconds:D2}"
            : $"{(int)Elapsed.TotalMinutes:D2}:{Elapsed.Seconds:D2}";

    public string NextRetryFormatted =>
        NextRetryIn > 0 ? $"in {NextRetryIn:F0}s" : "-";

    public void Reset()
    {
        State = HuntState.Idle;
        Attempts = 0;
        CapacityHits = 0;
        CurrentAd = null;
        LastError = null;
        SuccessInstanceId = null;
        PublicIp = null;
        StartTime = DateTime.UtcNow;
        NextRetryIn = 0;
    }
}

public enum HuntState
{
    Idle,
    Preflight,
    Hunting,
    Success,
    Aborted,
    Stopped
}
