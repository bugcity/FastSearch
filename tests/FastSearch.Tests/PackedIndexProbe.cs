using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FastSearch.Core;

internal static class PackedIndexProbe
{
    internal static void Run(Action<bool, string> check)
    {
        check(Unsafe.SizeOf<IndexEntry>() == 40 && Unsafe.SizeOf<SearchHit>() == 16,
            "packed index uses 40-byte rows and search hits never copy full metadata");
        foreach (bool directory in new[] { false, true })
        foreach (DateTime? modified in new DateTime?[] { null, new(0, DateTimeKind.Utc), DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) })
        {
            var entry = new IndexEntry(1, 5, "boundary", directory, long.MaxValue, modified);
            var flipped = entry with { IsDirectory = !directory };
            var cleared = entry with { ModifiedUtc = null, Size = null };
            check(entry.IsDirectory == directory && entry.ModifiedUtc == modified && entry.Size == long.MaxValue &&
                flipped.IsDirectory == !directory && flipped.ModifiedUtc == modified &&
                cleared.IsDirectory == directory && cleared.ModifiedUtc is null && cleared.Size is null,
                "packed attributes retain directory/date/size at null and datetime boundaries: " + directory + "/" + modified?.Ticks);
        }
        var bulk = IndexTable.CreateBulkBuilder(500);
        var expected = new Dictionary<ulong, IndexEntry>();
        for (int i = 499; i >= 0; i--)
        {
            ulong id = (ulong)i * 4096 + 7;
            var entry = new IndexEntry(id, 5, $"same-shard-{i}.txt", false, i, null);
            bulk.Add(entry); expected.Add(id, entry);
        }
        var index = bulk.ToImmutable();
        check(index.Count == 500 && index.Values.Count() == 500 && expected.All(pair => index[pair.Key] == pair.Value) &&
            !index.ContainsKey(0) && !index.ContainsKey(ulong.MaxValue),
            "bulk load grows skewed shards, sorts unsigned IDs and never exposes padding rows");
        bool immutable = false;
        try { bulk.Add(new(100,5,"late",false,1,null)); } catch (InvalidOperationException) { immutable = true; }
        check(immutable && index.Count == 500, "published bulk arrays cannot be mutated by reusing the loader");
        var changes = index.ToBuilder();
        changes.Remove(expected.Keys.First());
        changes[0] = new(0, 5, "zero-ID", false, 0, null);
        changes[ulong.MaxValue] = new(ulong.MaxValue, 5, "maximum-ID", false, 0, null);
        var changed = changes.ToImmutable();
        check(index.Count == 500 && changed.Count == 501 && changed[0].Name == "zero-ID" && changed[ulong.MaxValue].Name == "maximum-ID" &&
            !index.ContainsKey(0) && index.ContainsKey(expected.Keys.First()),
            "packed copy-on-write updates preserve old rows across low/high IDs and deletion");
        var duplicate = IndexTable.CreateBulkBuilder();
        var shared = new IndexEntry(5,5,".",true,null,null);
        duplicate.Add(shared); duplicate.Add(shared);
        bool rejected = false;
        try { duplicate.ToImmutable(); } catch (ArgumentException) { rejected = true; }
        check(rejected, "persisted bulk index rejects duplicate IDs");
        var mft = IndexTable.CreateBulkBuilder(rejectDuplicates: false);
        mft.Add(shared); mft.Add(shared);
        check(mft.ToImmutable().Count == 1, "MFT bulk stream deduplicates repeated file IDs");
        var canceled = IndexTable.CreateBulkBuilder(); canceled.Add(shared);
        bool stopped = false;
        try { canceled.ToImmutable(new CancellationToken(true)); } catch (OperationCanceledException) { stopped = true; }
        check(stopped, "bulk publication honors cancellation before publishing incomplete data");

        // Encode the original v1 wire format independently of the new row representation.
        string directoryPath = Path.Combine(Path.GetTempPath(), "FastSearch-v1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string path = Path.Combine(directoryPath, "index.bin");
            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
            {
                void Text(string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
                writer.Write(0x5844494843525346UL); writer.Write(1); Text("legacy-v1");
                writer.Write(5UL); writer.Write(123UL); writer.Write(100L); writer.Write(3);
                foreach (var entry in new[] { new IndexEntry(15,5,"legacy_.txt",false,4096,DateTime.UnixEpoch),
                    shared, new IndexEntry(9,5,"日本語",true,null,null) })
                {
                    writer.Write(entry.Id); writer.Write(entry.ParentId); Text(entry.Name); writer.Write(entry.IsDirectory);
                    writer.Write(entry.Size ?? -1); writer.Write(entry.ModifiedUtc?.Ticks ?? -1);
                }
            }
            byte[] bytes = payload.ToArray();
            using (var file = File.Create(path)) { file.Write(SHA256.HashData(bytes)); file.Write(bytes); }
            var loaded = new IndexStorage(path).Load(default)!;
            check(loaded is not null && loaded.Entries.Count == 3 && loaded.Entries[15].Size == 4096 && loaded.Entries[9].IsDirectory,
                "packed loader reuses original v1 files including arbitrary entry order without rebuilding");
            var results = SearchEngine.Search(loaded!, "legacy", new(SortColumn.Name), default);
            var later = loaded! with { Entries = loaded!.Entries.SetItem(15, loaded.Entries[15] with { Name = "new.txt", Size = 1 }) };
            check(results.Count == 1 && results[0].Name == "legacy_.txt" && results[0].Entry.Size == 4096 && later.Entries[15].Name == "new.txt",
                "result positions remain attached to the original immutable table after index updates");
            new IndexStorage(path).Save(later, default);
            check(new IndexStorage(path).Load(default)!.Entries[15].Name == "new.txt",
                "packed rows persist and reload through the unchanged atomic v1 format");
        }
        finally { Directory.Delete(directoryPath, true); }
    }
}
