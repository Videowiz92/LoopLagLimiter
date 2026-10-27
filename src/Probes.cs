using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LoopLagLimiter
{
    // Curated timing probes on the systems that usually explode in late-game runs.
    // Times are inclusive and probes nest (TakeDamage runs inside BulletAttack.Fire, etc.),
    // so the probe totals overlap and should not be summed.
    internal static class Probes
    {
        public const int MaxProbes = 24;

        sealed class Def
        {
            public string Name, TypeName;
            public string[] Methods;
            public Func<object, object[], string> Attr; // (instance, args) -> source label
            public ConfigEntry<int> Cap;                  // optional throttle
        }

        static readonly List<Def> defs = new List<Def>();
        static readonly Dictionary<MethodBase, int> probeOf = new Dictionary<MethodBase, int>();
        static readonly int[] depth = new int[MaxProbes];
        static readonly string[] currentSource = new string[MaxProbes];
        static readonly int[] tickCount = new int[MaxProbes];
        static readonly bool[] returnsVoid = new bool[MaxProbes];
        static int lastTickFrame = -1;
        static float lastTickFixed = -1f;

        public static string NameOf(int i) { return i < defs.Count ? defs[i].Name : "?"; }
        public static int Count { get { return defs.Count; } }

        static void Add(string name, string type, string[] methods, Func<object, object[], string> attr = null, ConfigEntry<int> cap = null)
        {
            defs.Add(new Def { Name = name, TypeName = type, Methods = methods, Attr = attr, Cap = cap });
        }

        public static void Install(Harmony h)
        {
            Add("HealthComponent.TakeDamage", "RoR2.HealthComponent", new[] { "TakeDamage", "TakeDamageProcess" }, (i, a) => DamageSource(Arg(a, 0)));
            Add("GlobalEventManager.OnHitEnemy", "RoR2.GlobalEventManager", new[] { "OnHitEnemy", "ProcessHitEnemy" }, (i, a) => DamageSource(Arg(a, 0)));
            Add("GlobalEventManager.OnHitAll", "RoR2.GlobalEventManager", new[] { "OnHitAll", "OnHitAllProcess" }, (i, a) => DamageSource(Arg(a, 0)));
            Add("GlobalEventManager.OnCharacterDeath", "RoR2.GlobalEventManager", new[] { "OnCharacterDeath" });
            Add("CharacterBody.RecalculateStats", "RoR2.CharacterBody", new[] { "RecalculateStats" }, (i, a) => ObjName(i));
            Add("CharacterBody.OnInventoryChanged", "RoR2.CharacterBody", new[] { "OnInventoryChanged" }, (i, a) => ObjName(i));
            Add("OrbManager.AddOrb", "RoR2.Orbs.OrbManager", new[] { "AddOrb" }, (i, a) => TypeName(Arg(a, 0)));
            Add("OrbManager.FixedUpdate", "RoR2.Orbs.OrbManager", new[] { "FixedUpdate" });
            Add("ProjectileManager.FireProjectile", "RoR2.Projectile.ProjectileManager", new[] { "FireProjectile", "FireProjectileServer" }, (i, a) => ProjectileSource(a));
            Add("BulletAttack.Fire", "RoR2.BulletAttack", new[] { "Fire" }, (i, a) => ObjName(Member(i, "owner")));
            Add("BlastAttack.Fire", "RoR2.BlastAttack", new[] { "Fire" }, (i, a) => ObjName(Member(i, "inflictor") ?? Member(i, "attacker")));
            Add("OverlapAttack.Fire", "RoR2.OverlapAttack", new[] { "Fire" }, (i, a) => ObjName(Member(i, "attacker")));
            Add("EffectManager.SpawnEffect", "RoR2.EffectManager", new[] { "SpawnEffect" }, (i, a) => EffectSource(Arg(a, 0)));
            Add("DotController.InflictDot", "RoR2.DotController", new[] { "InflictDot" });
            Add("PickupDroplet.Create", "RoR2.PickupDropletController", new[] { "CreatePickupDroplet" });
            Add("GenericPickup.AttemptGrant", "RoR2.GenericPickupController", new[] { "AttemptGrant" });
            Add("CombatDirector.FixedUpdate", "RoR2.CombatDirector", new[] { "FixedUpdate" }, (i, a) => ObjName(i));
            Add("CombatDirector.Spawn", "RoR2.CombatDirector", new[] { "AttemptSpawnOnTarget", "Spawn" }, (i, a) => ObjName(i));
            Add("BepInEx log write", "BepInEx.Logging.Logger", new[] { "InternalLogEvent" }, (i, a) => LogSource(Arg(a, 1)));

            var prefix = new HarmonyMethod(typeof(Probes).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic));
            var finalizer = new HarmonyMethod(typeof(Probes).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic));
            prefix.priority = Priority.First;     // start the clock before other mods' prefixes
            finalizer.priority = Priority.Last;

            int patched = 0;
            for (int id = 0; id < defs.Count; id++)
            {
                var d = defs[id];
                Type t = AccessTools.TypeByName(d.TypeName);
                if (t == null) { Plugin.Log.LogWarning("Probe skipped, type not found: " + d.TypeName); continue; }
                bool anyVoid = true;
                int found = 0;
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (Array.IndexOf(d.Methods, m.Name) < 0 || m.IsAbstract || m.ContainsGenericParameters) continue;
                    try
                    {
                        h.Patch(m, prefix: prefix, finalizer: finalizer);
                        probeOf[m] = id;
                        if (m.ReturnType != typeof(void)) anyVoid = false;
                        found++; patched++;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("Could not probe " + d.TypeName + "." + m.Name + ": " + e.Message); }
                }
                returnsVoid[id] = anyVoid;
                if (found == 0) Plugin.Log.LogWarning("Probe skipped, no methods found: " + d.Name);
            }
            Plugin.Log.LogInfo("Probes installed on " + patched + " methods.");
        }

        public static void ResetTickCounters()
        {
            for (int i = 0; i < tickCount.Length; i++) tickCount[i] = 0;
        }

        // __state: >=0 start timestamp (outermost call), -1 nested call, -2 skipped by throttle, -3 ignored (other thread / reporting)
        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args, out long __state)
        {
            __state = -3;
            try { return PrefixImpl(__originalMethod, __instance, __args, ref __state); }
            catch { __state = -3; return true; } // never let the profiler break the game
        }

        static bool PrefixImpl(MethodBase __originalMethod, object __instance, object[] __args, ref long __state)
        {
            if (Plugin.Reporting || Thread.CurrentThread.ManagedThreadId != Plugin.MainThreadId) return true;
            int id;
            if (!probeOf.TryGetValue(__originalMethod, out id)) return true;

            if (depth[id] > 0) { depth[id]++; __state = -1; return true; }

            var d = defs[id];
            string src = null;
            if (d.Attr != null && Plugin.AttributeSources.Value)
            {
                try { src = d.Attr(__instance, __args); } catch { src = "?"; }
            }

            // throttle (only void methods, only outermost call)
            if (d.Cap != null && d.Cap.Value > 0 && returnsVoid[id])
            {
                int f = Time.frameCount; float ft = Time.fixedTime;
                if (f != lastTickFrame || ft != lastTickFixed) { lastTickFrame = f; lastTickFixed = ft; ResetTickCounters(); }
                if (++tickCount[id] > d.Cap.Value)
                {
                    Record(id, src, 0, true);
                    __state = -2;
                    return false;
                }
            }

            depth[id] = 1;
            currentSource[id] = src;
            __state = Stopwatch.GetTimestamp();
            return true;
        }

        static Exception Finalizer(Exception __exception, MethodBase __originalMethod, long __state)
        {
            if (__state == -3 || __state == -2) return __exception;
            int id;
            if (!probeOf.TryGetValue(__originalMethod, out id)) return __exception;
            if (__state == -1) { depth[id]--; return __exception; }
            depth[id] = 0;
            try { Record(id, currentSource[id], Stopwatch.GetTimestamp() - __state, false); } catch { }
            currentSource[id] = null;
            return __exception;
        }

        static void Record(int id, string src, long ticks, bool skipped)
        {
            Rec(Frame.Current.Probes[id], src, ticks, skipped);
            Rec(Frame.Window.Probes[id], src, ticks, skipped);
        }

        static void Rec(ProbeStat p, string src, long ticks, bool skipped)
        {
            if (skipped) p.Skipped++; else { p.Calls++; p.Ticks += ticks; }
            if (src == null) return;
            SrcStat s;
            if (!p.Sources.TryGetValue(src, out s))
            {
                if (p.Sources.Count >= 400) src = "(other)";
                if (!p.Sources.TryGetValue(src, out s)) { s = new SrcStat(); p.Sources[src] = s; }
            }
            if (skipped) s.Skipped++; else { s.Calls++; s.Ticks += ticks; }
        }

        // ------------------------------------------------------------ attribution helpers

        static object Arg(object[] a, int i) { return a != null && a.Length > i ? a[i] : null; }

        static string TypeName(object o) { return o == null ? "null" : o.GetType().Name; }

        internal static string ObjName(object o)
        {
            if (o == null) return "none";
            var uo = o as UnityEngine.Object;
            if (!ReferenceEquals(uo, null))
            {
                if (!uo) return "(destroyed)";
                var c = uo as Component;
                string n = !ReferenceEquals(c, null) ? c.gameObject.name : uo.name;
                return Clean(n);
            }
            return o.GetType().Name;
        }

        internal static string Clean(string n)
        {
            if (n == null) return "null";
            return n.EndsWith("(Clone)", StringComparison.Ordinal) ? n.Substring(0, n.Length - 7) : n;
        }

        static string DamageSource(object damageInfo)
        {
            if (damageInfo == null) return "null";
            var inflictor = Member(damageInfo, "inflictor");
            var attacker = Member(damageInfo, "attacker");
            string a = ObjName(attacker);
            var inf = inflictor as UnityEngine.Object;
            if (!ReferenceEquals(inf, null) && inf && !ReferenceEquals(inflictor, attacker))
            {
                string i = ObjName(inflictor);
                if (i != a) return a + " via " + i;
            }
            return a;
        }

        static string ProjectileSource(object[] a)
        {
            var first = Arg(a, 0);
            if (first is GameObject) return ObjName(first);
            if (first != null) return ObjName(Member(first, "projectilePrefab"));
            return "null";
        }

        static string EffectSource(object first)
        {
            if (first is GameObject) return ObjName(first);
            if (first != null)
            {
                // EffectIndex overload: resolve the prefab name through EffectCatalog if we can
                try
                {
                    if (effectDefGetter == null && !effectLookupFailed)
                    {
                        var cat = AccessTools.TypeByName("RoR2.EffectCatalog");
                        effectDefGetter = cat == null ? null : AccessTools.Method(cat, "GetEffectDef");
                        effectLookupFailed = effectDefGetter == null;
                    }
                    if (effectDefGetter != null)
                    {
                        var def = effectDefGetter.Invoke(null, new[] { first });
                        var prefab = Member(def, "prefab") ?? Member(def, "prefabName");
                        if (prefab != null) return prefab is string ? (string)prefab : ObjName(prefab);
                    }
                }
                catch { effectLookupFailed = true; }
                return "EffectIndex " + first;
            }
            return "null";
        }
        static MethodInfo effectDefGetter;
        static bool effectLookupFailed;

        static string LogSource(object args)
        {
            var e = args as LogEventArgs;
            if (e == null || e.Source == null) return "?";
            return e.Source.SourceName + " [" + e.Level + "]";
        }

        // ------------------------------------------------------------ reflection member cache

        static readonly Dictionary<Type, Dictionary<string, Func<object, object>>> getters = new Dictionary<Type, Dictionary<string, Func<object, object>>>();

        internal static object Member(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            Dictionary<string, Func<object, object>> byName;
            if (!getters.TryGetValue(t, out byName)) { byName = new Dictionary<string, Func<object, object>>(); getters[t] = byName; }
            Func<object, object> g;
            if (!byName.TryGetValue(name, out g))
            {
                var f = AccessTools.Field(t, name);
                if (f != null && !f.IsStatic) g = x => f.GetValue(x);
                else
                {
                    var p = AccessTools.Property(t, name);
                    if (p != null && p.GetIndexParameters().Length == 0 && p.CanRead && !p.GetGetMethod(true).IsStatic) g = x => p.GetValue(x, null);
                    else g = x => null;
                }
                byName[name] = g;
            }
            return g(o);
        }
    }
}
