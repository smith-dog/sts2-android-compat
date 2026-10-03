using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;
using STS2Mobile.Android;

namespace STS2Mobile.Patches;

// Only naturally completed, unmodified stock effects are retained. Factories,
// Ready, animation timing and cancellation remain owned by the original game.
internal static class CombatVfxPoolPatches
{
    private static readonly ConditionalWeakTable<Node, Entry> Entries = new();
    private static readonly ConditionalWeakTable<NCombatRoom, RoomPool> Rooms = new();
    private static readonly Dictionary<Type, Contract> Contracts = new();
    private static readonly AsyncLocal<Playback> CurrentPlayback = new();
    private static StringName ColorParameter;
    private static Harmony _harmony;
    private static bool _installed;

    // TASK-051 (2026-10-03) generic-pool telemetry: cumulative pool hits
    // (bucket + warm spare), fresh fallbacks and warm-spare handouts, logged
    // per room close. The hit counter doubles as the empirical proof that the
    // transpiled factories are actually reached (a silently inlined or
    // wrongly-contracted family would show up as hits staying flat).
    private static int _rentHits;
    private static int _rentFresh;
    private static int _spareHits;

    // Exact node types a pooled tree may contain. Anything else (spine rigs,
    // trails, audio players, viewports) leaves the family on fresh spawns.
    // NSpriteAnimator/NVfxParticleSystem are the scripted utilities the storm
    // scenes (attack slash/blunt, slime impact, ...) are built from: their
    // _Ready re-runs on reuse, so only their one-shot CancellationTokenSource
    // needs pool-side resetting (see ResetScriptTransientState).
    private static readonly Type MegaLabelType = typeof(MegaCrit.Sts2.addons.mega_text.MegaLabel);
    private static readonly Type NSpriteAnimatorType = typeof(NSpriteAnimator);
    private static readonly Type NVfxParticleSystemType = typeof(NVfxParticleSystem);
    private static readonly HashSet<Type> SupportedChildTypes = new()
    {
        typeof(Node2D), typeof(GpuParticles2D), typeof(CpuParticles2D),
        typeof(Sprite2D), typeof(TextureRect), typeof(Control),
        typeof(Path2D), typeof(PathFollow2D),
        NSpriteAnimatorType, NVfxParticleSystemType,
        MegaLabelType,
    };

    // Scripted children allowed inside a pooled tree. Every other scripted
    // node type disqualifies the tree: their re-entry semantics are unknown.
    private static readonly HashSet<Type> ScriptedChildTypes = new()
    {
        MegaLabelType, NSpriteAnimatorType, NVfxParticleSystemType,
    };

    // Families whose trees failed capture, with the offending child type, so
    // repeat spawns skip the tree walk and the log line prints once.
    private static readonly HashSet<Type> UncapturableFamilies = new();

    // Cached "_cancelToken" fields of scripted utilities (null = no such field).
    private static readonly Dictionary<Type, FieldInfo> CancelTokenFields = new();

    // Animator families that grab REAL game cards/props via _card and free
    // themselves from signal handlers outside the playback lease. Pooling them
    // buys nothing (NCardFlyVfx never actually recycles) and is dangerous: a
    // re-rent re-fires _Ready (RequestReady), which started a second
    // concurrent PlayAnim on the same card — observed 2026-10-03 as "Signal
    // 'tree_exited' is already connected" from NCardFlyVfx._Ready plus a
    // flight frozen mid-tilt after MAYHEM exhausted a card. They stay vanilla.
    private static readonly HashSet<Type> ExcludedFamilies = new()
    {
        typeof(NCardFlyVfx), typeof(NCardFlyPowerVfx), typeof(NCardFlyShuffleVfx),
        typeof(NKinPriestGrenadeVfx), typeof(NMapNodeSelectVfx),
    };

    internal static void Apply(Harmony harmony)
    {
        _harmony = harmony;
        PatchHelper.Patch(harmony, typeof(NGame), "_Ready", postfix: PatchHelper.Method(typeof(CombatVfxPoolPatches), nameof(GameReadyPostfix)));
    }

