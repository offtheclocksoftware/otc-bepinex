// Minimal BepInEx 5 + HarmonyX stand-ins for the offline tests. Signatures match the members the library calls
// (the real ones are checked whenever the library is compiled into a plugin against a game's references).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BepInEx.Logging
{
    public enum LogLevel { Fatal, Error, Warning, Message, Info, Debug }

    public class ManualLogSource
    {
        public readonly string SourceName;
        public readonly List<(LogLevel Level, string Text)> Lines = new List<(LogLevel, string)>();
        public ManualLogSource(string name) { SourceName = name; }

        void Add(LogLevel l, object data) => Lines.Add((l, data?.ToString() ?? ""));
        public void LogFatal(object data) => Add(LogLevel.Fatal, data);
        public void LogError(object data) => Add(LogLevel.Error, data);
        public void LogWarning(object data) => Add(LogLevel.Warning, data);
        public void LogMessage(object data) => Add(LogLevel.Message, data);
        public void LogInfo(object data) => Add(LogLevel.Info, data);
        public void LogDebug(object data) => Add(LogLevel.Debug, data);

        /// <summary>Lines as BepInEx writes them to LogOutput.log ("[Info   :Source] text").</summary>
        public IEnumerable<string> DiskLines() => Lines.Select(l => $"[{l.Level,-7}:{SourceName}] {l.Text}");
        public int Count(LogLevel l) => Lines.Count(x => x.Level == l);
        public int Count(LogLevel l, string contains) => Lines.Count(x => x.Level == l && x.Text.Contains(contains));
        public void Clear() => Lines.Clear();
    }
}

namespace BepInEx.Configuration
{
    public sealed class ConfigDefinition
    {
        public ConfigDefinition(string section, string key) { Section = section; Key = key; }
        public string Section { get; }
        public string Key { get; }
    }

    public class ConfigEntry<T>
    {
        public ConfigEntry(string section, string key, T value) { Definition = new ConfigDefinition(section, key); Value = value; }
        public ConfigDefinition Definition { get; }
        public T Value { get; set; }
    }
}

namespace BepInEx
{
    public class BaseUnityPlugin : UnityEngine.MonoBehaviour
    {
        public BaseUnityPlugin() { gameObject = new UnityEngine.GameObject(); }
    }
}

namespace HarmonyLib
{
    /// <summary>Marks a patch class whose PatchAll must fail (simulates a renamed game method).</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class StubPatchFailsAttribute : Attribute { }

    public class HarmonyMethod
    {
        public MethodInfo method;
        public HarmonyMethod(MethodInfo m) { method = m; }
    }

    public class Harmony
    {
        public readonly string Id;
        public readonly List<string> Patched = new List<string>();
        public bool UnpatchedSelf;
        public Harmony(string id) { Id = id; }

        public void PatchAll(Type type)
        {
            if (type.GetCustomAttribute<StubPatchFailsAttribute>() != null)
                throw new MissingMethodException("Undefined target method for patch method " + type.Name + "::Postfix");
            Patched.Add(type.Name);
        }

        public MethodInfo Patch(MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            Patched.Add(original.DeclaringType.Name + "." + original.Name +
                        (prefix != null ? " pre=" + prefix.method.Name : "") + (postfix != null ? " post=" + postfix.method.Name : "") +
                        (finalizer != null ? " fin=" + finalizer.method.Name : ""));
            return original as MethodInfo;
        }

        public void UnpatchSelf() { UnpatchedSelf = true; Patched.Clear(); }
    }

    public static class AccessTools
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        public static Type TypeByName(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

        public static MethodInfo Method(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                var m = DeclaredMethod(t, name, parameters, generics);
                if (m != null) return m;
            }
            return null;
        }

        public static MethodInfo DeclaredMethod(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            if (type == null) return null;
            var found = type.GetMethods(All | BindingFlags.DeclaredOnly).Where(m => m.Name == name).ToList();
            if (parameters != null)
                found = found.Where(m => m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters)).ToList();
            if (found.Count > 1) throw new AmbiguousMatchException("Ambiguous match for " + type.Name + "." + name);
            return found.FirstOrDefault();
        }
    }
}
