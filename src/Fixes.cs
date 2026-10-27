using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace LoopLagLimiter
{
    // Guards for methods that throw NullReferenceException over and over in late-game runs.
    // Each throw normally aborts the whole attack (e.g. Resonance Disc beams stop at the first
    // bad hurtbox) and writes a full stack trace through Unity + BepInEx (console, GUI, disk).
    internal static class Fixes
    {
        static readonly Dictionary<MethodBase, int> suppressed = new Dictionary<MethodBase, int>();
        public static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

        public static void Install(Harmony h, string list)
        {
            var fVoid = new HarmonyMethod(typeof(Fixes).GetMethod("FinalizerVoid", BindingFlags.Static | BindingFlags.NonPublic));
            var fBool = new HarmonyMethod(typeof(Fixes).GetMethod("FinalizerBool", BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var raw in (list ?? "").Split(';'))
            {
                var entry = raw.Trim();
                if (entry.Length == 0) continue;
                int colon = entry.LastIndexOf(':');
                if (colon <= 0) { Plugin.Log.LogWarning("Bad guard entry (use Type:Method): " + entry); continue; }
                var t = AccessTools.TypeByName(entry.Substring(0, colon));
                string mName = entry.Substring(colon + 1);
                if (t == null) { Plugin.Log.LogWarning("Guard skipped, type not found: " + entry); continue; }
                int n = 0;
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != mName || m.IsAbstract || m.ContainsGenericParameters) continue;
                    HarmonyMethod fin = m.ReturnType == typeof(void) ? fVoid : m.ReturnType == typeof(bool) ? fBool : null;
                    if (fin == null) { Plugin.Log.LogWarning("Guard skipped (returns " + m.ReturnType.Name + "): " + entry); continue; }
                    try { h.Patch(m, finalizer: fin); suppressed[m] = 0; n++; }
                    catch (Exception e) { Plugin.Log.LogWarning("Could not guard " + entry + ": " + e.Message); }
                }
                if (n > 0) Plugin.Log.LogInfo("Guarding " + entry + " against NullReferenceException (" + n + " overload(s)).");
                else Plugin.Log.LogWarning("Guard skipped, method not found: " + entry);
            }
        }

        static Exception Handle(Exception ex, MethodBase original)
        {
            if (!(ex is NullReferenceException)) return ex;
            string key = original.DeclaringType.Name + "." + original.Name;
            int c; Counts.TryGetValue(key, out c); Counts[key] = c + 1;
            if (c == 0)
                Plugin.Log.LogWarning("LoopLagLimiter suppressed a NullReferenceException in " + key +
                    " (further ones are counted silently and listed in reports). First one:\n" + ex);
            return null;
        }

        static Exception FinalizerVoid(Exception __exception, MethodBase __originalMethod)
        {
            return __exception == null ? null : Handle(__exception, __originalMethod);
        }

        static Exception FinalizerBool(Exception __exception, MethodBase __originalMethod, ref bool __result)
        {
            if (__exception == null) return null;
            var r = Handle(__exception, __originalMethod);
            if (r == null) __result = false; // e.g. SimpleColliderBlockFilter: treat as "not blocked" so the hit lands
            return r;
        }
    }
}
