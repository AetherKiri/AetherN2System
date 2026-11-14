using System;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NitroSharp.NsScript.Utilities;

namespace NitroSharp.NsScript.VM;

internal sealed class GlobalsLookupTable
{
    private readonly Stream _stream;
    private readonly int _nameHeapStart;
    private readonly Dictionary<string, int> _sysVariables;
    private readonly Dictionary<string, int> _sysFlags;

    private readonly Lazy<FrozenDictionary<string, int>> _allVariables;
    private readonly Lazy<FrozenDictionary<string, int>> _allFlags;

    private GlobalsLookupTable(
        Stream stream,
        int[] varOffsets,
        int[] sysVars,
        int[] flagOffsets,
        int[] sysFlags,
        int nameHeapStart)
    {
        _stream = stream;
        _nameHeapStart = nameHeapStart;
        _sysVariables = new Dictionary<string, int>(sysVars.Length);
        _sysFlags = new Dictionary<string, int>();
        foreach (int idx in sysVars)
        {
            _sysVariables.Add(ReadString(varOffsets, idx), idx);
        }
        foreach (int idx in sysFlags)
        {
            _sysFlags.Add(ReadString(flagOffsets, idx), idx);
        }

        _allVariables = new Lazy<FrozenDictionary<string, int>>(CreateLookup(varOffsets));
        _allFlags = new Lazy<FrozenDictionary<string, int>>(CreateLookup(flagOffsets));
    }

    public FrozenDictionary<string, int> Variables => _allVariables.Value;
    public FrozenDictionary<string, int> Flags => _allFlags.Value;

    private GlobalsLookupTable(Dictionary<string, int> variables, Dictionary<string, int> flags)
    {
        _stream = Stream.Null;
        _nameHeapStart = 0;
        _allVariables = new(() => variables.ToFrozenDictionary());
        _allFlags = new(() => flags.ToFrozenDictionary());
        _sysVariables = new();
        _sysFlags = new();
        foreach ((string name, int slot) in variables)
            if (name.StartsWith("SYSTEM", StringComparison.Ordinal)) _sysVariables.Add(name, slot);
        foreach ((string name, int slot) in flags)
            if (name.StartsWith("SYSTEM", StringComparison.Ordinal)) _sysFlags.Add(name, slot);
    }

    internal GlobalsLookupTable WithGlobal(string name, bool flag)
    {
        var variables = new Dictionary<string, int>(Variables);
        var flags = new Dictionary<string, int>(Flags);
        Dictionary<string, int> target = flag ? flags : variables;
        target.Add(name, target.Count);
        return new GlobalsLookupTable(variables, flags);
    }

    private FrozenDictionary<string, int> CreateLookup(int[] nameOffsets)
    {
        var lookup = new Dictionary<string, int>();
        for (int i = 0 ; i < nameOffsets.Length; i++)
        {
            lookup.Add(ReadString(nameOffsets, i), i);
        }

        return lookup.ToFrozenDictionary();
    }

    public static GlobalsLookupTable Load(Stream stream)
    {
        (int[] varOffsets, int[] sysVars) = ReadTable(stream);
        (int[] flagOffsets, int[] sysFlags) = ReadTable(stream);
        int nameHeapStart = (int)stream.Position;
        return new GlobalsLookupTable(
            stream,
            varOffsets, sysVars,
            flagOffsets, sysFlags,
            nameHeapStart
        );
    }

    private static (int[] offets, int[] indices) ReadTable(Stream stream)
    {
        int entryCount = ReadUInt16(stream);
        var offsetTable = new byte[entryCount * 4];
        stream.ReadExactly(offsetTable);
        var offsetReader = new BufferReader(offsetTable);
        var nameOffsets = new int[entryCount];
        for (int i = 0; i < entryCount; i++)
        {
            nameOffsets[i] = offsetReader.ReadInt32LE();
        }

        int sysCount = ReadUInt16(stream);
        var sysList = new byte[sysCount * 2];
        stream.ReadExactly(sysList);
        var sysListReader = new BufferReader(sysList);
        var sysIndices = new int[sysCount];
        for (int i = 0; i < sysCount; i++)
        {
            sysIndices[i] = sysListReader.ReadUInt16LE();
        }

        return (nameOffsets, sysIndices);
    }

    public bool TryLookupSystemFlag(string name, out int index)
        => _sysFlags.TryGetValue(name, out index);

    public bool TryLookupSystemVariable(string name, out int index)
        => _sysVariables.TryGetValue(name, out index);

    private static int ReadUInt16(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[2];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    }

    private string ReadString(int[] table, int index)
    {
        const int stackallocSize = 256;
        _stream.Position = _nameHeapStart + table[index];
        int length = ReadUInt16(_stream);
        Span<byte> bytes = length <= stackallocSize
            ? (stackalloc byte[stackallocSize])[..length]
            : new byte[length];
        _stream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }
}
