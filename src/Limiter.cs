using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LoopLagLimiter
{
    // Fixes for the runaway systems found in a late-game Nest teleporter fight:
    //  1. RadialForce pile-up (Sentient Meat Hook's MeatHookDelayedForce): keep at most N alive per prefab, oldest removed first.
    //  2. Orbs aimed at destroyed targets (Polylute VoidLightningOrb) kept ticking forever: drop them.
    //  3. Optional cap on how many projectiles of one prefab may be alive at once (host only).
    internal static class Limiter
    {
        public static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

        static readonly Dictionary<string, LinkedList<GameObject>> forces = new Dictionary<string, LinkedList<GameObject>>();
        public static readonly Dictionary<string, int> AliveProjectiles = new Dictionary<string, int>();

        static PropertyInfo serverActive;
        static MethodInfo serverDestroy;
        static Type netIdentityType;
        static FieldInfo[] orbLists;
        static int orbSweepTick;

        public static void Install(Harmony h)
        {
            var nsType = TypeFinder.Find("UnityEngine.Networking.NetworkServer");
            if (nsType != null)
            {
                serverActive = AccessTools.Property(nsType, "active");
                serverDestroy = AccessTools.Method(nsType, "Destroy", new[] { typeof(GameObject) });
            }
            netIdentityType = TypeFinder.Find("UnityEngine.Networking.NetworkIdentity");

            // 1. RadialForce cap
            PatchFirst(h, "RoR2.RadialForce", new[] { "Start", "OnEnable", "Awake" }, null, "RadialForcePostfix");

            // 2. dead-target orbs
            {
                var om = TypeFinder.Find("RoR2.Orbs.OrbManager");
                if (om != null)
                {
                    var lists = new List<FieldInfo>();
                    foreach (var f in om.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        if (typeof(IList).IsAssignableFrom(f.FieldType)) lists.Add(f);
                    orbLists = lists.ToArray();
                    PatchFirst(h, "RoR2.Orbs.OrbManager", new[] { "FixedUpdate" }, "OrbManagerPrefix", null);
                }
            }

            // 4. leftover-credit dump into stage "camp" directors
            {
                var cd = TypeFinder.Find("RoR2.CombatDirector");
                if (cd != null)
                {
                    creditField = AccessTools.Field(cd, "monsterCredit");
                    if (creditField != null && creditField.FieldType == typeof(float))
                        PatchFirst(h, "RoR2.CombatDirector", new[] { "FixedUpdate" }, "DirectorPrefix", null);
                    else Plugin.Log.LogWarning("Camp credit cap skipped: CombatDirector.monsterCredit not found");
                }
            }

            // 3. projectile alive cap
            {
                PatchFirst(h, "RoR2.Projectile.ProjectileController", new[] { "Start", "Awake" }, null, "ProjectileStartPostfix");
                var pre = new HarmonyMethod(typeof(Limiter).GetMethod("FirePrefix", BindingFlags.Static | BindingFlags.NonPublic));
                pre.priority = Priority.High;
                foreach (var m in ProjectileFireMethods())
                    try { h.Patch(m, prefix: pre); } catch (Exception e) { Plugin.Log.LogWarning("Projectile cap: " + e.Message); }
            }
        }

        static void PatchFirst(Harmony h, string typeName, string[] names, string prefix, string postfix)
        {
            var t = TypeFinder.Find(typeName);
            if (t == null) { Plugin.Log.LogWarning("Limiter skipped, type not found: " + typeName); return; }
            foreach (var n in names)
            {
                var m = AccessTools.DeclaredMethod(t, n, Type.EmptyTypes);
                if (m == null) continue;
                try
                {
                    h.Patch(m,
                        prefix: prefix == null ? null : new HarmonyMethod(typeof(Limiter).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: postfix == null ? null : new HarmonyMethod(typeof(Limiter).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
                    Plugin.Log.LogInfo("Limiter hooked " + typeName + "." + n);
                }
                catch (Exception e) { Plugin.Log.LogWarning("Limiter could not hook " + typeName + "." + n + ": " + e.Message); }
                return;
            }
            Plugin.Log.LogWarning("Limiter skipped, no hookable method on " + typeName);
        }

        // ------------------------------------------------------------ 4. Camp director credits

        static FieldInfo creditField;

        static void DirectorPrefix(Component __instance)
        {
            try
            {
                if (creditField == null || ReferenceEquals(__instance, null) || !IsServer()) return;
                float credit = (float)creditField.GetValue(__instance);
                float cap = Plugin.MaxCampDirectorCredits.Value;
                if (cap <= 0 || credit <= cap) return;
                string name = __instance.gameObject.name ?? "";
                bool match = Plugin.CapHalcyonWaveDirector.Value && name.StartsWith("Activation and Tier Change Wave", StringComparison.OrdinalIgnoreCase);
                if (!match)
                    foreach (var p in (Plugin.CampDirectorNames.Value ?? "").Split(';'))
                        if (p.Trim().Length > 0 && name.StartsWith(p.Trim(), StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                if (!match) return;
                creditField.SetValue(__instance, cap);
                Count("Camp director credits capped (" + name + ")");
                Plugin.Log.LogInfo(string.Format("LoopLagLimiter capped '{0}' monster credits from {1:0} to {2:0}.", name, credit, cap));
            }
            catch (Exception e) { OnceError("Camp credit cap", e); }
        }

        // Server-side projectile spawn point(s): FireProjectileServer if it exists, else the FireProjectile overloads.
        internal static List<MethodInfo> ProjectileFireMethods()
        {
            var result = new List<MethodInfo>();
            var pm = TypeFinder.Find("RoR2.Projectile.ProjectileManager");
            if (pm == null) return result;
            var all = pm.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (var m in all)
                if (m.Name == "FireProjectileServer" && m.ReturnType == typeof(void) && !m.ContainsGenericParameters) result.Add(m);
            if (result.Count == 0)
                foreach (var m in all)
                    if (m.Name == "FireProjectile" && m.ReturnType == typeof(void) && !m.ContainsGenericParameters) result.Add(m);
            return result;
        }

        internal static void CountPublic(string key) { Count(key); }

        static void Count(string key) { int v; Counts.TryGetValue(key, out v); Counts[key] = v + 1; }

        internal static bool IsServer()
        {
            try { return serverActive == null || (bool)serverActive.GetValue(null, null); } catch { return true; }
        }

        static void Kill(GameObject go)
        {
            if (ReferenceEquals(go, null) || !go) return;
            bool networked = false;
            if (netIdentityType != null)
            {
                var ni = go.GetComponent(netIdentityType);
                networked = !ReferenceEquals(ni, null) && ni;
            }
            if (networked && serverDestroy != null) serverDestroy.Invoke(null, new object[] { go });
            else UnityEngine.Object.Destroy(go);
        }

        // ------------------------------------------------------------ 1. RadialForce

        static void RadialForcePostfix(Component __instance)
        {
            try
            {
                if (!IsServer() || ReferenceEquals(__instance, null) || !__instance) return;
                var go = __instance.gameObject;
                // Only free-standing pull effects are ever removed: never anything that is (or is part of) a character,
                // ally, minion, deployable or anything with health, whatever mod added it.
                if (!IsFreeStandingEffect(go)) return;
                string key = Probes.Clean(go.name);
                LinkedList<GameObject> list;
                if (!forces.TryGetValue(key, out list)) { list = new LinkedList<GameObject>(); forces[key] = list; }
                // forget ones that already expired
                for (var n = list.First; n != null;) { var next = n.Next; if (!n.Value) list.Remove(n); n = next; }
                if (list.Contains(go)) return;
                list.AddLast(go);
                int cap = Plugin.MaxRadialForcesPerPrefab.Value;
                if (cap <= 0) return;
                while (list.Count > cap)
                {
                    var oldest = list.First.Value;
                    list.RemoveFirst();
                    Kill(oldest);
                    Count("RadialForce removed (" + key + ")");
                }
            }
            catch (Exception e) { OnceError("RadialForce cap", e); }
        }

        // ------------------------------------------------------------ ally protection

        static readonly string[] livingTypes = { "RoR2.CharacterBody", "RoR2.CharacterMaster", "RoR2.HealthComponent", "RoR2.Deployable", "RoR2.TeamComponent" };
        static Type[] livingTypeCache;

        static Type[] LivingTypes()
        {
            if (livingTypeCache == null)
            {
                var l = new List<Type>();
                foreach (var n in livingTypes) { var t = TypeFinder.Find(n); if (t != null) l.Add(t); }
                livingTypeCache = l.ToArray();
            }
            return livingTypeCache;
        }

        // true only for a root object that carries none of the "this is a living thing" components
        internal static bool IsFreeStandingEffect(GameObject go)
        {
            if (ReferenceEquals(go, null) || !go) return false;
            var tr = go.transform;
            if (!ReferenceEquals(tr, null) && !ReferenceEquals(tr.parent, null)) return false; // part of something else
            foreach (var t in LivingTypes())
            {
                var c = go.GetComponent(t);
                if (!ReferenceEquals(c, null) && c) return false;
            }
            return true;
        }

        // Projectiles that create allies/minions/deployables are never blocked (e.g. Goobo Jr.'s clone projectile).
        static readonly Dictionary<string, bool> spawnsSomething = new Dictionary<string, bool>();

        internal static bool SpawnsAllies(GameObject prefab, string key)
        {
            bool v;
            if (spawnsSomething.TryGetValue(key, out v)) return v;
            v = false;
            try
            {
                var tr = prefab.transform;
                var comps = ReferenceEquals(tr, null) ? null : tr.GetComponents(typeof(Component));
                if (comps != null)
                    foreach (var c in comps)
                    {
                        if (ReferenceEquals(c, null)) continue;
                        string n = c.GetType().Name;
                        if (n.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Summon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Minion", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Deploy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("Master", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Clone", StringComparison.OrdinalIgnoreCase) >= 0)
                        { v = true; break; }
                    }
                if (!v && (key.IndexOf("Clone", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           key.IndexOf("Summon", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("Minion", StringComparison.OrdinalIgnoreCase) >= 0))
                    v = true;
            }
            catch { v = true; } // if in doubt, never block
            spawnsSomething[key] = v;
            if (v) Plugin.Log.LogInfo("Projectile '" + key + "' spawns something; the projectile cap will never block it.");
            return v;
        }

        // ------------------------------------------------------------ 2. Orbs

        // live orb count per orb class, rebuilt every physics tick (max over OrbManager's lists, so an orb that
        // sits in two lists isn't counted twice); OrbAllowed() adds to it between rebuilds.
        static readonly Dictionary<Type, int> orbCounts = new Dictionary<Type, int>();
        static readonly Dictionary<Type, int> orbScratch = new Dictionary<Type, int>();

        internal static bool OrbAllowed(Type t)
        {
            int cap = Plugin.MaxAliveOrbsPerType.Value;
            if (cap <= 0 || t == null) return true;
            int n; orbCounts.TryGetValue(t, out n);
            if (n >= cap) { Count("Orb not created, cap reached (" + t.Name + ")"); return false; }
            orbCounts[t] = n + 1;
            return true;
        }

        static void RecountOrbs(object manager)
        {
            orbCounts.Clear();
            foreach (var f in orbLists)
            {
                var list = f.GetValue(manager) as IList;
                if (list == null) continue;
                orbScratch.Clear();
                for (int i = 0; i < list.Count; i++)
                {
                    var o = list[i];
                    if (o == null) continue;
                    var t = o.GetType();
                    int n; orbScratch.TryGetValue(t, out n); orbScratch[t] = n + 1;
                }
                foreach (var kv in orbScratch)
                {
                    int n; orbCounts.TryGetValue(kv.Key, out n);
                    if (kv.Value > n) orbCounts[kv.Key] = kv.Value;
                }
            }
        }

        static void OrbManagerPrefix(object __instance)
        {
            if (orbLists == null || __instance == null) return;
            try { if (Plugin.MaxAliveOrbsPerType.Value > 0) RecountOrbs(__instance); } catch (Exception e) { OnceError("Orb count", e); }
            if (!Plugin.DropDeadTargetOrbs.Value) return;
            if (++orbSweepTick % 5 != 0) return; // every 5 physics ticks is plenty
            try
            {
                foreach (var f in orbLists)
                {
                    var list = f.GetValue(__instance) as IList;
                    if (list == null) continue;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var orb = list[i];
                        if (orb == null) continue;
                        var target = Probes.Member(orb, "target") as UnityEngine.Object;
                        // only "Unity-null": the target existed and has been destroyed. Orbs with no target at all are left alone.
                        if (!ReferenceEquals(target, null) && !target)
                        {
                            list.RemoveAt(i);
                            Count("Orb dropped, target gone (" + orb.GetType().Name + ")");
                        }
                    }
                }
            }
            catch (Exception e) { OnceError("Orb sweep", e); }
        }

        // ------------------------------------------------------------ 3. Projectile alive cap

        // Counts live projectiles per prefab. Uses enable/disable (not create/destroy) so the count stays right
        // even if the game recycles projectile objects instead of destroying them.
        internal sealed class ProjectileTracker : MonoBehaviour
        {
            public string Key;
            bool counted;
            public void Begin(string key) { Key = key; Count(true); }
            void OnEnable() { if (Key != null) Count(true); }
            void OnDisable() { Count(false); }
            void Count(bool alive)
            {
                if (Key == null || alive == counted) return;
                counted = alive;
                int v; AliveProjectiles.TryGetValue(Key, out v);
                AliveProjectiles[Key] = Math.Max(0, v + (alive ? 1 : -1));
            }
        }

        static void ProjectileStartPostfix(Component __instance)
        {
            try
            {
                if (ReferenceEquals(__instance, null) || !__instance) return;
                var go = __instance.gameObject;
                if (!ReferenceEquals(go.GetComponent(typeof(ProjectileTracker)), null)) return;
                var tr = go.AddComponent<ProjectileTracker>();
                if (ReferenceEquals(tr, null)) return;
                tr.Begin(Probes.Clean(go.name));
            }
            catch (Exception e) { OnceError("Projectile tracker", e); }
        }

        // Projectiles fired but not started yet. A projectile's tracker is only attached in its Start(), which Unity runs
        // just before the next physics step, so without this a burst fired within one tick all saw the old count and
        // could overshoot the cap (352 daggers against a 300 cap was seen). Reset every physics tick, by which point
        // the previous tick's projectiles have started and are in AliveProjectiles.
        static readonly Dictionary<string, int> inFlight = new Dictionary<string, int>();
        static int inFlightFrame = -1;
        static float inFlightFixed = -1f;

        static bool FirePrefix(object[] __args)
        {
            try
            {
                if (Plugin.MaxAliveProjectilesPerPrefab.Value <= 0 || !IsServer()) return true;
                var first = __args != null && __args.Length > 0 ? __args[0] : null;
                object prefab = first is GameObject ? first : Probes.Member(first, "projectilePrefab");
                var pgo = prefab as GameObject;
                if (ReferenceEquals(pgo, null) || !pgo) return true;
                string key = Probes.Clean(pgo.name);
                if (SpawnsAllies(pgo, key)) return true;
                int f = Time.frameCount; float ft = Time.fixedTime;
                if (f != inFlightFrame || ft != inFlightFixed) { inFlight.Clear(); inFlightFrame = f; inFlightFixed = ft; }
                int alive; AliveProjectiles.TryGetValue(key, out alive);
                int pending; inFlight.TryGetValue(key, out pending);
                if (alive + pending >= Plugin.MaxAliveProjectilesPerPrefab.Value)
                {
                    Count("Projectile not fired, cap reached (" + key + ")");
                    return false;
                }
                inFlight[key] = pending + 1;
            }
            catch (Exception e) { OnceError("Projectile cap", e); }
            return true;
        }

        static readonly HashSet<string> reported = new HashSet<string>();
        static void OnceError(string what, Exception e)
        {
            if (reported.Add(what)) Plugin.Log.LogError("LoopLagLimiter " + what + " failed (reported once): " + e);
        }

        public static void OnSceneChange()
        {
            orbCounts.Clear();
            inFlight.Clear();
            forces.Clear();
            AliveProjectiles.Clear();
        }
    }
}
