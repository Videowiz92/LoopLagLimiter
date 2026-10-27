using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LoopLagLimiter
{
    // Deep profile: times every Update / FixedUpdate / LateUpdate on every MonoBehaviour type in
    // RoR2 and in every mod. Unity calls these one at a time (they don't nest), so their sum is
    // the frame's script time, and frame time minus that sum is engine time (physics, rendering, GC).
    internal static class Deep
    {
        public static bool Enabled { get; private set; }
        static Harmony h;
        static readonly Dictionary<MethodBase, string> keyOf = new Dictionary<MethodBase, string>();
        static readonly HashSet<MethodBase> esmMethods = new HashSet<MethodBase>();
        static readonly HashSet<MethodBase> splitMethods = new HashSet<MethodBase>();
        static FieldInfo esmStateField;

        static readonly string[] skipPrefixes =
        {
            "UnityEngine", "Unity.", "System", "mscorlib", "netstandard", "Mono.", "MonoMod", "BepInEx",
            "0Harmony", "HarmonyX", "MMHOOK", "Microsoft", "Newtonsoft", "Rewired", "LoopLagLimiter",
        };
        static readonly string[] updateNames = { "Update", "FixedUpdate", "LateUpdate" };

        public static void Enable()
        {
            if (Enabled) return;
            var sw = Stopwatch.StartNew();
            if (h == null) h = new Harmony(Plugin.GUID + ".deep");
            var prefix = new HarmonyMethod(typeof(Deep).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
            var finalizer = new HarmonyMethod(typeof(Deep).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic));

            var esmType = TypeFinder.Find("RoR2.EntityStateMachine");
            if (esmType != null) esmStateField = AccessTools.Field(esmType, "state");

            var splitTypes = new HashSet<string>();
            foreach (var n in (Plugin.DeepSplitTypes.Value ?? "").Split(';')) if (n.Trim().Length > 0) splitTypes.Add(n.Trim());

            int ok = 0, failed = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = asm.GetName().Name;
                if (asm.IsDynamic || Skip(an)) continue;
                foreach (var t in SafeTypes(asm))
                {
                    if (t == null || t.IsAbstract || t.ContainsGenericParameters || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    foreach (var name in updateNames)
                    {
                        MethodInfo m;
                        try
                        {
                            m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        }
                        catch { continue; }
                        if (m == null || m.IsAbstract || m.ReturnType != typeof(void)) continue;
                        try
                        {
                            if (m.GetMethodBody() == null) continue;
                            h.Patch(m, prefix: prefix, finalizer: finalizer);
                            string key = (an == "RoR2" ? "" : "[" + an + "] ") + t.FullName + "." + name;
                            keyOf[m] = key;
                            if (t == esmType) esmMethods.Add(m);
                            if (splitTypes.Contains(t.FullName) || splitTypes.Contains(t.Name)) splitMethods.Add(m);
                            ok++;
                        }
                        catch { failed++; }
                    }
                }
            }
            Enabled = true;
            Plugin.Log.LogWarning(string.Format("LoopLagLimiter deep profile ON: {0} update methods patched ({1} skipped) in {2} ms.", ok, failed, sw.ElapsedMilliseconds));
        }

        public static void Disable()
        {
            if (!Enabled) return;
            try { h.UnpatchSelf(); } catch (Exception e) { Plugin.Log.LogError(e); }
            keyOf.Clear();
            esmMethods.Clear();
            splitMethods.Clear();
            Enabled = false;
            Plugin.Log.LogWarning("LoopLagLimiter deep profile OFF.");
        }

        static bool Skip(string asmName)
        {
            foreach (var p in skipPrefixes) if (asmName.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types; }
            catch { return new Type[0]; }
        }

        static void Prefix(out long __state)
        {
            __state = Plugin.Reporting ? -1 : Stopwatch.GetTimestamp();
        }

        static Exception Finalizer(Exception __exception, MethodBase __originalMethod, object __instance, long __state)
        {
            if (__state < 0) return __exception;
            long dt = Stopwatch.GetTimestamp() - __state;
            string key;
            if (!keyOf.TryGetValue(__originalMethod, out key)) return __exception;
            if (esmStateField != null && esmMethods.Contains(__originalMethod) && __instance != null)
            {
                // EntityStateMachine runs the current EntityState; attribute time to that state type
                try
                {
                    var st = esmStateField.GetValue(__instance);
                    key = key + " [" + (st == null ? "null" : st.GetType().FullName) + "]";
                }
                catch { }
            }
            else if (splitMethods.Contains(__originalMethod))
            {
                // per-object split: tells you WHICH prefab's copy of this component is expensive
                try { key = key + " {" + Probes.ObjName(__instance) + "}"; } catch { }
            }
            try { Add(Frame.Current.Deep, key, dt); Add(Frame.Window.Deep, key, dt); } catch { }
            return __exception;
        }

        static void Add(Dictionary<string, SrcStat> d, string key, long dt)
        {
            SrcStat s;
            if (!d.TryGetValue(key, out s)) { s = new SrcStat(); d[key] = s; }
            s.Calls++; s.Ticks += dt;
        }
    }
}
