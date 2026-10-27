using System.Collections.Generic;
using System.Diagnostics;

namespace LoopLagLimiter
{
    // ------------------------------------------------------------------ per-frame accumulators (diagnostics)

    internal sealed class SrcStat { public int Calls; public long Ticks; public int Skipped; }

    internal sealed class ProbeStat
    {
        public int Calls; public long Ticks; public int Skipped;
        public readonly Dictionary<string, SrcStat> Sources = new Dictionary<string, SrcStat>();
        public void Clear()
        {
            Calls = 0; Ticks = 0; Skipped = 0;
            if (Sources.Count > 0) Sources.Clear();
        }
    }

    internal sealed class FrameStats
    {
        public double FrameMs, WorstFrameMs;
        public int Frames, FixedSteps, UnityErrors;
        public readonly int[] GcDelta = new int[3];
        public readonly Dictionary<string, int> ErrorKinds = new Dictionary<string, int>();
        public readonly ProbeStat[] Probes = new ProbeStat[LoopLagLimiter.Probes.MaxProbes];
        public readonly Dictionary<string, SrcStat> Deep = new Dictionary<string, SrcStat>();

        public FrameStats() { for (int i = 0; i < Probes.Length; i++) Probes[i] = new ProbeStat(); }

        public void Clear()
        {
            FrameMs = 0; WorstFrameMs = 0; Frames = 0; FixedSteps = 0; UnityErrors = 0;
            for (int g = 0; g < 3; g++) GcDelta[g] = 0;
            if (ErrorKinds.Count > 0) ErrorKinds.Clear();
            for (int i = 0; i < Probes.Length; i++) Probes[i].Clear();
            if (Deep.Count > 0) Deep.Clear();
        }
    }

    internal static class Frame
    {
        public static FrameStats Current = new FrameStats();
        public static FrameStats Window = new FrameStats(); // since last manual report
    }

    internal static class Ticks
    {
        public static double Ms(long t) { return t * 1000.0 / Stopwatch.Frequency; }
    }
}
