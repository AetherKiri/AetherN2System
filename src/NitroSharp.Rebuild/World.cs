using System;
using System.Collections.Generic;
using NitroSharp.Common;
using NitroSharp.NsScript;
using NitroSharp.Input;
using NitroSharp.NsScript.VM;
using System.IO;

namespace NitroSharp;

internal sealed class World : EntityScope
{
    private readonly List<Process> _processes = [];
    private readonly List<Entity> _newEntities = [];
    private readonly List<Entity> _deletedEntities = [];
    private Process? _systemMenu;
    private NsScriptThreadState? _pendingReset;

    public World(Process mainProcess, Thread mainThread)
    {
        MainProcess = CurrentProcess = mainProcess;
        AddEntity(mainProcess);
        AddEntity(mainThread);
    }

    public Process CurrentProcess { get; private set; }
    public Process MainProcess { get; private set; }

    public T AddEntity<T>(T entity) where T : Entity
    {
        if (entity.Parent is { } parent)
        {
            if (parent.TryGetChild(entity.Name) is { } existingEntity)
            {
                DestroyEntity(existingEntity);
            }
            ((EntityInternal)parent).AddChild(entity);
        }

        _newEntities.Add(entity);
        if (entity is Process process)
        {
            _processes.Add(process);
        }

        entity.World = this;
        return entity;
    }

    public void DestroyEntity(Entity entity)
    {
        if (entity.IsPendingDestruction) return;
        entity.IsPendingDestruction = true;
        foreach (Entity descendant in entity.DescendantsAndSelf())
        {
            descendant.IsPendingDestruction = true;
            descendant.Disable();
            descendant.Process.Aliases.Remove(descendant);
        }
        if (entity.Parent is { } parent)
        {
            ((EntityInternal)parent).RemoveChild(entity);
        }
        _deletedEntities.Add(entity);
    }

    private void CommitDestroyEntity(Entity entity)
    {
        foreach (Entity child in entity.Children)
        {
            CommitDestroyEntity(child);
        }

        entity.Dispose();
        if (entity is Process process)
        {
            _processes.Remove(process);
        }
    }

    public void DisposeAll()
    {
        foreach (Process process in _processes.ToArray()) CommitDestroyEntity(process);
        _newEntities.Clear();
        _deletedEntities.Clear();
    }

    public void BeginFrame()
    {
        foreach (Entity entity in _deletedEntities)
        {
            CommitDestroyEntity(entity);
        }

        foreach (Entity entity in _newEntities)
        {
            if (!entity.IsPendingDestruction) entity.Enable();
        }

        _deletedEntities.Clear();
        _newEntities.Clear();
    }

    public Entity? Get(in EntityPath entityPath)
    {
        if (!TryResolvePath(entityPath, out EntityName name, out Entity? parent))
        {
            return null;
        }

        parent ??= CurrentProcess.CurrentThread.EntityContext;
        return parent.TryGetChild(name);
    }

    public bool IsValidAlias(EntityAlias alias)
    {
        var path = EntityPath.Parse(alias.Value);
        return IsValidPath(path);
    }

    public bool IsValidPath(in EntityPath path) => TryResolvePath(path, out _, out _);

    public bool TryResolvePath(in EntityPath path, out EntityName name, out Entity? parent)
    {
        parent = null;
        name = default;

        if (path.Parts is [var singlePart])
        {
            name = EntityName.Parse(singlePart.Value);
            parent = CurrentProcess.CurrentThread.EntityContext;
            return true;
        }

        ReadOnlySpan<EntityPathPart> remainingParts = path.Parts;
        EntityScope scope = CurrentProcess.CurrentThread.EntityContext;
        var results = new SmallList<Entity>();
        while (remainingParts is [var currentPart, _, ..])
        {
            var pattern = new EntityPattern(currentPart.Value, ContainsWildcard: false);
            scope = currentPart.IsAlias ? CurrentProcess.Aliases : scope;
            scope.Query(pattern, ref results);
            if (results is not [var singleResult]) { return false; }

            scope = parent = singleResult;
            remainingParts = remainingParts[1..];
            results.Clear();
        }

        name = EntityName.Parse(remainingParts[0].Value);
        return true;
    }

