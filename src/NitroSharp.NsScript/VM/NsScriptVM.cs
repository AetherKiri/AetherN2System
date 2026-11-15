using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NitroSharp.Common;
using MessagePack;
using NitroSharp.NsScript.Primitives;
using NitroSharp.NsScript.Utilities;
using ZeroLog;

namespace NitroSharp.NsScript.VM;

public readonly struct GlobalsDump
{
    internal (string, ConstantValue)[] Globals { get; init; }
    internal GlobalArrayAlias[] Aliases { get; init; }

    public GlobalsDump(ref MessagePackReader reader)
    {
        int fields = reader.ReadArrayHeader();
        if (fields is not (1 or 2)) throw new InvalidDataException("Invalid saved globals record.");
        int count = reader.ReadArrayHeader();
        if ((uint)count > 32768) throw new InvalidDataException("Invalid saved globals count.");
        Globals = new (string, ConstantValue)[count];
        for (int i = 0; i < count; i++)
        {
            if (reader.ReadArrayHeader() != 2) throw new InvalidDataException("Invalid saved global.");
            Globals[i] = (reader.ReadString() ?? throw new InvalidDataException("Invalid global name."), new ConstantValue(ref reader));
        }
        count = fields == 2 ? reader.ReadArrayHeader() : 0;
        if ((uint)count > 32768) throw new InvalidDataException("Invalid saved aliases count.");
        Aliases = new GlobalArrayAlias[count];
        for (int i = 0; i < count; i++) Aliases[i] = new GlobalArrayAlias(ref reader);
    }

    public void Serialize(ref MessagePackWriter writer)
    {
        writer.WriteArrayHeader(2);
        writer.WriteArrayHeader(Globals.Length);
        foreach ((string name, ConstantValue value) in Globals)
        {
            writer.WriteArrayHeader(2);
            writer.Write(name);
            value.Serialize(ref writer);
        }
        writer.WriteArrayHeader(Aliases?.Length ?? 0);
        foreach (GlobalArrayAlias alias in Aliases ?? []) alias.Serialize(ref writer);
    }
}

[Persistable]
internal readonly partial record struct GlobalArrayAlias(string Name, string Owner, ConstantValue[] Path);

public sealed class NsScriptVM
{
    private readonly Log _log;
    private readonly NsxModuleLocator _moduleLocator;
    private readonly Dictionary<string, NsxModule> _loadedModules;
    private readonly Dictionary<string, NsxModule> _scopedSceneModules = new();
    private NsxModule? _currentModule;
    private readonly BuiltInFunctionDispatcher _builtInCallDispatcher;
    private ConstantValue[] _variables;
    private ConstantValue[] _flags;
    private readonly Stack<CubicBezierSegment> _bezierSegmentStack;

    public NsScriptVM(NsxModuleLocator moduleLocator, Stream globalsLookupTableStream)
    {
        _log = LogManager.GetLogger("VM");
        _loadedModules = new Dictionary<string, NsxModule>(16);
        _moduleLocator = moduleLocator;
        GlobalsLookup = GlobalsLookupTable.Load(globalsLookupTableStream);
        _variables = new ConstantValue[Math.Max(5000, GlobalsLookup.Variables.Count)];
        _flags = new ConstantValue[Math.Max(2000, GlobalsLookup.Flags.Count)];
        _builtInCallDispatcher = new BuiltInFunctionDispatcher(_variables, _flags);
        InitializeAudioDefaults();
        SystemVariables = new SystemVariableLookup(this);
        _bezierSegmentStack = new Stack<CubicBezierSegment>();
    }

    internal GlobalsLookupTable GlobalsLookup { get; private set; }

    public SystemVariableLookup SystemVariables { get; }

    public GlobalsDump DumpVariables() => DumpGlobals(_variables, GlobalsLookup.Variables);
    public GlobalsDump DumpFlags() => DumpGlobals(_flags, GlobalsLookup.Flags);

    public void RestoreVariables(GlobalsDump dump)
    {
        RestoreGlobals(dump, flag: false);
    }

    public void RestoreFlags(GlobalsDump dump)
    {
        RestoreGlobals(dump, flag: true);
    }

    private static GlobalsDump DumpGlobals(ConstantValue[] table, FrozenDictionary<string, int> lookup)
    {
        var globals = new (string, ConstantValue)[lookup.Count];
        var aliases = new List<GlobalArrayAlias>();
        var nodes = new Dictionary<NsArray, (string Name, ConstantValue[] Path)>();
        foreach ((string name, int i) in lookup.OrderBy(entry => entry.Value))
        {
            globals[i] = (name, table[i]);
            if (table[i].Array is not { } array) continue;
            if (nodes.TryGetValue(array, out var existing))
                aliases.Add(new GlobalArrayAlias(name, existing.Name, existing.Path));
            else IndexSaveArray(array, name, [], nodes);
        }
        return new GlobalsDump { Globals = globals, Aliases = aliases.ToArray() };
    }

    private static void IndexSaveArray(NsArray node, string name, ConstantValue[] path,
        Dictionary<NsArray, (string Name, ConstantValue[] Path)> nodes)
    {
        if (!nodes.TryAdd(node, (name, path))) return;
        if (path.Length >= 128 || nodes.Count > 1_000_000) throw new InvalidDataException("Saved array graph is too complex.");
        foreach (var child in node.SaveChildren()) IndexSaveArray(child.Node, name, [.. path, child.Key], nodes);
    }

    public ConstantValue GetHostVariable(string name)
        => GlobalsLookup.Variables.TryGetValue(name, out int index) ? _variables[index] : ConstantValue.Null;

