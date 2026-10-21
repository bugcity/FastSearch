using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FastSearch.Core;

public sealed class IndexStorage(string path)
{
    private const int Version = 1;
    public IndexSnapshot? Load(CancellationToken token)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] expectedHash = new byte[32];
            file.ReadExactly(expectedHash);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[128 * 1024];
            int read;
            while ((read = file.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, hash.GetHashAndReset())) return null;
            file.Position = 32;
            using var reader = new BinaryReader(file, Encoding.UTF8, true);
            if (reader.ReadUInt64() != 0x5844494843525346 || reader.ReadInt32() != Version) return null;
            string identity = ReadString(reader);
            ulong root = reader.ReadUInt64(), journal = reader.ReadUInt64();
            long next = reader.ReadInt64();
            int count = reader.ReadInt32();
            if (count < 0 || count > 20_000_000 || next < 0 || root == 0) return null;
            var entries = ImmutableDictionary.CreateBuilder<ulong, IndexEntry>();
            for (int i = 0; i < count; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                ulong id = reader.ReadUInt64(), parent = reader.ReadUInt64();
                string name = ReadString(reader);
                bool directory = reader.ReadBoolean();
                long size = reader.ReadInt64(), ticks = reader.ReadInt64();
                if (name.Length == 0 || name.IndexOfAny(['\\', '/', '\0']) >= 0 || size < -1 ||
                    ticks < -1 || ticks > DateTime.MaxValue.Ticks || entries.ContainsKey(id)) return null;
                entries.Add(id, new(id, parent, name, directory, size < 0 ? null : size,
                    ticks < 0 ? null : new DateTime(ticks, DateTimeKind.Utc)));
            }
            if (file.Position != file.Length) return null;
            return new(identity, root, journal, next, entries.ToImmutable());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public void Save(IndexSnapshot snapshot, CancellationToken token)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                128 * 1024, FileOptions.SequentialScan))
            {
                file.Write(new byte[32]);
                using var sha = SHA256.Create();
                using (var crypto = new CryptoStream(file, sha, CryptoStreamMode.Write, true))
                using (var writer = new BinaryWriter(crypto, Encoding.UTF8, true))
                {
                    writer.Write(0x5844494843525346UL);
                    writer.Write(Version);
                    WriteString(writer, snapshot.VolumeIdentity);
                    writer.Write(snapshot.RootId);
                    writer.Write(snapshot.JournalId);
                    writer.Write(snapshot.NextUsn);
                    writer.Write(snapshot.Entries.Count);
                    int count = 0;
                    foreach (var entry in snapshot.Entries.Values)
                    {
                        if ((count++ & 1023) == 0) token.ThrowIfCancellationRequested();
                        writer.Write(entry.Id);
                        writer.Write(entry.ParentId);
                        WriteString(writer, entry.Name);
                        writer.Write(entry.IsDirectory);
                        writer.Write(entry.Size ?? -1);
                        writer.Write(entry.ModifiedUtc?.Ticks ?? -1);
                    }
                    writer.Flush();
                    crypto.FlushFinalBlock();
                }
                file.Position = 0;
                file.Write(sha.Hash!);
                file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            // Same-directory atomic replacement preserves the last valid checkpoint on interruption.
            if (File.Exists(fullPath)) File.Replace(temp, fullPath, null);
            else File.Move(temp, fullPath);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 128 * 1024) throw new InvalidDataException("文字列長が不正です。");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
