using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace TelemetryMod
{
    // Module A (per-function timing) + Module D (big-allocation attribution).
    //
    // Ports ToasterProfiler's proven per-target Harmony mechanism (a single
    // shared prefix/postfix pair, slot resolved from __originalMethod via a
    // dictionary, start-timestamp carried in a __state struct) but reshaped to
    // emit continuous per-window CSV rows instead of on-demand text dumps.
    //
    // Hard Mono lesson inherited from ToasterProfiler: per-call SMALL-allocation
    // attribution is unreliable on Mono (TLAB-refill noise), so Module A reports
    // TIME only; allocation attribution is delegated to Module D, which records
    // only single calls that allocate >= BigAllocBytes (KB-scale noise floor is
    // far below that threshold, so big events are trustworthy).
    internal static class FunctionProfiler
    {
        public static bool Enabled;

        // Carried prefix -> postfix per call. Struct => no per-call heap alloc.
        public struct CallState
        {
            public int  Idx;
            public long StartTicks;
            public long StartAlloc;
            public bool Tracked;
        }

        private sealed class MethodAccum
        {
            public string     Name;
            public string     Owner;     // "static" | mod assembly name | foreign harmony id(s)
            public string     Mode;      // "static" | "autoplugin" | "patched"
            public MethodBase Method;
            public bool       IsAuto;
            public long       TotalTicks;
            public long       CallCount;
            public long       MaxTicks;
            public long[]     Samples;   // per-window reservoir for percentiles
            public int        SampleIdx;
        }

        private struct BigAllocEvent
        {
            public int    Frame;
            public string Method;
            public string Owner;
            public long   Bytes;
            public long   Ticks;
        }

        private enum AllocMode { Unknown, PerThread, TotalMemory }

        private static readonly List<MethodAccum>          s_accums       = new List<MethodAccum>();
        private static readonly Dictionary<MethodBase,int> s_methodToIndex = new Dictionary<MethodBase, int>();
        private static Harmony s_harmony;
        private static AllocMode s_allocMode = AllocMode.Unknown;

        private static long   s_bigThreshold = 262144;
        private static readonly object         s_bigLock   = new object();
        private static readonly List<BigAllocEvent> s_bigBuffer = new List<BigAllocEvent>();

        private static int  s_cap = 400;
        private static bool s_doStatic, s_doAutoplugins, s_doPatched, s_doBigAlloc;
        private static readonly HashSet<string> s_excludeOwners =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Owners (harmony ids) that are part of OUR harness, never the
        // system-under-test. Anything else that patched a game method is the
        // SUT (or another mod) and IS worth attributing.
        private static readonly string[] s_harnessOwnerPrefixes = { "com.puckstresstest" };

        // NEVER instrument these — patching a method whose object is destroyed /
        // network-despawned mid-call NATIVE-crashes Mono. Learned the hard way:
        // the 4h C run crashed the server at the post-goal puck despawn/respawn
        // because the broad GetAllPatchedMethods discovery had wrapped Puck/Goal
        // lifecycle + collision methods. Belt-and-suspenders on top of the
        // patched-mode-off default. Match by declaring type OR method name.
        private static readonly HashSet<string> s_unsafeTypes =
            new HashSet<string>(StringComparer.Ordinal) { "Puck", "Goal", "PuckManager", "NetworkObject" };
        private static readonly string[] s_unsafeNamePrefixes =
            { "OnCollision", "OnTrigger", "OnNetworkSpawn", "OnNetworkDespawn", "OnNetworkPostSpawn" };
        private static readonly HashSet<string> s_unsafeNames =
            new HashSet<string>(StringComparer.Ordinal) { "OnDestroy", "Despawn", "Server_Despawn" };

        private static bool IsUnsafeToPatch(MethodBase m)
        {
            try
            {
                string tn = m.DeclaringType?.Name;
                if (tn != null && s_unsafeTypes.Contains(tn)) return true;
                string n = m.Name;
                if (s_unsafeNames.Contains(n)) return true;
                foreach (var p in s_unsafeNamePrefixes)
                    if (n.StartsWith(p, StringComparison.Ordinal)) return true;
            }
            catch { return true; } // if we can't tell, don't risk it
            return false;
        }

        public static int  StaticCount  { get; private set; }
        public static int  DynamicCount { get; private set; }
        public static string AllocModeString => s_allocMode.ToString();

        // ToasterProfiler's validated B897 target list (Type::Method, one per
        // line). These exact names resolve on our live server build.
        private const string DefaultTargets =
            "# TelemetryMod static profiling targets. Format: Type::Method  (one per line, # = comment).\n" +
            "# Seeded from ToasterProfiler's validated B897 list. Edit and re-launch to change.\n" +
            "\n" +
            "# --- SynchronizedObjectManager hot paths (200 Hz sync tick) ---\n" +
            "SynchronizedObjectManager::Server_ServerTick\n" +
            "SynchronizedObjectManager::Server_GatherSynchronizedObjectData\n" +
            "SynchronizedObjectManager::Client_SynchronizeObjects\n" +
            "SynchronizedObjectManager::EncodeSynchronizedObject\n" +
            "SynchronizedObjectManager::DecodeSynchronizedObjectData\n" +
            "\n" +
            "# --- Per-frame loops ---\n" +
            "GameManager::Server_Tick\n" +
            "ReplayRecorder::Server_Tick\n" +
            "# PhysicsManager::Update  <- DO NOT re-enable on the modded test rig.\n" +
            "#   It's the most mod-patched method in the corpus (14+ mods). Patching\n" +
            "#   it forces Harmony to recompile the shared wrapper, which trips a Mono\n" +
            "#   gsharedvt assertion inside oomtm450_ruleset's Postfix (GetZone ->\n" +
            "#   ReadOnlyDictionary<IceElement,ValueTuple<double,double>>) and SIGABRTs\n" +
            "#   the server during Play. Safe only when the SUT is the lone mod.\n" +
            "\n" +
            "# --- Event dispatch ---\n" +
            "EventManager::TriggerEvent\n" +
            "\n" +
            "# --- Manager getters (allocate lists) ---\n" +
            "PlayerManager::GetPlayers\n" +
            "PuckManager::GetPucks\n" +
            "PuckManager::GetPlayerPuck\n" +
            "\n" +
            "# --- Per-player hot paths (uncomment to enable; noisier/higher overhead) ---\n" +
            "# PlayerBody::FixedUpdate\n" +
            "# StickPositioner::FixedUpdate\n" +
            "# Puck::FixedUpdate\n" +
            "# Hover::FixedUpdate\n";

        public static void Init(bool doStatic, bool doAutoplugins, bool doPatched, bool doBigAlloc,
                                long bigThreshold, int cap, IEnumerable<string> excludes)
        {
            try
            {
                s_doStatic      = doStatic;
                s_doAutoplugins = doAutoplugins;
                s_doPatched     = doPatched;
                s_doBigAlloc    = doBigAlloc;
                s_bigThreshold = bigThreshold;
                s_cap = Math.Max(1, cap);
                if (excludes != null)
                    foreach (var e in excludes)
                        if (!string.IsNullOrEmpty(e)) s_excludeOwners.Add(e.Trim());

                DecideAllocMode();
                s_harmony = new Harmony("com.puckstresstest.telemetry.profiler");

                if (s_doStatic)
                {
                    var specs = LoadTargetsFile();
                    int n = ApplyTargets(specs, "static", "static", isAuto: false);
                    StaticCount = n;
                    Plugin.Log($"[profiler] static targets patched: {n}/{specs.Count}");
                }

                Enabled = true;
            }
            catch (Exception ex)
            {
                Plugin.LogError("FunctionProfiler.Init failed: " + ex);
                Enabled = false;
            }
        }

        // Called once, ~10s after enable, so the system-under-test has finished
        // applying ITS Harmony patches before we discover them.
        public static void RunDynamicDiscovery()
        {
            if (!Enabled || (!s_doAutoplugins && !s_doPatched)) return;
            try
            {
                int before = s_accums.Count;

                // (a) @autoplugins (default ON, safe): instrument each loaded mod's
                //     OWN lifecycle + Event_* methods. Catches cost in the mod's
                //     own code; these objects aren't despawned per-goal.
                if (s_doAutoplugins)
                    foreach (var kv in DiscoverPluginMethods())
                    {
                        if (s_accums.Count >= s_cap) break;
                        PatchOne(kv.Key, kv.Value.owner, "autoplugin", isAuto: true);
                    }

                // (b) Harmony.GetAllPatchedMethods (default OFF — CRASH RISK):
                //     re-wraps GAME methods other mods patched. This is what
                //     native-crashed the server at the post-goal puck respawn, so
                //     it's opt-in (TELEMETRY_PROFILE_PATCHED=1) and additionally
                //     filtered by IsUnsafeToPatch.
                if (s_doPatched)
                    foreach (var kv in DiscoverForeignPatchedMethods())
                    {
                        if (s_accums.Count >= s_cap) break;
                        PatchOne(kv.Key, kv.Value, "patched", isAuto: true);
                    }

                DynamicCount = s_accums.Count - before;
                Plugin.Log($"[profiler] dynamic discovery: +{DynamicCount} methods " +
                           $"(total {s_accums.Count}/{s_cap})");
            }
            catch (Exception ex)
            {
                Plugin.LogError("RunDynamicDiscovery failed: " + ex);
            }
        }

        // ---- Harmony shared prefix / postfix -------------------------------

        public static void Prefix(MethodBase __originalMethod, out CallState __state)
        {
            __state = default;
            if (!Enabled) return;
            if (s_methodToIndex.TryGetValue(__originalMethod, out var idx))
            {
                __state.Idx        = idx;
                __state.StartTicks = Stopwatch.GetTimestamp();
                __state.StartAlloc = s_doBigAlloc ? ReadAllocBytes() : 0;
                __state.Tracked    = true;
            }
        }

        public static void Postfix(CallState __state)
        {
            if (!__state.Tracked) return;
            try
            {
                long ticks = Stopwatch.GetTimestamp() - __state.StartTicks;
                var a = s_accums[__state.Idx];

                Interlocked.Add(ref a.TotalTicks, ticks);
                Interlocked.Increment(ref a.CallCount);
                long old;
                do { old = a.MaxTicks; if (ticks <= old) break; }
                while (Interlocked.CompareExchange(ref a.MaxTicks, ticks, old) != old);

                int si = Interlocked.Increment(ref a.SampleIdx) - 1;
                if (si >= 0) a.Samples[si % a.Samples.Length] = ticks;

                if (s_doBigAlloc)
                {
                    long bytes = ReadAllocBytes() - __state.StartAlloc;
                    if (bytes >= s_bigThreshold)
                    {
                        var ev = new BigAllocEvent
                        {
                            Frame  = Time.frameCount,
                            Method = a.Name,
                            Owner  = a.Owner,
                            Bytes  = bytes,
                            Ticks  = ticks,
                        };
                        lock (s_bigLock) { if (s_bigBuffer.Count < 4096) s_bigBuffer.Add(ev); }
                    }
                }
            }
            catch { /* telemetry must never throw into the server */ }
        }

        // ---- per-window flush ----------------------------------------------

        public static void FlushWindow(StreamWriter funcW, StreamWriter bigW, long tMs, int windowMs)
        {
            double usPerTick = 1_000_000.0 / Stopwatch.Frequency;

            if (funcW != null)
            {
                // Snapshot a stable copy of the list count; new methods only get
                // appended (never removed) so indexing is safe.
                int count = s_accums.Count;
                for (int i = 0; i < count; i++)
                {
                    var a = s_accums[i];
                    long calls = Interlocked.Exchange(ref a.CallCount, 0);
                    if (calls == 0) { Interlocked.Exchange(ref a.SampleIdx, 0); continue; }
                    long total = Interlocked.Exchange(ref a.TotalTicks, 0);
                    long max   = Interlocked.Exchange(ref a.MaxTicks, 0);
                    int  n     = Math.Min(Interlocked.Exchange(ref a.SampleIdx, 0), a.Samples.Length);

                    double p95 = 0, p99 = 0;
                    if (n > 0)
                    {
                        var buf = new long[n];
                        Array.Copy(a.Samples, buf, n);
                        Array.Sort(buf);
                        p95 = buf[Math.Min(n - 1, (int)(n * 0.95))] * usPerTick;
                        p99 = buf[Math.Min(n - 1, (int)(n * 0.99))] * usPerTick;
                    }
                    double totalUs = total * usPerTick;
                    double meanUs  = totalUs / calls;
                    funcW.WriteLine($"{tMs},{windowMs},{Csv(a.Name)},{Csv(a.Owner)},{a.Mode}," +
                                    $"{calls},{totalUs:F1},{meanUs:F2},{p95:F2},{p99:F2}," +
                                    $"{max * usPerTick:F2}");
                }
                funcW.Flush();
            }

            if (bigW != null && s_doBigAlloc)
            {
                List<BigAllocEvent> drained = null;
                lock (s_bigLock)
                {
                    if (s_bigBuffer.Count > 0)
                    {
                        drained = new List<BigAllocEvent>(s_bigBuffer);
                        s_bigBuffer.Clear();
                    }
                }
                if (drained != null)
                {
                    foreach (var e in drained)
                        bigW.WriteLine($"{tMs},{e.Frame},{Csv(e.Method)},{Csv(e.Owner)}," +
                                       $"{e.Bytes},{e.Ticks * usPerTick:F2}");
                    bigW.Flush();
                }
            }
        }

        public static void Stop()
        {
            Enabled = false;
            try { s_harmony?.UnpatchSelf(); } catch (Exception ex) { Plugin.LogError("UnpatchSelf: " + ex.Message); }
        }

        // ---- target resolution / patching ----------------------------------

        private static int ApplyTargets(List<string> specs, string owner, string mode, bool isAuto)
        {
            int n = 0;
            var skipped = new List<string>();
            foreach (var spec in specs)
            {
                var m = ResolveMethod(spec);
                if (m == null) { skipped.Add(spec); continue; }
                if (PatchOne(m, owner, mode, isAuto, spec)) n++;
            }
            if (skipped.Count > 0)
                Plugin.Log($"[profiler] {skipped.Count} target(s) unresolved: {string.Join(", ", skipped)}");
            return n;
        }

        private static bool PatchOne(MethodBase method, string owner, string mode, bool isAuto, string displayName = null)
        {
            if (method == null || s_methodToIndex.ContainsKey(method)) return false;
            if (s_accums.Count >= s_cap) return false;
            if (IsUnsafeToPatch(method))
            {
                if (!isAuto)
                    Plugin.Log($"[profiler] refusing unsafe-to-patch target " +
                               $"{method.DeclaringType?.Name}::{method.Name} (despawn/collision lifecycle)");
                return false;
            }

            int slot = s_accums.Count;
            var accum = new MethodAccum
            {
                Name    = displayName ?? (method.DeclaringType?.Name + "::" + method.Name),
                Owner   = owner,
                Mode    = mode,
                Method  = method,
                IsAuto  = isAuto,
                Samples = new long[isAuto ? 512 : 4096],
            };
            s_accums.Add(accum);
            s_methodToIndex[method] = slot;
            try
            {
                var pre  = new HarmonyMethod(typeof(FunctionProfiler).GetMethod(nameof(Prefix),
                               BindingFlags.Public | BindingFlags.Static)) { priority = Priority.First };
                var post = new HarmonyMethod(typeof(FunctionProfiler).GetMethod(nameof(Postfix),
                               BindingFlags.Public | BindingFlags.Static)) { priority = Priority.Last };
                s_harmony.Patch(method, pre, post);
                return true;
            }
            catch (Exception ex)
            {
                if (!isAuto) Plugin.Log($"[profiler] patch failed for {accum.Name}: {ex.Message}");
                s_accums.RemoveAt(slot);
                s_methodToIndex.Remove(method);
                return false;
            }
        }

        private static MethodBase ResolveMethod(string spec)
        {
            try
            {
                int sep = spec.IndexOf("::", StringComparison.Ordinal);
                if (sep < 0) return null;
                string typeName = spec.Substring(0, sep).Trim();
                string methName = spec.Substring(sep + 2).Trim();
                Type t = AccessTools.TypeByName(typeName) ?? FindTypeAcrossAssemblies(typeName);
                if (t == null) return null;
                MethodBase m = AccessTools.Method(t, methName, null, null)
                            ?? AccessTools.PropertyGetter(t, methName)
                            ?? AccessTools.PropertySetter(t, methName);
                if (m == null) return null;
                if (m is MethodInfo mi && mi.ContainsGenericParameters) return null; // crashes Mono
                return m;
            }
            catch { return null; }
        }

        private static Type FindTypeAcrossAssemblies(string typeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(typeName, false, false); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        // ---- dynamic discovery ---------------------------------------------

        // (a) Each loaded IPuckPlugin assembly's own Update/LateUpdate/FixedUpdate
        //     + Event_* handler methods.
        private static List<KeyValuePair<MethodBase, (string owner, string disp)>> DiscoverPluginMethods()
        {
            var result = new List<KeyValuePair<MethodBase, (string, string)>>();
            var self = typeof(FunctionProfiler).Assembly;
            string[] lifecycle = { "Update", "LateUpdate", "FixedUpdate" };

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm == self || asm.IsDynamic || !IsPluginAssembly(asm)) continue;
                    string modName = asm.GetName().Name;
                    if (s_excludeOwners.Contains(modName)) continue;

                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                    if (types == null) continue;

                    foreach (var type in types)
                    {
                        if (type == null || type.ContainsGenericParameters) continue;
                        try
                        {
                            if (typeof(MonoBehaviour).IsAssignableFrom(type))
                            {
                                foreach (var ln in lifecycle)
                                {
                                    var m = type.GetMethod(ln, BindingFlags.DeclaredOnly | BindingFlags.Instance |
                                            BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                                    if (IsPatchable(m))
                                        result.Add(new KeyValuePair<MethodBase, (string, string)>(
                                            m, (modName, modName + "!" + type.Name + "::" + ln)));
                                }
                            }
                            foreach (var m in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance |
                                     BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                if (m.Name.StartsWith("Event_", StringComparison.Ordinal) && IsPatchable(m))
                                    result.Add(new KeyValuePair<MethodBase, (string, string)>(
                                        m, (modName, modName + "!" + type.Name + "::" + m.Name)));
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return result;
        }

        // (b) Game methods patched by a foreign (non-harness) Harmony owner.
        private static List<KeyValuePair<MethodBase, string>> DiscoverForeignPatchedMethods()
        {
            var result = new List<KeyValuePair<MethodBase, string>>();
            try
            {
                foreach (var m in Harmony.GetAllPatchedMethods())
                {
                    try
                    {
                        if (m == null || s_methodToIndex.ContainsKey(m)) continue;
                        if (m is MethodInfo mi && mi.ContainsGenericParameters) continue;
                        var info = Harmony.GetPatchInfo(m);
                        if (info?.Owners == null) continue;
                        var foreign = new List<string>();
                        foreach (var o in info.Owners)
                            if (!IsHarnessOwner(o)) foreign.Add(o);
                        if (foreign.Count == 0) continue;
                        result.Add(new KeyValuePair<MethodBase, string>(
                            m, m.DeclaringType?.Name + "::" + m.Name + " <" + string.Join("+", foreign) + ">"));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Plugin.LogError("GetAllPatchedMethods: " + ex.Message); }
            return result;
        }

        private static bool IsHarnessOwner(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return true;
            if (s_excludeOwners.Contains(owner)) return true;
            foreach (var p in s_harnessOwnerPrefixes)
                if (owner.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool IsPluginAssembly(Assembly asm)
        {
            try
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                if (types == null) return false;
                foreach (var t in types)
                    if (t != null && !t.IsAbstract && !t.IsInterface && typeof(IPuckPlugin).IsAssignableFrom(t))
                        return true;
            }
            catch { }
            return false;
        }

        private static bool IsPatchable(MethodInfo m)
        {
            if (m == null || m.IsAbstract || m.ContainsGenericParameters) return false;
            try { if (m.GetMethodBody() == null) return false; }
            catch { return false; }
            return true;
        }

        // ---- allocation reading --------------------------------------------

        private static void DecideAllocMode()
        {
            if (s_allocMode != AllocMode.Unknown) return;
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var probe = new byte[4][];
                for (int i = 0; i < probe.Length; i++) probe[i] = new byte[262144];
                long after = GC.GetAllocatedBytesForCurrentThread();
                GC.KeepAlive(probe);
                s_allocMode = after > before ? AllocMode.PerThread : AllocMode.TotalMemory;
            }
            catch { s_allocMode = AllocMode.TotalMemory; }
            Plugin.Log($"[profiler] alloc mode: {s_allocMode}");
        }

        private static long ReadAllocBytes()
        {
            switch (s_allocMode)
            {
                case AllocMode.PerThread:   return GC.GetAllocatedBytesForCurrentThread();
                case AllocMode.TotalMemory: return GC.GetTotalMemory(false);
                default:                    return 0;
            }
        }

        // ---- targets.txt ----------------------------------------------------

        private static List<string> LoadTargetsFile()
        {
            var list = new List<string>();
            string path = TargetsPath();
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, DefaultTargets);
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length != 0 && line[0] != '#') list.Add(line);
                }
            }
            catch (Exception ex) { Plugin.LogError("targets.txt load failed: " + ex.Message); }
            return list;
        }

        private static string TargetsPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "targets.txt");
            }
            catch { }
            return Path.Combine(Path.GetFullPath("."), "targets.txt");
        }

        private static string Csv(string s) => s == null ? "" : s.Replace(',', ';').Replace('\n', ' ');
    }
}
