using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LoopLagLimiter
{
    // "Heavy load" detection plus the two features that only act under heavy load:
    //  - gold nuggets (Ghor's Tome BonusMoneyPack) are not spawned (also skipped whenever every player's money is maxed)
    //  - item drops queue once too many items are on the ground, and come out one at a time as items are picked up
    // Normal runs never reach the FPS threshold, so vanilla behaviour is untouched there.
    internal static class LoadManager
    {
        // ------------------------------------------------------------ heavy-load detection

        public static bool HeavyLoad { get; private set; }
        static long lastTs = Stopwatch.GetTimestamp();
        static double avgFrameMs = 16;
        static float belowSince = -1f, aboveSince = -1f;
        static float sceneStart;

        // called every rendered frame from Plugin.Update
        public static void OnFrame()
        {
            long now = Stopwatch.GetTimestamp();
            double ms = (now - lastTs) * 1000.0 / Stopwatch.Frequency;
            lastTs = now;
            if (ms > 5000) ms = 5000;
            avgFrameMs += (ms - avgFrameMs) * Math.Min(1.0, ms / 1000.0); // ~1 second average, real time
            float t = Time.realtimeSinceStartup;
            int threshold = Plugin.HeavyLoadFps.Value;
            if (threshold <= 0 || t - sceneStart < 10f) { SetHeavy(false); return; } // ignore loading hitches
            double fps = 1000.0 / Math.Max(1.0, avgFrameMs);
            if (fps < threshold)
            {
                aboveSince = -1f;
                if (belowSince < 0) belowSince = t;
                if (!HeavyLoad && t - belowSince >= 2f) SetHeavy(true);
            }
            else if (fps > threshold + 5)
            {
                belowSince = -1f;
                if (aboveSince < 0) aboveSince = t;
                if (HeavyLoad && t - aboveSince >= 3f) SetHeavy(false);
            }
        }

        static void SetHeavy(bool v)
        {
            if (HeavyLoad == v) return;
            HeavyLoad = v;
            if (v) { sweepNow = true; sweepTicks = 0; }
            Plugin.Log.LogInfo(v ? "Heavy load detected (below " + Plugin.HeavyLoadFps.Value + " FPS): gold nuggets and physics pickups paused and cleared, item drops queue past the limit."
                                 : "Heavy load over: gold nuggets and item drops back to normal" + (queue.Count > 0 ? " (" + queue.Count + " queued items will drop over the next few seconds)." : "."));
        }

        // ------------------------------------------------------------ "items still queued" warning

        static int warnTick;
        static float nextWarnAt;
        static bool teleporterWasCharged;

        // Called every physics tick. While items are queued, warns everyone in chat when a player is near a charged
        // teleporter or a portal, so nobody leaves the stage before the queue has dropped.
        public static void WarnTick()
        {
            if (queue.Count == 0 || !Plugin.WarnBeforeLeaving.Value || ++warnTick % 30 != 0 || !Limiter.IsServer()) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                var targets = new List<Vector3>();
                bool chargedNow = false;
                var tp = TypeFinder.Find("RoR2.TeleporterInteraction");
                if (tp != null)
                {
                    var instProp = tp.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    var tele = instProp == null ? null : instProp.GetValue(null, null) as Component;
                    if (!ReferenceEquals(tele, null) && tele)
                    {
                        object charged = Probes.Member(tele, "isCharged");
                        if (charged is bool && (bool)charged) { chargedNow = true; targets.Add(tele.transform.position); }
                    }
                }
                // portals: every SceneExitController, except the one on the teleporter itself (only counts once charged, above)
                var sec = TypeFinder.Find("RoR2.SceneExitController");
                if (sec != null)
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(sec))
                    {
                        var c = o as Component;
                        if (ReferenceEquals(c, null) || !c) continue;
                        if (tp != null && !ReferenceEquals(c.gameObject.GetComponent(tp), null)) continue;
                        targets.Add(c.transform.position);
                    }

                bool justCharged = chargedNow && !teleporterWasCharged;
                teleporterWasCharged = chargedNow;
                bool near = justCharged || (targets.Count > 0 && PlayerNear(targets, Plugin.WarnRadius.Value));
                if (!near || (!justCharged && now < nextWarnAt)) return;
                nextWarnAt = now + 15f;
                Broadcast("<size=150%><b><color=#FF2A2A>WAIT! " + queue.Count + " item" + (queue.Count == 1 ? " has" : "s have") +
                          " not dropped yet (item queue). Leaving now loses " + (queue.Count == 1 ? "it" : "them") + ".</color></b></size>");
            }
            catch (Exception e) { Plugin.Log.LogDebug("Queue warning: " + e.Message); }
        }

        static bool PlayerNear(List<Vector3> targets, float radius)
        {
            var cb = TypeFinder.Find("RoR2.CharacterBody");
            var listProp = cb == null ? null : cb.GetProperty("readOnlyInstancesList", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var list = listProp == null ? null : listProp.GetValue(null, null) as IEnumerable;
            if (list == null) return false;
            float r2 = radius * radius;
            foreach (var b in list)
            {
                var body = b as Component;
                if (ReferenceEquals(body, null) || !body) continue;
                object pc = Probes.Member(body, "isPlayerControlled");
                if (!(pc is bool) || !(bool)pc) continue;
                var pos = body.transform.position;
                foreach (var t in targets) if ((pos - t).sqrMagnitude <= r2) return true;
            }
            return false;
        }

        static MethodInfo sendBroadcast;
        static Type simpleMsgType;

        // Chat.SendBroadcastChat(new Chat.SimpleChatMessage { baseToken = text }): reaches every player, including
        // ones without the mod (an unknown token is shown as-is, rich text included).
        static void Broadcast(string text)
        {
            if (sendBroadcast == null)
            {
                var chat = TypeFinder.Find("RoR2.Chat");
                simpleMsgType = chat == null ? null : chat.GetNestedType("SimpleChatMessage", BindingFlags.Public | BindingFlags.NonPublic);
                var baseMsg = chat == null ? null : chat.GetNestedType("ChatMessageBase", BindingFlags.Public | BindingFlags.NonPublic) ?? TypeFinder.Find("RoR2.ChatMessageBase");
                if (chat != null && baseMsg != null) sendBroadcast = chat.GetMethod("SendBroadcastChat", BindingFlags.Static | BindingFlags.Public, null, new[] { baseMsg }, null);
                if (sendBroadcast == null || simpleMsgType == null) { Plugin.Log.LogWarning("Queue warning: chat API not found. " + text); return; }
            }
            var msg = Activator.CreateInstance(simpleMsgType);
            var f = AccessTools.Field(simpleMsgType, "baseToken");
            if (f == null) return;
            f.SetValue(msg, text);
            sendBroadcast.Invoke(null, new[] { msg });
            Limiter.CountPublic("Warned players: items still queued");
        }

        public static void OnSceneChange()
        {
            teleporterWasCharged = false;
            nextWarnAt = 0f;
            sceneStart = Time.realtimeSinceStartup;
            belowSince = aboveSince = -1f;
            HeavyLoad = false;
            if (queue.Count > 0) Plugin.Log.LogInfo(queue.Count + " queued item drop(s) were left behind with the stage (same as items left on the ground).");
            queue.Clear();
            groundItems = 0; airDroplets = 0;
        }

        // ------------------------------------------------------------ install

        public static void Install(Harmony h)
        {
            // gold nuggets: the pack's pickup trigger and its spawn-velocity component are both Unity Start() messages,
            // which the engine calls directly (never inlined)
            // physics pickups (gold nuggets, Monster Tooth healing orbs, Bandolier ammo packs, buff pickups, modded variants):
            // their Start() is a Unity message, called by the engine directly (never inlined)
            foreach (var tn in new[] { "RoR2.MoneyPickup", "RoR2.HealthPickup", "RoR2.AmmoPickup", "RoR2.BuffPickup", "RoR2.GravitatePickup", "RoR2.VelocityRandomOnStart" })
                HookMessage(h, tn, "PhysicsPickupPostfix");
            foreach (var tn in new[] { "RoR2.MoneyPickup", "RoR2.HealthPickup", "RoR2.AmmoPickup", "RoR2.BuffPickup" })
            {
                var t = TypeFinder.Find(tn);
                if (t != null) sweepTypes.Add(t);
            }

            // item counts
            HookMessage(h, "RoR2.GenericPickupController", "PickupStartPostfix");
            HookMessage(h, "RoR2.PickupDropletController", "DropletStartPostfix");

            // item drop queue
            var pdc = TypeFinder.Find("RoR2.PickupDropletController");
            if (pdc != null)
            {
                var pre = new HarmonyMethod(typeof(LoadManager).GetMethod("DropPrefix", BindingFlags.Static | BindingFlags.NonPublic));
                pre.priority = Priority.Low; // let other mods (e.g. drop-delay mods) run first; we only gate the final spawn
                var fin = new HarmonyMethod(typeof(LoadManager).GetMethod("DropFinalizer", BindingFlags.Static | BindingFlags.NonPublic));
                int n = 0;
                foreach (var m in pdc.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == "CreatePickupDroplet" && m.ReturnType == typeof(void) && !m.ContainsGenericParameters)
                        try { h.Patch(m, prefix: pre, finalizer: fin); n++; } catch (Exception e) { Plugin.Log.LogWarning("Item queue: " + e.Message); }
                Plugin.Log.LogInfo("Item drop queue hooked " + n + " CreatePickupDroplet overload(s).");
            }
        }

        static void HookMessage(Harmony h, string typeName, string postfix)
        {
            var t = TypeFinder.Find(typeName);
            if (t == null) { Plugin.Log.LogDebug("LoadManager: " + typeName + " not found"); return; }
            foreach (var n in new[] { "Start", "OnEnable", "Awake" })
            {
                var m = AccessTools.DeclaredMethod(t, n, Type.EmptyTypes);
                if (m == null) continue;
                try { h.Patch(m, postfix: new HarmonyMethod(typeof(LoadManager).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic))); }
                catch (Exception e) { Plugin.Log.LogWarning("LoadManager could not hook " + typeName + "." + n + ": " + e.Message); }
                return;
            }
        }

        // ------------------------------------------------------------ gold nuggets

        static readonly string[] neverTouch = { "GenericPickupController", "PickupDropletController", "PickupPickerController", "CharacterBody", "CharacterMaster", "HealthComponent" };

        // "money" for gold nuggets, "pickup" for any other physics pickup, null for anything we must leave alone
        static string Classify(GameObject root)
        {
            var comps = root.transform.GetComponentsInChildren(typeof(Component));
            if (comps == null) comps = root.transform.GetComponents(typeof(Component));
            if (comps == null) return null;
            bool money = false, pickup = false;
            foreach (var c in comps)
            {
                if (ReferenceEquals(c, null)) continue;
                string n = c.GetType().Name;
                foreach (var nt in neverTouch) if (n == nt) return null; // real items, item droplets, characters
                if (n == "MoneyPickup") money = true;
                else if (n == "GravitatePickup" || n.EndsWith("Pickup", StringComparison.Ordinal)) pickup = true;
            }
            if (money || root.name.StartsWith("BonusMoneyPack", StringComparison.Ordinal)) return "money";
            return pickup ? "pickup" : null;
        }

        static void PhysicsPickupPostfix(Component __instance)
        {
            try
            {
                if (!Limiter.IsServer() || ReferenceEquals(__instance, null) || !__instance) return;
                TryRemove(RootOf(__instance), false);
            }
            catch { }
        }

        static GameObject RootOf(Component c)
        {
            return c.transform != null && !ReferenceEquals(c.transform.root, null) ? c.transform.root.gameObject : c.gameObject;
        }

        // removes a physics pickup if the settings and current load say so; sweep = it was already lying on the ground
        static bool TryRemove(GameObject root, bool sweep)
        {
            if (ReferenceEquals(root, null) || !root) return false;
            if (killedFrame != Time.frameCount) { killedFrame = Time.frameCount; killedThisFrame.Clear(); }
            if (killedThisFrame.Contains(root)) return false; // several hooked components on the same pickup
            string kind = Classify(root);
            if (kind == null) return false;
            string what;
            if (kind == "money")
            {
                if (!Plugin.PauseGoldNuggets.Value) return false;
                bool maxed = AllPlayersMaxMoney();
                if (!HeavyLoad && !maxed) return false;
                what = sweep ? (maxed ? "Gold nugget removed from the ground (money already at maximum)" : "Gold nugget removed from the ground (heavy load)")
                             : (maxed ? "Gold nugget skipped (money already at maximum)" : "Gold nugget skipped (heavy load)");
            }
            else
            {
                if (!Plugin.PausePhysicsPickups.Value || !HeavyLoad) return false;
                what = (sweep ? "Physics pickup removed from the ground (heavy load): " : "Physics pickup skipped (heavy load): ") + Probes.Clean(root.name);
            }
            killedThisFrame.Add(root);
            Limiter.KillPublic(root);
            Limiter.CountPublic(what);
            return true;
        }

        // ------------------------------------------------------------ heavy-load sweep of pickups already on the ground

        static readonly List<Type> sweepTypes = new List<Type>();
        static int sweepTicks;
        static bool sweepNow;

        // called every physics tick: when heavy load starts, and every second while it lasts, clear the
        // nuggets/healing orbs/ammo packs that are already lying around (new ones are stopped at spawn)
        public static void SweepTick()
        {
            if (!HeavyLoad || sweepTypes.Count == 0) return;
            if (!sweepNow && ++sweepTicks < 60) return;
            sweepNow = false; sweepTicks = 0;
            if (!Plugin.SweepPickupsOnHeavyLoad.Value || !Limiter.IsServer()) return;
            if (!Plugin.PauseGoldNuggets.Value && !Plugin.PausePhysicsPickups.Value) return;
            int removed = 0;
            try
            {
                foreach (var t in sweepTypes)
                {
                    var found = UnityEngine.Object.FindObjectsOfType(t);
                    if (found == null) continue;
                    foreach (var o in found)
                    {
                        var c = o as Component;
                        if (ReferenceEquals(c, null) || !c) continue;
                        if (TryRemove(RootOf(c), true)) removed++;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogDebug("Pickup sweep failed: " + e.Message); }
            if (removed > 0) Plugin.Log.LogInfo("Heavy load: removed " + removed + " pickup(s) already on the ground.");
        }

        static readonly HashSet<GameObject> killedThisFrame = new HashSet<GameObject>();
        static int killedFrame = -1;

        static bool AllPlayersMaxMoney()
        {
            if (!Plugin.PreventMoneyOverflow.Value) return false;
            var cm = TypeFinder.Find("RoR2.CharacterMaster");
            var listProp = cm == null ? null : cm.GetProperty("readOnlyInstancesList", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var list = listProp == null ? null : listProp.GetValue(null, null) as IEnumerable;
            if (list == null) return false;
            int players = 0;
            foreach (var m in list)
            {
                if (m == null || Probes.Member(m, "playerCharacterMasterController") == null) continue;
                players++;
                uint money;
                if (!Limiter.TryGetMoney(m, out money) || money < (uint)int.MaxValue) return false;
            }
            return players > 0;
        }

        // ------------------------------------------------------------ item counts

        static int groundItems, airDroplets;

        internal sealed class CountTracker : MonoBehaviour
        {
            public bool Droplet;
            bool counted;
            public void Begin(bool droplet) { Droplet = droplet; Set(true); }
            void OnEnable() { if (counted == false && started) Set(true); }
            void OnDisable() { Set(false); }
            bool started;
            void Set(bool alive)
            {
                started = true;
                if (alive == counted) return;
                counted = alive;
                if (Droplet) airDroplets = Math.Max(0, airDroplets + (alive ? 1 : -1));
                else groundItems = Math.Max(0, groundItems + (alive ? 1 : -1));
            }
        }

        static void Track(Component c, bool droplet)
        {
            try
            {
                if (ReferenceEquals(c, null) || !c) return;
                var go = c.gameObject;
                if (!ReferenceEquals(go.GetComponent(typeof(CountTracker)), null)) return;
                var tr = go.AddComponent<CountTracker>();
                if (!ReferenceEquals(tr, null)) tr.Begin(droplet);
            }
            catch { }
        }

        static void PickupStartPostfix(Component __instance) { Track(__instance, false); }
        static void DropletStartPostfix(Component __instance) { Track(__instance, true); }

        public static int ItemsOnGround { get { return groundItems + airDroplets; } }

        // ------------------------------------------------------------ item drop queue

        sealed class Pending { public MethodBase Method; public object[] Args; }
        static readonly Queue<Pending> queue = new Queue<Pending>();
        static int depth;
        static bool releasing;
        static int pendingThisTick, pendingFrame = -1;
        static float pendingFixed = -1f, trickleBudget;

        public static int Queued { get { return queue.Count; } }

        static bool DropPrefix(MethodBase __originalMethod, object[] __args, out bool __state)
        {
            __state = false;
            try
            {
                if (releasing || depth > 0) { depth++; __state = true; return true; } // nested overload or our own release
                int cap = Plugin.MaxItemsOnGround.Value;
                if (cap > 0 && HeavyLoad && Limiter.IsServer())
                {
                    int f = Time.frameCount; float ft = Time.fixedTime;
                    if (f != pendingFrame || ft != pendingFixed) { pendingFrame = f; pendingFixed = ft; pendingThisTick = 0; }
                    if (ItemsOnGround + pendingThisTick >= cap)
                    {
                        queue.Enqueue(new Pending { Method = __originalMethod, Args = (object[])__args.Clone() });
                        Limiter.CountPublic("Item drop queued (too many items on the ground)");
                        return false;
                    }
                    pendingThisTick++;
                }
                depth++; __state = true;
            }
            catch { }
            return true;
        }

        static Exception DropFinalizer(Exception __exception, bool __state)
        {
            if (__state) depth = Math.Max(0, depth - 1);
            return __exception;
        }

        // called every physics tick: drop queued items while there is room (or heavy load is over, a few per second)
        public static void Tick()
        {
            if (queue.Count == 0) return;
            int cap = Plugin.MaxItemsOnGround.Value;
            bool room = cap <= 0 || !HeavyLoad || ItemsOnGround < cap;
            if (!room) return;
            trickleBudget += Plugin.QueuedItemsPerSecond.Value * Time.fixedDeltaTime;
            if (HeavyLoad && cap > 0) trickleBudget = Math.Max(trickleBudget, 1f); // under the cap again: next item comes out now
            int released = 0; // new droplets are only counted once they start (next tick), so count this tick's releases here
            while (trickleBudget >= 1f && queue.Count > 0 && (cap <= 0 || !HeavyLoad || ItemsOnGround + released < cap))
            {
                trickleBudget -= 1f;
                var p = queue.Dequeue();
                releasing = true;
                released++;
                try { p.Method.Invoke(null, p.Args); }
                catch (Exception e) { Plugin.Log.LogWarning("Queued item drop failed: " + (e.InnerException ?? e).Message); }
                finally { releasing = false; }
            }
            if (queue.Count == 0) trickleBudget = 0f;
        }
    }
}
