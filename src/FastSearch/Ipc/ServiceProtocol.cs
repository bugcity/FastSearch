using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Principal;
using System.Text;
using FastSearch.Core;

namespace FastSearch.Ipc;

internal static class ServiceProtocol
{
    internal const int Version = 2, PageSize = 128, MaxResults = 20_000_000;
    internal const string ServiceName = "FastSearch.IndexService";
    internal static string PipeName(string sid) => "FastSearch.Service." + sid;
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FastSearch");
    internal static string OwnerSid => new SecurityIdentifier(File.ReadAllText(Path.Combine(DataDirectory, "owner.sid")).Trim()).Value;
    internal static void WriteHeader(BinaryWriter writer, byte command)
    {
        Span<byte> header = stackalloc byte[9];
        BinaryPrimitives.WriteInt32LittleEndian(header, 0x32534646);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
        header[8] = command;
        writer.Write(header);
    }
    internal static byte ReadHeader(BinaryReader reader)
    {
        if (reader.ReadInt32() != 0x32534646 || reader.ReadInt32() != Version)
            throw new InvalidDataException("索引サービスと検索画面のバージョンが一致しません。サービスを更新してください。");
        return reader.ReadByte();
    }
    internal static void WriteRow(BinaryWriter writer, SearchResult row)
    { VolumeProtocol.WriteEntry(writer, row.Entry); VolumeProtocol.WriteString(writer, row.Folder); }
    internal static SearchResult ReadRow(BinaryReader reader)
    {
        var entry = VolumeProtocol.ReadEntry(reader);
        string folder = VolumeProtocol.ReadString(reader, 32760);
        if (entry.IsDirectory || !folder.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase) ||
            folder.Contains('\0') || folder.Contains('/') || folder.Contains(@"\..\") || folder.EndsWith(@"\..", StringComparison.Ordinal))
            throw new InvalidDataException("検索結果のパスが不正です。");
        return new(entry, folder);
    }
}
