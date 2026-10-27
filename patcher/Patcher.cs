using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Mono.Cecil;

namespace LoopLagLimiter.Patcher
{
    // BepInEx preloader patcher (goes in BepInEx/patchers). Runs before RoR2.dll is loaded and marks a few tiny methods
    // as NoInlining. Without this, Mono copies their bodies straight into their callers when those are compiled, so
    // LoopLagLimiter's hooks on them (money and price overflow guards) are skipped entirely.
    public static class LoopLagLimiterPatcher
    {
        public static IEnumerable<string> TargetDLLs { get; } = new[] { "RoR2.dll" };

        static readonly string[][] targets =
        {
            new[] { "RoR2.CharacterMaster", "GiveMoney" },
            new[] { "RoR2.CharacterMaster", "set_money" },
            new[] { "RoR2.Run", "GetDifficultyScaledCost" },
            new[] { "RoR2.PurchaseInteraction", "set_Networkcost" },
            new[] { "RoR2.PurchaseInteraction", "set_cost" },
            new[] { "RoR2.PurchaseInteraction", "SetCost" },
        };

        public static void Patch(AssemblyDefinition assembly)
        {
            var log = Logger.CreateLogSource("LoopLagLimiter.Patcher");
            int n = 0;
            foreach (var t in targets)
            {
                try
                {
                    var type = assembly.MainModule.GetType(t[0]);
                    if (type == null) continue;
                    foreach (var m in type.Methods)
                        if (m.Name == t[1] && !m.NoInlining) { m.NoInlining = true; n++; }
                }
                catch (Exception e) { log.LogWarning("Could not mark " + t[0] + "." + t[1] + ": " + e.Message); }
            }
            log.LogInfo("Disabled inlining for " + n + " method(s) so LoopLagLimiter's money and price guards always run.");
            Logger.Sources.Remove(log);
        }
    }
}
