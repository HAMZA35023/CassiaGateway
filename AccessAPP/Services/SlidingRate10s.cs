using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

internal sealed class SlidingRate10s
{
    private readonly object _lock = new();
    private readonly LinkedList<(long ms, double value)> _samples = new();

    // Call whenever you have a new "value" (e.g. percent progress)
    public double AddAndGetRatePerMinute(double valueNow)
    {
        var nowMs = Stopwatch.GetTimestamp();
        var nowMsReal = (long)(nowMs * 1000.0 / Stopwatch.Frequency);

        lock (_lock)
        {
            _samples.AddLast((nowMsReal, valueNow));

            // keep only last 10 seconds
            var cutoff = nowMsReal - 10_000;
            while (_samples.First != null && _samples.First.Value.ms < cutoff)
                _samples.RemoveFirst();

            if (_samples.Count < 2)
                return 0.0;

            var first = _samples.First!.Value;
            var last = _samples.Last!.Value;

            var dtSeconds = (last.ms - first.ms) / 1000.0;
            if (dtSeconds <= 0.0001)
                return 0.0;

            var dv = last.value - first.value; // percent points over window
            var ratePerMinute = (dv / dtSeconds) * 60.0;

            // If you never want negative (e.g. retries/resets), clamp:
            if (ratePerMinute < 0) ratePerMinute = 0;

            return ratePerMinute;
        }
    }
}
