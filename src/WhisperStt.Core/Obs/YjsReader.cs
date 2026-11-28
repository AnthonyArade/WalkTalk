using System.Text;

namespace WhisperStt.Core.Obs;

// Reads a Yjs document (an "update", format v1) into plain values: maps become Dictionary<string, object?>,
// arrays List<object?>, texts string, and the rest string, double, long, bool, byte[] or null.
// Only reading is supported, which is all importing a template made by another app needs.
public static class YjsReader
{
    public static Dictionary<string, object?> Read(byte[] update)
    {
        var decoder = new Decoder(update);
        var items = new List<Item>();
        var clients = decoder.VarUint();
        for (ulong c = 0; c < clients; c++)
        {
            var structs = decoder.VarUint();
            var client = decoder.VarUint();
            var clock = decoder.VarUint();
            for (ulong s = 0; s < structs; s++)
            {
                var info = decoder.Byte();
                var reference = info & 0x1F;
                if (reference is 0 or 10) // GC or skip: a range of clocks with nothing in it
                {
                    clock += decoder.VarUint();
                    continue;
                }
                var item = new Item { Client = client, Clock = clock };
                if ((info & 0x80) != 0)
                    item.Origin = (decoder.VarUint(), decoder.VarUint());
                if ((info & 0x40) != 0)
                    item.RightOrigin = (decoder.VarUint(), decoder.VarUint());
                if ((info & 0xC0) == 0)
                {
                    if (decoder.VarUint() == 1)
                        item.RootName = decoder.String();
                    else
                        item.ParentId = (decoder.VarUint(), decoder.VarUint());
                    if ((info & 0x20) != 0)
                        item.Key = decoder.String();
                }
                ReadContent(decoder, reference, item);
                items.Add(item);
                clock += item.Length;
            }
        }

        // The delete set: ranges of clocks, per client, that were removed.
        var deleted = new Dictionary<ulong, List<(ulong From, ulong To)>>();
        if (!decoder.End)
        {
            var count = decoder.VarUint();
            for (ulong c = 0; c < count; c++)
            {
                var client = decoder.VarUint();
                var ranges = decoder.VarUint();
                var list = deleted[client] = [];
                for (ulong r = 0; r < ranges; r++)
                {
                    var from = decoder.VarUint();
                    list.Add((from, from + decoder.VarUint()));
                }
            }
        }
        return new Builder(items, deleted).Build();
    }

    private static void ReadContent(Decoder d, int reference, Item item)
    {
        switch (reference)
        {
            case 1: // deleted
                item.Length = d.VarUint();
                item.Deleted = true;
                break;
            case 2: // JSON
                {
                    var n = d.VarUint();
                    for (ulong i = 0; i < n; i++)
                    {
                        var text = d.String();
                        item.Values.Add(text == "undefined" ? null : ParseJson(text));
                    }
                    item.Length = n;
                    break;
                }
            case 3: // binary
                item.Values.Add(d.Bytes());
                item.Length = 1;
                break;
            case 4: // string (its length counts UTF-16 units, as in JavaScript)
                {
                    var text = d.String();
                    item.Text = text;
                    item.Length = (ulong)text.Length;
                    break;
                }
            case 5: // embed
                item.Values.Add(ParseJson(d.String()));
                item.Length = 1;
                break;
            case 6: // format (rich text attributes): not needed, skipped
                d.String();
                d.String();
                item.Length = 1;
                item.Deleted = true;
                break;
            case 7: // a nested type
                {
                    var type = d.VarUint();
                    if (type is 3 or 5)
                        d.String();
                    item.TypeRef = (int)type;
                    item.Length = 1;
                    break;
                }
            case 8: // any
                {
                    var n = d.VarUint();
                    for (ulong i = 0; i < n; i++)
                        item.Values.Add(d.Any());
                    item.Length = n;
                    break;
                }
            case 9: // a sub-document: not needed
                d.String();
                d.Any();
                item.Length = 1;
                item.Deleted = true;
                break;
            default:
                throw new InvalidDataException($"Unknown Yjs content {reference}.");
        }
    }

