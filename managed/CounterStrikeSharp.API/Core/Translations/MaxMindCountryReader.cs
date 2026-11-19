using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CounterStrikeSharp.API.Core.Translations;

/// <summary>
/// Minimal reader for MaxMind DB (.mmdb) country databases (GeoLite2-Country, db-ip "IP to Country Lite").
/// Only what a country lookup needs: the search tree and a path walk through the data section to
/// <c>country.iso_code</c>. Written in-tree instead of referencing MaxMind.Db because plugins load with
/// PreferSharedTypes: a host copy of MaxMind.Db would replace the one a GeoIP plugin ships.
/// Format: https://maxmind.github.io/MaxMind-DB/
/// The whole file is held in a byte array, so a reader stays valid after its file is replaced on disk.
/// Instances are immutable and safe to use from any thread.
/// </summary>
internal sealed class MaxMindCountryReader
{
    private static readonly byte[] MetadataMarker =
        new byte[] { 0xAB, 0xCD, 0xEF }.Concat(Encoding.ASCII.GetBytes("MaxMind.com")).ToArray();

    private readonly byte[] _data;
    private readonly int _nodeCount;
    private readonly int _recordSize;
    private readonly int _nodeByteSize;
    private readonly int _dataSectionStart;
    private readonly int _ipv4Start;

    public int IpVersion { get; }
    public string DatabaseType { get; }
    public DateTimeOffset BuildTime { get; }

    private MaxMindCountryReader(byte[] data)
    {
        _data = data;

        var metadataStart = FindMetadataStart(data);
        if (metadataStart < 0) throw new InvalidDataException("No MaxMind DB metadata marker found");

        var pos = metadataStart;
        if (Decode(ref pos, metadataStart) is not Dictionary<string, object?> metadata)
            throw new InvalidDataException("MaxMind DB metadata is not a map");

        _nodeCount = checked((int)ToULong(metadata.GetValueOrDefault("node_count")));
        _recordSize = (int)ToULong(metadata.GetValueOrDefault("record_size"));
        IpVersion = (int)ToULong(metadata.GetValueOrDefault("ip_version"));
        DatabaseType = metadata.GetValueOrDefault("database_type") as string ?? "";
        BuildTime = DateTimeOffset.FromUnixTimeSeconds((long)ToULong(metadata.GetValueOrDefault("build_epoch")));

        if (_recordSize is not (24 or 28 or 32))
            throw new InvalidDataException($"Unsupported MaxMind DB record size {_recordSize}");
        if (IpVersion is not (4 or 6))
            throw new InvalidDataException($"Unsupported MaxMind DB ip_version {IpVersion}");

        _nodeByteSize = _recordSize * 2 / 8;
        var treeSize = (long)_nodeByteSize * _nodeCount;
        if (treeSize + 16 > metadataStart) throw new InvalidDataException("MaxMind DB search tree exceeds the file");
        _dataSectionStart = (int)treeSize + 16;

        // IPv4 addresses live under ::/96 in an IPv6 tree.
        var node = 0;
        if (IpVersion == 6)
        {
            for (var i = 0; i < 96 && node < _nodeCount; i++) node = ReadRecord(node, 0);
        }
        _ipv4Start = node;
    }

    public static MaxMindCountryReader FromBytes(byte[] data) => new(data);

    public static MaxMindCountryReader FromFile(string path) => new(File.ReadAllBytes(path));

    /// <summary>
    /// ISO 3166-1 alpha-2 code of the country the address is in (falls back to registered_country),
    /// or null when the address is not in the database.
    /// </summary>
    public string? LookupCountry(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        int node;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            node = _ipv4Start;
        }
        else
        {
            if (IpVersion == 4) return null;
            node = 0;
        }

        var bitCount = bytes.Length * 8;
        for (var i = 0; i < bitCount && node < _nodeCount; i++)
        {
            var bit = (bytes[i >> 3] >> (7 - (i & 7))) & 1;
            node = ReadRecord(node, bit);
        }

