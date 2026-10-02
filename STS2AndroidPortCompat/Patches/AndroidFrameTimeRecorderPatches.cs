using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

// Measures what a frame actually costs on device and prints the game's own memory snapshot on the
// same cadence, so a hitch can be read next to VRAM/static-memory/GC generation movement.
// Measurement only: no behaviour is changed and nothing is freed or preloaded differently.
public static class AndroidFrameTimeRecorderPatches
{
    private const int WindowFrames = 600;

    // Short first window so the log proves within a second that the callback really is running.
    private const int FirstWindowFrames = 60;
    private static bool _firstWindow = true;
    private const float SlowMilliseconds = 33f;
    private const float StallMilliseconds = 50f;

    // The spikes are what the player actually feels, so each big one gets a memory snapshot (which
    // carries Nodes/Orphans/CachedAssets/MissedCache/GC generations) - throttled so a storm of spikes
    // cannot turn into a log flood the way the arity bug did.
    private const float SnapshotMilliseconds = 150f;
    private const ulong SnapshotThrottleMs = 5000UL;

    private static ulong _lastSnapshotMs;

    // Bucket edges in milliseconds: <=8 <=12 <=16 <=20 <=25 <=33 <=50 <=100 and >100.
    private static readonly float[] BucketEdges = { 8f, 12f, 16f, 20f, 25f, 33f, 50f, 100f };

    private static readonly int[] Buckets = new int[BucketEdges.Length + 1];

    private static Callable _frameCallable;
    private static bool _started;
    private static bool _broken;