    private static object? ParseJson(string text)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return FromJson(doc.RootElement);
        }
        catch (System.Text.Json.JsonException)
        {
            return text;
        }
    }

    private static object? FromJson(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value)),
        System.Text.Json.JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        System.Text.Json.JsonValueKind.String => e.GetString(),
        System.Text.Json.JsonValueKind.Number => e.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => null
    };

    private sealed class Item
    {
        public ulong Client, Clock, Length;
        public (ulong Client, ulong Clock)? Origin, RightOrigin, ParentId;
        public string? RootName, Key, Text;
        public int TypeRef = -1;
        public bool Deleted;
        public List<object?> Values = [];
        public Item? Parent; // resolved: the type item this belongs to (null for a root type)
        public string? Root;  // resolved: the root type's name when Parent is null
    }

    private sealed class Builder(List<Item> items, Dictionary<ulong, List<(ulong From, ulong To)>> deleted)
    {
        private readonly Dictionary<ulong, List<Item>> _byClient = items.GroupBy(i => i.Client)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Clock).ToList());

        private Item? Find((ulong Client, ulong Clock) id)
        {
            if (!_byClient.TryGetValue(id.Client, out var list))
                return null;
            int lo = 0, hi = list.Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                var it = list[mid];
                if (id.Clock < it.Clock) hi = mid - 1;
                else if (id.Clock >= it.Clock + Math.Max(1, it.Length)) lo = mid + 1;
                else return it;
            }
            return null;
        }

        private bool IsDeleted(Item item) =>
            item.Deleted || (deleted.TryGetValue(item.Client, out var ranges) &&
                             ranges.Any(r => item.Clock >= r.From && item.Clock + Math.Max(1, item.Length) <= r.To));

        // An item written next to another one (origin) has the same parent and, in a map, the same key.
        private void Resolve(Item item, HashSet<Item> seen)
        {
            if (item.Parent is not null || item.Root is not null || !seen.Add(item))
                return;
            if (item.RootName is not null) { item.Root = item.RootName; return; }
            if (item.ParentId is { } pid) { item.Parent = Find(pid); return; }
            var neighbour = (item.Origin is { } o ? Find(o) : null) ?? (item.RightOrigin is { } r ? Find(r) : null);
            if (neighbour is null)
                return;
            Resolve(neighbour, seen);
            item.Parent = neighbour.Parent;
            item.Root = neighbour.Root;
            item.Key ??= neighbour.Key;
        }

        public Dictionary<string, object?> Build()
        {
            foreach (var item in items)
                Resolve(item, []);
            var children = items.Where(i => i.Parent is not null || i.Root is not null)
                .GroupBy(i => (object?)i.Parent ?? i.Root!).ToDictionary(g => g.Key, g => g.ToList());

            var result = new Dictionary<string, object?>();
            foreach (var root in items.Where(i => i.Root is not null).Select(i => i.Root!).Distinct())
                result[root] = Value(children.GetValueOrDefault(root) ?? [], children, 0);
            return result;
        }

        // A type's content: a map when its items have keys, otherwise an array (or a text, when it is all strings).
        private object? Value(List<Item> content, Dictionary<object, List<Item>> children, int depth)
        {
            if (depth > 64)
                return null;
            if (content.Any(i => i.Key is not null))
            {
                var map = new Dictionary<string, object?>();
                foreach (var group in content.Where(i => i.Key is not null).GroupBy(i => i.Key!))
                {
                    // The current value is the one nothing was written after (the last of the key's chain).
                    var live = group.Where(i => !IsDeleted(i)).ToList();
                    var winner = live.FirstOrDefault(i => !live.Any(o => o.Origin is { } id && Find(id) == i)) ?? live.LastOrDefault();
                    if (winner is not null)
                        map[group.Key] = One(winner, children, depth);
                }
                return map;
            }

            var ordered = Order(content).Where(i => !IsDeleted(i)).ToList();
            if (ordered.Count > 0 && ordered.All(i => i.Text is not null))
                return string.Concat(ordered.Select(i => i.Text));
            var list = new List<object?>();
            foreach (var item in ordered)
            {
                if (item.TypeRef >= 0) list.Add(Nested(item, children, depth));
                else if (item.Text is not null) list.Add(item.Text);
                else list.AddRange(item.Values);
            }
            return list;
        }

        private object? One(Item item, Dictionary<object, List<Item>> children, int depth) =>
            item.TypeRef >= 0 ? Nested(item, children, depth) : item.Text ?? item.Values.LastOrDefault();

        private object? Nested(Item item, Dictionary<object, List<Item>> children, int depth)
        {
            var value = Value(children.GetValueOrDefault(item) ?? [], children, depth + 1);
            return item.TypeRef switch
            {
                1 when value is List<object?> { Count: 0 } => new Dictionary<string, object?>(),
                2 or 6 when value is List<object?> { Count: 0 } => "",
                _ => value
            };
        }

        // Array order: each item follows the one it was inserted after (its origin).
        private List<Item> Order(List<Item> content)
        {
            var after = new Dictionary<Item, List<Item>>();
            var first = new List<Item>();
            foreach (var item in content)
            {
                var left = item.Origin is { } o ? Find(o) : null;
                if (left is null || !content.Contains(left)) first.Add(item);
                else (after.TryGetValue(left, out var l) ? l : after[left] = []).Add(item);
            }
            var result = new List<Item>();
            var stack = new Stack<Item>(first.OrderBy(i => i.Client).ThenBy(i => i.Clock).Reverse());
            var seen = new HashSet<Item>();
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                if (!seen.Add(item))
                    continue;
                result.Add(item);
                if (after.TryGetValue(item, out var next))
                    foreach (var n in next.OrderBy(i => i.Client).ThenBy(i => i.Clock).Reverse())
                        stack.Push(n);
            }
            return result;
        }
    }

    private sealed class Decoder(byte[] data)
    {
        private int _at;
        public bool End => _at >= data.Length;

        public byte Byte() => _at < data.Length ? data[_at++] : throw new InvalidDataException("The file ends too soon.");

        public ulong VarUint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = Byte();
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                    return value;
            }
            throw new InvalidDataException("A number in the file is too long.");
        }

        private long VarInt()
        {
            var b = Byte();
            long value = b & 0x3F;
            var negative = (b & 0x40) != 0;
            var shift = 6;
            while ((b & 0x80) != 0)
            {
                b = Byte();
                value |= (long)(b & 0x7F) << shift;
                shift += 7;
            }
            return negative ? -value : value;
        }

        public byte[] Bytes()
        {
            var length = (int)VarUint();
            if (length < 0 || _at + length > data.Length)
                throw new InvalidDataException("The file ends too soon.");
            var bytes = data.AsSpan(_at, length).ToArray();
            _at += length;
            return bytes;
        }

        public string String() => Encoding.UTF8.GetString(Bytes());

        public object? Any()
        {
            switch (Byte())
            {
                case 127: return null; // undefined
                case 126: return null;
                case 125: return VarInt();
                case 124: { var v = BitConverter.ToSingle(Reverse(4)); return (double)v; }
                case 123: return BitConverter.ToDouble(Reverse(8));
                case 122: return BitConverter.ToInt64(Reverse(8));
                case 121: return false;
                case 120: return true;
                case 119: return String();
                case 118:
                    {
                        var n = VarUint();
                        var map = new Dictionary<string, object?>();
                        for (ulong i = 0; i < n; i++)
                        {
                            var key = String();
                            map[key] = Any();
                        }
                        return map;
                    }
                case 117:
                    {
                        var n = VarUint();
                        var list = new List<object?>();
                        for (ulong i = 0; i < n; i++)
                            list.Add(Any());
                        return list;
                    }
                case 116: return Bytes();
                default: return null;
            }
        }

        // Numbers are written big-endian.
        private byte[] Reverse(int count)
        {
            if (_at + count > data.Length)
                throw new InvalidDataException("The file ends too soon.");
            var bytes = data.AsSpan(_at, count).ToArray();
            _at += count;
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return bytes;
        }
    }
}
