namespace NitroSharp.Graphics;

internal sealed class DialogueBox : ConstraintBox
{
    private readonly DesignSize _size;

    public DialogueBox(EntityName name, Entity? parent, int priority, DesignSize size, bool inheritTransform)
        : base(name, parent, priority)
    {
        _size = size;
        IsContainer = inheritTransform;
    }

    public override DesignSize GetSize(RenderContext ctx) => _size;
    public override bool IsContainer { get; }
}