    private static void GameReadyPostfix()
    {
        if (_installed || OS.GetName() != "Android") return;
        _installed = true;
        try
        {
            ColorParameter = "color";
            Install(typeof(NDamageNumVfx), "AnimVfx", 16);
            Install(typeof(NHitSparkVfx), "FlashAndFree", 8);
            Install(typeof(NShivThrowVfx), "PlaySequence", 8);
            _harmony.Patch(AccessTools.Method(typeof(GodotTreeExtensions), "QueueFreeSafely", new[] { typeof(Node) }),
                prefix: new HarmonyMethod(typeof(CombatVfxPoolPatches), nameof(QueueFreePrefix)));
            _harmony.Patch(AccessTools.Method(typeof(NShivThrowVfx), "ApplyTint"),
                prefix: new HarmonyMethod(typeof(CombatVfxPoolPatches), nameof(TintPrefix)));
            PatchHelper.Log($"Combat VFX reuse enabled: per-room retained limits damage=16 hit=8 shiv=8; overflow uses original allocation. pool_scope={AndroidSettingsBridge.GetString("preload_vfx_pool_scope", "all")}");
            // TASK-051 (2026-10-03): the generic pool is back. Its earlier
            // removal blamed the day's crashes on mass patching, but those
            // crashes were the pre-existing Godot static-StringName recycling
            // bug (they also kill sessions with no pooling active at all, and
            // session 27018 ran this very pool for 77 minutes with no
            // pool-attributable fault). ModEntry now pins the affected names.
            // preload_vfx_pool_scope=stock restores three-family-only behavior
            // without a rebuild.
            if (AndroidSettingsBridge.GetString("preload_vfx_pool_scope", "all") != "stock")
            {
                try
                {
                    TryInstallGenericPool();
                }
                catch (Exception genericFailure)
                {
                    // The three explicit families stay contracted; a generic-pool
                    // failure must not take them down with it.
                    PatchHelper.Log($"Combat VFX generic pool: install failed, explicit families kept: {genericFailure.GetType().Name}: {genericFailure.Message}");
                }
            }
            else
            {
                PatchHelper.Log("Combat VFX generic pool: disabled by preload_vfx_pool_scope=stock; only the three explicit families are pooled.");
            }
        }
        catch (Exception exception)
        {
            // Partially installed factory hooks must not rent without the release contract.
            Contracts.Clear();
            PatchHelper.Log($"Combat VFX reuse unavailable: {exception}");
        }
    }

    private static void Install(Type type, string playback, int capacity)
    {
        Install(type, playback, capacity, verifyFactoryIL: true);
    }

    private static void Install(Type type, string playback, int capacity, bool verifyFactoryIL)
    {
        var contract = new Contract(type, playback, capacity, verifyFactoryIL);
        foreach (var method in contract.Factories)
            _harmony.Patch(method, transpiler: new HarmonyMethod(typeof(CombatVfxPoolPatches), nameof(FactoryTranspiler)));
        _harmony.Patch(contract.Play, prefix: new HarmonyMethod(typeof(CombatVfxPoolPatches), nameof(PlaybackPrefix)),
            finalizer: new HarmonyMethod(typeof(CombatVfxPoolPatches), nameof(PlaybackFinalizer)));
        Contracts.Add(type, contract);
    }

    // TASK-051 (2026-10-03): startup-warm trees retained by the loading screen
    // (see RetainWarmTree) sit here as global spares. They are handed out on
    // the first Rent of their family (before the room bucket exists), then
    // follow the normal room-bucket recycle loop. Bounded per family.
    private static readonly object GlobalSpareLock = new();
    private static readonly Dictionary<Type, Stack<Node2D>> GlobalSpares = new();

    private static Node TakeGlobalSpare(Type type, Bucket bucket, ulong sceneId, out Entry entry)
    {
        entry = null;
        lock (GlobalSpareLock)
        {
            if (!GlobalSpares.TryGetValue(type, out var stack))
                return null;
            while (stack.TryPop(out var canvas))
            {
                if (!GodotObject.IsInstanceValid(canvas) || canvas.IsQueuedForDeletion())
                    continue;
                // Snapshots captured here describe the tree at rest after the
                // startup warm render; Restore returns it to that state.
                if (!Capture(canvas, out var snapshots, out _))
                {
                    // Retained before this build knew the tree was uncapturable
                    // (e.g. NCardTrailVfx keeps a scripted NCardTrail child).
                    // Handing it out would build an Entry with null snapshots
                    // and NRE through PrepareForReentry into the caller's _Ready.
                    canvas.QueueFree();
                    continue;
                }
                entry = new Entry(canvas, bucket, sceneId, snapshots);
                // Godot 4 clears nothing on tree exit: _Ready fires once per
                // Node lifetime unless RequestReady() is called. Tree-warmed
                // spares already ran _Ready during the loading screen, so hand
                // them back prepared for re-entry (bucket-path rentals get the
                // same treatment via Snapshot.Restore).
                entry.PrepareForReentry();
                _rentHits++;
                _spareHits++;
                return canvas;
            }
            return null;
        }
    }

