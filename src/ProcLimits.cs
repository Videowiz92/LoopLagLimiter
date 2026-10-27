using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace LoopLagLimiter
{
    // Proc rate limits. The idea comes from ProcLimiter by DrinkableWater (Sylkadi): give lag-heavy proc
    // items a cooldown. This is a from-scratch reimplementation that limits what a proc SPAWNS (the orb or
    // projectile) rather than editing the item's code, so it also covers modded copies of the same item
    // (e.g. ItemQualities' quality tiers fire the same orbs/projectiles) and survives game updates.
    //
    // Each limit is "at most N per second" (token bucket, 1 second of burst), on the host only.
    internal static class ProcLimits
    {
        internal sealed class Limit
        {
            public string Label;
            public string[] Keys;          // orb keys or projectile prefab names
            public ConfigEntry<int> PerSecond;
        }

        public static readonly List<Limit> Limits = new List<Limit>();
        static readonly Dictionary<string, ConfigEntry<int>> byKey = new Dictionary<string, ConfigEntry<int>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> extra = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, float[]> buckets = new Dictionary<string, float[]>(); // key -> {tokens, lastTime}
        static string extraParsedFrom;
        static FieldInfo lightningTypeField;

        static int effectTickCount, effectTickFrame = -1;
        static float effectTickFixed = -1f;

        public static void Bind(ConfigFile cfg)
        {
            const string S = "5. Proc limits";
            Add(cfg, S, "Sentient Meat Hook", "hook orbs (BounceOrb)", new[] { "BounceOrb" });
            Add(cfg, S, "Polylute", "lightning orbs (VoidLightningOrb)", new[] { "VoidLightningOrb" });
            Add(cfg, S, "Ukulele", "chain lightning orbs", new[] { "LightningOrb.Ukulele" });
            Add(cfg, S, "Plasma Shrimp", "missile orbs (MissileVoidOrb)", new[] { "MissileVoidOrb" });
            Add(cfg, S, "Charged Perforator", "lightning strikes (SimpleLightningStrikeOrb)", new[] { "SimpleLightningStrikeOrb" });
            Add(cfg, S, "AtG Missile", "missiles (MissileProjectile)", new[] { "MissileProjectile" });
            Add(cfg, S, "Sticky Bomb", "bombs (StickyBomb)", new[] { "StickyBomb" });
            Add(cfg, S, "Molten Perforator", "fireballs (FireMeatBall)", new[] { "FireMeatBall" });
            Add(cfg, S, "Ceremonial Dagger", "daggers (DaggerProjectile)", new[] { "DaggerProjectile" });
            Add(cfg, S, "Bundle of Fireworks", "fireworks (FireworkProjectile, tripled by Pocket I.C.B.M.)", new[] { "FireworkProjectile" });
        }

        static void Add(ConfigFile cfg, string section, string item, string what, string[] keys)
        {
            var e = cfg.Bind(section, item + " per second", 0,
                "0 = no limit. At most this many " + item + " " + what + " may be created per second (host only). " +
                "Covers every copy of the item, including modded quality tiers. Fewer procs means less damage.");
            Limits.Add(new Limit { Label = item, Keys = keys, PerSecond = e });
            foreach (var k in keys) byKey[k] = e;
        }

        public static void Install(Harmony h)
        {
            var om = TypeFinder.Find("RoR2.Orbs.OrbManager");
            if (om != null)
                foreach (var m in om.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == "AddOrb" && m.ReturnType == typeof(void) && m.GetParameters().Length == 1)
                        Patch(h, m, "OrbPrefix");

            // Every server-side projectile (host's own or sent by a client) goes through FireProjectileServer;
            // the public FireProjectile overloads funnel into it, so hooking only it counts each projectile once.
            foreach (var m in Limiter.ProjectileFireMethods()) Patch(h, m, "ProjectilePrefix");

            var em = TypeFinder.Find("RoR2.EffectManager");
            if (em != null)
                foreach (var m in em.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == "SpawnEffect" && m.ReturnType == typeof(void) && !m.ContainsGenericParameters)
                        Patch(h, m, "EffectPrefix");
        }

        static void Patch(Harmony h, MethodBase m, string prefix)
        {
            try
            {
                var hm = new HarmonyMethod(typeof(ProcLimits).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic));
                hm.priority = Priority.High;
                h.Patch(m, prefix: hm);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Proc limit could not hook " + m.DeclaringType.Name + "." + m.Name + ": " + e.Message); }
        }

        // ------------------------------------------------------------ limit check

        static int LimitFor(string key)
        {
            ConfigEntry<int> e;
            if (byKey.TryGetValue(key, out e) && e.Value > 0) return e.Value;
            ParseExtra();
            int v;
            return extra.TryGetValue(key, out v) ? v : 0;
        }

        static void ParseExtra()
        {
            string raw = Plugin.ExtraProcLimits.Value ?? "";
            if (ReferenceEquals(raw, extraParsedFrom)) return;
            extraParsedFrom = raw;
            extra.Clear();
            foreach (var part in raw.Split(';'))
            {
                int eq = part.IndexOf('=');
                int n;
                if (eq > 0 && int.TryParse(part.Substring(eq + 1).Trim(), out n) && n > 0) extra[part.Substring(0, eq).Trim()] = n;
            }
        }

        static bool Allow(string key)
        {
            int perSecond = LimitFor(key);
            if (perSecond <= 0) return true;
            float now = Time.fixedTime;
            float[] b;
            if (!buckets.TryGetValue(key, out b)) { b = new[] { (float)perSecond, now }; buckets[key] = b; }
            float elapsed = now - b[1];
            if (elapsed < 0) elapsed = 0; // new run / stage reset the clock
            b[0] = Math.Min(perSecond, b[0] + elapsed * perSecond);
            b[1] = now;
            if (b[0] >= 0.999f) { b[0] = Math.Max(0f, b[0] - 1f); return true; }
            Limiter.CountPublic("Proc limited (" + key + ")");
            return false;
        }

        // ------------------------------------------------------------ hooks

        static bool OrbPrefix(object[] __args)
        {
            try
            {
                if (!Limiter.IsServer()) return true;
                var orb = __args != null && __args.Length > 0 ? __args[0] : null;
                if (orb == null) return true;
                if (!Limiter.OrbAllowed(orb.GetType())) return false;
                string key = orb.GetType().Name;
                if (key == "LightningOrb")
                {
                    // LightningOrb is shared by Ukulele, Tesla Coil, etc.; tell them apart by lightningType
                    if (lightningTypeField == null) lightningTypeField = AccessTools.Field(orb.GetType(), "lightningType");
                    if (lightningTypeField != null) key = "LightningOrb." + lightningTypeField.GetValue(orb);
                }
                return Allow(key);
            }
            catch { return true; }
        }

        static bool ProjectilePrefix(object[] __args)
        {
            try
            {
                if (!Limiter.IsServer()) return true;
                var first = __args != null && __args.Length > 0 ? __args[0] : null;
                object prefab = first is GameObject ? first : Probes.Member(first, "projectilePrefab");
                var go = prefab as GameObject;
                if (ReferenceEquals(go, null) || !go) return true;
                string key = Probes.Clean(go.name);
                if (Limiter.SpawnsAllies(go, key)) return true;
                return Allow(key);
            }
            catch { return true; }
        }

        static bool EffectPrefix()
        {
            try
            {
                int cap = Plugin.MaxEffectsPerTick.Value;
                if (cap <= 0) return true;
                int f = Time.frameCount; float ft = Time.fixedTime;
                if (f != effectTickFrame || ft != effectTickFixed) { effectTickFrame = f; effectTickFixed = ft; effectTickCount = 0; }
                if (++effectTickCount <= cap) return true;
                Limiter.CountPublic("Visual effect skipped (per-tick cap)");
                return false;
            }
            catch { return true; }
        }

        public static void OnSceneChange() { buckets.Clear(); }
    }
}
