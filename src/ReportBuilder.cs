using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace LoopLagLimiter
{
    internal static class ReportBuilder
    {
        public static string Build(FrameStats s, string title, bool isWindow)
        {
            var sb = new StringBuilder();
            sb.AppendLine("==================== LoopLagLimiter diagnostics " + Plugin.VERSION + " ====================");
            sb.AppendLine(title);
            sb.AppendLine(string.Format("Real time {0:yyyy-MM-dd HH:mm:ss}, frame #{1}, game time {2:0.0}s", DateTime.Now, Time.frameCount, Time.time));
            if (isWindow)
                sb.AppendLine(string.Format("Window: {0} frames, {1:0} ms total, avg {2:0.0} ms/frame, worst {3:0} ms, {4} physics ticks",
                    s.Frames, s.FrameMs, s.Frames > 0 ? s.FrameMs / s.Frames : 0, s.WorstFrameMs, s.FixedSteps));
            else
                sb.AppendLine(string.Format("Frame time {0:0} ms, physics ticks this frame {1}, GC collections gen0/1/2: {2}/{3}/{4}, heap {5:0} MB",
                    s.FrameMs, s.FixedSteps, s.GcDelta[0], s.GcDelta[1], s.GcDelta[2], GC.GetTotalMemory(false) / 1048576.0));
            double total = isWindow ? s.FrameMs : s.FrameMs;

            // ---- deep profile
            if (s.Deep.Count > 0)
            {
                long deepTicks = s.Deep.Values.Sum(x => x.Ticks);
                double deepMs = Ticks.Ms(deepTicks);
                sb.AppendLine();
                sb.AppendLine(string.Format("-- Deep profile: script Update/FixedUpdate/LateUpdate total {0:0} ms ({1:0}% of frame time). Remaining {2:0} ms is engine work (physics, rendering, GC, loading) or coroutines.",
                    deepMs, total > 0 ? deepMs * 100 / total : 0, Math.Max(0, total - deepMs)));
                foreach (var kv in s.Deep.OrderByDescending(x => x.Value.Ticks).Take(25))
                    sb.AppendLine(string.Format("  {0,9:0.0} ms {1,8} calls  {2}", Ticks.Ms(kv.Value.Ticks), kv.Value.Calls, kv.Key));
            }
            else if (!Deep.Enabled)
            {
                sb.AppendLine("(Deep profile is off. Press F10 before reproducing to also see which component's Update is slow.)");
            }

            // ---- probes
            sb.AppendLine();
            sb.AppendLine("-- Probes (inclusive times; probes nest, so do not add them up):");
            var order = Enumerable.Range(0, Probes.Count).Where(i => s.Probes[i].Calls > 0 || s.Probes[i].Skipped > 0)
                                  .OrderByDescending(i => s.Probes[i].Ticks).ToList();
            if (order.Count == 0) sb.AppendLine("  (no probe activity)");
            foreach (int i in order)
            {
                var p = s.Probes[i];
                sb.AppendLine(string.Format("  {0,9:0.0} ms {1,8} calls{2}  {3}", Ticks.Ms(p.Ticks), p.Calls,
                    p.Skipped > 0 ? " (" + p.Skipped + " throttled)" : "", Probes.NameOf(i)));
                foreach (var kv in p.Sources.OrderByDescending(x => x.Value.Ticks).ThenByDescending(x => x.Value.Calls).Take(6))
                    sb.AppendLine(string.Format("      {0,9:0.0} ms {1,8}x{2}  {3}", Ticks.Ms(kv.Value.Ticks), kv.Value.Calls,
                        kv.Value.Skipped > 0 ? " (" + kv.Value.Skipped + " throttled)" : "", kv.Key));
            }

            // ---- errors
            if (s.UnityErrors > 0)
            {
                sb.AppendLine();
                sb.AppendLine("-- Unity errors/exceptions logged: " + s.UnityErrors);
                foreach (var kv in s.ErrorKinds.OrderByDescending(x => x.Value).Take(8))
                    sb.AppendLine(string.Format("  {0,6}x  {1}", kv.Value, kv.Key));
            }
            if (Fixes.Counts.Count > 0)
            {
                sb.AppendLine("-- Exceptions suppressed by LoopLagLimiter this session: " +
                    string.Join(", ", Fixes.Counts.Select(kv => kv.Key + " x" + kv.Value).ToArray()));
            }

            if (Limiter.Counts.Count > 0)
            {
                sb.AppendLine("-- Limits applied this session:");
                foreach (var kv in Limiter.Counts.OrderByDescending(x => x.Value).Take(12))
                    sb.AppendLine(string.Format("  {0,8}x  {1}", kv.Value, kv.Key));
            }

            // ---- census
            sb.AppendLine();
            sb.AppendLine("-- What exists right now:");
            try { Census(sb); } catch (Exception e) { sb.AppendLine("  census failed: " + e.Message); }
            sb.AppendLine("=====================================================================");
            return sb.ToString();
        }

        static void Census(StringBuilder sb)
        {
            CountByName(sb, "RoR2.CharacterBody", "Character bodies", true);
            CountByName(sb, "RoR2.Projectile.ProjectileController", "Projectiles", false);
            CountByName(sb, "RoR2.GenericPickupController", "Item pickups on the ground", false);
            CountByName(sb, "RoR2.PickupDropletController", "Pickup droplets in the air", false);
            CountByName(sb, "RoR2.EffectComponent", "Active effects (VFX)", false);
            CountByName(sb, "RoR2.DotController", "DoT controllers", false);
            CountByName(sb, "RoR2.Deployable", "Deployables (turrets, drones, minions)", false);
            Orbs(sb);
            Physics(sb);
            Directors(sb);
            Inspect(sb);
        }

        static UnityEngine.Object[] Find(string typeName)
        {
            var t = AccessTools.TypeByName(typeName);
            if (t == null) return null;
            return UnityEngine.Object.FindObjectsOfType(t);
        }

        static void CountByName(StringBuilder sb, string typeName, string label, bool teams)
        {
            var objs = Find(typeName);
            if (objs == null) { sb.AppendLine("  " + label + ": (type not found)"); return; }
            sb.Append(string.Format("  {0}: {1}", label, objs.Length));
            if (objs.Length == 0) { sb.AppendLine(); return; }
            if (teams)
            {
                var byTeam = objs.GroupBy(o => Convert.ToString(Probes.Member(Probes.Member(o, "teamComponent"), "teamIndex")) ?? "?")
                                 .Select(g => g.Key + " " + g.Count());
                sb.Append("  (by team: " + string.Join(", ", byTeam.ToArray()) + ")");
            }
            sb.AppendLine();
            foreach (var g in objs.GroupBy(o => Probes.ObjName(o)).OrderByDescending(g => g.Count()).Take(8))
                sb.AppendLine(string.Format("      {0,6}  {1}", g.Count(), g.Key));
        }

        static void Orbs(StringBuilder sb)
        {
            var t = AccessTools.TypeByName("RoR2.Orbs.OrbManager");
            if (t == null) return;
            object inst = null;
            var f = AccessTools.Field(t, "instance");
            if (f != null && f.IsStatic) inst = f.GetValue(null);
            if (inst == null) { var p = AccessTools.Property(t, "instance"); if (p != null) inst = p.GetValue(null, null); }
            if (inst == null) { sb.AppendLine("  Orbs: (no OrbManager)"); return; }
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var col = field.GetValue(inst) as ICollection;
                if (col == null) continue;
                sb.AppendLine(string.Format("  Orbs in OrbManager.{0}: {1}", field.Name, col.Count));
                if (col.Count == 0) continue;
                var groups = new Dictionary<string, int>();
                int deadTargets = 0;
                foreach (var o in col)
                {
                    Plugin.Inc(groups, o == null ? "null" : o.GetType().Name);
                    try
                    {
                        var tgt = Probes.Member(o, "target") as UnityEngine.Object;
                        if (ReferenceEquals(tgt, null) || !tgt) deadTargets++;
                    }
                    catch { }
                }
                if (deadTargets > 0) sb.AppendLine("      (" + deadTargets + " of these have no target or a destroyed target)");
                foreach (var kv in groups.OrderByDescending(x => x.Value).Take(8))
                    sb.AppendLine(string.Format("      {0,6}  {1}", kv.Value, kv.Key));
            }
        }

        static void Physics(StringBuilder sb)
        {
            var rbs = Find("UnityEngine.Rigidbody");
            if (rbs != null)
            {
                var sleeping = AccessTools.Method(AccessTools.TypeByName("UnityEngine.Rigidbody"), "IsSleeping");
                int awake = 0;
                if (sleeping != null) foreach (var r in rbs) { try { if (!(bool)sleeping.Invoke(r, null)) awake++; } catch { } }
                sb.AppendLine(string.Format("  Rigidbodies: {0} ({1} awake)", rbs.Length, awake));
                var top = rbs.GroupBy(o => Probes.ObjName(o)).OrderByDescending(g => g.Count()).Take(5).ToList();
                foreach (var g in top)
                    sb.AppendLine(string.Format("      {0,6}  {1}", g.Count(), g.Key));
                foreach (var g in top.Take(2)) Describe(sb, g.Key, g.Cast<Component>().ToList());
            }
            var cols = Find("UnityEngine.Collider");
            if (cols != null) sb.AppendLine("  Colliders: " + cols.Length);
        }

        // Where do objects with this name live, and what are they made of?
        static void Describe(StringBuilder sb, string name, List<Component> items)
        {
            if (items.Count == 0) return;
            sb.AppendLine("    About '" + name + "' (" + items.Count + "):");
            try
            {
                var ex = items[0];
                var comps = ex.GetComponents(typeof(Component));
                sb.AppendLine("      components: " + string.Join(", ", (comps ?? new Component[0]).Select(c => ReferenceEquals(c, null) ? "null" : c.GetType().Name).ToArray()));
            }
            catch (Exception e) { sb.AppendLine("      components: ? " + e.Message); }
            try
            {
                var byScene = items.GroupBy(c => { try { return c.gameObject.scene.name ?? "?"; } catch { return "?"; } })
                                   .Select(g => "'" + g.Key + "' " + g.Count());
                sb.AppendLine("      scene: " + string.Join(", ", byScene.ToArray()));
                var byParent = items.GroupBy(c => { try { var p = c.transform.parent; return ReferenceEquals(p, null) ? "(no parent)" : Path(p); } catch { return "?"; } })
                                    .OrderByDescending(g => g.Count()).Take(5).Select(g => g.Count() + " under " + g.Key);
                sb.AppendLine("      parents: " + string.Join("; ", byParent.ToArray()));
                int inactive = items.Count(c => { try { return !c.gameObject.activeSelf; } catch { return false; } });
                if (inactive > 0) sb.AppendLine("      inactive: " + inactive);
            }
            catch (Exception e) { sb.AppendLine("      ? " + e.Message); }
        }

        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; i < 6 && !ReferenceEquals(t, null); i++) { parts.Insert(0, Probes.Clean(t.gameObject.name)); t = t.parent; }
            if (!ReferenceEquals(t, null)) parts.Insert(0, "...");
            return string.Join("/", parts.ToArray());
        }

        static void Inspect(StringBuilder sb)
        {
            foreach (var raw in (Plugin.InspectTypes.Value ?? "").Split(';'))
            {
                var tn = raw.Trim();
                if (tn.Length == 0) continue;
                var objs = Find(tn);
                if (objs == null) { sb.AppendLine("  " + tn + ": (type not found)"); continue; }
                sb.AppendLine(string.Format("  {0} components: {1}", tn, objs.Length));
                foreach (var g in objs.GroupBy(o => Probes.ObjName(o)).OrderByDescending(g => g.Count()).Take(8))
                {
                    string root = "";
                    try
                    {
                        var c = g.First() as Component;
                        var r = c.transform.root;
                        if (!ReferenceEquals(r, null) && r != c.transform) root = "  (root: " + Probes.Clean(r.gameObject.name) + ")";
                    }
                    catch { }
                    sb.AppendLine(string.Format("      {0,6}  {1}{2}", g.Count(), g.Key, root));
                }
            }
        }

        static void Directors(StringBuilder sb)
        {
            var dirs = Find("RoR2.CombatDirector");
            if (dirs == null || dirs.Length == 0) return;
            sb.AppendLine("  Combat directors: " + dirs.Length);
            foreach (var d in dirs.OrderByDescending(d => Convert.ToDouble(Probes.Member(d, "monsterCredit") ?? 0.0)).Take(6))
            {
                var b = d as Behaviour;
                sb.AppendLine(string.Format("      credits {0,12:0.0}  {1}{2}", Convert.ToDouble(Probes.Member(d, "monsterCredit") ?? 0.0),
                    Probes.ObjName(d), (!ReferenceEquals(b, null) && !b.enabled) ? " (disabled)" : ""));
            }
        }
    }
}
