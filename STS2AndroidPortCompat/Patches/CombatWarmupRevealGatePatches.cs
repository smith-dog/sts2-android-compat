using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

// The combat animation warmup already runs inside the game's own room transition window
// (RoomFadeOut has covered the screen, RoomFadeIn reveals it), but it is fire-and-forget, so the
// reveal used to start mid-warmup and expose the duplicated Spine previews - that is why the
// warmup drew its own full-screen black cover. Holding the reveal instead lets the game's own
// transition cover the wait, and the warmup no longer needs a black mask of its own.
public static class CombatWarmupRevealGatePatches
{
    private const int MaxHoldMilliseconds = 12000;

    // Skip the covered collect when the managed heap has nothing worth reclaiming.
    private const long MinHeapBytesForCoverCollect = 32L * 1024 * 1024;

    private static bool _revealing;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NTransition), "RoomFadeIn",
            prefix: PatchHelper.Method(typeof(CombatWarmupRevealGatePatches), nameof(RoomFadeInPrefix)));
        PatchHelper.Patch(harmony, typeof(NTransition), "FadeIn",
            prefix: PatchHelper.Method(typeof(CombatWarmupRevealGatePatches), nameof(FadeInPrefix)));
    }

    // True from the moment the transition has finished covering the screen until it starts revealing.
    // A fade already in progress does not count: its cover is going transparent, so the warmup has to
    // fall back to its own mask.
    public static bool IsTransitionCovering()
    {
        try
        {
            NTransition transition = NGame.Instance?.Transition;
            if (transition == null || !transition.Visible || !transition.InTransition)
                return false;
            float alpha = CoverAlpha(transition);
            return alpha < 0f || alpha >= 0.99f;
        }
        catch
        {
            return false;
        }
    }

    internal static string DescribeCoverState()
    {
        try
        {
            NTransition transition = NGame.Instance?.Transition;
            if (transition == null)
                return "transition=<null>";
            return $"visible={transition.Visible} inTransition={transition.InTransition} coverAlpha={CoverAlpha(transition):F3}";
        }
        catch (Exception exception)
        {
            return $"cover_state_unavailable={exception.GetType().Name}";
        }
    }

    // Alpha of the transition's opaque cover child; -1 when it cannot be read.
    internal static float CoverAlpha(NTransition transition)
    {
        try
        {
            if (transition != null && transition.GetNodeOrNull("SimpleTransition") is Control cover)
                return cover.Modulate.A;
        }
        catch
        {
        }
        return -1f;
    }

    public static bool RoomFadeInPrefix(NTransition __instance, ref Task __result, bool showTransition)
    {
        DiagRevealRequest("RoomFadeIn", __instance);
        return TryHoldUntilWarmupDone(__instance, ref __result, () => __instance.RoomFadeIn(showTransition));
    }

    public static bool FadeInPrefix(NTransition __instance, ref Task __result, float time, string transitionPath, CancellationToken? cancelToken)
    {
        DiagRevealRequest("FadeIn", __instance);
        return TryHoldUntilWarmupDone(__instance, ref __result, () => __instance.FadeIn(time, transitionPath, cancelToken));
    }

    private static void DiagRevealRequest(string which, NTransition transition)
    {
        if (!IsDebugEnabled())
            return;
        try
        {
            Task pending = CombatAnimationWarmupPatches.CurrentRoomWarmup;
            PatchHelper.Log($"[GateDiag] t={Time.GetTicksMsec()} reveal={which} visible={transition?.Visible.ToString() ?? "<null>"} " +
                $"inTransition={transition?.InTransition.ToString() ?? "<null>"} coverAlpha={CoverAlpha(transition):F3} " +
                $"pending={(pending == null ? "none" : pending.IsCompleted ? "done" : "running")} revealing={_revealing}.");
        }
        catch
        {
        }
    }

    private static bool TryHoldUntilWarmupDone(NTransition transition, ref Task __result, Func<Task> reveal)
    {
        try
        {
            if (_revealing || transition == null || !transition.Visible)
                return true;
            Task pending = CombatAnimationWarmupPatches.CurrentRoomWarmup;
            if (pending == null || pending.IsCompleted)
                return true;
            __result = HoldThenRevealAsync(transition, pending, reveal);
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Combat warmup reveal gate scheduling failed: {exception.Message}");
            return true;
        }
    }

    private static async Task HoldThenRevealAsync(NTransition transition, Task pending, Func<Task> reveal)
    {
        ulong started = Time.GetTicksMsec();
        SceneTree tree = Engine.GetMainLoop() as SceneTree;
        try
        {
            if (tree == null)
                return;
            // Poll on engine frames only: a Task.Delay continuation would leave the main thread, and
            // everything below has to stay on it.
            while (!pending.IsCompleted && Time.GetTicksMsec() - started < MaxHoldMilliseconds)
            {
                if (!IsValid(transition))
                    return;
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            if (pending.IsCompleted)
                CollectInsideCover();
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Combat warmup reveal gate hold failed: {exception.Message}");
        }
        finally
        {
            bool stalled = !pending.IsCompleted;
            ulong held = Time.GetTicksMsec() - started;
            _revealing = true;
            try
            {
                await reveal();
            }
            catch (Exception exception)
            {
                PatchHelper.Log($"Combat warmup reveal gate reveal failed: {exception.Message}");
            }
            finally
            {
                _revealing = false;
            }
            PatchHelper.Log($"[GateDiag] t={Time.GetTicksMsec()} reveal gate held={held:N0}ms released_before_done={stalled}.");
        }
    }

    // A forced gen2 collection costs one frame or two, which is exactly what a covered screen can
    // absorb: doing it here moves that pause out of visible combat play (measured ~1 per 17 s there).
    private static void CollectInsideCover()
    {
        try
        {
            long before = System.GC.GetTotalMemory(false);
            if (before < MinHeapBytesForCoverCollect)
                return;
            int gen0Before = System.GC.CollectionCount(0);
            int gen2Before = System.GC.CollectionCount(2);
            ulong started = Time.GetTicksMsec();
            System.GC.Collect(2, GCCollectionMode.Forced, true);
            ulong ms = Time.GetTicksMsec() - started;
            long reclaimed = before - System.GC.GetTotalMemory(false);
            PatchHelper.Log($"[GateDiag] cover-collect ms={ms:N0} reclaimed={reclaimed / (1024.0 * 1024.0):F1}MB " +
                $"gen0={gen0Before}->{System.GC.CollectionCount(0)} gen2={gen2Before}->{System.GC.CollectionCount(2)}.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Combat warmup cover collect failed: {exception.Message}");
        }
    }

    private static bool IsValid(GodotObject value)
    {
        return value != null && GodotObject.IsInstanceValid(value);
    }

    private static bool IsDebugEnabled()
    {
        return AndroidSettingsBridge.GetBool("preload_enabled", true)
            && AndroidSettingsBridge.GetBool("preload_debug_enabled", false);
    }
}
