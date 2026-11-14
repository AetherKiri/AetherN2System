using System.Collections.Generic;
using NitroSharp.Media;

namespace NitroSharp;

// Chapter and scene calls own a namespace while sharing the process renderer and audio.
internal sealed class CallScopeEntity(EntityName name, Entity parent) : Entity(name, parent)
{
    private bool _retired;

    public void Retire()
    {
        _retired = true;
        Prune(this);
    }

    public override void Update(GameContext ctx)
    {
        if (_retired && !IsPendingDestruction) Prune(this);
    }

    private bool Prune(Entity entity)
    {
        if (entity.IsPendingDestruction) return false;
        // Explicit locks and playing media survive a completed call, as in the original host.
        if (entity.IsLocked || entity is AudioEntity { IsPlaying: true }) return true;
        var children = new List<Entity>();
        foreach (Entity child in entity.Children) children.Add(child);
        bool retained = false;
        foreach (Entity child in children) retained |= Prune(child);
        if (retained) return true;
        World.DestroyEntity(entity);
        return false;
    }
}
