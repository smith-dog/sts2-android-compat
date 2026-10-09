using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace MegaCrit.Sts2.Core.Nodes.Vfx;

// Synthetic particle-only shape, with the fire factory's independent tint/scale.
public partial class NFireBurstVfx : Node2D
{
    public static PackedScene Scene;
    [Export] private Godot.Collections.Array<GpuParticles2D> _particles = new();
    [Export] private Godot.Collections.Array<GpuParticles2D> _modulateParticles = new();
    private CancellationTokenSource _cts;
    public TaskCompletionSource<bool> Finish;
    public int PlayCount;
    public CancellationToken Token => _cts.Token;

    public void AddParticle(GpuParticles2D particle)
    {
        AddChild(particle);
        particle.Owner = this;
        _particles.Add(particle);
        _modulateParticles.Add(particle);
    }
    public static NFireBurstVfx Create(Vector2 position, float scale, Color tint)
    {
        var effect = Scene.Instantiate<NFireBurstVfx>(PackedScene.GenEditState.Disabled);
        effect.Position = position;
        effect.ApplyTint(tint);
        effect.Scale = Vector2.One * scale;
        return effect;
    }
    public void ApplyTint(Color tint)
    {
        foreach (var particle in _modulateParticles) particle.SelfModulate = tint;
    }
    public override void _Ready()
    {
        Finish = new();
        _ = PlaySequence();
    }
    private async Task PlaySequence()
    {
        PlayCount++;
        _cts = new();
        foreach (var particle in _particles) particle.Restart();
        await Finish.Task;
        Scale = Vector2.One * 7;
        Modulate = Colors.Green;
        foreach (var particle in _particles)
        {
            particle.Position = new Vector2(-70, 80);
            particle.Visible = false;
            particle.Modulate = Colors.Black;
        }
        this.QueueFreeSafely();
    }
    public override void _ExitTree() => _cts?.Cancel();
}
