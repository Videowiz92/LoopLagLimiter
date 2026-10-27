using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace LoopLagLimiter
{
    // Breaks recursive on-kill chains that overflow the stack.
    //
    // Gasoline (GlobalEventManager.ProcIgniteOnKill) fires its explosion synchronously inside the death
    // handler. When that explosion kills another enemy, the new death runs Gasoline again, one level deeper:
    //   OnCharacterDeath -> ProcIgniteOnKill -> BlastAttack.Fire -> TakeDamage -> OnCharacterDeath -> ...
    // With a dense pack of low-health enemies (e.g. a Halcyon Shrine wave deep into a loop) the chain goes
    // hundreds of levels deep and throws StackOverflowException, freezing the game.
    //
    // Fix: once the chain is deeper than the configured limit, the next proc is queued and runs on the next
    // physics tick at depth 0. Every explosion still happens, just spread over a few ticks.
    internal static class ChainGuard
    {
        sealed class Pending { public MethodBase Method; public object Instance; public object[] Args; }

        static readonly Dictionary<MethodBase, int> depth = new Dictionary<MethodBase, int>();
        static readonly Queue<Pending> queue = new Queue<Pending>();
        public static int Deferred, Dropped;

        public static void Install(Harmony h)
        {
            var gem = TypeFinder.Find("RoR2.GlobalEventManager");
            if (gem == null) { Plugin.Log.LogWarning("Chain guard skipped: GlobalEventManager not found"); return; }
            var pre = new HarmonyMethod(typeof(ChainGuard).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
            pre.priority = Priority.First;
            var fin = new HarmonyMethod(typeof(ChainGuard).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic));
            int n = 0;
            foreach (var m in gem.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name != "ProcIgniteOnKill" || m.ReturnType != typeof(void) || m.ContainsGenericParameters) continue;
                try { h.Patch(m, prefix: pre, finalizer: fin); depth[m] = 0; n++; }
                catch (Exception e) { Plugin.Log.LogWarning("Chain guard could not hook " + m.Name + ": " + e.Message); }
            }
            Plugin.Log.LogInfo(n > 0 ? "Chain guard hooked GlobalEventManager.ProcIgniteOnKill (Gasoline)." : "Chain guard: ProcIgniteOnKill not found, skipped.");
        }

        // __state: true when this call counted toward the depth
        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args, out bool __state)
        {
            __state = false;
            try
            {
                int limit = Plugin.MaxOnKillChainDepth.Value;
                int d;
                depth.TryGetValue(__originalMethod, out d);
                if (limit > 0 && d >= limit)
                {
                    if (queue.Count < 5000)
                    {
                        queue.Enqueue(new Pending { Method = __originalMethod, Instance = __instance, Args = (object[])__args.Clone() });
                        Deferred++;
                        Limiter.CountPublic("Gasoline chain deferred to next tick");
                    }
                    else { Dropped++; Limiter.CountPublic("Gasoline chain proc dropped (queue full)"); }
                    return false;
                }
                depth[__originalMethod] = d + 1;
                __state = true;
            }
            catch { }
            return true;
        }

        static Exception Finalizer(Exception __exception, MethodBase __originalMethod, bool __state)
        {
            if (__state)
            {
                int d;
                if (depth.TryGetValue(__originalMethod, out d)) depth[__originalMethod] = Math.Max(0, d - 1);
            }
            return __exception;
        }

        // Called every physics tick: run queued procs at depth 0. Their own chains may queue more for later ticks.
        public static void Drain()
        {
            if (queue.Count == 0) return;
            int budget = Math.Max(1, Plugin.MaxDeferredProcsPerTick.Value);
            int count = Math.Min(budget, queue.Count);
            for (int i = 0; i < count; i++)
            {
                var p = queue.Dequeue();
                try
                {
                    // the victim body (3rd argument) may have been destroyed since; skip if so
                    var body = p.Args.Length > 2 ? p.Args[2] as UnityEngine.Object : null;
                    if (!ReferenceEquals(body, null) && !body) continue;
                    p.Method.Invoke(p.Instance, p.Args);
                }
                catch (Exception e) { Plugin.Log.LogDebug("Deferred proc failed: " + (e.InnerException ?? e).Message); }
            }
        }

        public static void OnSceneChange() { queue.Clear(); foreach (var k in new List<MethodBase>(depth.Keys)) depth[k] = 0; }
    }
}
