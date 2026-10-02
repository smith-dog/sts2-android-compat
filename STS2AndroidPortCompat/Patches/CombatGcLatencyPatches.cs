using System;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

// Combat-window frame spikes correlate 1:1 with blocking gen2 collections
// (session data: 13/13 windows containing a gen2 had a >=100 ms frame, every
// 200-800 ms spike carried dGC2>=1, while gen0-only windows stayed under
// 200 ms). GCSettings.LatencyMode is not implemented by the bundled runtime
// (PlatformNotSupportedException), so instead keep the gen2 debt paid
// continuously while a combat room is on screen: every interval, request a
// NON-BLOCKING BACKGROUND gen2 (GC.Collect(2, Optimized, false, true)).
// Background collections run on their own thread and do not stop the world,
// which converts the mid-fight blocking pause into small concurrent slices.
// GCCollectionMode.Optimized lets the collector skip the request when it
// would not be productive. If the runtime rejects background collection the
// loop disables itself after logging once.
public static class CombatGcLatencyPatches
{
    private const int SweepIntervalMs = 12000;

    private static bool _disabled;
    private static volatile bool _combatActive;
    private static bool _loggedStart;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(NCombatRoom),
            "_Ready",
            postfix: PatchHelper.Method(typeof(CombatGcLatencyPatches), nameof(CombatRoomReadyPostfix)));
    }

    private static bool Enabled() =>
        AndroidSettingsBridge.GetBool("preload_enabled", true)
        // Default OFF (2026-10-02 session data): the 12 s forced collect fired a
        // gen2 in 41/42 combat windows and 25/26 spiky frames coincided with it —
        // more frequent than the natural cadence it was meant to smooth. The
        // 64 MB nursery (MONO_GC_PARAMS) already removed combat gen0 churn.
        && AndroidSettingsBridge.GetBool("preload_combat_gc_low_latency", false);

    private static void CombatRoomReadyPostfix(NCombatRoom __instance)
    {
        if (_disabled || !Enabled())
            return;
        __instance.TreeExited += () => _combatActive = false;
        if (_combatActive)
            return;
        _combatActive = true;
        _ = BackgroundSweepLoop();
    }

    private static async Task BackgroundSweepLoop()
    {
        int sweeps = 0;
        try
        {
            while (_combatActive && !_disabled)
            {
                await Task.Delay(SweepIntervalMs).ConfigureAwait(false);
                if (!_combatActive || _disabled)
                    break;
                int before = System.GC.CollectionCount(2);
                // The bundled BCL surface lacks the 4-argument overload with the
                // background flag; the non-blocking hint is the remaining lever.
                System.GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                sweeps++;
                if (!_loggedStart)
                {
                    _loggedStart = true;
                    PatchHelper.Log($"[PreloadDiag] combat background GC sweep interval={SweepIntervalMs}ms first gen2 {before}->{System.GC.CollectionCount(2)}.");
                }
                else if (AndroidSettingsBridge.GetBool("preload_debug_enabled", false))
                {
                    PatchHelper.Log($"[PreloadDiag] combat background GC sweep #{sweeps} gen2 {before}->{System.GC.CollectionCount(2)}.");
                }
            }
        }
        catch (PlatformNotSupportedException exception)
        {
            _disabled = true;
            PatchHelper.Log($"[PreloadDiag] combat background GC sweep unavailable: {exception.Message}");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"[PreloadDiag] combat background GC sweep failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            _combatActive = false;
        }
    }
}
