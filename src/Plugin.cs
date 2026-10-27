using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoopLagLimiter
{
    // LoopLagLimiter: performance fixes for very long / heavily looped Risk of Rain 2 runs.
    //
    // It deliberately does NOT reference RoR2.dll: every game type is looked up by name at runtime, so the
    // DLL keeps working across game patches. Anything it cannot find is skipped with a log line.
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("com.rune580.riskofoptions", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.Videowiz92.LoopLagLimiter";
        public const string NAME = "LoopLagLimiter";
        public const string VERSION = "1.2.0";

        internal static ManualLogSource Log;
        internal static int MainThreadId;
        internal static bool Reporting; // true while a diagnostics report is written, so our own logging isn't measured

        // Option metadata for the Risk of Options page
        internal sealed class OptionInfo { public ConfigEntryBase Entry; public float Min, Max, Step; public bool Restart; }
        internal static readonly List<OptionInfo> Options = new List<OptionInfo>();

        // 0. Troubleshooting
        internal static ConfigEntry<bool> InstallLimits, InstallProcLimits, RegisterRiskOfOptions, StartupTrace;
        // 1. General
        internal static ConfigEntry<float> MaxCatchUpSeconds;
        // 2. Limits
        internal static ConfigEntry<int> MaxRadialForcesPerPrefab;
        internal static ConfigEntry<bool> DropDeadTargetOrbs;
        internal static ConfigEntry<int> MaxCampDirectorCredits;
        internal static ConfigEntry<string> CampDirectorNames;
        internal static ConfigEntry<int> MaxAliveProjectilesPerPrefab;
        internal static ConfigEntry<int> MaxAliveProjectilesTotal;
        internal static ConfigEntry<int> MaxEffectsPerTick;
        internal static ConfigEntry<int> MaxOnKillChainDepth;
        internal static ConfigEntry<int> MaxAliveOrbsPerType;
        internal static ConfigEntry<int> MaxFiredPerKindPerTick;
        internal static ConfigEntry<bool> PreventMoneyOverflow;
        internal static ConfigEntry<bool> PreventPriceOverflow;
        internal static ConfigEntry<bool> CapHalcyonWaveDirector;
        internal static ConfigEntry<int> MaxDeferredProcsPerTick;
        // 6. Heavy load
        internal static ConfigEntry<int> HeavyLoadFps;
        internal static ConfigEntry<bool> PauseGoldNuggets;
        internal static ConfigEntry<bool> PausePhysicsPickups;
        internal static ConfigEntry<bool> SweepPickupsOnHeavyLoad;
        internal static ConfigEntry<int> MaxItemsOnGround;
        internal static ConfigEntry<int> QueuedItemsPerSecond;
        internal static ConfigEntry<bool> WarnBeforeLeaving;
        internal static ConfigEntry<float> WarnRadius;
        // 3. Fixes
        internal static ConfigEntry<bool> SuppressKnownExceptions;
        internal static ConfigEntry<string> SuppressList;
        // 4. Proc limits (per-item entries live in ProcLimits)
        internal static ConfigEntry<string> ExtraProcLimits;
        // 5. Diagnostics
        internal static ConfigEntry<bool> DiagnosticsEnabled;
        internal static ConfigEntry<int> StallThresholdMs;
        internal static ConfigEntry<float> ReportCooldownSeconds;
        internal static ConfigEntry<int> MaxReportFiles;
        internal static ConfigEntry<bool> AttributeSources;
        internal static ConfigEntry<string> DeepSplitTypes;
        internal static ConfigEntry<string> InspectTypes;
        internal static ConfigEntry<KeyboardShortcut> DeepToggleKey;
        internal static ConfigEntry<KeyboardShortcut> ReportKey;

        Harmony harmony;
        bool diagnostics;
        float defaultMaxDeltaTime = -1f;
        long lastFrameTs;
        float lastReportRealtime = -9999f;
        int reportsWritten;
        bool sceneLoadedThisFrame;
        string reportDir;
        readonly int[] gcAtFrameStart = new int[3];

        void Awake()
        {
            Log = Logger;
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
            BindConfig();
            MigrateRenamedSections();

            harmony = new Harmony(GUID);
            Trace("Awake: config bound");
            if (SuppressKnownExceptions.Value) Fixes.Install(harmony, SuppressList.Value);
            Trace("Awake: exception guards done");
            if (InstallLimits.Value) { Limiter.Install(harmony); ChainGuard.Install(harmony); LoadManager.Install(harmony); } else Log.LogWarning("Troubleshooting: limit hooks NOT installed");
            Trace("Awake: limit hooks done");
            if (InstallProcLimits.Value) ProcLimits.Install(harmony); else Log.LogWarning("Troubleshooting: proc limit hooks NOT installed");
            Trace("Awake: proc limit hooks done");

            diagnostics = DiagnosticsEnabled.Value;
            reportDir = Path.Combine(Paths.BepInExRootPath, NAME);
            if (diagnostics)
            {
                Probes.Install(harmony);
                Application.logMessageReceived += OnUnityLog;
            }

            SceneManager.sceneLoaded += (s, m) =>
            {
                Trace("scene loaded: " + s.name);
                sceneLoadedThisFrame = true;
                Limiter.OnSceneChange();
                ChainGuard.OnSceneChange();
                LoadManager.OnSceneChange();
                ProcLimits.OnSceneChange();
                ApplyCatchUpCap(false);
            };
            MaxCatchUpSeconds.SettingChanged += (s, e) => ApplyCatchUpCap(true);

            // Risk of Options page: registered in Awake like regular Risk of Options mods do
            // (doing it in Start, while the game is loading, could hang the boot).
            if (RegisterRiskOfOptions.Value)
            {
                try { RiskOfOptionsCompat.TryRegister(); }
                catch (Exception e) { Log.LogWarning("Risk of Options integration failed (the mod still works, edit the .cfg instead): " + e.Message); }
            }
            Trace("Awake: Risk of Options step done");

            lastFrameTs = Stopwatch.GetTimestamp();
            Log.LogInfo(NAME + " " + VERSION + " loaded." + (diagnostics ? " Diagnostics ON: F9 = report now, F10 = toggle deep profile. Reports go to " + reportDir : ""));
        }

        void Start()
        {
            Trace("Start: begin");
            defaultMaxDeltaTime = Time.maximumDeltaTime;
            ApplyCatchUpCap(true);
            Trace("Start: end (maximumDeltaTime=" + Time.maximumDeltaTime + ")");
        }

        int traceFrames;
        internal static void Trace(string msg)
        {
            if (StartupTrace != null && StartupTrace.Value) Log.LogInfo("[trace] " + msg);
        }

        void LateUpdate()
        {
            // startup trace: proves the game keeps producing frames after our Start
            if (StartupTrace.Value && traceFrames < 400)
            {
                traceFrames++;
                if (traceFrames == 1 || traceFrames == 10 || traceFrames == 100 || traceFrames == 400)
                    Trace("frame " + traceFrames + " reached (Time.frameCount=" + Time.frameCount + ")");
            }
        }

        // NoFrameSkip's idea (McAwesome, MIT): Unity runs as many physics ticks as needed to catch up after a slow
        // frame, which makes the next frame slow too. Capping maximumDeltaTime caps that catch-up, so an overloaded
        // fight runs in slow motion instead of freezing.
        void ApplyCatchUpCap(bool log)
        {
            float v = MaxCatchUpSeconds.Value;
            float target = v < 0f ? defaultMaxDeltaTime : Math.Max(v, Time.fixedDeltaTime);
            if (target <= 0f || Math.Abs(Time.maximumDeltaTime - target) < 0.0001f) return;
            Time.maximumDeltaTime = target;
            if (log) Log.LogInfo(string.Format("Max catch-up per frame set to {0:0.###} s (about {1:0} physics ticks).", target, target / Time.fixedDeltaTime));
        }

        // ------------------------------------------------------------------ config

        ConfigEntry<T> B<T>(string section, string key, T def, string desc, float min = 0, float max = 0, float step = 1, bool restart = false)
        {
            var e = Config.Bind(section, key, def, desc);
            Options.Add(new OptionInfo { Entry = e, Min = min, Max = max, Step = step, Restart = restart });
            return e;
        }

        // Sections were renumbered in 1.2.0 (Heavy load moved up to 3). Values saved under the old section
        // names are carried over so nobody loses their settings, then the old entries are dropped from the file.
        static readonly string[,] RenamedSections = {
            { "3. Fixes", "4. Fixes" }, { "4. Proc limits", "5. Proc limits" },
            { "5. Diagnostics", "6. Diagnostics" }, { "6. Heavy load", "3. Heavy load" } };

        void MigrateRenamedSections()
        {
            try
            {
                var prop = typeof(ConfigFile).GetProperty("OrphanedEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                var orphans = prop == null ? null : prop.GetValue(Config, null) as System.Collections.Generic.Dictionary<ConfigDefinition, string>;
                if (orphans == null || orphans.Count == 0) return;
                int moved = 0;
                foreach (var o in Options)
                {
                    var def = o.Entry.Definition;
                    for (int i = 0; i < RenamedSections.GetLength(0); i++)
                    {
                        if (def.Section != RenamedSections[i, 1]) continue;
                        var old = new ConfigDefinition(RenamedSections[i, 0], def.Key);
                        string v;
                        if (!orphans.TryGetValue(old, out v)) continue;
                        o.Entry.SetSerializedValue(v);
                        orphans.Remove(old);
                        moved++;
                    }
                }
                if (moved > 0) { Config.Save(); Log.LogInfo("Carried over " + moved + " setting(s) from renumbered config sections."); }
            }
            catch (Exception e) { Log.LogWarning("Config section migration skipped: " + e.Message); }
        }

        void BindConfig()
        {
            const string T = "0. Troubleshooting";
            InstallLimits = B(T, "Install limit hooks", true, "Troubleshooting: set false to skip all hooks in sections 2 (Limits) and 3 (Heavy load).", restart: true);
            InstallProcLimits = B(T, "Install proc limit hooks", true, "Troubleshooting: set false to skip all hooks in section 5 (Proc limits).", restart: true);
            RegisterRiskOfOptions = B(T, "Register Risk of Options page", true, "Set false to skip the in-game settings page (if Risk of Options causes problems).", restart: true);
            StartupTrace = B(T, "Startup trace logging", false, "Logs each startup step (and the first few hundred frames) so a stuck boot can be pinpointed.");

            const string G = "1. General", L = "2. Limits", F = "4. Fixes", P = "5. Proc limits", D = "6. Diagnostics";

            MaxCatchUpSeconds = B(G, "Max catch-up per frame (seconds)", 0.1f,
                "After a slow frame Unity runs extra physics ticks to catch up, which makes the next frame slow too (a lag spiral). " +
                "This caps the catch-up: 0.1 = at most ~6 ticks per frame, so the game only slows down when below ~10 FPS. " +
                "0 = one tick per frame (always slow-motion below 60 FPS). -1 = leave the game's default alone. " +
                "Based on NoFrameSkip by McAwesome; don't run both.", -1f, 0.35f, 0.01f);

            MaxRadialForcesPerPrefab = B(L, "Max pull effects per prefab", 25,
                "0 = off. Keeps at most this many RadialForce pull objects of one kind alive; the oldest is removed first. " +
                "Sentient Meat Hook spawns a pull (MeatHookDelayedForce) on every proc and big builds stack hundreds, each " +
                "searching for targets every physics tick. Only the pull is affected, not hook damage. Host only.", 0, 200);
            DropDeadTargetOrbs = B(L, "Drop orbs whose target died", true,
                "Removes orbs (Polylute, Meat Hook, vine and lightning orbs) whose target has been destroyed. They can never hit " +
                "anything but otherwise keep updating, and can even outlive the stage.");
            MaxCampDirectorCredits = B(L, "Max camp director credits", 3000,
                "0 = off. When the teleporter finishes, its leftover monster credits go to the stage's camp director. Deep into a " +
                "loop that can be hundreds of thousands of credits, spent on an endless post-teleporter wave. This clamps camp " +
                "directors to this many credits. The main and teleporter directors are never touched. Host only.", 0, 50000, 500);
            CampDirectorNames = B(L, "Camp director names", "Camp",
                "Semicolon-separated GameObject name prefixes of the directors the credit cap applies to.");
            CapHalcyonWaveDirector = B(L, "Also cap Halcyon Shrine wave credits", true,
                "Apply the credit cap to the Halcyon Shrine's wave director (\"Activation and Tier Change Wave\"), which was " +
                "seen holding 650,000+ credits deep into a loop.");
            // New key name in 1.1.0 so the new default applies to people upgrading from 1.0.0 (where it was off).
            MaxAliveProjectilesPerPrefab = B(L, "Max alive projectiles per kind", 500,
                "0 = off. If this many projectiles of one kind (StickyBomb, FireMeatBall, LightningStake...) already exist, new " +
                "ones of that kind are not fired until some expire. Covers every item. A heavily looped run had 10,577 Sticky " +
                "Bombs alive at once (14 s of a 24 s frame). Only matters at that kind of scale. Host only.", 0, 5000, 50);
            MaxAliveProjectilesTotal = B(L, "Max alive projectiles total", 1000,
                "0 = off. Once this many projectiles of all kinds exist together, the big kinds (those with more than a tenth of " +
                "this number alive, e.g. 100+ Sticky Bombs) stop firing until the total drops. Small kinds (your skills, rare " +
                "items, enemy attacks) keep working. Testing saw 1,437 projectiles alive with every kind under its own cap. Host only.", 0, 10000, 50);
            MaxAliveOrbsPerType = B(L, "Max alive orbs per kind", 300,
                "0 = off. If this many orbs of one kind (Polylute, Meat Hook, Ukulele, healing orbs...) are already in flight, new " +
                "ones of that kind are not created until some land. Covers every item. A heavily looped run had 1,500 Polylute " +
                "orbs at once. Host only.", 0, 3000, 50);
            MaxFiredPerKindPerTick = B(L, "Max projectiles fired per kind per tick", 30,
                "0 = off. At most this many projectiles of one kind may be fired per physics tick (60 ticks per second). Catches " +
                "items that create and destroy projectiles faster than they live: Ghost Blades are capped by the game at 30 alive, " +
                "so each new blade destroys the oldest, and 4,960 were created and destroyed in one 33-second frame. Covers every " +
                "item; projectiles that spawn allies are never limited. Host only.", 0, 500, 5);
            PreventMoneyOverflow = B(L, "Prevent money overflow", true,
                "Money is stored as an unsigned 32-bit number. Deep into a loop, per-kill gold makes it wrap past about 4.29 billion " +
                "back to almost nothing, and the HUD shows anything above about 2.15 billion as negative. This stops gains at " +
                "2,147,483,647 instead. Host only.");
            PreventPriceOverflow = B(L, "Prevent price overflow", true,
                "Chest, shrine and shop prices are base cost times a difficulty multiplier. Deep into a loop the result no longer fits " +
                "and turns into a negative price. Negative prices become 2,147,483,647 instead, the same number money stops at, so " +
                "you can still buy things once your money reaches the maximum. Host only.");
            MaxOnKillChainDepth = B(L, "Max Gasoline chain depth", 20,
                "0 = off. Gasoline's on-kill explosion runs inside the death that triggered it, so a pack of low-health enemies " +
                "can chain hundreds of explosions deep in one go and crash the game with a stack overflow (a total freeze). " +
                "Past this depth the next explosion is queued for the next physics tick instead. Every explosion still happens. Host only.", 0, 100, 1);
            MaxDeferredProcsPerTick = B(L, "Max queued Gasoline explosions per tick", 200,
                "How many queued Gasoline explosions run per physics tick (keeps a huge chain from becoming one huge frame).", 10, 2000, 10);
            MaxEffectsPerTick = B(L, "Max visual effects per tick", 0,
                "0 = off. Cap on EffectManager.SpawnEffect calls per physics tick. Purely visual (try 150).", 0, 1000, 10);

            const string H = "3. Heavy load";
            HeavyLoadFps = B(H, "Heavy load below FPS", 15,
                "0 = never. When the average frame rate stays below this for 2 seconds, the game counts as under heavy load until it " +
                "recovers (above this + 5 FPS for 3 seconds). The settings below only act under heavy load, so normal runs are untouched.", 0, 60, 1);
            PauseGoldNuggets = B(H, "Pause gold nuggets", true,
                "Under heavy load, gold nuggets (Ghor's Tome bonus money packs) are not spawned: at that point enemies die instantly and " +
                "hundreds of nuggets on the ground just add lag. They are also skipped whenever every player's money is already at " +
                "the maximum (money overflow guard), since they couldn't give anything. Host only.");
            PausePhysicsPickups = B(H, "Pause other physics pickups", true,
                "Under heavy load, other pickups that drop on the ground and fly to players are not spawned either: Monster Tooth " +
                "healing orbs, Bandolier ammo packs, buff pickups, and modded variants (e.g. ItemQualities' quality tiers). Testing " +
                "saw 100+ healing orbs and 70+ ammo packs at once. Real items, item drops and characters are never affected. Host only.");
            SweepPickupsOnHeavyLoad = B(H, "Clear pickups already on the ground", true,
                "When heavy load starts, and every second while it lasts, gold nuggets and other physics pickups already lying on the " +
                "ground are removed too (only the kinds paused above). Real items are never removed. Host only.");
            MaxItemsOnGround = B(H, "Max items on the ground", 50,
                "0 = off. Under heavy load, once this many items are on the ground or falling, new item drops wait in a queue. Each " +
                "time the count drops below the limit (someone picks an item up), the next queued item drops where it would have. " +
                "Queued items left when the stage ends are lost, the same as items left on the ground. Host only.", 0, 500, 10);
            QueuedItemsPerSecond = B(H, "Queued items released per second", 2,
                "When heavy load ends, queued items drop at this rate until the queue is empty.", 1, 50, 1);

            WarnBeforeLeaving = B(H, "Warn before leaving with queued items", true,
                "While items are still queued, post a big red chat warning when a player is near a fully charged teleporter or a " +
                "portal (at most every 15 seconds), and once as soon as the teleporter finishes charging. Everyone sees it, with or " +
                "without the mod. Host only.");
            WarnRadius = B(H, "Warning distance (meters)", 30f, "How close a player has to be to a charged teleporter or portal.", 5f, 100f, 5f);

            SuppressKnownExceptions = B(F, "Suppress known exception spam", true,
                "Catch NullReferenceExceptions from the methods below so the attack continues instead of aborting, and the full " +
                "stack trace isn't logged every time (e.g. Resonance Disc beams hitting a blocker shield).", restart: true);
            SuppressList = B(F, "Methods to guard",
                "RoR2.HurtBox:SimpleColliderBlockFilter;RoR2.AttackSpeedPerNearbyCollider:ServerUpdateValuesFromInventory",
                "Semicolon-separated Type:Method list. Only void or bool methods (bool returns false on error).", restart: true);

            ProcLimits.Bind(Config);
            foreach (var l in ProcLimits.Limits) Options.Add(new OptionInfo { Entry = l.PerSecond, Min = 0, Max = 200, Step = 1 });
            ExtraProcLimits = B(P, "Extra limits", "",
                "More per-second limits as Name=N;Name=N where Name is an orb class (e.g. VineOrb, LightningOrb.Tesla) or a " +
                "projectile prefab (e.g. LightningStake). Use diagnostics reports to find names.");

            DiagnosticsEnabled = B(D, "Enable diagnostics", false,
                "Time the game's hot spots and write a report to BepInEx/LoopLagLimiter whenever a frame stalls. Small overhead; " +
                "turn on to find the cause of lag in your own runs.", restart: true);
            StallThresholdMs = B(D, "Stall threshold (ms)", 250, "A frame that takes at least this long gets a report.", 50, 2000, 50);
            ReportCooldownSeconds = B(D, "Report cooldown (seconds)", 3f, "Minimum real time between automatic reports.", 0, 30, 1);
            MaxReportFiles = B(D, "Max report files per session", 60, "Stops writing report files after this many in one session.", 0, 500, 10);
            AttributeSources = B(D, "Attribute sources", true, "Record who triggered each probe (projectile, orb type, attacker...).");
            DeepSplitTypes = B(D, "Deep profile: split by object",
                "RoR2.RadialForce;RoR2.RTPCController;RoR2.DelayBlast;AkGameObj;RoR2.Projectile.ProjectileImpactExplosion;RoR2.ExperienceOrbBehavior",
                "Component types whose deep-profile time is broken down per GameObject name.");
            InspectTypes = B(D, "Inspect component types", "RoR2.RadialForce;RoR2.RTPCController;AkGameObj;RoR2.DelayBlast",
                "Reports count these components and list which objects carry them.");
            DeepToggleKey = B(D, "Deep profile toggle key", new KeyboardShortcut(KeyCode.F10),
                "Times every component update in the game and all mods (hitch of a few seconds when toggled).");
            ReportKey = B(D, "Report-now key", new KeyboardShortcut(KeyCode.F9), "Writes a report covering everything since the last one.");
        }

        // ------------------------------------------------------------------ diagnostics runtime

        void OnDestroy()
        {
            if (diagnostics) Application.logMessageReceived -= OnUnityLog;
        }

        static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (Reporting) return;
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (Thread.CurrentThread.ManagedThreadId != MainThreadId) return;
            string firstFrame = "";
            if (!string.IsNullOrEmpty(stackTrace))
            {
                int nl = stackTrace.IndexOf('\n');
                firstFrame = nl > 0 ? stackTrace.Substring(0, nl) : stackTrace;
                int at = firstFrame.IndexOf(" (at ", StringComparison.Ordinal);
                if (at > 0) firstFrame = firstFrame.Substring(0, at);
            }
            string cond = condition ?? "";
            if (cond.Length > 100) cond = cond.Substring(0, 100);
            string key = firstFrame.Length > 0 ? cond + "  @ " + firstFrame : cond;
            Frame.Current.UnityErrors++;
            Frame.Window.UnityErrors++;
            Inc(Frame.Current.ErrorKinds, key);
            Inc(Frame.Window.ErrorKinds, key);
        }

        internal static void Inc(Dictionary<string, int> d, string key)
        {
            int v; d.TryGetValue(key, out v); d[key] = v + 1;
        }

        void FixedUpdate()
        {
            try { ChainGuard.Drain(); } catch (Exception e) { Log.LogDebug(e.Message); }
            try { if (InstallLimits.Value) Limiter.MoneySweep(); } catch (Exception e) { Log.LogDebug(e.Message); }
            try { if (InstallLimits.Value) { LoadManager.Tick(); LoadManager.WarnTick();
            LoadManager.SweepTick(); } } catch (Exception e) { Log.LogDebug(e.Message); }
            if (!diagnostics) return;
            Frame.Current.FixedSteps++;
            Frame.Window.FixedSteps++;
        }

        void Update()
        {
            try { LoadManager.OnFrame(); } catch { }
            if (!diagnostics) return;
            long now = Stopwatch.GetTimestamp();
            double frameMs = (now - lastFrameTs) * 1000.0 / Stopwatch.Frequency;
            lastFrameTs = now;

            Frame.Current.FrameMs = frameMs;
            Frame.Window.FrameMs += frameMs;
            Frame.Window.Frames++;
            if (frameMs > Frame.Window.WorstFrameMs) Frame.Window.WorstFrameMs = frameMs;
            for (int g = 0; g < 3; g++)
            {
                Frame.Current.GcDelta[g] = GC.CollectionCount(g) - gcAtFrameStart[g];
                Frame.Window.GcDelta[g] += Frame.Current.GcDelta[g];
            }

            try
            {
                if (DeepToggleKey.Value.IsDown())
                {
                    if (Deep.Enabled) Deep.Disable(); else Deep.Enable();
                    lastFrameTs = Stopwatch.GetTimestamp(); // don't report the patching hitch
                    ResetFrame();
                    return;
                }
                if (ReportKey.Value.IsDown())
                {
                    WriteReport(Frame.Window, "manual: everything since last report", true);
                    Frame.Window = new FrameStats();
                }
            }
            catch (Exception e) { Log.LogError(e); }

            bool sceneLoad = sceneLoadedThisFrame;
            sceneLoadedThisFrame = false;

            if (frameMs >= StallThresholdMs.Value && Time.frameCount > 10)
            {
                float rt = Time.realtimeSinceStartup;
                bool cooldownOk = rt - lastReportRealtime >= ReportCooldownSeconds.Value
                                  || frameMs >= ReportCooldownSeconds.Value * 1000.0;
                if (!sceneLoad && cooldownOk)
                {
                    lastReportRealtime = rt;
                    WriteReport(Frame.Current, string.Format("STALL: one frame took {0:0} ms", frameMs), false);
                }
            }

            ResetFrame();
        }

        void ResetFrame()
        {
            Frame.Current.Clear();
            for (int g = 0; g < 3; g++) gcAtFrameStart[g] = GC.CollectionCount(g);
            Probes.ResetTickCounters();
        }

        void WriteReport(FrameStats s, string title, bool isWindow)
        {
            Reporting = true;
            try
            {
                string text = ReportBuilder.Build(s, title, isWindow);
                foreach (var line in text.Split('\n')) Log.LogWarning(line.TrimEnd('\r'));
                if (reportsWritten < MaxReportFiles.Value)
                {
                    Directory.CreateDirectory(reportDir);
                    string file = Path.Combine(reportDir, string.Format("LoopLagLimiter_{0:yyyyMMdd_HHmmss}_f{1}.txt", DateTime.Now, Time.frameCount));
                    File.WriteAllText(file, text);
                    reportsWritten++;
                    Log.LogWarning("Report saved: " + file);
                }
            }
            catch (Exception e) { Log.LogError("Failed to write report: " + e); }
            finally { Reporting = false; }
            lastFrameTs = Stopwatch.GetTimestamp(); // don't count report-writing time as the next frame's stall
        }
    }
}
