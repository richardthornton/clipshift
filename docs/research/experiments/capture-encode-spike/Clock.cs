namespace ClipShiftSpike;

/// <summary>
/// QPC normalised to nanoseconds once at the boundary, per §10.1 of av-sync-strategy.md, with a
/// 128-bit intermediate so the multiply cannot overflow on a long session.
/// </summary>
internal static unsafe class Clock
{
    private static readonly long Frequency = ReadFrequency();

    private static long ReadFrequency()
    {
        long f;
        Native.QueryPerformanceFrequency(&f);
        return f;
    }

    public static long Ticks()
    {
        long t;
        Native.QueryPerformanceCounter(&t);
        return t;
    }

    public static long TicksToNs(long ticks)
    {
        Int128 scaled = (Int128)ticks * 1_000_000_000;
        return (long)(scaled / Frequency);
    }

    public static long NowNs() => TicksToNs(Ticks());
}

/// <summary>
/// The 60.000 fps grid of issue #12: absolute deadlines from T0, and a high-resolution waitable timer
/// rather than timeBeginPeriod(1), so the spike never raises the system timer resolution underneath a
/// game.
/// </summary>
internal sealed unsafe class FrameGrid : IDisposable
{
    private readonly nint _timer;
    public long T0Ns { get; }
    public long PeriodNs { get; }
    public bool UsingHighResolutionTimer { get; }

    public FrameGrid(long t0Ns, double fps)
    {
        T0Ns = t0Ns;
        PeriodNs = (long)Math.Round(1_000_000_000.0 / fps);

        _timer = Native.CreateWaitableTimerEx(0, null,
            Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        UsingHighResolutionTimer = _timer != 0;

        if (_timer == 0)
        {
            // Pre-1803 fallback. Recorded rather than papered over: without the high-resolution flag
            // the wait quantises to the system timer resolution, which is exactly the thing #12
            // refused to raise.
            _timer = Native.CreateWaitableTimerEx(0, null, 0, Native.TIMER_ALL_ACCESS);
            if (_timer == 0) throw new SpikeException("CreateWaitableTimerEx failed");
        }
    }

    public long DueNs(long frameIndex) => T0Ns + frameIndex * PeriodNs;

    /// <summary>Sleeps until the absolute deadline. Never accumulates: deadlines come from T0.</summary>
    public void WaitUntil(long dueNs)
    {
        long remaining = dueNs - Clock.NowNs();
        if (remaining <= 0) return;

        // Leave a short margin for the wait to return late, then spin the remainder. The margin is
        // what keeps a coarse wait from pushing every tick past its deadline.
        const long MarginNs = 500_000;   // 0.5 ms
        if (remaining > MarginNs)
        {
            long due100Ns = -((remaining - MarginNs) / 100);   // negative == relative
            if (Native.SetWaitableTimer(_timer, &due100Ns, 0, 0, 0, false))
                Native.WaitForSingleObject(_timer, 0xFFFFFFFF);
        }

        while (Clock.NowNs() < dueNs) Thread.SpinWait(64);
    }

    public void Dispose()
    {
        if (_timer != 0) Native.CloseHandle(_timer);
    }
}

/// <summary>The ledger of issue #12 — seven counters, kept distinct.</summary>
internal sealed class Counters
{
    public long DuplicatedIdle;          // no new surface at the tick — healthy
    public long DuplicatedLagged;        // the pacing loop itself overran — content loss
    public long DuplicatedBackpressure;  // encoder ring full at the tick
    public long DuplicatedRecovery;      // emitted while duplication was rebuilt after ACCESS_LOST
    public long BlackLeadIn;             // ticks before the first real surface
    public long Superseded;              // surfaces acquired then discarded before a tick — healthy
    public long CaptureMissed;           // DDA AccumulatedFrames > 1 — presents lost upstream

    public long TotalDuplicated => DuplicatedIdle + DuplicatedLagged + DuplicatedBackpressure + DuplicatedRecovery;

    public void Report(TextWriter w, long frames)
    {
        w.WriteLine("  ledger (issue #12, seven counters kept distinct):");
        Row(w, "duplicated_idle", DuplicatedIdle, frames, "healthy — no new surface at the tick");
        Row(w, "duplicated_lagged", DuplicatedLagged, frames, "FAULT — the pacing loop overran");
        Row(w, "duplicated_backpressure", DuplicatedBackpressure, frames, "FAULT — encoder ring full");
        Row(w, "duplicated_recovery", DuplicatedRecovery, frames, "expected during ACCESS_LOST rebuild");
        Row(w, "black_lead_in", BlackLeadIn, frames, "expected at start");
        Row(w, "superseded", Superseded, frames, "healthy above 60 Hz");
        Row(w, "capture_missed", CaptureMissed, frames, "FAULT — presents lost upstream");
    }

    private static void Row(TextWriter w, string name, long value, long frames, string note)
    {
        double pct = frames == 0 ? 0 : 100.0 * value / frames;
        w.WriteLine($"    {name,-26} {value,8}  {pct,6:F2}%   {note}");
    }
}
