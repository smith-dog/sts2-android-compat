using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;

public partial class Main
{
    private async Task CheckExpandedVfxReuse()
    {
        var room = new NCombatRoom();
        NCombatRoom.Instance = room;
        AddChild(room);
        using var material = new ParticleProcessMaterial { Color = Colors.White };
        var slashTemplate = new NBigSlashVfx();
        slashTemplate.AddParticle(new GpuParticles2D
        {
            Name = "Particle", Position = new Vector2(10, 20), Emitting = false,
            OneShot = true, Amount = 1, Lifetime = 10, ProcessMaterial = material
        });
        using var slashScene = Pack(slashTemplate);
        NBigSlashVfx.Scene = slashScene;
        var fireTemplate = new NFireBurstVfx();
        fireTemplate.AddParticle(new GpuParticles2D
        {
            Name = "Particle", Position = new Vector2(-12, 5), Emitting = false,
            OneShot = true, Amount = 1, Lifetime = 10, ProcessMaterial = material
        });
        using var fireScene = Pack(fireTemplate);
        NFireBurstVfx.Scene = fireScene;

        var slash = NBigSlashVfx.Create(Vector2.One, false, Colors.Red);
        room.AddChild(slash);
        var oldToken = slash.Token;
        var oldLate = slash.Late;
        slash.Finish.SetResult(true);
        await Frame(); await Frame();
        Require(GodotObject.IsInstanceValid(slash) && !slash.IsInsideTree()
            && !slash.GetNode<GpuParticles2D>("Particle").Emitting && oldToken.IsCancellationRequested,
            "A completed slash must retain an idle, non-emitting tree and close its playback token.");
        var nextSlash = NBigSlashVfx.Create(new Vector2(33, 42), true, Colors.Blue);
        Require(ReferenceEquals(slash, nextSlash) && nextSlash.Scale == Vector2.One && nextSlash.Modulate == Colors.White,
            "Slash re-rent must restore the prior playback's root state before applying new facing/position.");
        room.AddChild(nextSlash);
        var nextParticle = nextSlash.GetNode<GpuParticles2D>("Particle");
        Require(nextSlash.Position == new Vector2(33, 42) && nextSlash.PlayCount == 2 && !nextSlash.Token.IsCancellationRequested
            && nextParticle.Position == new Vector2(10, 20) && nextParticle.Visible
            && nextParticle.Modulate == Colors.White && nextParticle.SelfModulate == Colors.Blue && nextParticle.Emitting,
            "Slash reuse must replay Ready/particles with a fresh token, restored child geometry and the new tint.");
        oldLate.SetResult(true);
        await Frame(); await Frame();
        Require(nextSlash.IsInsideTree() && !nextSlash.IsQueuedForDeletion(),
            "A stale slash continuation must not destroy its later rental.");
        room.RemoveChild(nextSlash);
        nextSlash.Finish.SetResult(true);
        await Frame(); await Frame();
        Require(!GodotObject.IsInstanceValid(nextSlash), "An externally removed slash must not become a reusable completion.");

        var fire = NFireBurstVfx.Create(Vector2.Zero, 3, Colors.Red);
        room.AddChild(fire);
        fire.Finish.SetResult(true);
        await Frame(); await Frame();
        Require(GodotObject.IsInstanceValid(fire) && !fire.IsInsideTree(), "A completed fire burst must be reusable.");
        var fireBurst = new List<NFireBurstVfx>();
        for (int i = 0; i < 3; i++)
        {
            var effect = NFireBurstVfx.Create(new Vector2(20 + i, 30), 2, Colors.Blue);
            room.AddChild(effect);
            fireBurst.Add(effect);
        }
        var fireParticle = fireBurst[0].GetNode<GpuParticles2D>("Particle");
        Require(ReferenceEquals(fire, fireBurst[0]) && fireBurst[0].PlayCount == 2
            && fireBurst[0].Scale == Vector2.One * 2 && fireBurst[0].Modulate == Colors.White
            && fireParticle.Position == new Vector2(-12, 5) && fireParticle.Visible && fireParticle.Emitting
            && fireParticle.Modulate == Colors.White && fireParticle.SelfModulate == Colors.Blue,
            "Fire reuse must preserve the new factory scale/tint and restart a clean particle tree.");
        Require(fireBurst.Distinct().Count() == 3 && fireBurst.All(effect => effect.IsInsideTree() && !effect.Token.IsCancellationRequested),
            "Three simultaneous fire bursts must stay complete and distinct even when idle capacity is two.");
        foreach (var effect in fireBurst) effect.Finish.SetResult(true);
        await Frame(); await Frame(); await Frame();
        Require(fireBurst.Count(GodotObject.IsInstanceValid) == 2,
            "The new fire family must retain only two idle instances, never shrink active effect output.");
        room.QueueFree();
        await Frame(); await Frame(); await Frame();
        Require(fireBurst.All(effect => !GodotObject.IsInstanceValid(effect)), "Room exit must free the expanded idle family.");

        using var childScript = new GDScript { SourceCode = "extends GPUParticles2D\nvar mod_state = 1\n" };
        Require(childScript.Reload() == Error.Ok, "Synthetic foreign particle script must load.");
        var scriptedTemplate = new NFireBurstVfx();
        var scriptedParticle = new GpuParticles2D { Name = "Particle", Emitting = false, ProcessMaterial = material };
        scriptedParticle.SetScript(childScript);
        scriptedTemplate.AddParticle(scriptedParticle);
        using var scriptedScene = Pack(scriptedTemplate);
        NFireBurstVfx.Scene = scriptedScene;
        var scriptedRoom = new NCombatRoom();
        NCombatRoom.Instance = scriptedRoom;
        AddChild(scriptedRoom);
        var scriptedEffect = NFireBurstVfx.Create(Vector2.Zero, 1, Colors.White);
        scriptedRoom.AddChild(scriptedEffect);
        scriptedEffect.Finish.SetResult(true);
        await Frame(); await Frame();
        Require(!GodotObject.IsInstanceValid(scriptedEffect), "Unknown child scripts must keep original allocation/destruction, not snapshot their custom state.");
        scriptedRoom.QueueFree();
        await Frame();

        var foreign = new Harmony("frame-tests.foreign-slash-tint");
        var tint = AccessTools.DeclaredMethod(typeof(NBigSlashVfx), "ModulateParticles");
        foreign.Patch(tint, prefix: new HarmonyMethod(typeof(Main), nameof(ForeignReady)));
        try
        {
            var modifiedRoom = new NCombatRoom();
            NCombatRoom.Instance = modifiedRoom;
            AddChild(modifiedRoom);
            var effect = NBigSlashVfx.Create(Vector2.Zero, true, Colors.White);
            modifiedRoom.AddChild(effect);
            effect.Finish.SetResult(true);
            await Frame(); await Frame();
            Require(!GodotObject.IsInstanceValid(effect), "Foreign slash tint hooks must opt out of reuse just like factory/lifecycle hooks.");
            modifiedRoom.QueueFree();
            await Frame();
        }
        finally { foreign.Unpatch(tint, HarmonyPatchType.All, foreign.Id); }
        NCombatRoom.Instance = null;
        GD.Print("PASS: expanded slash/fire reuse, native particle replay and reset, stale/cancelled leases, two-slot overflow, script/mod opt-out and room teardown.");
    }
}
