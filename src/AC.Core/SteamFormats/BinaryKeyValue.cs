// Парсер бинарного формата Valve KeyValues (файлы Steam/appcache/stats/*.bin).
// Написан по мотивам SAM.Game/KeyValue.cs из Steam Achievement Manager
// (c) 2024 Rick (gibbed), zlib license. Это изменённая версия, не оригинал.

using System.Text;

namespace AC.Core.SteamFormats;

public enum KvType : byte
{
    None = 0,
    String = 1,
    Int32 = 2,
    Float32 = 3,
    Pointer = 4,
    WideString = 5,
    Color = 6,
    UInt64 = 7,
    End = 8,
}

public sealed class KvNode
{
    public static readonly KvNode Invalid = new() { Name = "<invalid>" };

    public string Name { get; init; } = "<root>";
    public KvType Type { get; init; }
    public object? Value { get; init; }
    public bool Valid { get; init; }
    public List<KvNode>? Children { get; init; }

    public KvNode this[string key]
    {
        get
        {
            if (Children is null) return Invalid;
            foreach (var c in Children)
                if (string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase)) return c;
            return Invalid;
        }
    }

    public string AsString(string fallback = "") =>
        Valid && Value is not null ? Convert.ToString(Value, System.Globalization.CultureInfo.InvariantCulture) ?? fallback : fallback;

    public int AsInt(int fallback = 0)
    {
        if (!Valid) return fallback;
        return Value switch
        {
            int i => i,
            float f => (int)f,
            ulong u => (int)(u & 0xFFFFFFFF),
            uint u => unchecked((int)u),
            string s when int.TryParse(s, out var v) => v,
            _ => fallback,
        };
    }

    public bool AsBool(bool fallback = false) => Valid ? AsInt(fallback ? 1 : 0) != 0 : fallback;

    public override string ToString() => Valid ? $"{Name} = {Value}" : Name;
}

public static class BinaryKeyValue
{
    public static KvNode? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(fs);
        }
        catch (IOException) { return null; }
        catch (FormatException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static KvNode Read(Stream input)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        var children = ReadChildren(reader, depth: 0);
        return new KvNode { Name = "<root>", Type = KvType.None, Valid = true, Children = children };
    }

    private static List<KvNode> ReadChildren(BinaryReader r, int depth)
    {
        if (depth > 64) throw new FormatException("слишком глубокая вложенность");
        var list = new List<KvNode>();
        while (true)
        {
            if (r.BaseStream.Position >= r.BaseStream.Length) break; // файл без завершающего End
            var type = (KvType)r.ReadByte();
            if (type == KvType.End) break;
            var name = ReadCString(r);
            KvNode node = type switch
            {
                KvType.None => new KvNode { Name = name, Type = type, Valid = true, Children = ReadChildren(r, depth + 1) },
                KvType.String => new KvNode { Name = name, Type = type, Valid = true, Value = ReadCString(r) },
                KvType.Int32 => new KvNode { Name = name, Type = type, Valid = true, Value = r.ReadInt32() },
                KvType.Float32 => new KvNode { Name = name, Type = type, Valid = true, Value = r.ReadSingle() },
                KvType.Pointer or KvType.Color => new KvNode { Name = name, Type = type, Valid = true, Value = r.ReadUInt32() },
                KvType.UInt64 => new KvNode { Name = name, Type = type, Valid = true, Value = r.ReadUInt64() },
                _ => throw new FormatException($"неизвестный тип {(byte)type}"),
            };
            list.Add(node);
        }
        return list;
    }

    private static string ReadCString(BinaryReader r)
    {
        var bytes = new List<byte>(32);
        while (true)
        {
            byte b = r.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
