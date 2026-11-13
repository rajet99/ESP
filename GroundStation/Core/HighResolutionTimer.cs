using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace GroundStation.Core;

/// <summary>
/// High-precision 50 Hz timer loop running on a dedicated high-priority thread.
/// Sets Windows OS timer granularity to 1 ms via winmm.dll to avoid standard 15.6 ms jitter.
/// </summary>
public sealed class HighResolutionTimer : IDisposable
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private readonly Action _callback;
    private readonly double _intervalMs;
    private Thread? _thread;
    private volatile bool _isRunning;
    private bool _disposed;

    public HighResolutionTimer(double intervalMs, Action callback)
    {
        _intervalMs = intervalMs;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    public void Start()
    {
        if (_isRunning) return;

        TimeBeginPeriod(1);
        _isRunning = true;

        _thread = new Thread(TimerLoop)
        {
            Name = "GroundStation.50HzLoop",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _thread?.Join(200);
        _thread = null;
        TimeEndPeriod(1);
    }

    private void TimerLoop()
    {
        var stopwatch = Stopwatch.StartNew();
        double targetTicks = 0.0;
        double ticksPerInterval = Stopwatch.Frequency * (_intervalMs / 1000.0);

        while (_isRunning)
        {
            try
            {
                _callback();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HighResolutionTimer] Callback error: {ex.Message}");
            }

            targetTicks += ticksPerInterval;
            while (stopwatch.ElapsedTicks < targetTicks && _isRunning)
            {
                long remainingTicks = (long)(targetTicks - stopwatch.ElapsedTicks);
                double remainingMs = (remainingTicks * 1000.0) / Stopwatch.Frequency;

                if (remainingMs > 2.0)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.SpinWait(50);
                }
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _disposed = true;
        }
    }
}
