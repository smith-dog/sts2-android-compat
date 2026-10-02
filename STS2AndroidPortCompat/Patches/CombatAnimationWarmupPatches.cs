using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Rooms;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

public static class CombatAnimationWarmupPatches
{
    private const string ModeOff = "off";
    private const string ModeSafe = "safe";
    private const string ModeAll = "all";
    private const int MaxClipsPerCreature = 128;

    // Total rendered poses one combat may spend (see the pose-budget comment in the warmup loop).
    private const int MaxWarmupPoseFrames = 216;
    private const int MinWarmupPosesPerClip = 2;

    // Poses stepped between two presented frames. 1 = the original one-frame-per-pose pacing.
    private const int PosesPerPresentedFrame = 2;

    private static int ResolvePoseBudget(int requestedPoses, int plannedClips)
    {
        if (plannedClips <= 0 || requestedPoses <= 0)
            return Math.Max(1, requestedPoses);
        int affordable = MaxWarmupPoseFrames / plannedClips;
        return Math.Max(MinWarmupPosesPerClip, Math.Min(requestedPoses, affordable));
    }

    private static readonly ConditionalWeakTable<NCombatRoom, WarmupState> RoomStates = new();

    // Handle for the in-flight warmup so the room reveal (CombatWarmupRevealGatePatches) can wait
    // for it instead of fading the transition out underneath it.
    internal static Task CurrentRoomWarmup;

    private static NCombatRoom CurrentWarmupRoom;

    public static void Apply(Harmony harmony)
    {
        PatchHelper.Patch(harmony, typeof(NCombatRoom), "_Ready", postfix: PatchHelper.Method(typeof(CombatAnimationWarmupPatches), nameof(CombatRoomReadyPostfix)));
    }

    public static void CombatRoomReadyPostfix(NCombatRoom __instance)
    {
        try
        {
            if (!OS.GetName().Equals("Android", StringComparison.OrdinalIgnoreCase) || __instance == null)
                return;
            string mode = GetWarmupMode();
            bool hitEffectWarmup = IsCombatHitEffectWarmupEnabled();
            if (mode == ModeOff && !hitEffectWarmup)
                return;
            if (__instance.Mode != CombatRoomMode.ActiveCombat)
                return;

            var state = RoomStates.GetOrCreateValue(__instance);
            if (state.Started)
                return;
            state.Started = true;

            // Arm the handle synchronously: NTransition.RoomFadeIn can arrive before a deferred
            // callable runs, and an unarmed gate would let the reveal fade out mid-warmup.
            CurrentWarmupRoom = __instance;
            CurrentRoomWarmup = WarmupCombatAnimationsAsync(__instance, mode, GetWarmupFrames(), hitEffectWarmup);
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Combat animation warmup scheduling failed: {exception.Message}");
        }
    }

