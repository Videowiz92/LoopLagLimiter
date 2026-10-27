using System;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace LoopLagLimiter
{
    // Optional Risk of Options page. Everything is done through reflection so the mod has no hard
    // dependency on Risk of Options and still loads without it. The (option, modGuid, modName)
    // overloads are used because the parameterless ones identify the mod from the *calling assembly*,
    // which would be wrong when called through reflection.
    internal static class RiskOfOptionsCompat
    {
        const string ROO = "com.rune580.riskofoptions";

        public static void TryRegister()
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(ROO)) return;

            var msm = AccessTools.TypeByName("RiskOfOptions.ModSettingsManager");
            if (msm == null) { Plugin.Log.LogWarning("Risk of Options found but ModSettingsManager is missing; skipping settings page."); return; }
            var baseOption = AccessTools.TypeByName("RiskOfOptions.Options.BaseOption");
            var addOption = msm.GetMethod("AddOption", new[] { baseOption, typeof(string), typeof(string) });
            if (addOption == null) { Plugin.Log.LogWarning("Risk of Options API mismatch (AddOption); skipping settings page."); return; }

            int added = 0;
            foreach (var o in Plugin.Options)
            {
                try
                {
                    object option = Create(o);
                    if (option == null) continue;
                    addOption.Invoke(null, new[] { option, Plugin.GUID, Plugin.NAME });
                    added++;
                }
                catch (Exception e) { Plugin.Log.LogDebug("Risk of Options: skipped " + o.Entry.Definition.Key + ": " + e.Message); }
            }

            try
            {
                var setDesc = msm.GetMethod("SetModDescription", new[] { typeof(string), typeof(string), typeof(string) });
                if (setDesc != null) setDesc.Invoke(null, new object[] {
                    "Performance fixes for long, heavily looped runs: caps runaway Meat Hook pulls, dead orbs, post-teleporter " +
                    "waves and the physics catch-up spiral, plus optional proc limits and a lag diagnostics tool.", Plugin.GUID, Plugin.NAME });

                var sprite = LoadIcon();
                var setIcon = msm.GetMethod("SetModIcon", new[] { typeof(Sprite), typeof(string), typeof(string) });
                if (sprite != null && setIcon != null) setIcon.Invoke(null, new object[] { sprite, Plugin.GUID, Plugin.NAME });
            }
            catch (Exception e) { Plugin.Log.LogDebug("Risk of Options icon/description skipped: " + e.Message); }

            Plugin.Log.LogInfo("Risk of Options page registered (" + added + " settings).");
        }

        static object Create(Plugin.OptionInfo o)
        {
            Type t = o.Entry.SettingType;
            string optionType, configType;
            if (t == typeof(bool)) { optionType = "CheckBoxOption"; configType = "CheckBoxConfig"; }
            else if (t == typeof(int)) { optionType = "IntSliderOption"; configType = "IntSliderConfig"; }
            else if (t == typeof(float)) { optionType = "StepSliderOption"; configType = "StepSliderConfig"; }
            else if (t == typeof(string)) { optionType = "StringInputFieldOption"; configType = "InputFieldConfig"; }
            else if (t == typeof(KeyboardShortcut)) { optionType = "KeyBindOption"; configType = "KeyBindConfig"; }
            else return null;

            var ot = AccessTools.TypeByName("RiskOfOptions.Options." + optionType);
            var ct = AccessTools.TypeByName("RiskOfOptions.OptionConfigs." + configType);
            if (ot == null) return null;
            if (ct == null) return Activator.CreateInstance(ot, o.Entry);

            object cfg = Activator.CreateInstance(ct);
            Set(cfg, "restartRequired", o.Restart);
            if (t == typeof(int) && o.Max > o.Min)
            {
                Set(cfg, "min", (int)o.Min);
                Set(cfg, "max", (int)o.Max);
            }
            else if (t == typeof(float) && o.Max > o.Min)
            {
                Set(cfg, "min", o.Min);
                Set(cfg, "max", o.Max);
                Set(cfg, "increment", o.Step);
                var fp = AccessTools.Property(ct, "FormatString");
                if (fp != null && fp.CanWrite) fp.SetValue(cfg, "{0:0.00}", null);
            }
            return Activator.CreateInstance(ot, o.Entry, cfg);
        }

        static void Set(object target, string field, object value)
        {
            var f = AccessTools.Field(target.GetType(), field);
            if (f != null) f.SetValue(target, value);
        }

        static Sprite LoadIcon()
        {
            var asm = Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("LoopLagLimiter.icon.png"))
            {
                if (s == null) return null;
                var ms = new MemoryStream();
                s.CopyTo(ms);
                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, ms.ToArray())) return null;
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
        }
    }
}
