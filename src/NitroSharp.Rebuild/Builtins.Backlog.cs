using System.Collections.Generic;
using NitroSharp.Graphics;
using NitroSharp.NsScript;
using NitroSharp.Text;

namespace NitroSharp;

internal sealed partial class Builtins
{
    internal const int MaximumBacklogEntries = 512;
    private readonly List<BacklogEntry> _backlog = [];
    private long _backlogOpeningFrame = -1;
    private bool _backlogEnabled = true;

    internal bool HasBacklog => _backlog.Count != 0;
    internal IReadOnlyList<BacklogEntry> Backlog => _backlog;
    public override bool IsBacklogEnabled => _backlogEnabled;
    public override void SetBacklogEnabled(bool enabled) => _backlogEnabled = enabled;
    public override void ClearBacklog() => _backlog.Clear();
    public override void SetBacklog(string text) => AddBacklogText(text, null);
    internal void SuppressBacklogOpeningInput(long frame) => _backlogOpeningFrame = frame;
    internal bool IsBacklogOpeningFrame => _ctx.FrameStamp.FrameId == _backlogOpeningFrame;

    internal void AddBacklogText(string text, VoiceSegment? voice)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8192) return;
        if (_backlog.Count == MaximumBacklogEntries) _backlog.RemoveAt(0);
        _backlog.Add(new BacklogEntry(text.Trim(), voice));
    }

    internal void RestoreBacklog(BacklogEntry[] entries)
    {
        _backlog.Clear();
        _backlog.AddRange(entries);
        _backlogEnabled = true;
    }

    public override void CreateBacklog(in EntityPath path, int priority)
    {
        if (ResolveEntityPath(path, out EntityName name, out Entity? parent))
            _world.AddEntity(new BacklogView(name, parent, priority, _backlog.ToArray()));
    }
}