    private static async Task WarmupCombatAnimationsAsync(NCombatRoom room, string mode, int frames, bool hitEffectWarmup)
    {
        ulong started = Time.GetTicksMsec();
        int warmedCreatures = 0;
        int warmedClips = 0;
        int failedClips = 0;
        int warmedHitEffects = 0;
        int warmedHitAudio = 0;
        int failedHitEffects = 0;
        ulong previewMs = 0;
        ulong poseMs = 0;
        int plannedClips = 0;
        int posesPerClip = 0;
        try
        {
            await WaitForFramesAsync(2);
            if (!IsActiveRoom(room))
                return;

            var creatures = room.CreatureNodes
                .Where(IsWarmableCreature)
                .ToArray();
            if (creatures.Length == 0)
                return;

            PatchHelper.Log($"Combat animation warmup begin: mode={mode} creatures={creatures.Length:N0} frames={frames:N0} hit_effects={hitEffectWarmup}.");
            ColorRect warmupMask = null;
            if (CombatWarmupRevealGatePatches.IsTransitionCovering())
            {
                // The game's own transition already covers the room and the reveal is gated on this
                // task, so an extra full-screen cover would only add a second black phase.
                Diag($"step=cover=game_transition t={Time.GetTicksMsec()}");
            }
            else
            {
                Diag($"step=mask_begin {CombatWarmupRevealGatePatches.DescribeCoverState()}");
                warmupMask = CreateWarmupMask(room);
                Diag($"step=mask_done created={warmupMask != null}");
                if (warmupMask == null)
                    return;
                await WaitForFramesAsync(1);
            }
            try
            {
                if (mode != ModeOff)
                {
                    // Pose budget: hold time measures at ~19-26ms per rendered pose, so a 3-4 creature
                    // fight at 12 poses per clip (29-32 clips) runs 6.5-7.6s while a 2 creature fight
                    // (13-19 clips) runs ~4s. Spend a fixed total per fight instead: small fights keep
                    // the requested pose count, big fights get fewer poses per clip.
                    var plan = new List<(NCreature Creature, string[] Clips)>();
                    plannedClips = 0;
                    foreach (NCreature creature in creatures)
                    {
                        if (!IsActiveRoom(room) || !IsWarmableCreature(creature))
                            continue;
                        try
                        {
                            string[] names = CollectWarmupAnimationNames(creature, mode);
                            Diag($"step=names_collected creature={DescribeCreature(creature)} clips={names.Length}");
                            if (names.Length == 0)
                                continue;
                            plan.Add((creature, names));
                            plannedClips += names.Length;
                        }
                        catch (Exception exception)
                        {
                            failedClips++;
                            PatchHelper.Log($"Combat animation warmup skipped creature={DescribeCreature(creature)}: {exception.GetType().Name}: {exception.Message}");
                        }
                    }
                    int poses = ResolvePoseBudget(frames, plannedClips);
                    posesPerClip = poses;
                    PatchHelper.Log($"Combat animation warmup plan: creatures={plan.Count:N0} clips={plannedClips:N0} poses_per_clip={poses:N0} requested={frames:N0} budget={MaxWarmupPoseFrames:N0}.");

                    foreach ((NCreature creature, string[] animationNames) in plan)
                    {
                        if (!IsActiveRoom(room) || !IsWarmableCreature(creature))
                            continue;
                        Node2D preview = null;
                        try
                        {
                            ulong previewStarted = Time.GetTicksMsec();
                            preview = CreateSpinePreview(room, creature);
                            previewMs += Time.GetTicksMsec() - previewStarted;
                            if (preview == null)
                                continue;
                            await WaitForFramesAsync(2);
                            Diag("step=preview_frames_settled");
                            warmedCreatures++;
                            if (IsPreloadDebugEnabled())
                                PatchHelper.Log($"Combat animation warmup creature={DescribeCreature(creature)} isolated_clips=[{DescribeList(animationNames)}].");
                            ulong poseStarted = Time.GetTicksMsec();
                            foreach (string animationName in animationNames)
                            {
                                if (!IsActiveRoom(room) || !IsValid(preview) || !preview.IsInsideTree())
                                    break;
                                if (await WarmAnimationClipAsync(room, preview, animationName, poses))
                                    warmedClips++;
                                else
                                    failedClips++;
                            }
                            poseMs += Time.GetTicksMsec() - poseStarted;
                        }
                        catch (Exception exception)
                        {
                            failedClips++;
                            PatchHelper.Log($"Combat animation warmup skipped creature={DescribeCreature(creature)}: {exception.GetType().Name}: {exception.Message}");
                        }
                        finally
                        {
                            Diag($"step=freeing_preview creature={DescribeCreature(creature)} valid={IsValid(preview)}");
                            ulong freeStarted = Time.GetTicksMsec();
                            FreeWarmupNode(preview);
                            previewMs += Time.GetTicksMsec() - freeStarted;
                            Diag("step=preview_freed");
                            await WaitForFramesAsync(1);
                        }
                    }
                }

                if (hitEffectWarmup && IsActiveRoom(room))
                {
                    (warmedHitEffects, failedHitEffects, warmedHitAudio) = await WarmCombatHitEffectsAsync(room, creatures, Math.Max(frames, 6));
                }
            }
            finally
            {
                RemoveWarmupMask(warmupMask);
                if (warmupMask != null)
                    await WaitForFramesAsync(1);
            }

            PatchHelper.Log($"Combat animation warmup complete: mode={mode} isolated=true creatures={warmedCreatures:N0}/{creatures.Length:N0} clips={warmedClips:N0} clip_failed={failedClips:N0} hit_effects={warmedHitEffects:N0} hit_audio={warmedHitAudio:N0} hit_effect_failed={failedHitEffects:N0} poses_per_clip={posesPerClip:N0} preview_ms={previewMs:N0} pose_ms={poseMs:N0} elapsed={Time.GetTicksMsec() - started:N0}ms.");
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Combat animation warmup failed: {exception}");
        }
        finally
        {
            if (ReferenceEquals(CurrentWarmupRoom, room))
            {
                CurrentWarmupRoom = null;
                CurrentRoomWarmup = null;
            }
        }
    }

