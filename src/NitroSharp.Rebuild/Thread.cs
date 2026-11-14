using System;
using System.Diagnostics;
using System.Collections.Generic;
using NitroSharp.Input;
using NitroSharp.Graphics;
using NitroSharp.NsScript;
using NitroSharp.NsScript.VM;

namespace NitroSharp;

internal sealed class Thread : Entity, IVmThread
{
    private NsScriptThreadState _vmState;
    private WaitOperation? _waitOperation;
    private bool _isPaused;
    private readonly Stack<CallScopeEntity> _callScopes = new();
    private int _nextCallScope;
    internal const int ScriptSlicesPerFrame = 256;

    public Thread(EntityName name, Entity? parent, NsScriptThreadState vmState, bool isMain)
        : base(name, parent)
    {
        _vmState = vmState;
        IsMain = isMain;
    }

    public Entity EntityContext => _callScopes.TryPeek(out CallScopeEntity? scope) ? scope : this;

    public void EnterCallScope()
    {
        var scope = World.AddEntity(new CallScopeEntity(EntityName.Parse("call-scope-" + ++_nextCallScope), EntityContext));
        _callScopes.Push(scope);
    }

    public void LeaveCallScope()
    {
        if (_callScopes.TryPop(out CallScopeEntity? scope)) scope.Retire();
    }

    public bool IsMain { get; }
    public ref NsScriptThreadState State => ref _vmState;

    public override void Update(GameContext ctx)
    {
        if (!_isPaused && IsEnabled && !_vmState.DoneExecuting && HasWaitExpired(ctx))
        {
            _waitOperation = null;
            AdvanceScript(ctx.VM, ctx.Builtins);
        }
    }

    internal int AdvanceScript(NsScriptVM vm, BuiltInFunctions builtins, int sliceBudget = ScriptSlicesPerFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sliceBudget);
        if (_waitOperation is { Condition: WaitCondition.FrameSequenceCompleted } frameWait
            && FrameSequencesCompleted(frameWait)) _waitOperation = null;
        int slices = 0;
        // Calls and builtin dispatch return Ok; finish polling before this input frame expires.
        while (slices < sliceBudget && IsEnabled && !_isPaused && _waitOperation is null && !_vmState.DoneExecuting)
        {
            slices++;
            if (vm.Tick(ref _vmState, builtins) == NsScriptVM.TickResult.Yield) break;
        }
        return slices;
    }

    public override bool Request(NsEntityAction action)
    {
        switch (action)
        {
            case NsEntityAction.Stop:
            case NsEntityAction.Pause:
                _isPaused = true;
                return true;
            case NsEntityAction.Start:
            case NsEntityAction.Resume:
                _isPaused = false;
                return true;
            default:
                return base.Request(action);
        }
    }

    public void Restart(NsScriptThreadState state)
    {
        Debug.Assert(_vmState.DoneExecuting);
        _vmState = state;
        _waitOperation = null;
        _isPaused = false;
    }

    public void Wait(WaitOperation waitOperation)
    {
        Debug.Assert(_waitOperation is null, "Thread is already suspended.");
        _waitOperation = waitOperation;
    }

    internal void WaitForFrameSequence(in EntityQuery query)
    {
        var targets = new List<FrameSequence>();
        foreach (Entity entity in World.Query(query))
            foreach (Entity node in entity.DescendantsAndSelf())
                if (node is FrameSequence sequence && sequence.IsAnimationActive(AnimationKind.FrameSequence))
                    targets.Add(sequence);
        if (targets.Count > 0)
            Wait(new WaitOperation(WaitCondition.FrameSequenceCompleted, query, null) { FrameSequences = targets.ToArray() });
    }

    private static bool FrameSequencesCompleted(in WaitOperation wait)
    {
        foreach (FrameSequence sequence in wait.FrameSequences ?? [])
            if (sequence.IsAnimationActive(AnimationKind.FrameSequence)) return false;
        return true;
    }

    internal bool InterruptFrameSequenceWait(InputContext input)
    {
        if (_waitOperation is not { Condition: WaitCondition.FrameSequenceCompleted, FrameSequences: { } targets })
            return false;
        bool active = false;
        foreach (FrameSequence sequence in targets)
            active |= sequence.IsAnimationActive(AnimationKind.FrameSequence);
        if (!active || !input.ConsumeFrameSequenceInterrupt()) return false;
        foreach (FrameSequence sequence in targets)
            if (sequence.IsAnimationActive(AnimationKind.FrameSequence)) sequence.Request(NsEntityAction.Stop);
        return true;
    }

    private bool HasWaitExpired(GameContext ctx)
    {
        Stopwatch clock = ctx.Clock;
        if (_waitOperation is not { } waitOperation) { return true; }
        if (waitOperation.Deadline is { } deadline && clock.Elapsed >= deadline)
        {
            return true;
        }

        InputContext input = ctx.InputContext;
        InterruptFrameSequenceWait(input);
        return waitOperation switch
        {
            { Condition: WaitCondition.UserInput } => input.Consume(InputAction.Advance),
            { Condition: WaitCondition.EntityIdle, EntityQuery: { } query } => isTextIdle(query),
            { Condition: WaitCondition.FrameSequenceCompleted } => FrameSequencesCompleted(waitOperation),
            {
                Condition: WaitCondition.MoveCompleted or WaitCondition.FadeCompleted or WaitCondition.ZoomCompleted or WaitCondition.BezierMoveCompleted or WaitCondition.RotateCompleted or WaitCondition.TransitionCompleted,
                EntityQuery: { } query
            } => HasAnimationCompleted(query, waitOperation.Condition),
            _ => false
        };

        bool isTextIdle(in EntityQuery query)
        {
            foreach (Entity entity in World.Query(query))
            {
                if (entity is DialoguePage { IsIdle: false }) { return false; }
            }
            return true;
        }

    }

    internal bool HasAnimationCompleted(in EntityQuery query, WaitCondition condition)
    {
        AnimationKind animationKind = condition switch
        {
            WaitCondition.FadeCompleted => AnimationKind.Fade,
            WaitCondition.ZoomCompleted => AnimationKind.Zoom,
            WaitCondition.MoveCompleted => AnimationKind.Move,
            WaitCondition.BezierMoveCompleted => AnimationKind.BezierMove,
            WaitCondition.RotateCompleted => AnimationKind.Rotate,
            WaitCondition.TransitionCompleted => AnimationKind.Transition,
            WaitCondition.FrameSequenceCompleted => AnimationKind.FrameSequence,
            _ => throw ThrowHelper.UnexpectedValueOf<WaitCondition>()
        };

        foreach (Entity entity in World.Query(query))
        {
            foreach (Entity node in entity.DescendantsAndSelf())
                if (node.IsAnimationActive(animationKind)) { return false; }
        }

        return true;
    }


    internal enum WaitCondition
    {
        Timeout,
        UserInput,
        MoveCompleted,
        ZoomCompleted,
        RotateCompleted,
        FadeCompleted,
        BezierMoveCompleted,
        TransitionCompleted,
        EntityIdle,
        FrameSequenceCompleted,
    }

    internal record struct WaitOperation(WaitCondition Condition, EntityQuery? EntityQuery, TimeSpan? Deadline)
    {
        public FrameSequence[]? FrameSequences { get; init; }
        public static WaitOperation Suspend(TimeSpan deadline) => new(WaitCondition.Timeout, null, deadline);
        public static WaitOperation UserInput(TimeSpan? deadline) => new(WaitCondition.UserInput, null, deadline);

    }
}