    // Called by AndroidStartupLoadingScreen for every VFX tree it warmed during
    // the startup loading screen. Retained only for families whose contract was
    // installed (generic pool), and only up to a per-family depth; everything
    // else takes the original free path.
    internal static void RetainWarmTree(Node node, PackedScene scene)
    {
        if (node == null)
            return;
        try
        {
            var type = node.GetType();
            if (!Contracts.TryGetValue(type, out var contract))
                return;
            if (!(node is Node2D canvas))
                return;
            // Do not retain trees we cannot snapshot: they would NRE on handout
            // (TakeGlobalSpare bails anyway; this keeps the spare stack clean).
            if (!Capture(canvas, out _, out _))
                return;
            lock (GlobalSpareLock)
            {
                if (!GlobalSpares.TryGetValue(type, out var stack))
                    GlobalSpares[type] = stack = new Stack<Node2D>();
                if (stack.Count >= 4)
                    return;
                node.GetParent()?.RemoveChild(node);
                stack.Push(canvas);
            }
        }
        catch
        {
            // Retention is opportunistic; failures fall back to freeing.
        }
    }

    // TASK-051 (2026-10-03): card-play frames of 60-300 ms are the per-play
    // PackedScene.Instantiate of the non-pooled VFX families. Static analysis
    // of the decompiled assembly: 68 of the 151 stock families in this exact
    // namespace match the contract (static factories named "Create" plus
    // exactly one declared Task-returning playback method); the rest are
    // skipped, not pooled. At spawn time, families whose trees carry state
    // Capture cannot snapshot stay on fresh instantiation automatically.
    private static void TryInstallGenericPool()
    {
        ulong started = Time.GetTicksMsec();
        int installed = 0;
        int skipped = 0;
        Type[] families;
        try
        {
            try
            {
                families = typeof(NDamageNumVfx).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                families = exception.Types.Where(type => type != null).Cast<Type>().ToArray();
            }
        }
        catch (Exception exception)
        {
            PatchHelper.Log($"Combat VFX generic pool: family scan failed: {exception.GetType().Name}: {exception.Message}");
            return;
        }
        foreach (var type in families)
        {
            if (type == null || !type.IsClass || type.IsAbstract) continue;
            if (type.Namespace != "MegaCrit.Sts2.Core.Nodes.Vfx") continue;
            if (Contracts.ContainsKey(type)) continue;
            if (ExcludedFamilies.Contains(type))
            {
                skipped++;
                continue;
            }
            try
            {
                string playback = FindSingleTaskPlayback(type);
                if (playback == null)
                {
                    skipped++;
                    continue;
                }
                Install(type, playback, 2, verifyFactoryIL: false);
                installed++;
            }
            catch
            {
                skipped++;
            }
        }
        PatchHelper.Log($"Combat VFX generic pool: installed={installed} skipped={skipped} contracts={Contracts.Count} elapsed={Time.GetTicksMsec() - started}ms.");
    }

