using System;
using System.Collections.Generic;
using System.Globalization;
using MessagePack;

namespace NitroSharp.NsScript;

// Each child is a value node: its own scalar survives Array(child, ...),
// and named and numeric indices address the same node.
internal sealed class NsArray(ConstantValue scalar)
{
    public ConstantValue Scalar = scalar.Scalar;
    private readonly List<NsArray> _items = [];
    private readonly Dictionary<string, NsArray> _names = new(StringComparer.Ordinal);

    public int Count => _items.Count;

    internal IEnumerable<(ConstantValue Key, NsArray Node)> SaveChildren()
    {
        for (int i = 0; i < _items.Count; i++) yield return (ConstantValue.Number(i), _items[i]);
        foreach ((string name, NsArray node) in _names) yield return (ConstantValue.String(name), node);
    }

    public ConstantValue Value => Scalar.WithArray(this);

    public NsArray(ConstantValue scalar, ref MessagePackReader reader) : this(scalar)
    {
        if (reader.ReadArrayHeader() != 3)
            throw new MessagePackSerializationException("Invalid indexed value record.");
        int orderedCount = reader.ReadInt32();
        int count = reader.ReadArrayHeader();
        if (orderedCount < 0 || orderedCount > count)
            throw new MessagePackSerializationException("Invalid indexed value count.");
        var nodes = new NsArray[count];
        for (int i = 0; i < count; i++)
        {
            var value = new ConstantValue(ref reader);
            nodes[i] = value.Array ?? new NsArray(value);
            if (i < orderedCount) _items.Add(nodes[i]);
        }
        int nameCount = reader.ReadMapHeader();
        for (int i = 0; i < nameCount; i++)
        {
            string name = reader.ReadString()!;
            int index = reader.ReadInt32();
            if ((uint)index >= (uint)nodes.Length)
                throw new MessagePackSerializationException("Invalid named value index.");
            _names.Add(name, nodes[index]);
        }
    }

    public void Serialize(ref MessagePackWriter writer)
    {
        var nodes = new List<NsArray>(_items);
        var indices = new Dictionary<NsArray, int>();
        for (int i = 0; i < nodes.Count; i++) indices.Add(nodes[i], i);
        foreach (NsArray node in _names.Values)
        {
            if (indices.ContainsKey(node)) continue;
            indices.Add(node, nodes.Count);
            nodes.Add(node);
        }
        writer.WriteArrayHeader(3);
        writer.Write(_items.Count);
        writer.WriteArrayHeader(nodes.Count);
        foreach (NsArray node in nodes) node.Value.Serialize(ref writer);
        writer.WriteMapHeader(_names.Count);
        foreach ((string name, NsArray node) in _names)
        {
            writer.Write(name);
            writer.Write(indices[node]);
        }
    }

    public void SetItems(ReadOnlySpan<ConstantValue> values)
    {
        _items.Clear();
        _names.Clear();
        foreach (ConstantValue value in values)
            _items.Add(new NsArray(value));
    }

    public void SetNames(ReadOnlySpan<ConstantValue> names)
    {
        _names.Clear();
        for (int i = 0; i < names.Length; i++)
        {
            while (_items.Count <= i) _items.Add(new NsArray(ConstantValue.Number(0)));
            _names[names[i].ConvertToString()] = _items[i];
        }
    }

    public NsArray Get(in ConstantValue key)
    {
        float? number = key.AsNumber();
        if (!number.HasValue && key.AsString() is { } text
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            number = parsed;
        if (number is { } index && index >= 0 && index == MathF.Truncate(index) && index < 1_000_000)
        {
            while (_items.Count <= (int)index) _items.Add(new NsArray(ConstantValue.Number(0)));
            return _items[(int)index];
        }
        string name = key.ConvertToString();
        if (!_names.TryGetValue(name, out NsArray? item))
        {
            item = new NsArray(ConstantValue.Number(0));
            _names.Add(name, item);
        }
        return item;
    }
}