    private static int _frames;
    private static int _windowFrames;
    private static double _windowSumMs;
    private static double _windowMaxMs;
    private static int _windowSlow;
    private static int _windowStall;
    private static double _worstMs;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready",
            postfix: PatchHelper.Method(typeof(AndroidFrameTimeRecorderPatches), nameof(GameReadyPostfix)));
    }

    public static void GameReadyPostfix(NGame __instance)
    {
        if (_started || _broken || __instance == null)
            return;
        if (!IsOsAndroid() || !IsDebugEnabled())
            return;
        try
        {
            SceneTree tree = __instance.GetTree();
            if (tree == null)
                return;
            // SceneTree.process_frame carries no arguments (physics_frame does); connecting a
            // Callable.From<float> to it throws "Invalid argument count for invoked method" once per
            // frame, i.e. a backtrace per frame in the log. Measure the interval ourselves.
            _frameCallable = Callable.From(SampleFrame);
            tree.Connect(SceneTree.SignalName.ProcessFrame, _frameCallable);
            _started = true;
            MemoryProfiler.SetBaseline();
            PatchHelper.Log($"[FrameDiag] recorder started: first window={FirstWindowFrames} frames then {WindowFrames} frames, slow>{SlowMilliseconds}ms, stall>{StallMilliseconds}ms.");
        }
        catch (Exception exception)
        {
            _broken = true;
            PatchHelper.Log($"[FrameDiag] recorder failed to start: {exception.Message}");
        }
    }

    private static ulong _lastUsec;

    private static void SampleFrame()
    {
        if (_broken)
            return;
        try
        {
            ulong now = Time.GetTicksUsec();
            ulong previous = _lastUsec;
            _lastUsec = now;
            if (previous == 0UL)
                return;

            double ms = (now - previous) / 1000.0;
            // Tab switches and load screens can park the loop for seconds; those are not frame times.
            if (ms <= 0.0 || ms > 1000.0)
                return;

            _frames++;
            _windowFrames++;
            _windowSumMs += ms;
            if (ms > _windowMaxMs)
                _windowMaxMs = ms;
            if (ms > _worstMs)
                _worstMs = ms;
            int bucket = 0;
            while (bucket < BucketEdges.Length && ms > BucketEdges[bucket])
                bucket++;
            Buckets[bucket]++;
            if (ms > SlowMilliseconds)
                _windowSlow++;
            if (ms > StallMilliseconds)
            {
                _windowStall++;
                ulong nowMs = Time.GetTicksMsec();
                // Spike attribution: high frame_setup = CPU scene prep (Spine/nodes);
                // low setup with a long frame = GPU submit/fill wait. Draw call and
                // primitive counts distinguish geometry-heavy from fill-heavy frames.
                long setupUsec = 0;
                int drawCalls = -1;
                long primitives = -1;
                try
                {
                    setupUsec = (long)RenderingServer.Singleton.Get("get_frame_setup_time_cpu");
                    drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame).ToString().Length > 0
                        ? (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)
                        : -1;
                    primitives = (long)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
                }
                catch
                {
                    // Metric unavailability must not break the recorder; keep raw timing only.
                }
                PatchHelper.Log($"[FrameDiag] stall t={nowMs} ms={ms:F1} frame={_frames:N0} phase={CurrentPhase()} setup={setupUsec / 1000.0:F1}ms draws={drawCalls:N0} prims={primitives:N0} gc0={System.GC.CollectionCount(0)} gc1={System.GC.CollectionCount(1)} gc2={System.GC.CollectionCount(2)}.");
                if (ms > SnapshotMilliseconds && (_lastSnapshotMs == 0UL || nowMs - _lastSnapshotMs >= SnapshotThrottleMs))
                {
                    _lastSnapshotMs = nowMs;
                    MemoryProfiler.LogSnapshot($"slow-frame:{ms:F0}ms");
                }
            }
            int window = _firstWindow ? FirstWindowFrames : WindowFrames;
            if (_windowFrames >= window)
            {
                _firstWindow = false;
                FlushWindow();
            }
        }
        catch (Exception exception)
        {
            // One bad sample must not spam a log line every frame for the rest of the session.
            _broken = true;
            PatchHelper.Log($"[FrameDiag] recorder disabled: {exception.Message}");
        }
    }

    private static void FlushWindow()
    {
        double average = _windowSumMs / Math.Max(1, _windowFrames);
        PatchHelper.Log($"[FrameDiag] window t={Time.GetTicksMsec()} frames={_windowFrames:N0} avg={average:F2}ms max={_windowMaxMs:F1}ms " +
            $">slow={_windowSlow:N0} >stall={_windowStall:N0} session_worst={_worstMs:F1}ms phase={CurrentPhase()} " +
            $"gc0={System.GC.CollectionCount(0)} gc1={System.GC.CollectionCount(1)} gc2={System.GC.CollectionCount(2)} " +
            $"setup={RenderingServer.Singleton.Get("get_frame_setup_time_cpu")}us " +
            $"draws={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):N0} prims={Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame):N0} " +
            $"buckets[<=8]={Buckets[0]:N0}[<=12]={Buckets[1]:N0}[<=16]={Buckets[2]:N0}[<=20]={Buckets[3]:N0}" +
            $"[<=25]={Buckets[4]:N0}[<=33]={Buckets[5]:N0}[<=50]={Buckets[6]:N0}[<=100]={Buckets[7]:N0}[>100]={Buckets[8]:N0}.");
        // The game's profiler prints StaticMem/VRAM/objects/nodes/orphans/cached assets/GC generations
        // against the startup baseline and against the previous snapshot.
        MemoryProfiler.LogSnapshot($"android-frame-window:{_windowFrames}f");
        _windowFrames = 0;
        _windowSumMs = 0.0;
        _windowMaxMs = 0.0;
        _windowSlow = 0;
        _windowStall = 0;
        Array.Clear(Buckets, 0, Buckets.Length);
    }

    // One read per window (never per frame): which room the run is sitting in, so a slow window can be
    // attributed to combat vs map vs event instead of averaged across all of them. RunManager.State is
    // private, and this is a measurement unit, so reflection with cached PropertyInfo is the cheap route.
    private static System.Reflection.PropertyInfo _stateProperty;
    private static System.Reflection.PropertyInfo _currentRoomProperty;

    private static string CurrentPhase()
    {
        try
        {
            RunManager manager = RunManager.Instance;
            if (manager == null)
                return "no-run-manager";
            _stateProperty ??= AccessTools.Property(typeof(RunManager), "State");
            object state = _stateProperty == null ? null : _stateProperty.GetValue(manager);
            if (state == null)
                return "no-run";
            _currentRoomProperty ??= AccessTools.Property(state.GetType(), "CurrentRoom");
            object room = _currentRoomProperty == null ? null : _currentRoomProperty.GetValue(state);
            return room == null ? "no-room" : room.GetType().Name;
        }
        catch (Exception exception)
        {
            return "phase_unavailable=" + exception.GetType().Name;
        }
    }

    private static bool IsOsAndroid()
    {
        try
        {
            return OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDebugEnabled()
    {
        return AndroidSettingsBridge.GetBool("preload_enabled", true)
            && AndroidSettingsBridge.GetBool("preload_debug_enabled", false);
    }
}