    private static string FindSingleTaskPlayback(Type type)
    {
        var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.ReturnType == typeof(System.Threading.Tasks.Task)
                && method.Name != "Create"
                && !method.IsSpecialName)
            .ToArray();
        return candidates.Length == 1 ? candidates[0].Name : null;
    }

    private static bool IsInstantiation(CodeInstruction instruction, Type type) =>
        instruction.operand is MethodInfo method && method.DeclaringType == typeof(PackedScene)
        && method.Name == "Instantiate" && method.IsGenericMethod && method.GetGenericArguments()[0] == type;

    private static IEnumerable<CodeInstruction> FactoryTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        foreach (var instruction in instructions)
        {
            if (IsInstantiation(instruction, __originalMethod.DeclaringType))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(CombatVfxPoolPatches), nameof(Rent)).MakeGenericMethod(__originalMethod.DeclaringType);
            }
            yield return instruction;
        }
    }

    private static T Rent<T>(PackedScene scene, PackedScene.GenEditState editState) where T : Node
    {
        var room = NCombatRoom.Instance;
        if (editState != PackedScene.GenEditState.Disabled || room == null || !GodotObject.IsInstanceValid(room)
            || !room.IsInsideTree() || !Contracts.TryGetValue(typeof(T), out var contract))
            return scene.Instantiate<T>(editState);
        var pool = Rooms.GetValue(room, static owner => new RoomPool(owner));
        var bucket = pool.GetBucket(contract);
        if (pool.Closed || !bucket.Allowed) return scene.Instantiate<T>(editState);
        ulong sceneId = scene.GetInstanceId();

        // TASK-051 (2026-10-03): prefer a startup-warm spare before instantiating
        // fresh. The spare was built and rendered during the loading screen, so
        // card play pays no tree-build or scene-init cost at all. The spare is
        // registered as a tracked entry: playback and recycling then follow the
        // same room-bucket lifecycle as native spawns.
        Node warm = TakeGlobalSpare(typeof(T), bucket, sceneId, out var warmEntry);
        if (warm != null)
        {
            Entries.Add(warm, warmEntry);
            warm.TreeExiting += warmEntry.Exiting;
            return (T)warm;
        }

        while (bucket.Free.TryPop(out var entry))
        {
            entry.InPool = false;
            if (!GodotObject.IsInstanceValid(entry.Node)) continue;
            if (entry.SceneId != sceneId || entry.Node.IsQueuedForDeletion() || !entry.Restore())
            {
                entry.Node.QueueFree();
                continue;
            }
            entry.Active = true;
            entry.Generation++;
            _rentHits++;
            return (T)(Node)entry.Node;
        }
        var node = scene.Instantiate<T>(editState);
        _rentFresh++;
        Type unsupported = null;
        if (node.GetType() == typeof(T) && node is Node2D canvas
            && !UncapturableFamilies.Contains(typeof(T))
            && Capture(canvas, out var snapshots, out unsupported))
        {
            var entry = new Entry(canvas, bucket, sceneId, snapshots);
            Entries.Add(node, entry);
            node.TreeExiting += entry.Exiting;
        }
        else if (unsupported != null && UncapturableFamilies.Add(typeof(T)))
        {
            PatchHelper.Log($"Combat VFX pool: {typeof(T).Name} not capturable (child {unsupported.Name}); family stays on fresh spawns.");
        }
        return node;
    }

    private static bool Capture(Node2D root, out Snapshot[] snapshots, out Type unsupported)
    {
        var result = new List<Snapshot>();
        Type badType = null;
        bool Visit(Node node)
        {
            if (node != root)
            {
                var type = node.GetType();
                if (!SupportedChildTypes.Contains(type))
                {
                    badType = type;
                    return false;
                }
                if (!ScriptedChildTypes.Contains(type))
                {
                    using var script = node.GetScript();
                    if (script.VariantType != Variant.Type.Nil)
                    {
                        badType = type;
                        return false;
                    }
                }
            }
            result.Add(new Snapshot((CanvasItem)node));
            for (int i = 0; i < node.GetChildCount(); i++) if (!Visit(node.GetChild(i))) return false;
            return true;
        }
        bool supported = Visit(root);
        snapshots = supported ? result.ToArray() : null;
        unsupported = badType;
        return supported;
    }

    private static void PlaybackPrefix(Node __instance, out PlaybackScope __state)
    {
        __state = default;
        if (!Entries.TryGetValue(__instance, out var entry) || !entry.Active) return;
        __state = new PlaybackScope(CurrentPlayback.Value, true);
        // ExecutionContext carries this immutable lease through the original async
        // state machine. An old continuation can never release a later rental.
        CurrentPlayback.Value = new Playback(entry, entry.Generation);
    }

    private static void PlaybackFinalizer(PlaybackScope __state)
    {
        if (__state.Changed) CurrentPlayback.Value = __state.Previous;
    }

    private static bool QueueFreePrefix(Node __0)
    {
        if (__0 == null || !Entries.TryGetValue(__0, out var entry)) return true;
        if (entry.InPool)
        {
            // Stray lifecycle callbacks (e.g. the SceneTreeTimer a pooled
            // NVfxParticleSystem scheduled in _Ready, firing long after its
            // rental ended) must never destroy a pooled spare. The pool itself
            // frees through Node.QueueFree(), which never reaches this prefix.
            return false;
        }
        var playback = CurrentPlayback.Value;
        if (playback != null && ReferenceEquals(playback.Entry, entry))
        {
            if (entry.InPool || entry.Returning) return false;
            if (playback.Generation != entry.Generation)
                return !entry.Active; // Preserve external-detach cleanup, but never destroy a later rental.
        }
        if (!entry.Active || playback == null || !ReferenceEquals(playback.Entry, entry)
            || entry.Bucket.Owner.Closed || !GodotObject.IsInstanceValid(entry.Node) || entry.Node.IsQueuedForDeletion())
        {
            entry.Active = false;
            entry.Generation++;
            return true; // External cancellation/removal is not a reusable completion.
        }
        entry.Active = false;
        entry.Returning = true;
        // Match QueueFree's end-of-frame lifetime. Do not detach in the middle of
        // particle notifications or expose the rental before its task has returned.
        Callable.From(entry.Return).CallDeferred();
        return false;
    }

    // Scripted utilities cancel their one-shot CancellationTokenSource in
    // _ExitTree (NSpriteAnimator animates via Task.Delay on it). RemoveChild
    // during pooling ran _ExitTree, so a reused tree needs a live token or its
    // animation dies on the first frame. Replace the token after RemoveChild;
    // a type that refuses the reset is demoted from the whitelists so its
    // family falls back to fresh spawns (fail-safe, never wrong visuals).
    private static void ResetScriptTransientState(Node node)
    {
        var type = node.GetType();
        if (!ScriptedChildTypes.Contains(type) || type == MegaLabelType)
            return;
        if (!CancelTokenFields.TryGetValue(type, out var field))
        {
            field = AccessTools.Field(type, "_cancelToken");
            CancelTokenFields[type] = field;
        }
        if (field == null)
            return;
        try
        {
            if (field.GetValue(node) is CancellationTokenSource cancelled)
                cancelled.Dispose();
            field.SetValue(node, new CancellationTokenSource());
        }
        catch
        {
            SupportedChildTypes.Remove(type);
            ScriptedChildTypes.Remove(type);
            PatchHelper.Log($"Combat VFX pool: {type.Name} cannot be reset for reuse; pooled trees drop it and affected families stay on fresh spawns.");
        }
    }

    private static bool TintPrefix(NShivThrowVfx __instance, Color __0)
    {
        if (!Entries.TryGetValue(__instance, out var entry) || !entry.Active) return true;
        var particles = entry.Bucket.Contract.TintedParticles(__instance);
        if (entry.Materials == null || entry.Materials.Length != particles.Count)
            entry.Materials = new ParticleProcessMaterial[particles.Count];
        for (int i = 0; i < particles.Count; i++)
        {
            var particle = particles[i];
            var material = particle.ProcessMaterial;
            if (entry.Materials[i] == null || !GodotObject.IsInstanceValid(entry.Materials[i])
                || material != entry.Materials[i])
            {
                // Keep the original per-particle material isolation, but duplicate
                // only when the instance does not already own this material.
                entry.Materials[i] = (ParticleProcessMaterial)material.Duplicate();
                particle.ProcessMaterial = entry.Materials[i];
            }
            entry.Materials[i].Set(ColorParameter, __0);
        }
        return false;
    }

    private sealed class Contract
    {
        internal readonly Type Type;
        internal readonly int Capacity;
        internal readonly MethodInfo Play;
        internal readonly MethodInfo[] Factories;
        internal readonly MethodInfo[] GuardedMethods;
        internal readonly AccessTools.FieldRef<NShivThrowVfx, Godot.Collections.Array<GpuParticles2D>> TintedParticles;
        internal readonly FieldInfo TransientField;
        // Every instance CancellationTokenSource the family root holds
        // (_cancelToken / _cts / ...). _ExitTree cancels them when the pool
        // detaches the tree; a reused rental needs live tokens or gates like
        // NCardFlyVfx.PlayAnim's IsCancellationRequested die on frame one and
        // the animated card/Effect stays wherever it was.
        internal readonly FieldInfo[] CancelTokens;

        internal Contract(Type type, string playback, int capacity, bool verifyFactoryIL = true)
        {
            Type = type;
            Capacity = capacity;
            Play = AccessTools.DeclaredMethod(type, playback) ?? throw new MissingMethodException(type.FullName, playback);
            if (Play.ReturnType != typeof(System.Threading.Tasks.Task)) throw new InvalidOperationException($"Unknown VFX playback contract: {Play}");
            // IL verification is skipped for the generic pool (fast reflection
            // enumeration over ~600 families); FactoryTranspiler no-ops on Create
            // bodies without a self-type Instantiate call, so the name convention
            // plus namespace filter is sufficient there.
            Factories = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "Create" && (!verifyFactoryIL || PatchProcessor.GetOriginalInstructions(method).Any(instruction => IsInstantiation(instruction, type)))).ToArray();
            if (Factories.Length == 0) throw new MissingMethodException(type.FullName, "Create/Instantiate");
            GuardedMethods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => method.Name is "Create" or "_Ready" or "_ExitTree" or "ApplyTint" || method == Play).ToArray();
            if (type == typeof(NShivThrowVfx))
                TintedParticles = AccessTools.FieldRefAccess<NShivThrowVfx, Godot.Collections.Array<GpuParticles2D>>("_modulateParticles");
            TransientField = AccessTools.Field(type, type == typeof(NDamageNumVfx) ? "_tween" : type == typeof(NHitSparkVfx) ? "_creatureNode" : "_cts");
            CancelTokens = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(field => field.FieldType == typeof(CancellationTokenSource)).ToArray();
        }

        internal bool AllowsReuse() => GuardedMethods.All(method => Harmony.GetPatchInfo(method)?.Owners.All(owner => owner == _harmony.Id) != false);
    }

    private sealed class RoomPool
    {
        private readonly Dictionary<Type, Bucket> _buckets = new();
        internal bool Closed;
        internal RoomPool(NCombatRoom room) { room.TreeExiting += Close; }
        internal Bucket GetBucket(Contract contract)
        {
            if (!_buckets.TryGetValue(contract.Type, out var bucket))
            {
                bucket = new Bucket(this, contract);
                _buckets.Add(contract.Type, bucket);
            }
            return bucket;
        }
        private void Close()
        {
            Closed = true;
            PatchHelper.Log($"Combat VFX pool room closed: buckets={_buckets.Count} cumulative hits={_rentHits} (spares={_spareHits}) fresh={_rentFresh}.");
            foreach (var bucket in _buckets.Values)
                while (bucket.Free.TryPop(out var entry))
                    if (GodotObject.IsInstanceValid(entry.Node)) entry.Node.QueueFree();
            _buckets.Clear();
        }
    }

    private sealed class Bucket
    {
        internal readonly RoomPool Owner;
        internal readonly Contract Contract;
        internal readonly bool Allowed;
        internal readonly Stack<Entry> Free;
        internal Bucket(RoomPool owner, Contract contract)
        {
            Owner = owner;
            Contract = contract;
            Allowed = contract.AllowsReuse();
            Free = new Stack<Entry>(contract.Capacity);
        }
    }

    private sealed class Entry
    {
        internal readonly Node2D Node;
        internal readonly Bucket Bucket;
        internal readonly ulong SceneId;
        private readonly Snapshot[] _snapshots;
        internal ParticleProcessMaterial[] Materials;
        internal bool Active = true;
        internal bool Returning;
        internal bool InPool;
        internal long Generation = 1;
        internal Entry(Node2D node, Bucket bucket, ulong sceneId, Snapshot[] snapshots)
        {
            Node = node; Bucket = bucket; SceneId = sceneId; _snapshots = snapshots;
        }
        internal bool Restore()
        {
            foreach (var snapshot in _snapshots)
                if (!GodotObject.IsInstanceValid(snapshot.Node) || snapshot.Node.GetChildCount() != snapshot.ChildCount) return false;
            foreach (var snapshot in _snapshots) snapshot.Restore();
            return true;
        }
        internal void Exiting()
        {
            if (Returning) return;
            Active = false;
            Generation++;
            // External tree-exit mid-play — e.g. the combat room closes while a
            // NCardFlyVfx is still flying its card home (it is parented under
            // the creature's VfxContainer). The family's own _ExitTree just
            // cancelled its tokens, and animations gated on them freeze
            // forever in that state: PlayAnim's time stops advancing, the
            // animated card is never freed and stays on screen blocking the
            // next room. Hand back live tokens so the animation runs to
            // completion and cleans itself up; the node itself is out of the
            // tree and renders nothing.
            ResetRootTransientTokens();
            if (_snapshots == null) return;
            foreach (var snapshot in _snapshots)
                ResetScriptTransientState(snapshot.Node);
        }

        // Warm spares already ran _Ready during the loading screen, and Godot
        // never re-fires it on its own (ready_first only resets via
        // RequestReady). Arm every node for re-entry before the spare is
        // handed out: the family's own _Ready then re-initializes the tree on
        // AddChild exactly as it would for a fresh Instantiate.
        internal void PrepareForReentry()
        {
            ResetRootTransientTokens();
            if (_snapshots == null) return;
            foreach (var snapshot in _snapshots)
            {
                snapshot.Node.RequestReady();
                ResetScriptTransientState(snapshot.Node);
            }
        }

        // The family root's own cancellation tokens (NCardFlyVfx._cancelToken
        // and friends): cancelled by its _ExitTree when Return detached the
        // tree, so a reused rental must get live ones back. A token that
        // refuses the reflection write demotes the whole family out of the
        // pool — fresh spawns are always correct, dead animations are not.
        private void ResetRootTransientTokens()
        {
            foreach (var field in Bucket.Contract.CancelTokens)
            {
                try
                {
                    if (field.GetValue(Node) is CancellationTokenSource cancelled)
                        cancelled.Dispose();
                    field.SetValue(Node, new CancellationTokenSource());
                }
                catch
                {
                    if (Contracts.Remove(Bucket.Contract.Type))
                        PatchHelper.Log($"Combat VFX pool: {Bucket.Contract.Type.Name}.{field.Name} cannot be reset for reuse; family returns to fresh spawns.");
                    return;
                }
            }
        }
        internal void Return()
        {
            if (!GodotObject.IsInstanceValid(Node)) return;
            if (!Returning || Bucket.Owner.Closed || Node.IsQueuedForDeletion() || Bucket.Free.Count >= Bucket.Contract.Capacity)
            {
                Returning = false;
                Node.QueueFree();
                return;
            }
            Node.GetParent()?.RemoveChild(Node);
            ResetRootTransientTokens();
            if (_snapshots != null)
            {
                foreach (var snapshot in _snapshots)
                    if (snapshot.Node is GpuParticles2D particles) particles.Emitting = false;
                foreach (var snapshot in _snapshots)
                    ResetScriptTransientState(snapshot.Node);
            }
            Returning = false;
            var transient = Bucket.Contract.TransientField;
            if (transient?.GetValue(Node) is CancellationTokenSource cancellation) cancellation.Dispose();
            transient?.SetValue(Node, null);
            InPool = true;
            Bucket.Free.Push(this);
        }
    }

    private readonly struct Snapshot
    {
        internal readonly CanvasItem Node;
        internal readonly int ChildCount;
        private readonly Transform2D _transform;
        private readonly Vector2 _controlScale;
        private readonly Color _modulate, _selfModulate;
        private readonly bool _visible, _emitting;
        private readonly Material _material;
        internal Snapshot(CanvasItem node)
        {
            Node = node;
            ChildCount = node.GetChildCount();
            _transform = node is Node2D spatial ? spatial.Transform : Transform2D.Identity;
            _controlScale = node is Control control ? control.Scale : Vector2.One;
            _modulate = node.Modulate; _selfModulate = node.SelfModulate; _visible = node.Visible;
            _emitting = node is GpuParticles2D gpu ? gpu.Emitting : node is CpuParticles2D cpu && cpu.Emitting;
            _material = node.Material;
        }
        internal void Restore()
        {
            if (Node is Node2D spatial) spatial.Transform = _transform;
            else if (Node is Control control) control.Scale = _controlScale;
            Node.Modulate = _modulate; Node.SelfModulate = _selfModulate; Node.Visible = _visible;
            Node.Material = _material;
            if (Node is GpuParticles2D gpu) gpu.Emitting = _emitting;
            else if (Node is CpuParticles2D cpu) cpu.Emitting = _emitting;
            Node.RequestReady();
        }
    }

    private sealed record Playback(Entry Entry, long Generation);
    private readonly record struct PlaybackScope(Playback Previous, bool Changed);
}