    public ConstantValue GetHostFlag(string name)
        => GlobalsLookup.Flags.TryGetValue(name, out int index) ? _flags[index] : ConstantValue.Null;

    public void SetHostFlag(string name, ConstantValue value)
    {
        int index = EnsureSaveGlobal(name, flag: true);
        _flags[index] = value;
    }

    private int EnsureSaveGlobal(string name, bool flag)
    {
        if (name.Length is 0 or > 4096) throw new InvalidDataException("Invalid saved global name.");
        var lookup = flag ? GlobalsLookup.Flags : GlobalsLookup.Variables;
        if (lookup.TryGetValue(name, out int index)) return index;
        if (lookup.Count > short.MaxValue - 2) throw new InvalidDataException("Too many saved globals.");
        index = lookup.Count;
        UpdateGlobals(GlobalsLookup.WithGlobal(name, flag));
        return index;
    }

    private void RestoreGlobals(in GlobalsDump dump, bool flag)
    {
        Array.Clear(flag ? _flags : _variables);
        foreach ((string name, ConstantValue val) in dump.Globals)
        {
            int index = EnsureSaveGlobal(name, flag);
            if (flag) _flags[index] = val;
            else _variables[index] = val;
        }
        foreach (GlobalArrayAlias alias in dump.Aliases ?? [])
        {
            int target = EnsureSaveGlobal(alias.Name, flag), owner = EnsureSaveGlobal(alias.Owner, flag);
            ConstantValue[] table = flag ? _flags : _variables;
            NsArray node = table[owner].Array ?? throw new InvalidDataException("Missing saved array owner.");
            if (alias.Path.Length > 128) throw new InvalidDataException("Invalid saved array path.");
            foreach (ConstantValue key in alias.Path) node = node.Get(key);
            table[target] = table[target].WithArray(node);
        }
    }

    internal NsxModule GetModule(string name)
    {
        name = _moduleLocator.NormalizeModuleName(name);
        if (!_loadedModules.TryGetValue(name, out NsxModule? module))
        {
            if (_moduleLocator.CompileModuleIfMissing(name, GlobalsLookup, _loadedModules) is { } compiled)
            {
                UpdateGlobals(compiled.Globals);
                foreach ((string moduleName, byte[] bytes) in compiled.Modules)
                    _loadedModules.Add(moduleName, NsxModule.LoadModule(new MemoryStream(bytes), moduleName));
                return _loadedModules[name];
            }
            Stream stream = _moduleLocator.OpenModule(name);
            module = NsxModule.LoadModule(stream, name);
            _loadedModules.Add(name, module);
        }

        return module;
    }

    public NsxModule GetSaveModule(string moduleName) => GetModule(moduleName);

    public void WriteSaveModule(BinaryWriter writer, NsxModule module)
    {
        writer.Write(module.Name);
        writer.Write(module.SourceScopes.Length);
        foreach (string scope in module.SourceScopes)
        {
            writer.Write(scope);
            writer.Write(HashSaveSource(scope));
        }
        byte[] hash;
        bool source;
        try { hash = HashSaveSource(module.Name); source = true; }
        catch (FileNotFoundException) { hash = CacheNameHash.HashData(module.Code); source = false; }
        catch (DirectoryNotFoundException) { hash = CacheNameHash.HashData(module.Code); source = false; }
        writer.Write(source);
        writer.Write(hash);
    }

    public NsxModule ReadSaveModule(BinaryReader reader)
    {
        string name = ReadSaveName(reader);
        int count = ReadSaveCount(reader, 256);
        var scopes = new string[count];
        for (int i = 0; i < count; i++)
        {
            scopes[i] = ReadSaveName(reader);
            VerifySaveHash(reader, HashSaveSource(scopes[i]));
        }
        bool source = reader.ReadBoolean();
        byte[] hash = reader.ReadBytes(32);
        if (hash.Length != 32) throw new EndOfStreamException();
        NsxModule result;
        if (count == 0) result = GetModule(name);
        else
        {
            string key = "save\0" + string.Join('\0', scopes);
            if (!_scopedSceneModules.TryGetValue(key, out NsxModule? root))
            {
                var compiled = _moduleLocator.CompileModuleIfMissing(scopes[0], GlobalsLookup, _loadedModules, scopes[1..])
                    ?? throw new InvalidDataException("The saved scene's source scope cannot be restored.");
                UpdateGlobals(compiled.Globals);
                var modules = new Dictionary<string, NsxModule>();
                foreach ((string moduleName, byte[] bytes) in compiled.Modules)
                    modules.Add(moduleName, NsxModule.LoadModule(new MemoryStream(bytes), moduleName));
                foreach (NsxModule module in modules.Values)
                {
                    module.SourceScopes = scopes;
                    module.ScopedModules = modules;
                }
                if (!modules.TryGetValue(scopes[0], out root))
                    throw new InvalidDataException("The saved scene root was not found.");
                _scopedSceneModules.Add(key, root);
            }
            result = root.ScopedModules is { } savedModules && savedModules.TryGetValue(name, out NsxModule? moduleResult)
                ? moduleResult : throw new InvalidDataException("The saved scene module was not found.");
        }
        byte[] actual = source ? HashSaveSource(name) : CacheNameHash.HashData(result.Code);
        if (!hash.AsSpan().SequenceEqual(actual)) throw new InvalidDataException("The saved script has changed.");
        return result;
    }

    private byte[] HashSaveSource(string name)
    {
        using SourceMappingScope mapping = _moduleLocator.BeginSourceMapping();
        return CacheNameHash.HashData(Encoding.UTF8.GetBytes(mapping.GetSourceText(name).Source));
    }

