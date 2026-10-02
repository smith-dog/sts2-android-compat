using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;

namespace STS2Mobile.Patches;

// The v0.107.1 payload is missing vine_shambler_normal_node_skel_data, so that
// monster's SpineSprite carries a skeleton whose native data resource never loads.
// MegaSkeleton.GetData() builds its result through MegaSpineBinding's constructor,
// which throws InvalidOperationException on the resulting nil variant. That throw
// defeats the game's own degradation in NCreatureVisuals._Ready
// (`SpineBody.GetSkeleton()?.GetData() == null` never becomes true), leaves the
// creature half-initialized with HasSpineAnimation=true, and later dies in native
// spine calls (combat animation warmup, animator updates, skin setup).
// This prefix evaluates the original's single native call itself and turns a nil
// result into a null MegaSkeletonDataResource, restoring the intended null-data
// semantics without ever running the throwing constructor path.
// A Harmony finalizer was tried first and is not viable here: on Mono its DMD
// wrapper turned every GetData call (healthy skeletons included) into a
// RuntimeWrappedException, which silently killed the reward flow mid-combat.
public static class SpineSkeletonDataSafetyPatches
{
    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(
            harmony,
            typeof(MegaSkeleton),
            nameof(MegaSkeleton.GetData),
            prefix: PatchHelper.Method(typeof(SpineSkeletonDataSafetyPatches), nameof(GetDataPrefix)));
    }

    private static bool GetDataPrefix(MegaSkeleton __instance, ref MegaSkeletonDataResource __result)
    {
        try
        {
            GodotObject bound = __instance.BoundObject;
            if (bound == null)
            {
                __result = null;
                return false;
            }
            Variant native = bound.Call("get_data");
            if (native.VariantType != Variant.Type.Object || native.AsGodotObject() == null)
            {
                PatchHelper.Log("Spine skeleton data unavailable (nil); degrading to null so spine animation can be disabled.");
                __result = null;
                return false;
            }
            __result = new MegaSkeletonDataResource(native);
            return false;
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Spine skeleton data unavailable ({exception.GetType().Name}: {exception.Message}); degrading to null.");
            __result = null;
            return false;
        }
    }
}
