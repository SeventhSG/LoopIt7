using System.Runtime.InteropServices;

namespace LoopIt7.Audio.Interop;

/// <summary>
/// Asks Windows for a 1 ms scheduler tick while something in the app is keeping its own
/// clock.
/// <para>
/// A virtual output has no hardware clock to ride, so it renders on a timer thread. At the
/// default 15.6 ms tick that thread wakes late by more than a whole buffer, the cables
/// leaving it overflow their queues, and the trim that keeps a monitor mix on time turns
/// into a dropout. Reference counted, because raising the tick for the whole machine is a
/// cost to everything else running on it.
/// </para>
/// </summary>
internal static class TimerResolution
{
    private const uint Period = 1;

    private static readonly object Sync = new();
    private static int _holders;

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint period);

    public static void Acquire()
    {
        lock (Sync)
        {
            if (_holders++ == 0)
            {
                try { timeBeginPeriod(Period); } catch { }
            }
        }
    }

    public static void Release()
    {
        lock (Sync)
        {
            if (_holders == 0) return;
            if (--_holders > 0) return;

            try { timeEndPeriod(Period); } catch { }
        }
    }
}
