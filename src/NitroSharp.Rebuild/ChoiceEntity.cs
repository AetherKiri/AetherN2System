using System;
using System.Collections.Generic;
using NitroSharp.Graphics;
using NitroSharp.NsScript;

namespace NitroSharp;

internal sealed class ChoiceEntity(EntityName name, Entity? parent, bool preserveOpacity = false) : Entity(name, parent)
{
    private readonly Dictionary<NsFocusDirection, ChoiceEntity> _nextFocus = [];

    public bool IsClickable { get; private set; } = true;
    public bool PreserveOpacity { get; } = preserveOpacity;
    public bool PointerInside { get; set; }
    public bool IsAttached => Parent is { } parent && ReferenceEquals(parent.TryGetChild(Name), this);

    public override bool Request(NsEntityAction action)
    {
        switch (action)
        {
            case NsEntityAction.Disable:
                IsClickable = false;
                ShowState(MouseState.Normal);
                return true;
            case NsEntityAction.Enable:
                IsClickable = true;
                return true;
            default:
                return base.Request(action);
        }
    }

    public void SetNextFocus(NsFocusDirection direction, ChoiceEntity choice) => _nextFocus[direction] = choice;

    public ChoiceEntity? GetNextFocus(NsFocusDirection direction)
        => _nextFocus.TryGetValue(direction, out ChoiceEntity? choice)
            && choice.IsAttached && choice.IsClickable ? choice : null;

    public RenderItem? GetHitTarget()
    {
        if (TryGetChild(EntityName.Parse("MouseUsual")) is not { } usual) return null;
        // N2 uses the first direct usual visual, rather than every nested visual.
        foreach (Entity child in usual.Children)
            if (child is RenderItem visual) return visual;
        return null;
    }

    public void ShowState(MouseState state)
    {
        bool hasOver = HasVisual("MouseOver");
        bool hasClick = HasVisual("MouseClick");
        foreach (Entity child in Children)
        {
            if (child is not MouseStateEntity group) continue;
            group.IsVisible = group.Name.Value switch
            {
                "MouseUsual" => state == MouseState.Normal
                    || state == MouseState.Over && !hasOver
                    || state == MouseState.Down && !hasClick,
                "MouseOver" => state == MouseState.Over,
                "MouseClick" => state == MouseState.Down,
                _ => false
            };
        }

        if (!PreserveOpacity && (state is MouseState.Over or MouseState.Down))
            SetGroupOpacity(state == MouseState.Over ? "MouseOver" : "MouseClick", 1);
    }

    private void SetGroupOpacity(string name, float opacity)
    {
        if (TryGetChild(EntityName.Parse(name)) is not { } group) return;
        foreach (Entity entity in group.DescendantsAndSelf())
            if (entity is RenderItem visual)
                visual.Fade(opacity, TimeSpan.Zero, NsEaseFunction.Linear);
    }

    private bool HasVisual(string name)
    {
        if (TryGetChild(EntityName.Parse(name)) is not { } group) return false;
        foreach (Entity child in group.Children)
            if (child is RenderItem) return true;
        return false;
    }

    public static ChoiceEntity? FindOwner(Entity entity)
    {
        for (Entity? current = entity; current is not null; current = current.Parent)
            if (current is ChoiceEntity choice) return choice;
        return null;
    }
}

// State visibility is independent of script-owned alpha and hidden hit sprites.
internal sealed class MouseStateEntity(EntityName name, ChoiceEntity parent) : Entity(name, parent)
{
    public ChoiceEntity Choice { get; } = parent;
    public bool IsVisible { get; internal set; } = name.Value == "MouseUsual";
}
