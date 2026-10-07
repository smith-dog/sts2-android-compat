using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace MegaCrit.Sts2.Core.Nodes.Vfx;

// Synthetic particle-only shape; all nodes, packing, callbacks and leases are native.
public partial class NBigSlashVfx : Node2D
{
    public static PackedScene Scene;
    [Export] private Godot.Collections.Array<GpuParticles2D> _slashParticles = new();
    [Export] private Godot.Collections.Array<GpuParticles2D> _modulateParticles = new();
    private CancellationTokenSource _cts;
    public TaskCompletionSource<bool> Finish;
    public TaskCompletionSource<bool> Late;
    public int PlayCount;
    public CancellationToken Token => _cts.Token;

    public void AddParticle(GpuParticles2D particle)
    {
        AddChild(particle);
        particle.Owner = this;
        _slashParticles.Add(particle);
        _modulateParticles.Add(particle);
    }
    public static NBigSlashVfx Create(Vector2 position, bool facingRight, Color tint)
    {
        var effect = Scene.Instantiate<NBigSlashVfx>(PackedScene.GenEditState.Disabled);
        effect.Position = position;
        effect.Scale = new Vector2(facingRight ? 1 : -1, 1);
        effect.ModulateParticles(tint);
        return effect;
    }
    private void ModulateParticles(Color tint)
    {
        foreach (var particle in _modulateParticles) particle.SelfModulate = tint;
    }
    public override void _Ready()
    {
        Finish = new();
        Late = new();
        _ = PlaySequence();
    }
    private async Task PlaySequence()
    {
        PlayCount++;
        _cts = new();
        foreach (var particle in _slashParticles) particle.Restart();
        _ = DelayedRelease(Late.Task);
        await Finish.Task;
        Scale = Vector2.One * 9;
        Modulate = Colors.Red;
        foreach (var particle in _slashParticles)
        {
            particle.Position = new Vector2(90, 80);
            particle.Visible = false;
            particle.Modulate = Colors.Black;
        }
        this.QueueFreeSafely();
    }
    private async Task DelayedRelease(Task completion)
    {
        await completion;
        this.QueueFreeSafely();
    }
    public override void _ExitTree() => _cts?.Cancel();
}
