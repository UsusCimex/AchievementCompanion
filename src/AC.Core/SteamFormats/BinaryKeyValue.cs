using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AC.Core.SteamFormats;

/// <summary>Type byte of an entry in Valve's binary KeyValues.</summary>
public enum KvType : byte
{
    Object = 0,
    String = 1,
    Int32 = 2,
    Float32 = 3,
    Pointer = 4,
    Color = 6,
    UInt64 = 7,
    End = 8,
    Int64 = 10,
    AlternateEnd = 11,
}

/// <summary>
/// A key with a value or nested keys. A missing key gives <see cref="Missing"/>, so lookups chain without checks:
/// <c>root["480"]["stats"]</c>.
/// </summary>
public sealed class KvNode(string name, KvType type, object? value, IReadOnlyList<KvNode>? children)
{
    public static readonly KvNode Missing = new("", KvType.Object, null, null);

    public string Name { get; } = name;
    public KvType Type { get; } = type;
    public object? Value { get; } = value;
    public IReadOnlyList<KvNode> Children { get; } = children ?? [];
    public bool Exists => !ReferenceEquals(this, Missing);

    public KvNode this[string key]
    {
        get
        {
            foreach (var child in Children)
                if (string.Equals(child.Name, key, StringComparison.OrdinalIgnoreCase)) return child;
            return Missing;
        }
    }

    public string Text(string fallback = "") => Value switch
    {
        null => fallback,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => fallback,
    };

    public long Number(long fallback = 0) => Value switch
    {
        int i => i,
        uint u => u,
        long l => l,
        ulong u => unchecked((long)u),
        float f => (long)f,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => fallback,
    };

    public bool Flag => Number() != 0;

    public override string ToString() => Children.Count > 0 ? $"{Name} {{{Children.Count}}}" : $"{Name} = {Value}";
}

/// <summary>
/// Valve's binary KeyValues (the stats files in Steam/appcache/stats): a list of entries, each a type byte,
/// a zero-terminated UTF-8 name and a value; an object holds entries up to its end byte.
/// </summary>
public static class BinaryKeyValue
{
    private const int MaxDepth = 64;

    /// <summary>The file's root, or null when it is missing, locked or not in this format.</summary>
    public static KvNode? Load(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    public static KvNode Read(Stream input)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        return new KvNode("", KvType.Object, null, ReadObject(reader, 0));
    }

    private static List<KvNode> ReadObject(BinaryReader reader, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("nesting too deep");
        var entries = new List<KvNode>();
        // Steam leaves out the root's end byte in some files.
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var type = (KvType)reader.ReadByte();
            if (type is KvType.End or KvType.AlternateEnd) break;
            var name = ReadString(reader);
            entries.Add(type switch
            {
                KvType.Object => new KvNode(name, type, null, ReadObject(reader, depth + 1)),
                KvType.String => new KvNode(name, type, ReadString(reader), null),
                KvType.Int32 => new KvNode(name, type, reader.ReadInt32(), null),
                KvType.Float32 => new KvNode(name, type, reader.ReadSingle(), null),
                KvType.Pointer or KvType.Color => new KvNode(name, type, reader.ReadUInt32(), null),
                KvType.UInt64 => new KvNode(name, type, reader.ReadUInt64(), null),
                KvType.Int64 => new KvNode(name, type, reader.ReadInt64(), null),
                _ => throw new FormatException($"unknown type {(byte)type}"),
            });
        }
        return entries;
    }

    private static string ReadString(BinaryReader reader)
    {
        var bytes = new List<byte>(32);
        for (byte b; (b = reader.ReadByte()) != 0;) bytes.Add(b);
        return Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(bytes));
    }
}
