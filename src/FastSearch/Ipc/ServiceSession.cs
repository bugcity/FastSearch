using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FastSearch.Core;

namespace FastSearch.Ipc;

internal static class ServiceSession
{
    internal static void Serve(Stream stream, IIndexSource index, Func<string> status, CancellationToken shutdown)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        using var cancel = stop.Token.Register(() => stream.Dispose());
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        byte command;
        try { command = ServiceProtocol.ReadHeader(reader); }
        catch (InvalidDataException ex)
        { writer.Write((byte)2); VolumeProtocol.WriteString(writer, ex.Message); writer.Flush(); return; }
        if (command == 1)
        {
            var snapshot = index.Snapshot;
            writer.Write((byte)0); VolumeProtocol.WriteString(writer, status());
            writer.Write(snapshot is not null); writer.Write(snapshot?.Entries.Count ?? 0);
            writer.Write(snapshot?.Revision ?? -1); writer.Write(Environment.ProcessId); writer.Flush();
            return;
        }
        if (command != 2) throw new InvalidDataException("未対応のサービス操作です。");
        string query = VolumeProtocol.ReadString(reader);
        var sort = new SortOrder((SortColumn)reader.ReadInt32(), reader.ReadBoolean());
        ulong selectedId = reader.ReadUInt64();
        if (!Enum.IsDefined(sort.Column)) throw new InvalidDataException("ソート列が不正です。");
        // A dedicated read observes EOF while CPU search/sort runs, so superseded searches stop.
        var pages = Channel.CreateBounded<int>(1);
        var input = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                    await pages.Writer.WriteAsync(reader.ReadInt32(), stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            { }
            finally { pages.Writer.TryComplete(); stop.Cancel(); }
        });
        try
        {
            var snapshot = index.Snapshot;
            if (snapshot is null)
            {
                writer.Write((byte)2); VolumeProtocol.WriteString(writer, status()); writer.Flush();
                return;
            }
            using var results = SearchEngine.Search(snapshot, query, sort, stop.Token);
            var selected = selectedId == 0 ? null : results.FindById(selectedId);
            int selectedIndex = selected is null ? -1 : results.IndexOf(selected);
            stop.Token.ThrowIfCancellationRequested();
            writer.Write((byte)0); writer.Write(results.Count); writer.Write(selectedIndex); writer.Flush();
            while (pages.Reader.WaitToReadAsync(stop.Token).AsTask().GetAwaiter().GetResult())
            {
                int start = pages.Reader.ReadAsync(stop.Token).AsTask().GetAwaiter().GetResult();
                if (start < 0 || start >= results.Count || start % ServiceProtocol.PageSize != 0)
                    throw new InvalidDataException("ページ位置が不正です。");
                int count = Math.Min(ServiceProtocol.PageSize, results.Count - start);
                writer.Write(count);
                for (int i = 0; i < count; i++) ServiceProtocol.WriteRow(writer, results[start + i]);
                writer.Flush();
            }
        }
        finally
        {
            stop.Cancel();
            // Cancellation disposes the pipe, unblocking the one outstanding reader.
            input.GetAwaiter().GetResult();
        }
    }
}