    public SmallList<Entity> Query(in EntityQuery query)
    {
        var lastResults = new SmallList<Entity>(CurrentProcess.CurrentThread.EntityContext);
        var currentResults = new SmallList<Entity>();
        foreach (EntityQueryPart queryPart in query.Parts)
        {
            foreach (Entity entity in lastResults)
            {
                EntityScope scope = queryPart.Scope switch
                {
                    EntityQueryScope.Current => entity,
                    EntityQueryScope.MainThread => CurrentProcess.MainThread.EntityContext,
                    EntityQueryScope.CurrentAliases => CurrentProcess.Aliases,
                    EntityQueryScope.AllAliases => this,
                    _ => throw ThrowHelper.UnexpectedValueOf<EntityQueryScope>()
                };

                scope.Query(queryPart.Pattern, ref currentResults);
            }

            lastResults.Clear();
            foreach (Entity entity in currentResults)
            {
                lastResults.Add(entity);
            }
            currentResults.Clear();
        }

        return lastResults;
    }

    public void Query(EntityPattern pattern, ref SmallList<Entity> results)
    {
        foreach (Process process in _processes)
        {
            process.Query(pattern, ref results);
            process.Aliases.Query(pattern, ref results);
        }
    }

    public void Update(GameContext ctx)
    {
        CurrentProcess.MainThread.InterruptVideoWait(ctx.InputContext);
        if (_systemMenu is null && !(ctx.VM.SystemVariables.MenuLock.AsBool() ?? false)
            && ctx.InputContext.Consume(InputAction.Back))
        {
            TryBeginSystemMenu(ctx.VM, ctx.Profile.SysScripts.Menu);
        }
        CurrentProcess.Update(ctx);
        if (!ApplyPendingReset()) FinishSystemMenu();
    }

    internal void RequestReset(NsScriptThreadState startup)
    {
        _pendingReset = startup;
        // End the requesting script slice and stop other scene threads before
        // the current process update list continues.
        foreach (Process process in _processes)
            foreach (Entity entity in process.GetDescendants())
                if (entity is Thread thread) thread.Request(NsEntityAction.Stop);
    }

    internal bool ApplyPendingReset()
    {
        if (_pendingReset is not { } startup) return false;
        _pendingReset = null;
        var font = MainProcess.FontSettings;
        EntityName processName = MainProcess.Name;
        foreach (Process process in _processes.ToArray()) DestroyEntity(process);
        _systemMenu = null;
        MainProcess = CurrentProcess = AddEntity(new Process(processName, null, font));
        var thread = AddEntity(new Thread(EntityName.Parse("main"), MainProcess, startup, isMain: true));
        MainProcess.Enable();
        thread.Enable();
        return true;
    }

    internal bool TryBeginSystemMenu(NsScriptVM vm, string module)
    {
        if (_systemMenu is not null) return false;
        try
        {
            return vm.CreateThread(module, "main") is { } state && BeginSystemMenu(state);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal bool BeginSystemMenu(NsScriptThreadState state)
    {
        if (_systemMenu is not null) return false;
        _systemMenu = AddEntity(new Process(EntityName.Parse("system-menu"), null, MainProcess.FontSettings));
        var thread = AddEntity(new Thread(EntityName.Parse("main"), _systemMenu, state, isMain: true));
        _systemMenu.Enable();
        thread.Enable();
        CurrentProcess = _systemMenu;
        return true;
    }

    internal bool FinishSystemMenu()
    {
        if (_systemMenu is not { } menu || !menu.MainThread.State.DoneExecuting) return false;
        CurrentProcess = MainProcess;
        _systemMenu = null;
        DestroyEntity(menu);
        return true;
    }

    public void Render(GameContext ctx)
    {
        CurrentProcess.RenderAll(ctx);
    }
}
