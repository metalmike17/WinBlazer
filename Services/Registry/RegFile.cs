using System.Globalization;
using System.Text;

namespace PCOptimizer.Services.RegistryTools;

/// <summary>
/// Writes standard .reg files and reads back only the format written here. Strings are stored as hex,
/// which keeps their exact bytes.
/// </summary>
public static class RegFile
{
    private const string Header = "Windows Registry Editor Version 5.00";
    private const int Binary = 3, DWord = 4;

    public static string Write(IEnumerable<KeyData> keys)
    {
        var text = new StringBuilder(Header + "\r\n");
        foreach (var key in keys)
        {
            text.Append($"\r\n[{RegistryStore.HiveName(key.Hive)}\\{key.Path}]\r\n");
            foreach (var value in key.Values)
            {
                if (value.Name.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || key.Path.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new InvalidDataException("A name cannot be stored safely in a .reg file.");
                text.Append(value.Name == "" ? "@" : $"\"{value.Name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
                text.Append('=');
                if (value.Type == DWord && value.Data.Length == 4)
                    text.Append("dword:" + BitConverter.ToUInt32(value.Data).ToString("x8"));
                else
                    text.Append((value.Type == Binary ? "hex:" : $"hex({value.Type:x}):") +
                                string.Join(",", value.Data.Select(b => b.ToString("x2"))));
                text.Append("\r\n");
            }
        }
        return text.ToString();
    }

    public static List<KeyData> Read(string text)
    {
        var lines = text.Split("\r\n");
        if (lines.Length == 0 || lines[0].TrimStart('﻿') != Header) throw Bad();
        var keys = new List<KeyData>();
        List<RawValue>? values = null;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            if (line[0] == '[')
            {
                if (line[^1] != ']') throw Bad();
                var full = line[1..^1];
                var slash = full.IndexOf('\\');
                if (slash < 0) throw Bad();
                var hive = full[..slash] switch
                {
                    "HKEY_CURRENT_USER" => Hive.CurrentUser,
                    "HKEY_LOCAL_MACHINE" => Hive.LocalMachine,
                    _ => throw Bad()
                };
                values = new List<RawValue>();
                keys.Add(new KeyData(hive, full[(slash + 1)..], values));
                continue;
            }
            if (values == null) throw Bad();
            values.Add(ReadValue(line));
        }
        return keys;
    }

    private static RawValue ReadValue(string line)
    {
        var name = new StringBuilder();
        var i = 0;
        if (line[0] == '@') i = 1;
        else
        {
            if (line[0] != '"') throw Bad();
            for (i = 1; i < line.Length && line[i] != '"'; i++)
            {
                if (line[i] == '\\' && ++i >= line.Length) throw Bad();
                name.Append(line[i]);
            }
            if (i >= line.Length) throw Bad();
            i++;
        }
        if (i >= line.Length || line[i] != '=') throw Bad();
        var data = line[(i + 1)..];
        try
        {
            if (data.StartsWith("dword:", StringComparison.Ordinal))
                return new RawValue(name.ToString(), DWord,
                    BitConverter.GetBytes(uint.Parse(data[6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            var colon = data.IndexOf(':');
            if (colon < 0) throw Bad();
            var kind = data[..colon];
            int type;
            if (kind == "hex") type = Binary;
            else if (kind.StartsWith("hex(", StringComparison.Ordinal) && kind.EndsWith(')'))
                type = int.Parse(kind[4..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            else throw Bad();
            var body = data[(colon + 1)..];
            var bytes = body.Length == 0 ? Array.Empty<byte>() : body.Split(',')
                .Select(b => byte.Parse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            return new RawValue(name.ToString(), type, bytes);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw Bad(); }
    }

    private static InvalidDataException Bad() => new("This is not a backup file this app can read.");
}
