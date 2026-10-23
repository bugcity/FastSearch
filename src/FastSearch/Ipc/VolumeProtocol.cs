using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using FastSearch.Core;
using FastSearch.Native;

namespace FastSearch.Ipc;

internal enum VolumeCommand : byte { Hello = 1, Query, Enumerate, Journal, Metadata }

/// <summary>Bounded binary messages. The only exposed operations read the fixed C: volume.</summary>
internal static class VolumeProtocol
{
    internal const int Version = 1, BatchLimit = 4096;
    internal static string ReadString(BinaryReader reader, int limit = 1024)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > limit * 4) throw new InvalidDataException("IPC文字列が長すぎます。");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        string value = new UTF8Encoding(false, true).GetString(bytes);
        if (value.Length > limit) throw new InvalidDataException("IPC文字列が長すぎます。");
        return value;
    }
    internal static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
    internal static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > BatchLimit) throw new InvalidDataException("IPC件数が不正です。");
        return count;
    }
    internal static void WriteRecord(BinaryWriter writer, UsnRecord record)
    {
        writer.Write(record.Id); writer.Write(record.ParentId);
        WriteString(writer, record.Name); writer.Write(record.IsDirectory); writer.Write(record.Reason);
    }
    internal static UsnRecord ReadRecord(BinaryReader reader)
    {
        ulong id = reader.ReadUInt64(), parent = reader.ReadUInt64();
        string name = ReadString(reader, 255);
        if (name.Length == 0 || name.IndexOfAny(['\\', '/', '\0']) >= 0)
            throw new InvalidDataException("IPCファイル名が不正です。");
        return new(id, parent, name, reader.ReadBoolean(), reader.ReadUInt32());
    }
    internal static void WriteEntry(BinaryWriter writer, IndexEntry entry)
    {
        WriteRecord(writer, new(entry.Id, entry.ParentId, entry.Name, entry.IsDirectory, 0));
        writer.Write(entry.Size ?? -1); writer.Write(entry.ModifiedUtc?.Ticks ?? -1);
    }
    internal static IndexEntry ReadEntry(BinaryReader reader)
    {
        var record = ReadRecord(reader);
        long size = reader.ReadInt64(), ticks = reader.ReadInt64();
        if (size < -1 || ticks < -1 || ticks > DateTime.MaxValue.Ticks) throw new InvalidDataException("IPC属性値が不正です。");
        return new(record.Id, record.ParentId, record.Name, record.IsDirectory,
            size < 0 ? null : size, ticks < 0 ? null : new DateTime(ticks, DateTimeKind.Utc));
    }
    internal static void CheckResponse(BinaryReader reader)
    {
        byte status = reader.ReadByte();
        if (status == 0) return;
        string message = ReadString(reader, 4096);
        if (status == 1) throw new JournalUnavailableException(message);
        throw new IOException(message);
    }

    internal static void Serve(Stream stream, IIndexVolume volume, CancellationToken token)
    {
        using var cancellation = token.Register(() => stream.Dispose());
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        if ((VolumeCommand)reader.ReadByte() != VolumeCommand.Hello || reader.ReadInt32() != Version)
            throw new InvalidDataException("IPCバージョンが一致しません。");
        writer.Write((byte)0); writer.Write(Version);
        WriteString(writer, volume.Identity); writer.Write(volume.RootId); writer.Flush();
        while (!token.IsCancellationRequested)
        {
            VolumeCommand command;
            try { command = (VolumeCommand)reader.ReadByte(); }
            catch (EndOfStreamException) { return; }
            // Streaming cannot be rolled back once a response begins. A failed enumeration closes
            // the connection, so the frontend discards the incomplete, unpublished index.
            if (command == VolumeCommand.Enumerate)
            {
                writer.Write((byte)0);
                var block = new List<IndexEntry>(512);
                foreach (var entry in volume.EnumerateEntries(token))
                {
                    token.ThrowIfCancellationRequested();
                    block.Add(entry);
                    if (block.Count == 512) { WriteBlock(); block.Clear(); }
                }
                if (block.Count != 0) WriteBlock();
                writer.Write(0); writer.Flush();
                void WriteBlock()
                {
                    writer.Write(block.Count);
                    foreach (var entry in block) WriteEntry(writer, entry);
                    writer.Flush();
                }
                continue;
            }
            try
            {
                token.ThrowIfCancellationRequested();
                switch (command)
                {
                    case VolumeCommand.Query:
                        var state = volume.QueryJournal();
                        writer.Write((byte)0); writer.Write(state.Id); writer.Write(state.FirstUsn);
                        writer.Write(state.NextUsn); writer.Write(state.LowestValidUsn);
                        break;
                    case VolumeCommand.Journal:
                        var batch = volume.ReadJournal(reader.ReadUInt64(), reader.ReadInt64());
                        writer.Write((byte)0); writer.Write(batch.NextUsn);
                        // Native journal batches can exceed a transport block. Stream bounded chunks.
                        for (int offset = 0; offset < batch.Records.Count; offset += BatchLimit)
                        {
                            int count = Math.Min(BatchLimit, batch.Records.Count - offset);
                            writer.Write(count);
                            for (int i = 0; i < count; i++) WriteRecord(writer, batch.Records[offset + i]);
                        }
                        writer.Write(0);
                        break;
                    case VolumeCommand.Metadata:
                        int length = ReadCount(reader);
                        var records = new UsnRecord[length];
                        for (int i = 0; i < length; i++) records[i] = ReadRecord(reader);
                        var entries = volume.ReadMetadata(records);
                        if (entries.Count != length) throw new InvalidDataException("属性件数が一致しません。");
                        writer.Write((byte)0); writer.Write(entries.Count);
                        foreach (var entry in entries) WriteEntry(writer, entry);
                        break;
                    default: throw new InvalidDataException("未対応のIPC操作です。");
                }
            }
            catch (JournalUnavailableException ex)
            { writer.Write((byte)1); WriteString(writer, ex.Message); }
            // Protocol errors and other I/O failures terminate the connection instead of guessing
            // whether a response has been partly written.
            writer.Flush();
        }
    }
}