        if (node <= _nodeCount) return null; // == node_count: no data; < : ran out of bits

        var offset = (long)node - _nodeCount - 16 + _dataSectionStart;
        if (offset < _dataSectionStart || offset >= _data.Length)
            throw new InvalidDataException("MaxMind DB record points outside the data section");

        return FindString((int)offset, "country", "iso_code") ?? FindString((int)offset, "registered_country", "iso_code");
    }

    private int ReadRecord(int node, int side)
    {
        var p = node * _nodeByteSize;
        var d = _data;
        switch (_recordSize)
        {
            case 24:
                p += side * 3;
                return (d[p] << 16) | (d[p + 1] << 8) | d[p + 2];
            case 28:
                if (side == 0)
                    return ((d[p + 3] & 0xF0) << 20) | (d[p] << 16) | (d[p + 1] << 8) | d[p + 2];
                return ((d[p + 3] & 0x0F) << 24) | (d[p + 4] << 16) | (d[p + 5] << 8) | d[p + 6];
            default:
                p += side * 4;
                var v = ((uint)d[p] << 24) | ((uint)d[p + 1] << 16) | ((uint)d[p + 2] << 8) | d[p + 3];
                return v > int.MaxValue ? throw new InvalidDataException("MaxMind DB record too large") : (int)v;
        }
    }

    // Walks map keys along `path` from the value at `offset`; returns the string at the end, or null.
    private string? FindString(int offset, params string[] path)
    {
        var pos = offset;
        foreach (var key in path)
        {
            pos = Resolve(pos, _dataSectionStart);
            var (type, size) = ReadControl(ref pos);
            if (type != 7) return null;

            var found = false;
            for (var i = 0; i < size; i++)
            {
                var k = Decode(ref pos, _dataSectionStart) as string;
                if (k == key)
                {
                    found = true;
                    break;
                }
                Skip(ref pos, 0);
            }
            if (!found) return null;
        }

        return Decode(ref pos, _dataSectionStart) as string;
    }

    // If the value at pos is a pointer, returns the position it points to; otherwise pos.
    private int Resolve(int pos, int baseOffset)
    {
        var p = pos;
        var ctrl = Byte(p);
        if (ctrl >> 5 != 1) return pos;
        p++;
        return ReadPointer(ctrl, ref p, baseOffset);
    }

    private int ReadPointer(byte ctrl, ref int p, int baseOffset)
    {
        var ss = (ctrl >> 3) & 3;
        var vvv = ctrl & 7;
        long target = ss switch
        {
            0 => (vvv << 8) | Byte(p),
            1 => ((vvv << 16) | (Byte(p) << 8) | Byte(p + 1)) + 2048,
            2 => ((vvv << 24) | (Byte(p) << 16) | (Byte(p + 1) << 8) | Byte(p + 2)) + 526336,
            _ => ((long)Byte(p) << 24) | ((long)Byte(p + 1) << 16) | ((long)Byte(p + 2) << 8) | Byte(p + 3),
        };
        p += ss + 1;
        target += baseOffset;
        if (target < 0 || target >= _data.Length) throw new InvalidDataException("MaxMind DB pointer out of range");
        return (int)target;
    }

    private (int type, int size) ReadControl(ref int p)
    {
        var ctrl = Byte(p++);
        var type = ctrl >> 5;
        if (type == 0) type = 7 + Byte(p++);
        if (type == 1) throw new InvalidDataException("Unexpected MaxMind DB pointer");

        var size = ctrl & 0x1F;
        switch (size)
        {
            case 29:
                size = 29 + Byte(p);
                p += 1;
                break;
            case 30:
                size = 285 + ((Byte(p) << 8) | Byte(p + 1));
                p += 2;
                break;
            case 31:
                size = 65821 + ((Byte(p) << 16) | (Byte(p + 1) << 8) | Byte(p + 2));
                p += 3;
                break;
        }
        return (type, size);
    }

    private void Skip(ref int p, int depth)
    {
        if (depth > 32) throw new InvalidDataException("MaxMind DB data nests too deep");

        var ctrl = Byte(p);
        if (ctrl >> 5 == 1)
        {
            p += 1 + ((ctrl >> 3) & 3) + 1;
            return;
        }

        var (type, size) = ReadControl(ref p);
        switch (type)
        {
            case 7:
                for (var i = 0; i < size * 2; i++) Skip(ref p, depth + 1);
                break;
            case 11:
                for (var i = 0; i < size; i++) Skip(ref p, depth + 1);
                break;
            case 14: // boolean: value is the size
                break;
            case 3:
                p += 8;
                break;
            case 15:
                p += 4;
                break;
            default:
                p += size;
                break;
        }
        if (p > _data.Length) throw new InvalidDataException("MaxMind DB value runs past the end of the file");
    }

    private object? Decode(ref int p, int baseOffset, int depth = 0)
    {
        if (depth > 32) throw new InvalidDataException("MaxMind DB data nests too deep");

        var ctrl = Byte(p);
        if (ctrl >> 5 == 1)
        {
            p++;
            var target = ReadPointer(ctrl, ref p, baseOffset);
            return Decode(ref target, baseOffset, depth + 1);
        }

        var (type, size) = ReadControl(ref p);
        if (type != 7 && type != 11 && type != 14 && (long)p + size > _data.Length)
            throw new InvalidDataException("MaxMind DB value runs past the end of the file");

        switch (type)
        {
            case 2:
            {
                var s = Encoding.UTF8.GetString(_data, p, size);
                p += size;
                return s;
            }
            case 5 or 6 or 9 or 10:
            {
                if (size > 8) { p += size; return null; } // uint128: not needed here
                ulong v = 0;
                for (var i = 0; i < size; i++) v = (v << 8) | _data[p + i];
                p += size;
                return v;
            }
            case 8:
            {
                long v = 0;
                for (var i = 0; i < size; i++) v = (v << 8) | _data[p + i];
                p += size;
                return v;
            }
            case 7:
            {
                // Every entry takes at least two bytes, so a declared size past the end is corrupt; never
                // pre-size from it (size code 31 alone could ask for ~16.8M entries).
                if (size > (_data.Length - p) / 2) throw new InvalidDataException("MaxMind DB map size exceeds the file");
                var map = new Dictionary<string, object?>(Math.Min(size, 64));
                for (var i = 0; i < size; i++)
                {
                    var key = Decode(ref p, baseOffset, depth + 1) as string
                              ?? throw new InvalidDataException("MaxMind DB map key is not a string");
                    map[key] = Decode(ref p, baseOffset, depth + 1);
                }
                return map;
            }
            case 11:
            {
                if (size > _data.Length - p) throw new InvalidDataException("MaxMind DB array size exceeds the file");
                var list = new List<object?>(Math.Min(size, 64));
                for (var i = 0; i < size; i++) list.Add(Decode(ref p, baseOffset, depth + 1));
                return list;
            }
            case 14:
                return size != 0;
            case 3:
                p += 8;
                return null;
            case 15:
                p += 4;
                return null;
            default:
                p += size;
                return null;
        }
    }

    private byte Byte(int p) =>
        (uint)p < (uint)_data.Length ? _data[p] : throw new InvalidDataException("MaxMind DB read past the end of the file");

    private static ulong ToULong(object? value) => value switch
    {
        ulong u => u,
        long l when l >= 0 => (ulong)l,
        _ => throw new InvalidDataException("MaxMind DB metadata field missing or not an integer"),
    };

    private static int FindMetadataStart(byte[] data)
    {
        // The marker sits within the last 128 KiB; take the last occurrence.
        var searchFrom = Math.Max(0, data.Length - 128 * 1024);
        var idx = data.AsSpan(searchFrom).LastIndexOf(MetadataMarker);
        return idx < 0 ? -1 : searchFrom + idx + MetadataMarker.Length;
    }
}