    private static void VerifySaveHash(BinaryReader reader, byte[] actual)
    {
        byte[] saved = reader.ReadBytes(32);
        if (saved.Length != 32) throw new EndOfStreamException();
        if (!saved.AsSpan().SequenceEqual(actual)) throw new InvalidDataException("The saved script scope has changed.");
    }

    private static string ReadSaveName(BinaryReader reader)
    {
        string name = reader.ReadString();
        if (name.Length is 0 or > 4096) throw new InvalidDataException("Invalid saved name.");
        return name;
    }

    // Saves retain bytecode offsets, so callers must verify the game/save format before restoring.
    public void WriteThreadState(BinaryWriter writer, in NsScriptThreadState state)
    {
        writer.Write(1);
        writer.Write(state.CallFrameStack.Count);
        foreach (CallFrame frame in state.CallFrameStack.AsSpan())
        {
            WriteSaveModule(writer, frame.Module);
            writer.Write(frame.SubroutineIndex);
            writer.Write(frame.ProgramCounter);
            writer.Write(frame.EndPosition.HasValue);
            if (frame.EndPosition is { } end) writer.Write(end);
            writer.Write(frame.HasCallScope);
        }
        var arrayReferences = new Dictionary<NsArray, (string Name, ConstantValue[] Path)>();
        foreach ((string name, int index) in GlobalsLookup.Variables.OrderBy(entry => entry.Value))
            if (_variables[index].Array is { } array) IndexSaveArray(array, "$" + name, [], arrayReferences);
        foreach ((string name, int index) in GlobalsLookup.Flags.OrderBy(entry => entry.Value))
            if (_flags[index].Array is { } array) IndexSaveArray(array, "#" + name, [], arrayReferences);
        writer.Write(state.EvalStack.Count);
        foreach (ConstantValue value in state.EvalStack.AsSpan())
        {
            WriteSaveValue(writer, value);
            bool reference = value.GetSlotInfo(out short slot);
            writer.Write(reference);
            if (reference)
            {
                bool flag = slot < 0;
                int index = flag ? -slot - 2 : slot;
                string name = (flag ? GlobalsLookup.Flags : GlobalsLookup.Variables)
                    .First(entry => entry.Value == index).Key;
                writer.Write(flag);
                writer.Write(name);
            }
            bool arrayReference = value.Array is { } valueArray && arrayReferences.ContainsKey(valueArray);
            writer.Write(arrayReference);
            if (arrayReference)
            {
                var target = arrayReferences[value.Array!];
                writer.Write(target.Name);
                writer.Write(target.Path.Length);
                foreach (ConstantValue key in target.Path) WriteSaveValue(writer, key);
            }
        }
        writer.Write(state.Yielded);
        writer.Write(state.SelectResult);
        // Instruction-budget yields can interrupt a curve literal between its segments.
        WriteSaveValue(writer, ConstantValue.BezierCurve(new CompositeBezier(_bezierSegmentStack.ToImmutableArray())));
    }

    public NsScriptThreadState ReadThreadState(BinaryReader reader)
    {
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported VM thread save version.");
        var state = new NsScriptThreadState
        {
            CallFrameStack = new ValueStack<CallFrame>(4),
            EvalStack = new ValueStack<ConstantValue>(4)
        };
        int frames = ReadSaveCount(reader, 4096);
        for (int i = 0; i < frames; i++)
        {
            NsxModule module = ReadSaveModule(reader);
            ushort subroutine = reader.ReadUInt16();
            Subroutine routine;
            try { routine = module.GetSubroutine(subroutine); }
            catch (IndexOutOfRangeException exception) { throw new InvalidDataException("Invalid saved subroutine.", exception); }
            int pc = reader.ReadInt32();
            int? end = reader.ReadBoolean() ? reader.ReadInt32() : null;
            if (pc < routine.BytecodeStart || pc > module.Code.Length || (pc == module.Code.Length && end != pc)
                || end is { } limit && (limit < pc || limit > module.Code.Length))
                throw new InvalidDataException("Invalid saved bytecode position.");
            state.CallFrameStack.Push(new CallFrame(module, subroutine, pc)
            {
                EndPosition = end,
                HasCallScope = reader.ReadBoolean()
            });
        }
        int values = ReadSaveCount(reader, 1_000_000);
        for (int i = 0; i < values; i++)
        {
            ConstantValue value = ReadSaveValue(reader);
            if (reader.ReadBoolean())
            {
                bool flag = reader.ReadBoolean();
                int index = EnsureSaveGlobal(ReadSaveName(reader), flag);
                short slot = checked((short)(flag ? -index - 2 : index));
                ConstantValue global = flag ? _flags[index] : _variables[index];
                // Preserve array aliasing between an evaluated reference and its restored global.
                if (value.Array is not null && global.Array is { } array) value = value.WithArray(array);
                value = value.WithSlot(slot);
            }
            if (reader.ReadBoolean())
            {
                string owner = ReadSaveName(reader);
                if (owner[0] is not ('$' or '#')) throw new InvalidDataException("Invalid saved array owner.");
                bool flag = owner[0] == '#';
                int index = EnsureSaveGlobal(owner[1..], flag);
                NsArray node = (flag ? _flags[index] : _variables[index]).Array
                    ?? throw new InvalidDataException("Missing saved array reference.");
                int keys = ReadSaveCount(reader, 128);
                for (int key = 0; key < keys; key++) node = node.Get(ReadSaveValue(reader));
                bool hasSlot = value.GetSlotInfo(out short slot);
                value = value.WithArray(node);
                if (hasSlot) value = value.WithSlot(slot);
            }
            state.EvalStack.Push(value);
        }
        state.Yielded = reader.ReadBoolean();
        state.SelectResult = reader.ReadBoolean();
        ConstantValue curve = ReadSaveValue(reader);
        if (curve.Type != BuiltInType.BezierCurve) throw new InvalidDataException("Invalid saved curve state.");
        RestoreTransientSaveState(curve.AsBezierCurve()!.Value);
        return state;
    }

