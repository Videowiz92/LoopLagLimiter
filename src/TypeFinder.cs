using System;
using System.Collections.Generic;
using System.Reflection;

namespace LoopLagLimiter
{
    // Finds a type by full name WITHOUT enumerating every type of every assembly.
    // HarmonyX's AccessTools.TypeByName walks all types (GetTypes) of all loaded assemblies, including
    // large dynamic/hook assemblies. Doing that while the game is still loading on other threads hung the
    // boot, so lookups go through Assembly.GetType(name) instead, which is a direct, cheap lookup.
    internal static class TypeFinder
    {
        static readonly Dictionary<string, Type> cache = new Dictionary<string, Type>();

        public static Type Find(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            Type t;
            if (cache.TryGetValue(fullName, out t)) return t;
            t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try { t = asm.GetType(fullName, false); } catch { t = null; }
                if (t != null) break;
            }
            cache[fullName] = t;
            return t;
        }

        public static Type FindIn(Assembly asm, string fullName)
        {
            if (asm == null) return Find(fullName);
            try { return asm.GetType(fullName, false) ?? Find(fullName); } catch { return Find(fullName); }
        }
    }
}
