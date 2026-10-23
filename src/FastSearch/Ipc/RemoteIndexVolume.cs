using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using FastSearch.Core;
using FastSearch.Native;

namespace FastSearch.Ipc;

/// <summary>A single background caller owns this RPC connection; searches never use it.</summary>
internal sealed class RemoteIndexVolume : IIndexVolume
{
    private readonly Stream _stream;
    private readonly BinaryReader _reader;
    private readonly BinaryWriter _writer;
    private readonly CancellationTokenRegistration _cancel;
    public string Identity { get; }
    public ulong RootId { get; }

    internal RemoteIndexVolume(Stream stream, CancellationToken token)
    {
        _stream = stream;
        _reader = new(stream, Encoding.UTF8, true);
        _writer = new(stream, Encoding.UTF8, true);
        _cancel = token.Register(() => stream.Dispose());
        try
        {
            _writer.Write((byte)VolumeCommand.Hello); _writer.Write(VolumeProtocol.Version); _writer.Flush();
            VolumeProtocol.CheckResponse(_reader);
            if (_reader.ReadInt32() != VolumeProtocol.Version) throw new InvalidDataException("ワーカーのバージョンが一致しません。");
            Identity = VolumeProtocol.ReadString(_reader); RootId = _reader.ReadUInt64();
        }
        catch { _cancel.Dispose(); stream.Dispose(); throw; }
    }

    private void Begin(VolumeCommand command)
    { _writer.Write((byte)command); }
    private void Receive() { _writer.Flush(); VolumeProtocol.CheckResponse(_reader); }
    public JournalState QueryJournal()
    {
        Begin(VolumeCommand.Query); Receive();
        return new(_reader.ReadUInt64(), _reader.ReadInt64(), _reader.ReadInt64(), _reader.ReadInt64());
    }
    public IEnumerable<IndexEntry> EnumerateEntries(CancellationToken token)
    {
        Begin(VolumeCommand.Enumerate); Receive();
        for (int count; (count = VolumeProtocol.ReadCount(_reader)) != 0;)
            for (int i = 0; i < count; i++)
            { token.ThrowIfCancellationRequested(); yield return VolumeProtocol.ReadEntry(_reader); }
    }
    public JournalBatch ReadJournal(ulong journalId, long startUsn)
    {
        Begin(VolumeCommand.Journal); _writer.Write(journalId); _writer.Write(startUsn); Receive();
        long next = _reader.ReadInt64();
        var records = new List<UsnRecord>();
        for (int count; (count = VolumeProtocol.ReadCount(_reader)) != 0;)
        {
            if (records.Count > 100_000 - count) throw new InvalidDataException("Journal応答が大きすぎます。");
            for (int i = 0; i < count; i++) records.Add(VolumeProtocol.ReadRecord(_reader));
        }
        return new(next, records);
    }
    public IReadOnlyList<IndexEntry> ReadMetadata(IReadOnlyList<UsnRecord> records)
    {
        var result = new IndexEntry[records.Count];
        for (int offset = 0; offset < records.Count; offset += VolumeProtocol.BatchLimit)
        {
            int count = Math.Min(VolumeProtocol.BatchLimit, records.Count - offset);
            Begin(VolumeCommand.Metadata); _writer.Write(count);
            for (int i = 0; i < count; i++) VolumeProtocol.WriteRecord(_writer, records[offset + i]);
            Receive();
            if (VolumeProtocol.ReadCount(_reader) != count) throw new InvalidDataException("属性の応答件数が一致しません。");
            for (int i = 0; i < count; i++) result[offset + i] = VolumeProtocol.ReadEntry(_reader);
        }
        return result;
    }
    public void Dispose()
    {
        _cancel.Dispose();
        _stream.Dispose();
    }
}