    private static async Task<(int Warmed, int Failed, int Audio)> WarmCombatHitEffectsAsync(NCombatRoom room, IReadOnlyList<NCreature> creatures, int frames)
    {
        int warmed = 0;
        int failed = 0;
        int warmedAudio = 0;
        var nodesToFree = new List<Node>();
        ulong started = Time.GetTicksMsec();
        try
        {
            if (!IsValid(room?.CombatVfxContainer))
                return (0, 0, 0);

            NCreature[] targets = creatures
                .Where(creature => IsValid(creature) && creature.Entity != null && creature.Entity.IsAlive)
                .OrderBy(creature => creature.Entity.IsPlayer ? 1 : 0)
                .Take(4)
                .ToArray();
            if (targets.Length == 0)
                targets = creatures.Where(IsValid).Take(2).ToArray();

            PatchHelper.Log($"Combat hit-effect warmup begin: targets={targets.Length:N0} frames={frames:N0}.");
            warmedAudio += WarmCommonCombatHitAudio();
            foreach (NCreature target in targets)
            {
                try
                {
                    if (!IsActiveRoom(room) || !IsValid(target) || !target.IsInsideTree())
                        continue;
                    Vector2 position = target.VfxSpawnPosition;
                    int visualBefore = nodesToFree.Count;
                    AddWarmupNode(room.CombatVfxContainer, NDamageNumVfx.Create(target.Entity, 6, requireInteractable: false) ?? NDamageNumVfx.Create(position, 6), nodesToFree);
                    AddWarmupNode(room.CombatVfxContainer, NDamageNumVfx.Create(position + new Vector2(24f, -8f), 1234567890), nodesToFree);
                    AddWarmupNode(room.CombatVfxContainer, NHitSparkVfx.Create(target.Entity, requireInteractable: false), nodesToFree);
                    AddWarmupNode(room.CombatVfxContainer, CreatePositionedVfx("vfx/vfx_attack_slash", target.GlobalPosition), nodesToFree);
                    AddWarmupNode(room.CombatVfxContainer, CreatePositionedVfx("vfx/vfx_attack_blunt", position + new Vector2(16f, 0f)), nodesToFree);
                    warmed += nodesToFree.Count - visualBefore;
                    warmedAudio += WarmCombatHitAudio(target.Entity);
                }
                catch (Exception exception)
                {
                    failed++;
                    PatchHelper.Log($"Combat hit-effect warmup skipped target={DescribeCreature(target)}: {exception.GetType().Name}: {exception.Message}");
                }
            }

            await WaitForFramesAsync(Math.Max(12, Math.Min(30, frames)));
        }
        catch (Exception exception)
        {
            failed++;
            PatchHelper.Log($"Combat hit-effect warmup failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            ConcealWarmupNodes(nodesToFree);
            _ = FreeWarmupNodesAfterDelayAsync(nodesToFree.ToArray(), 180);
            await WaitForFramesAsync(1);
        }

        PatchHelper.Log($"Combat hit-effect warmup complete: warmed={warmed:N0} audio={warmedAudio:N0} failed={failed:N0} nodes={nodesToFree.Count:N0} elapsed={Time.GetTicksMsec() - started:N0}ms.");
        return (warmed, failed, warmedAudio);
    }

    private static int WarmCombatHitAudio(Creature creature)
    {
        try
        {
            var audio = NAudioManager.Instance;
            MonsterModel monster = creature?.Monster;
            if (audio == null || monster == null)
                return 0;

            int warmed = 0;
            if (!string.IsNullOrWhiteSpace(monster.TakeDamageSfx))
            {
                audio.PlayOneShot(monster.TakeDamageSfx, new Dictionary<string, float> { { "EnemyImpact_Intensity", 2f } }, 0f);
                warmed++;
            }
            if (monster.HasHurtSfx && !string.IsNullOrWhiteSpace(monster.HurtSfx))
            {
                audio.PlayOneShot(monster.HurtSfx, 0f);
                warmed++;
            }
            return warmed;
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Combat hit audio warmup skipped creature={creature}: {exception.GetType().Name}: {exception.Message}");
            return 0;
        }
    }

    private static int WarmCommonCombatHitAudio()
    {
        try
        {
            var audio = NAudioManager.Instance;
            if (audio == null)
                return 0;

            var impactParameters = new Dictionary<string, float> { { "EnemyImpact_Intensity", 2f } };
            string[] paths =
            {
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_armor",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_armor_big",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_fur",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_insect",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_magic",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_plant",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_slime",
                "event:/sfx/enemy/enemy_impact_enemy_size/enemy_impact_stone",
            };

            int warmed = 0;
            foreach (string path in paths)
            {
                audio.PlayOneShot(path, impactParameters, 0f);
                warmed++;
            }
            audio.PlayOneShot("event:/sfx/block_break", 0f);
            return warmed + 1;
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Common combat hit audio warmup skipped: {exception.GetType().Name}: {exception.Message}");
            return 0;
        }
    }

    private static Node2D CreatePositionedVfx(string path, Vector2 position)
    {
        string scenePath = SceneHelper.GetScenePath(path);
        Node2D node = PreloadManager.Cache.GetScene(scenePath).Instantiate<Node2D>(PackedScene.GenEditState.Disabled);
        TryApplyMobileShaderCompatibility(node);
        node.GlobalPosition = position;
        return node;
    }

    private static void TryApplyMobileShaderCompatibility(Node node)
    {
        try
        {
            var type = typeof(PreloadManager).Assembly.GetType("MegaCrit.Sts2.Core.Helpers.MobileShaderCompatibility");
            var method = type?.GetMethod("ApplyIfEnabled", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Node) }, null);
            method?.Invoke(null, new object[] { node });
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Combat hit-effect shader compatibility skipped: {exception.Message}");
        }
    }

    private static void AddWarmupNode(Node parent, Node node, ICollection<Node> nodesToFree)
    {
        if (!IsValid(parent) || node == null)
            return;
        parent.AddChildSafely(node);
        nodesToFree.Add(node);
    }

    private static void FreeWarmupNode(Node node)
    {
        try
        {
            if (!IsValid(node))
                return;
            if (node.IsInsideTree())
                node.QueueFree();
            else
                node.Free();
        }
        catch
        {
        }
    }

    private static void ConcealWarmupNodes(IEnumerable<Node> nodes)
    {
        foreach (Node node in nodes)
        {
            try
            {
                if (IsValid(node) && node is CanvasItem canvasItem)
                {
                    canvasItem.Modulate = WithAlpha(canvasItem.Modulate, 0f);
                    canvasItem.Visible = false;
                }
            }
            catch
            {
            }
        }
    }

    private static async Task FreeWarmupNodesAfterDelayAsync(Node[] nodes, int frames)
    {
        try
        {
            await WaitForFramesAsync(frames);
            foreach (Node node in nodes)
                FreeWarmupNode(node);
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Delayed combat hit-effect cleanup skipped: {exception.Message}");
        }
    }

    private static ColorRect CreateWarmupMask(NCombatRoom room)
    {
        try
        {
            if (!IsValid(room))
                return null;

            var mask = new ColorRect
            {
                Name = "AndroidCombatAnimationWarmupMask",
                LayoutMode = 1,
                AnchorRight = 1f,
                AnchorBottom = 1f,
                Color = Colors.Black,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ZIndex = 4096,
                ZAsRelative = false,
            };
            room.AddChild(mask);
            room.MoveChild(mask, room.GetChildCount() - 1);
            return mask;
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Combat animation warmup mask unavailable: {exception.Message}");
            return null;
        }
    }

    private static void RemoveWarmupMask(ColorRect mask)
    {
        try
        {
            if (IsValid(mask))
                mask.QueueFree();
        }
        catch
        {
        }
    }

    // Duplicate() copies a node's internal children as regular children, so the preview's
    // object graph no longer matches what the native SpineSprite holds references to: freeing
    // those copies crashed the process on the preview's first rendered frame. Keeping the graph
    // intact and only stopping the copies from drawing/processing avoids that, while the
    // preview SpineSprite itself stays visible so the warmup really reaches the renderer.
    private static void SuppressPreviewChildren(Node preview)
    {
        foreach (Node child in preview.GetChildren())
        {
            if (child is CanvasItem canvasItem)
                canvasItem.Hide();
            child.ProcessMode = Node.ProcessModeEnum.Disabled;
        }
    }

    private static Node2D CreateSpinePreview(NCombatRoom room, NCreature creature)
    {
        if (creature.Visuals?.SpineBody?.BoundObject is not Node2D source
            || !IsValid(source) || !source.IsClass("SpineSprite"))
            return null;
        // No scripts, signal connections, groups, or scene re-instantiation.
        // Only the native visual is copied; never clone NCreature or its animator.
        Diag($"step=duplicate_begin creature={DescribeCreature(creature)} source_children={source.GetChildCount()}");
        var preview = source.Duplicate() as Node2D;
        if (preview == null)
            return null;
        Diag($"step=duplicate_done preview_children={preview.GetChildCount()} inside_tree={preview.IsInsideTree()}");
        try
        {
            SuppressPreviewChildren(preview);
            Diag($"step=preview_children_suppressed remaining={preview.GetChildCount()}");
            preview.ZIndex = 0;
            preview.ZAsRelative = false;
            room.AddChild(preview);
            Diag("step=preview_added_to_room");
            preview.GlobalTransform = source.GlobalTransform;
            Diag("step=preview_transform_set");
            return preview;
        }
        catch
        {
            FreeWarmupNode(preview);
            throw;
        }
    }

    private static async Task<bool> WarmAnimationClipAsync(NCombatRoom room, Node2D preview, string animationName, int frames)
    {
        if (!IsActiveRoom(room) || !IsValid(preview) || !preview.IsInsideTree())
            return false;
        try
        {
            using var stateValue = preview.Call("get_animation_state");
            using var skeletonValue = preview.Call("get_skeleton");
            using GodotObject state = stateValue.AsGodotObject();
            using GodotObject skeleton = skeletonValue.AsGodotObject();
            if (state == null || skeleton == null)
                return false;
            using var trackValue = state.Call("set_animation", animationName, IsLoopingAnimationName(animationName), 0);
            using GodotObject track = trackValue.AsGodotObject();
            if (track == null)
                return false;
            track.Call("set_mix_duration", 0f).Dispose();
            float duration = track.Call("get_animation_end").AsSingle();
            int samples = Math.Max(1, frames);
            for (int i = 0; i < samples; i++)
            {
                if (!IsActiveRoom(room) || !IsValid(preview) || !preview.IsInsideTree())
                    return false;
                if (duration > 0.001f)
                {
                    float ratio = samples == 1 ? 0.05f : (float)i / Math.Max(1, samples - 1);
                    track.Call("set_track_time", Math.Min(duration - 0.001f, Math.Max(0f, duration * ratio))).Dispose();
                }
                state.Call("update", 0f).Dispose();
                state.Call("apply", skeleton).Dispose();
                // The hold is paid in presented frames (measured 26-39ms per pose at p50 frame time
                // ~26.5ms), not in poses: step the pose every iteration, present every Nth one.
                if (samples == 1 || (i + 1) % PosesPerPresentedFrame == 0 || i == samples - 1)
                    await WaitForFramesAsync(1);
            }
            return true;
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Isolated combat animation warmup skipped clip={animationName}: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static string[] CollectWarmupAnimationNames(NCreature creature, string mode)
    {
        var names = CollectAllAnimationNames(creature);
        if (mode == ModeSafe)
            names = names.Where(name => IsSafeCombatAnimationName(creature, name));
        return names
            .Where(name => IsUsableAnimationName(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(GetAnimationWarmupPriority)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Take(MaxClipsPerCreature)
            .ToArray();
    }

    private static IEnumerable<string> CollectAllAnimationNames(NCreature creature)
    {
        MegaSprite sprite = creature.Visuals?.SpineBody;
        MegaSkeleton skeleton = sprite?.GetSkeleton();
        MegaSkeletonDataResource data = skeleton?.GetData();
        Diag($"step=skeleton_data creature={DescribeCreature(creature)} has_data={data != null}");
        if (data == null)
            yield break;

        Godot.Collections.Array<GodotObject> animations;
        try
        {
            animations = (Godot.Collections.Array<GodotObject>)data.BoundObject.Call("get_animations");
        }
        catch (Exception exception)
        {
            if (IsPreloadDebugEnabled())
                PatchHelper.Log($"Combat animation list unavailable creature={DescribeCreature(creature)}: {exception.Message}");
            yield break;
        }

        foreach (GodotObject animation in animations)
        {
            string name = null;
            try
            {
                name = animation?.Call("get_name").AsString();
            }
            catch
            {
                name = null;
            }
            if (!string.IsNullOrWhiteSpace(name))
                yield return name;
        }
    }

    private static bool IsWarmableCreature(NCreature creature)
    {
        try
        {
            return IsValid(creature)
                && creature.HasSpineAnimation
                && creature.Visuals?.SpineBody != null
                && creature.Entity != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeCombatAnimationName(NCreature creature, string name)
    {
        if (!IsUsableAnimationName(name))
            return false;
        if (IsDangerousAnimationName(name))
            return false;
        string lower = name.ToLowerInvariant();
        if (creature.Entity?.IsPlayer == true)
            return ContainsAny(lower, "idle", "attack", "cast", "hurt", "hit", "shiv", "relaxed", "block");
        return ContainsAny(lower,
            "idle", "attack", "cast", "hurt", "hit", "buff", "debuff", "heal", "rally",
            "bite", "slash", "stab", "smash", "swipe", "throw", "poke", "spit", "vomit",
            "hug", "chomp", "flail", "ram", "breaker", "bomb", "laser", "grenade",
            "heavy", "uppercut", "sharpen", "vines", "thrash");
    }


    private static bool IsUsableAnimationName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        string lower = name.ToLowerInvariant();
        return !lower.Equals("empty", StringComparison.Ordinal)
            && !lower.EndsWith("/empty", StringComparison.Ordinal)
            && !lower.Equals("_tracks/empty", StringComparison.Ordinal)
            && !lower.Equals("tracks/empty", StringComparison.Ordinal);
    }

    private static bool IsDangerousAnimationName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        string lower = name.ToLowerInvariant();
        return ContainsAny(lower, "die", "dead", "death", "flee", "escape", "spawn", "summon", "revive", "hatch", "burrow", "sleep", "wake", "intro");
    }


    private static int GetAnimationWarmupPriority(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.Contains("idle"))
            return 0;
        if (lower.Contains("attack"))
            return 1;
        if (lower.Contains("shiv"))
            return 2;
        if (lower.Contains("cast"))
            return 3;
        if (lower.Contains("hurt") || lower.Contains("hit"))
            return 4;
        if (lower.Contains("die") || lower.Contains("dead"))
            return 90;
        return 50;
    }


    private static bool IsLoopingAnimationName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        string lower = name.ToLowerInvariant();
        return lower.Contains("loop") || lower.Contains("idle") || lower.Contains("hover") || lower.Contains("writhe");
    }

    private static async Task WaitForFramesAsync(int frames)
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null)
        {
            await Task.Yield();
            return;
        }
        for (int i = 0; i < Math.Max(1, frames); i++)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        return new Color(color.R, color.G, color.B, Math.Max(0f, Math.Min(1f, alpha)));
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (value.Contains(needle, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool IsValid(GodotObject value)
    {
        return value != null && GodotObject.IsInstanceValid(value);
    }

    private static bool IsActiveRoom(NCombatRoom room)
    {
        return IsValid(room) && room.IsInsideTree() && !room.IsQueuedForDeletion()
            && room.Mode == CombatRoomMode.ActiveCombat;
    }

    private static string GetWarmupMode()
    {
        if (!AndroidSettingsBridge.GetBool("preload_enabled", true))
            return ModeOff;
        string value = AndroidSettingsBridge.GetString("preload_combat_animation_warmup_mode", ModeAll).Trim().ToLowerInvariant();
        return value switch
        {
            "safe" or "current_room_safe" or "room_safe" => ModeSafe,
            "all" or "full" or "current_room_all" or "room_all" => ModeAll,
            _ => ModeOff,
        };
    }

    private static int GetWarmupFrames()
    {
        if (!AndroidSettingsBridge.GetBool("preload_enabled", true))
            return 0;
        int frames = AndroidSettingsBridge.GetInt("preload_combat_animation_warmup_frames", 1);
        return Math.Max(1, Math.Min(12, frames));
    }

    private static bool IsCombatHitEffectWarmupEnabled()
    {
        return AndroidSettingsBridge.GetBool("preload_enabled", true)
            && AndroidSettingsBridge.GetBool("preload_combat_hit_effect_warmup_enabled", false);
    }

    private static bool IsPreloadDebugEnabled() => AndroidSettingsBridge.GetBool("preload_debug_enabled", false);

    private static void Diag(string message)
    {
        if (IsPreloadDebugEnabled())
            PatchHelper.Log($"[WarmupDiag] {message}");
    }

    private static string DescribeCreature(NCreature creature)
    {
        try
        {
            return creature?.Entity?.ToString() ?? creature?.Name.ToString() ?? "<null>";
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static string DescribeList(IReadOnlyList<string> values)
    {
        if (values == null || values.Count == 0)
            return "";
        const int max = 16;
        string joined = string.Join(",", values.Take(max));
        if (values.Count > max)
            joined += $",+{values.Count - max:N0}";
        return joined;
    }

    private sealed class WarmupState
    {
        public bool Started;
    }
}