    public CompositeBezier CaptureTransientSaveState() => new(_bezierSegmentStack.ToImmutableArray());

    public void RestoreTransientSaveState(CompositeBezier curve)
    {
        _bezierSegmentStack.Clear();
        for (int i = curve.Segments.Length - 1; i >= 0; i--) _bezierSegmentStack.Push(curve.Segments[i]);
    }

    private static int ReadSaveCount(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        if ((uint)count > maximum) throw new InvalidDataException("Invalid saved collection size.");
        return count;
    }

    private static void WriteSaveValue(BinaryWriter writer, ConstantValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var messageWriter = new MessagePackWriter(buffer);
        value.Serialize(ref messageWriter);
        messageWriter.Flush();
        writer.Write(buffer.WrittenCount);
        writer.Write(buffer.WrittenSpan);
    }

    private static ConstantValue ReadSaveValue(BinaryReader reader)
    {
        int size = ReadSaveCount(reader, 16 * 1024 * 1024);
        byte[] bytes = reader.ReadBytes(size);
        if (bytes.Length != size) throw new EndOfStreamException();
        var validation = new MessagePackReader(bytes);
        int budget = 1_000_000;
        ValidateSaveValue(ref validation, 0, ref budget);
        if (!validation.End) throw new InvalidDataException("Trailing saved value data.");
        var messageReader = new MessagePackReader(bytes);
        var value = new ConstantValue(ref messageReader);
        if (!messageReader.End) throw new InvalidDataException("Trailing saved value data.");
        return value;
    }

