using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FastSearch.Core;
using Microsoft.Win32.SafeHandles;

namespace FastSearch.Native;

public readonly record struct JournalState(ulong Id, long FirstUsn, long NextUsn, long LowestValidUsn)
{
    public bool CanResume(IndexSnapshot snapshot, string identity) => snapshot.VolumeIdentity == identity &&
        snapshot.JournalId == Id && snapshot.NextUsn >= Math.Max(FirstUsn, LowestValidUsn) && snapshot.NextUsn <= NextUsn;
}

public readonly record struct UsnRecord(ulong Id, ulong ParentId, string Name, bool IsDirectory, uint Reason);
public sealed record JournalBatch(long NextUsn, IReadOnlyList<UsnRecord> Records);

public sealed class JournalUnavailableException(string message) : IOException(message);

public sealed class NtfsVolume : IDisposable
{
    private const uint EnumUsn = 0x000900B3, ReadUsn = 0x000900BB, QueryUsn = 0x000900F4;
    private readonly SafeFileHandle _volume;
    private readonly byte[] _buffer = new byte[1024 * 1024];
    public string Identity { get; }
    public ulong RootId { get; }

    public NtfsVolume()
    {
        var fs = new StringBuilder(32);
        if (!GetVolumeInformationW(@"C:\", null, 0, out uint serial, out _, out _, fs, fs.Capacity)) ThrowLastError();
        if (!string.Equals(fs.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("CドライブのファイルシステムがNTFSではありません。");
        var guid = new StringBuilder(128);
        if (!GetVolumeNameForVolumeMountPointW(@"C:\", guid, guid.Capacity)) ThrowLastError();
        Identity = $"{guid}:{serial:X8}";
        _volume = CreateFileW(@"\\.\C:", 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (_volume.IsInvalid) { _volume.Dispose(); ThrowLastError(); }
        using var root = CreateFileW(@"\\?\C:\", 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        bool hasInfo = GetFileInformationByHandle(root, out var info);
        if (root.IsInvalid || !hasInfo)
        {
            _volume.Dispose();
            ThrowLastError();
        }
        RootId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
    }

    public JournalState QueryJournal()
    {
        if (!DeviceIoControl(_volume, QueryUsn, null, 0, _buffer, _buffer.Length, out int count, IntPtr.Zero))
        {
            if (Marshal.GetLastWin32Error() != 1179) ThrowJournalError();
            // Enable a missing journal without deleting/resetting an existing journal.
            byte[] settings = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(settings, 32UL * 1024 * 1024);
            BinaryPrimitives.WriteUInt64LittleEndian(settings.AsSpan(8), 8UL * 1024 * 1024);
            if (!DeviceIoControl(_volume, 0x000900E7, settings, settings.Length, _buffer, _buffer.Length, out _, IntPtr.Zero))
                ThrowJournalError();
            if (!DeviceIoControl(_volume, QueryUsn, null, 0, _buffer, _buffer.Length, out count, IntPtr.Zero))
                ThrowJournalError();
        }
        if (count < 56) throw new InvalidDataException("USN Journalの応答が不完全です。");
        return new(U64(_buffer, 0), I64(_buffer, 8), I64(_buffer, 16), I64(_buffer, 24));
    }

    public IEnumerable<UsnRecord> EnumerateMft(CancellationToken token)
    {
        byte[] input = new byte[24];
        // Enumerate all current records; replay from the journal checkpoint taken BEFORE enumeration.
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), long.MaxValue);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!DeviceIoControl(_volume, EnumUsn, input, input.Length, _buffer, _buffer.Length, out int bytes, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 38) yield break; // ERROR_HANDLE_EOF
                throw new Win32Exception(error, "MFTを列挙できません。");
            }
            if (bytes < 8) throw new InvalidDataException("MFTの応答が不完全です。");
            ulong next = U64(_buffer, 0);
            if (next <= U64(input, 0)) throw new InvalidDataException("MFTの列挙位置が進みません。");
            var records = ParseRecords(_buffer, bytes);
            foreach (var record in records) yield return record;
            BinaryPrimitives.WriteUInt64LittleEndian(input, next);
        }
    }

    public JournalBatch ReadJournal(ulong journalId, long startUsn)
    {
        byte[] input = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(input, startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        // BytesToWaitFor=0 and Timeout=0 keep this synchronous call nonblocking at the journal end.
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);
        if (!DeviceIoControl(_volume, ReadUsn, input, input.Length, _buffer, _buffer.Length, out int bytes, IntPtr.Zero))
            ThrowJournalError();
        if (bytes < 8) throw new InvalidDataException("USN Journalの応答が不完全です。");
        long next = I64(_buffer, 0);
        if (next < startUsn) throw new InvalidDataException("USN Journalの継続位置が逆行しました。");
        return new(next, ParseRecords(_buffer, bytes));
    }

    public IndexEntry ReadMetadata(UsnRecord record)
    {
        var descriptor = new FileIdDescriptor { Size = 24, Type = 0, FileId = record.Id };
        using var handle = OpenFileById(_volume, ref descriptor, 0, 7, IntPtr.Zero, 0x02200000);
        if (!handle.IsInvalid && GetFileInformationByHandle(handle, out var info))
        {
            long size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            long time = ((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow;
            DateTime? modified = time > 0 && time <= 2650467743999999999L
                ? DateTime.FromFileTimeUtc(time) : null;
            return new(record.Id, record.ParentId, record.Name, (info.Attributes & 0x10) != 0,
                (info.Attributes & 0x10) != 0 ? null : size, modified);
        }
        // Protected, deleted or transient files must not stop indexing. Unknown metadata stays blank.
        return new(record.Id, record.ParentId, record.Name, record.IsDirectory, null, null);
    }

    public static IReadOnlyList<UsnRecord> ParseRecords(byte[] buffer, int bytes)
    {
        if (bytes < 8 || bytes > buffer.Length) throw new InvalidDataException("USNバッファ長が不正です。");
        var result = new List<UsnRecord>();
        for (int offset = 8; offset < bytes;)
        {
            if (bytes - offset < 60) throw new InvalidDataException("USNレコードが切り詰められています。");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 4));
            if (length < 60 || length > bytes - offset) throw new InvalidDataException("USNレコード長が不正です。");
            // C: is NTFS, whose 64-bit FRNs use USN_RECORD_V2. Never silently skip an unknown version.
            if (version != 2) throw new InvalidDataException($"未対応のUSNレコード形式です: V{version}");
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 56));
            ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 58));
            if ((nameLength & 1) != 0 || nameOffset < 60 || nameOffset + nameLength > length)
                throw new InvalidDataException("USNファイル名の範囲が不正です。");
            string name = Encoding.Unicode.GetString(buffer, offset + nameOffset, nameLength);
            if (name.Length == 0 || name.IndexOfAny(['\\', '/', '\0']) >= 0)
                throw new InvalidDataException("USNファイル名が不正です。");
            result.Add(new(U64(buffer, offset + 8), U64(buffer, offset + 16), name,
                (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 52)) & 0x10) != 0,
                BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 40))));
            offset += (int)length;
        }
        return result;
    }

    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static long I64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
    private static void ThrowLastError() => throw new Win32Exception(Marshal.GetLastWin32Error());
    private static void ThrowJournalError()
    {
        int error = Marshal.GetLastWin32Error();
        if (error is 1178 or 1179 or 1181)
            throw new JournalUnavailableException($"USN Journalの継続性を確認できません (Win32 {error})。Journalが有効か確認してください。");
        throw new Win32Exception(error, "USN Journalを読み取れません。");
    }
    public void Dispose() => _volume.Dispose();

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public ulong FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes, CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh,
            LastWriteTimeLow, LastWriteTimeHigh, VolumeSerialNumber, FileSizeHigh, FileSizeLow,
            NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize,
        byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileIdDescriptor id,
        uint access, uint share, IntPtr security, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder? name, int nameSize,
        out uint serial, out uint maxLength, out uint flags, StringBuilder fileSystem, int fileSystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder name, int length);
}
