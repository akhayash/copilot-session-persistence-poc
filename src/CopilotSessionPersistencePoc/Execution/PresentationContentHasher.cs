using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CopilotSessionPersistencePoc.Execution;

public static class PresentationContentHasher
{
    public static string Compute(BinaryData content)
    {
        using var stream = content.ToStream();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengths = stackalloc byte[12];
        foreach (ZipArchiveEntry entry in archive.Entries
            .Where(static entry => !string.IsNullOrEmpty(entry.Name))
            .OrderBy(static entry => entry.FullName, StringComparer.Ordinal))
        {
            byte[] name = Encoding.UTF8.GetBytes(entry.FullName);
            BinaryPrimitives.WriteInt32LittleEndian(lengths, name.Length);
            BinaryPrimitives.WriteInt64LittleEndian(lengths[4..], entry.Length);
            incremental.AppendData(lengths);
            incremental.AppendData(name);
            using Stream member = entry.Open();
            member.CopyTo(new HashingStream(incremental));
        }

        return Convert.ToHexStringLower(incremental.GetHashAndReset());
    }

    private sealed class HashingStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            hash.AppendData(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