    private static void ValidateSaveValue(ref MessagePackReader reader, int depth, ref int budget)
    {
        if (depth > 128 || --budget < 0) throw new InvalidDataException("Saved value is too complex.");
        int children;
        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Array:
                children = reader.ReadArrayHeader();
                break;
            case MessagePackType.Map:
                int entries = reader.ReadMapHeader();
                if (entries > budget / 2) throw new InvalidDataException("Saved value map is too large.");
                children = entries * 2;
                break;
            default:
                reader.Skip();
                return;
        }
        if (children > budget) throw new InvalidDataException("Saved value array is too large.");
        for (int i = 0; i < children; i++) ValidateSaveValue(ref reader, depth + 1, ref budget);
    }

    public NsScriptThreadState? CreateThread(string moduleName, string symbol)
    {
        NsxModule? module = _currentModule is { } current && current.Name == _moduleLocator.NormalizeModuleName(moduleName)
            ? current : GetModule(moduleName);
        if (!module.TryLookupSubroutineIndex(symbol, out int index))
        {
            NsxModule owner = module;
            module = module.Imports
                .Select(name => GetImportedModule(owner, name))
                .FirstOrDefault(import => import.TryLookupSubroutineIndex(symbol, out index));
        }

        if (module is null) { return null; }
        var frame = CreateEntryPointCallFrame(module, (ushort)index);
        return new NsScriptThreadState(frame);
    }

    public NsScriptThreadState CreateDialogueCodeThread(in DialogueBlockToken token,
        CompiledDialogueBlockPart.CodeBlock code)
    {
        var frame = new CallFrame(token.Module, (ushort)token.SubroutineIndex, code.Position)
        {
            EndPosition = code.Position + code.Length
        };
        return new NsScriptThreadState(frame);
    }

    internal ref ConstantValue GetVariable(int index)
    {
        ref ConstantValue val = ref _variables[index];
        if (val.Type == BuiltInType.Uninitialized)
        {
            val = ConstantValue.Number(0);
        }

        return ref val;
    }

    internal ref ConstantValue GetFlag(int index)
    {
        ref ConstantValue val = ref _flags[index];
        if (val.Type == BuiltInType.Uninitialized)
        {
            val = ConstantValue.Number(0);
        }

        return ref val;
    }

    public enum TickResult
    {
        Ok,
        Yield
    }

    public TickResult Tick(ref NsScriptThreadState thread, BuiltInFunctions builtins)
    {
        NsxModule? previousModule = _currentModule;
        try
        {
            return TickCore(ref thread, builtins);
        }
        catch (Exception e)
        {
            ReportException(thread, e);
            throw;
        }
        finally
        {
            _currentModule = previousModule;
        }
    }

    public int InstructionBudget { get; set; } = 100_000;

    private TickResult TickCore(ref NsScriptThreadState thread, BuiltInFunctions builtins)
    {
        if (thread.CallFrameStack.Count == 0)
        {
            return TickResult.Yield;
        }

        ref CallFrame frame = ref thread.CurrentFrame;
        NsxModule thisModule = frame.Module;
        _currentModule = thisModule;
        builtins._vm = this;
        builtins.CurrentModule = thisModule;
        var program = new BytecodeStream(thisModule.Code, frame.ProgramCounter);
        ref ValueStack<ConstantValue> evalStack = ref thread.EvalStack;
        int instructions = 0;
        while (true)
        {
            frame.ProgramCounter = program.Position;
            if (frame.EndPosition is { } end && program.Position >= end)
            {
                if (frame.HasCallScope) builtins.LeaveCallScope();
                thread.CallFrameStack.Pop();
                return TickResult.Ok;
            }
            if (++instructions > InstructionBudget) return TickResult.Yield;
            Opcode opcode = program.NextOpcode();
            if (opcode is >= Opcode.LoadImm0 and <= Opcode.LoadFlag)
            {
                handleLoadOp(opcode, ref program, ref thread);
                continue;
            }

            switch (opcode)
            {
                case Opcode.StoreVar:
                    int index = program.DecodeToken();
                    GetVariable(index) = evalStack.Pop();
                    break;
                case Opcode.StoreFlag:
                    index = program.DecodeToken();
                    GetFlag(index) = evalStack.Pop();
                    break;
                case Opcode.EnsureArray:
                    ref ConstantValue arrayTarget = ref evalStack.Peek();
                    arrayTarget = EnsureArray(arrayTarget).Value;
                    break;
                case Opcode.LoadIndex:
                    ConstantValue key = evalStack.Pop();
                    ConstantValue arrayValue = evalStack.Pop();
                    evalStack.Push(EnsureArray(arrayValue).Get(key).Value);
                    break;
                case Opcode.StoreIndex:
                    NsArray item = evalStack.Pop().Array
                        ?? throw new InvalidOperationException("Indexed assignment requires an array value.");
                    ConstantValue assigned = evalStack.Pop();
                    var assignment = (AssignmentOperatorKind)program.ReadByte();
                    item.Scalar = assignment switch
                    {
                        AssignmentOperatorKind.Assign => assigned.Scalar,
                        AssignmentOperatorKind.AddAssign => item.Scalar + assigned,
                        AssignmentOperatorKind.SubtractAssign => item.Scalar - assigned,
                        AssignmentOperatorKind.MultiplyAssign => item.Scalar * assigned,
                        AssignmentOperatorKind.DivideAssign => item.Scalar / assigned,
                        AssignmentOperatorKind.Increment => item.Scalar + ConstantValue.Number(1),
                        AssignmentOperatorKind.Decrement => item.Scalar - ConstantValue.Number(1),
                        _ => throw new InvalidOperationException("Unknown indexed assignment operator.")
                    };
                    break;
                case Opcode.Binary:
                    var opKind = (BinaryOperatorKind)program.ReadByte();
                    ConstantValue op1 = evalStack.Pop();
                    ConstantValue op2 = evalStack.Pop();
                    evalStack.Push(BinOp(op1, opKind, op2));
                    break;
                case Opcode.Equal:
                    op1 = evalStack.Pop();
                    op2 = evalStack.Pop();
                    evalStack.Push(op1 == op2);
                    break;
                case Opcode.NotEqual:
                    op1 = evalStack.Pop();
                    op2 = evalStack.Pop();
                    evalStack.Push(op1 != op2);
                    break;
                case Opcode.Neg:
                    ref ConstantValue val = ref evalStack.Peek();
                    val = val.Type switch
                    {
                        BuiltInType.Numeric => ConstantValue.Number(-val.AsNumber()!.Value),
                        // TODO: runtime error
                        _ => throw ThrowHelper.Unreachable()
                    };
                    break;
                case Opcode.Inc:
                    val = ref evalStack.Peek();
                    Debug.Assert(val.Type == BuiltInType.Numeric); // TODO: runtime error
                    val = ConstantValue.Number(val.AsNumber()!.Value + 1);
                    break;
                case Opcode.Dec:
                    val = ref evalStack.Peek();
                    Debug.Assert(val.Type == BuiltInType.Numeric); // TODO: runtime error
                    val = ConstantValue.Number(val.AsNumber()!.Value - 1);
                    break;
                case Opcode.Delta:
                    val = ref evalStack.Peek();
                    if ((val.AsNumber() ?? val.AsDeltaNumber()) is { } num)
                    {
                        val = ConstantValue.Delta(num);
                    }
                    else
                    {
                        val = ConstantValue.String("@" + val.AsString()!);
                    }
                    break;
                case Opcode.Invert:
                    val = ref evalStack.Peek();
                    bool boolean = val.AsBool()
                        ?? throw new InvalidOperationException($"Operator '!' cannot be applied to a value of type '{val.Type}'.");
                    val = ConstantValue.Boolean(!boolean);
                    break;
                case Opcode.Pop:
                    evalStack.Pop();
                    break;
                case Opcode.Call:
                    ushort subroutineToken = program.DecodeToken();
                    frame.ProgramCounter = program.Position;
                    CallFrame newFrame = CreateEntryPointCallFrame(frame.Module, subroutineToken);
                    thread.CallFrameStack.Push(newFrame);
                    if (_log.IsDebugEnabled)
                    {
                        logCall(newFrame);
                    }
                    return TickResult.Ok;
                case Opcode.CallFar:
                    newFrame = externalCall(ref program);
                    frame.ProgramCounter = program.Position;
                    thread.CallFrameStack.Push(newFrame);
                    return TickResult.Ok;
                case Opcode.CallChapter:
                case Opcode.CallScene:
                    newFrame = externalCall(ref program);
                    frame.ProgramCounter = program.Position;
                    newFrame.HasCallScope = true;
                    builtins.EnterCallScope();
                    thread.CallFrameStack.Push(newFrame);
                    return TickResult.Ok;
                case Opcode.CallSceneDynamic:
                    newFrame = ResolveScene(SceneCaller(ref thread), evalStack.Pop().ConvertToString());
                    frame.ProgramCounter = program.Position;
                    newFrame.HasCallScope = true;
                    builtins.EnterCallScope();
                    thread.CallFrameStack.Push(newFrame);
                    return TickResult.Ok;
                case Opcode.Jump:
                    int @base = program.Position - 1;
                    int offset = program.DecodeOffset();
                    program.Position = @base + offset;
                    break;
                case Opcode.JumpIfTrue:
                    @base = program.Position - 1;
                    ConstantValue condition = evalStack.Pop();
                    offset = program.DecodeOffset();
                    Debug.Assert(condition.Type == BuiltInType.Boolean);
                    if (condition.AsBool()!.Value)
                    {
                        program.Position = @base + offset;
                    }
                    break;
                case Opcode.JumpIfFalse:
                    @base = program.Position - 1;
                    condition = evalStack.Pop();
                    offset = program.DecodeOffset();
                    if (!condition.AsBool()!.Value)
                    {
                        program.Position = @base + offset;
                    }
                    break;
                case Opcode.Return:
                    if (thread.CallFrameStack.Count > 0)
                    {
                        if (frame.HasCallScope) builtins.LeaveCallScope();
                        thread.CallFrameStack.Pop();
                    }
                    return TickResult.Ok;
                case Opcode.BezierStart:
                    _bezierSegmentStack.Clear();
                    break;
                case Opcode.BezierEndSeg:

                    var seg = new CubicBezierSegment(
                        popPoint(ref evalStack),
                        popPoint(ref evalStack),
                        popPoint(ref evalStack),
                        popPoint(ref evalStack)
                    );
                    _bezierSegmentStack.Push(seg);
                    break;

                    static BezierControlPoint popPoint(ref ValueStack<ConstantValue> stack)
                    {
                        var x = NsCoordinate.FromValue(stack.Pop());
                        var y = NsCoordinate.FromValue(stack.Pop());
                        return new BezierControlPoint(x, y);
                    }
                case Opcode.BezierEnd:
                    var curve = new CompositeBezier(_bezierSegmentStack.ToImmutableArray());
                    evalStack.Push(ConstantValue.BezierCurve(curve));
                    break;
                case Opcode.Dispatch:
                    builtins.DialogueCallerModule = SceneCaller(ref thread);
                    dispatchBuiltIn(ref program, ref thread);
                    frame.ProgramCounter = program.Position;
                    return TickResult.Ok;

                case Opcode.ActivateBlock:
                    ushort blockId = program.DecodeToken();
                    (string box, string textName) = frame.RuntimeInfo.DialogueBlockInfos[blockId];
                    SystemVariables.CurrentDialogueBox = ConstantValue.String(box);
                    SystemVariables.CurrentDialogueBlock = ConstantValue.String($"@{textName}");
                    break;
                case Opcode.SelectLoopStart:
                    thread.SelectResult = false;
                    break;
                case Opcode.IsPressed:
                case Opcode.IsPressedDynamic:
                    string choice = opcode == Opcode.IsPressedDynamic
                        ? evalStack.Pop().ConvertToString()
                        : thisModule.GetString(program.DecodeToken());
                    bool pressed = builtins.HandleInputEvents(EntityQuery.Parse(choice));
                    evalStack.Push(ConstantValue.Boolean(pressed));
                    thread.SelectResult |= pressed;
                    break;
                case Opcode.SelectLoopEnd:
                    evalStack.Push(ConstantValue.Boolean(thread.SelectResult));
                    frame.ProgramCounter = program.Position;
                    return TickResult.Yield;
                case Opcode.SelectEnd:
                    builtins.SelectEnd();
                    break;
            }
        }

        void handleLoadOp(Opcode opcode, ref BytecodeStream program, ref NsScriptThreadState thread)
        {
            ushort varToken = ushort.MaxValue;
            ushort flagToken;
            ConstantValue? imm = opcode switch
            {
                Opcode.LoadImm => readConst(ref program),
                Opcode.LoadImm0 => ConstantValue.Number(0),
                Opcode.LoadImm1 => ConstantValue.Number(1),
                Opcode.LoadImmTrue => ConstantValue.True,
                Opcode.LoadImmFalse => ConstantValue.False,
                Opcode.LoadImmNull => ConstantValue.Null,
                Opcode.LoadImmEmptyStr => ConstantValue.EmptyString,
                Opcode.LoadVar => GetVariable(varToken = program.DecodeToken())
                    .WithSlot((short)varToken),
                Opcode.LoadFlag => GetFlag(flagToken = program.DecodeToken())
                    .WithSlot((short)(-flagToken - 2)),
                _ => null
            };

            if (varToken != ushort.MaxValue && varToken == SystemVariables.PresentProcess)
            {
                string subName = thisModule.GetSubroutineName(thread.CurrentFrame.SubroutineIndex);
                imm = _variables[varToken] = ConstantValue.String(subName);
            }

            ConstantValue value = imm!.Value;
            thread.EvalStack.Push(ref value);
        }

        void dispatchBuiltIn(ref BytecodeStream program, ref NsScriptThreadState thread)
        {
            ref CallFrame frame = ref thread.CurrentFrame;
            ref ValueStack<ConstantValue> evalStack = ref thread.EvalStack;
            var func = (BuiltInFunction)program.ReadByte();
            int argCount = program.ReadByte();
            ReadOnlySpan<ConstantValue> args = evalStack.AsSpan(evalStack.Count - argCount, argCount);
            ConstantValue? result = null;
            switch (func)
            {
                case BuiltInFunction.Array:
                    if (args.Length > 0) EnsureArray(args[0]).SetItems(args[1..]);
                    break;
                case BuiltInFunction.AssocArray:
                    if (args.Length > 0) EnsureArray(args[0]).SetNames(args[1..]);
                    break;
                case BuiltInFunction.VariableValue:
                    result = VariableValue(args);
                    break;
                default:
                    result = _builtInCallDispatcher.Dispatch(builtins, func, args);
                    break;

                case BuiltInFunction.log:
                    _log.Info(args[0].ConvertToString());
                    break;
                case BuiltInFunction.fail:
                    string subName = frame.RuntimeInfo.SubroutineName;
                    _log.Error($"{subName} + {program.Position - 1}: test failed.");
                    break;
                case BuiltInFunction.fail_msg:
                    subName = frame.RuntimeInfo.SubroutineName;
                    _log.Error($"{subName} + {program.Position - 1}: {args[0].ToString()}.");
                    break;
            }

            evalStack.Pop(argCount);
            evalStack.Push(result ?? ConstantValue.Null);
        }

        CallFrame externalCall(ref BytecodeStream program)
        {
            ushort importTableIndex = program.DecodeToken();
            ushort subroutineToken = program.DecodeToken();
            string externalModuleName = thisModule.Imports[importTableIndex];
            NsxModule externalModule = GetImportedModule(thisModule, externalModuleName);
            return CreateEntryPointCallFrame(externalModule, subroutineToken);
        }

        ConstantValue readConst(ref BytecodeStream stream)
        {
            Immediate imm = stream.DecodeImmediateValue();
            return imm.Type switch
            {
                BuiltInType.Numeric => ConstantValue.Number(imm.Numeric),
                BuiltInType.DeltaNumeric => ConstantValue.Delta(imm.Numeric),
                BuiltInType.BuiltInConstant => ConstantValue.BuiltInConstant(imm.Constant),
                BuiltInType.String => ConstantValue.String(thisModule.GetString(imm.StringToken)),
                _ => throw ThrowHelper.UnexpectedValueOf<BuiltInType>()
            };
        }

        void logCall(in CallFrame callFrame)
        {
            string moduleName = callFrame.Module.Name;
            string routineName = callFrame.RuntimeInfo.SubroutineName;
            _log.Debug($"{moduleName}::{routineName}");
        }
    }

    private void UpdateGlobals(GlobalsLookupTable globals)
    {
        GlobalsLookup = globals;
        if (_variables.Length < globals.Variables.Count) Array.Resize(ref _variables, globals.Variables.Count);
        if (_flags.Length < globals.Flags.Count) Array.Resize(ref _flags, globals.Flags.Count);
        InitializeAudioDefaults();
        _builtInCallDispatcher.UpdateGlobals(_variables, _flags);
        SystemVariables.Refresh();
    }

    private void InitializeAudioDefaults()
    {
        // These engine flags exist before scripts run; zero and false remain valid overrides.
        foreach (string channel in new[] { "bgm", "se", "voice" })
        {
            Initialize("SYSTEM_sound_" + channel, ConstantValue.True);
            Initialize("SYSTEM_sound_volume_" + channel, ConstantValue.Number(1000));
            Initialize("SYSTEM_sound_volume_" + channel + "_default", ConstantValue.Number(1000));
        }

        void Initialize(string name, ConstantValue value)
        {
            if (GlobalsLookup.TryLookupSystemFlag(name, out int index)
                && _flags[index].Type == BuiltInType.Uninitialized)
                _flags[index] = value;
        }
    }

    private ConstantValue VariableValue(ReadOnlySpan<ConstantValue> arguments)
    {
        if (arguments.Length is < 2 or > 3)
            throw new ArgumentException("VariableValue requires a sigil, path, and optional value.");
        string sigil = arguments[0].ConvertToString();
        bool flag = sigil switch { "#" => true, "$" => false, _ => throw new ArgumentException("Invalid global sigil.") };
        string path = arguments[1].ConvertToString();
        int firstBracket = path.IndexOf('[');
        string name = firstBracket >= 0 ? path[..firstBracket] : path;
        if (name.Length == 0 || name.Contains(']') || name.Contains('$') || name.Contains('#'))
            throw new ArgumentException("Invalid global path.");
        var keys = new List<ConstantValue>();
        int position = firstBracket >= 0 ? firstBracket : path.Length;
        while (position < path.Length)
        {
            if (path[position] != '[') throw new ArgumentException("Invalid indexed global path.");
            int close = path.IndexOf(']', position + 1);
            if (close < 0) throw new ArgumentException("Unterminated global index.");
            string key = path[(position + 1)..close];
            if (key.Length == 0 || key.Contains('[')) throw new ArgumentException("Invalid global index.");
            keys.Add(ConstantValue.String(key));
            position = close + 1;
        }
        bool setter = arguments.Length == 3;
        var lookup = flag ? GlobalsLookup.Flags : GlobalsLookup.Variables;
        if (!lookup.TryGetValue(name, out int slot))
        {
            if (!setter) return ConstantValue.Number(0);
            slot = lookup.Count;
            if (slot > short.MaxValue - 2) throw new InvalidOperationException("Too many dynamic globals.");
            UpdateGlobals(GlobalsLookup.WithGlobal(name, flag));
        }
        ref ConstantValue root = ref (flag ? ref GetFlag(slot) : ref GetVariable(slot));
        if (keys.Count == 0)
        {
            if (setter)
            {
                if (root.Array is { } array) { array.Scalar = arguments[2].Scalar; root = array.Value; }
                else root = arguments[2];
            }
            return root;
        }
        NsArray node = EnsureArray(root.WithSlot((short)(flag ? -slot - 2 : slot)));
        foreach (ConstantValue key in keys) node = node.Get(key);
        if (setter) node.Scalar = arguments[2].Scalar;
        return node.Value;
    }

    private static NsxModule SceneCaller(ref NsScriptThreadState thread)
    {
        ReadOnlySpan<CallFrame> frames = thread.CallFrameStack.AsSpan();
        for (int i = frames.Length - 1; i >= 0; i--)
            if (frames[i].RuntimeInfo.SubroutineKind != SubroutineKind.Function) return frames[i].Module;
        return frames[0].Module;
    }

    private NsxModule GetImportedModule(NsxModule owner, string name)
    {
        name = _moduleLocator.NormalizeModuleName(name);
        return owner.ScopedModules is { } modules && modules.TryGetValue(name, out NsxModule? module)
            ? module : GetModule(name);
    }

    private NsxModule GetSceneModule(string name, NsxModule caller)
    {
        name = _moduleLocator.NormalizeModuleName(name);
        if (name == caller.Name) return caller;
        string[] callerScopes = caller.SourceScopes.Length > 0 ? caller.SourceScopes : [caller.Name];
        string cacheKey = name + "\0" + string.Join('\0', callerScopes);
        if (_scopedSceneModules.TryGetValue(cacheKey, out NsxModule? cached)) return cached;
        if (_moduleLocator.CompileModuleIfMissing(name, GlobalsLookup, _loadedModules, callerScopes) is not { } compiled)
            return GetModule(name);
        UpdateGlobals(compiled.Globals);
        string[] scopes = new[] { name }.Concat(callerScopes).Distinct(StringComparer.Ordinal).ToArray();
        var modules = new Dictionary<string, NsxModule>();
        foreach ((string moduleName, byte[] bytes) in compiled.Modules)
            modules.Add(moduleName, NsxModule.LoadModule(new MemoryStream(bytes), moduleName));
        foreach (NsxModule module in modules.Values)
        {
            module.SourceScopes = scopes;
            module.ScopedModules = modules;
        }
        NsxModule result = modules[name];
        _scopedSceneModules.Add(cacheKey, result);
        return result;
    }

    private CallFrame ResolveScene(NsxModule caller, string target)
    {
        int arrow = target.IndexOf("->", StringComparison.Ordinal);
        NsxModule module = caller;
        string scene = target;
        if (arrow >= 0)
        {
            string moduleName = target[..arrow];
            scene = target[(arrow + 2)..];
            if (moduleName.Length > 0 && moduleName != "@") module = GetSceneModule(moduleName, caller);
        }
        if (tryFind(module, new HashSet<string>(), out CallFrame frame)) return frame;
        throw new InvalidOperationException($"Scene '{scene}' was not found in module '{module.Name}'.");

        bool tryFind(NsxModule candidate, HashSet<string> visited, out CallFrame result)
        {
            if (visited.Add(candidate.Name))
            {
                if (candidate.TryLookupSubroutineIndex(scene, out int index)
                    && candidate.GetSubroutineRuntimeInfo(index).SubroutineKind == SubroutineKind.Scene)
                {
                    result = CreateEntryPointCallFrame(candidate, (ushort)index);
                    return true;
                }
                foreach (string import in candidate.Imports)
                    if (tryFind(GetImportedModule(candidate, import), visited, out result)) return true;
            }
            result = default;
            return false;
        }
    }

    private NsArray EnsureArray(in ConstantValue value)
    {
        if (value.Array is { } existing) return existing;
        var array = new NsArray(value);
        if (value.GetSlotInfo(out short slot))
        {
            if (slot >= 0) GetVariable(slot) = value.WithArray(array);
            else GetFlag(-slot - 2) = value.WithArray(array);
        }
        return array;
    }

    private static ConstantValue BinOp(
        in ConstantValue left,
        BinaryOperatorKind opKind,
        in ConstantValue right)
    {
        return opKind switch
        {
            BinaryOperatorKind.Add => left + right,
            BinaryOperatorKind.Subtract => left - right,
            BinaryOperatorKind.Multiply => left * right,
            BinaryOperatorKind.Divide => left / right,
            BinaryOperatorKind.LessThan => left < right,
            BinaryOperatorKind.LessThanOrEqual => left <= right,
            BinaryOperatorKind.GreaterThan => left > right,
            BinaryOperatorKind.GreaterThanOrEqual => left >= right,
            BinaryOperatorKind.And => left && right,
            BinaryOperatorKind.Or => left || right,
            BinaryOperatorKind.Remainder => left % right,
            _ => throw ThrowHelper.UnexpectedValueOf<BinaryOperatorKind>()
        };
    }

    private static CallFrame CreateEntryPointCallFrame(NsxModule module, ushort subroutineIndex)
    {
        Subroutine subroutine = module.GetSubroutine(subroutineIndex);
        return new CallFrame(module, subroutineIndex, subroutine.BytecodeStart);
    }

    private void ReportException(in NsScriptThreadState thread, Exception exception)
    {
        LogMessage message = _log.ForLevel(LogLevel.Error)
            .Append("Runtime error: ")
            .Append(exception.Message)
            .Append("\n");

        message = WriteStackTrace(thread.CallFrameStack, message);
        message.Log();
    }

    private LogMessage WriteStackTrace(ValueStack<CallFrame> frames, LogMessage message)
    {
        using SourceMappingScope sourceMapping = _moduleLocator.BeginSourceMapping();
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            ref CallFrame frame = ref frames[i];
            NsxModule module = frame.Module;
            string routineName = frame.RuntimeInfo.SubroutineName;
            CodeOffset codeOffset = i == frames.Count - 1
                ? frame.ProgramCounter
                : frame.ProgramCounter - 1;

            int? lineNumber = null;
            if (module.GetSourceSpan(codeOffset) is { } sourceSpan)
            {
                SourceText sourceText = sourceMapping.GetSourceText(module.Name);
                lineNumber = sourceText.GetLineNumberFromPosition(sourceSpan.Start) + 1;
                if (i == frames.Count - 1)
                {
                    ReadOnlySpan<char> context = sourceText.GetCharacterSpan(sourceSpan);
                    message = message.Append($"Context: {context.TrimEnd()}\n");
                }
            }

            message = message.Append($"    at {routineName} in {module.Name}.nss");
            if (lineNumber is not null)
            {
                message = message.Append($":line {lineNumber}");
            }

            message = message.Append('\n');
        }

        return message;
    }
}
