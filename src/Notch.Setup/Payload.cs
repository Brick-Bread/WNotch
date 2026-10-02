using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Notch.Setup;

/// <summary>
/// The setup executable carries the app as a zip appended to it:
/// <c>[setup program][zip][zip length: int64][magic: 8 bytes]</c>. Uninstall.exe is the same
/// program without the zip and trailer, so it stays small.
/// </summary>
internal static class Payload
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NOTCHPAY");
    private const int TrailerSize = 16;

    public static string SelfPath => Process.GetCurrentProcess().MainModule!.FileName;

    /// <summary>Length of the setup program alone, and the zip's position and length; false when there is no payload.</summary>
    public static bool TryLocate(out long programLength, out long zipLength)
    {
        programLength = 0;
        zipLength = 0;
        using FileStream file = OpenSelf();
        if (file.Length < TrailerSize)
        {
            return false;
        }

        byte[] trailer = new byte[TrailerSize];
        file.Seek(-TrailerSize, SeekOrigin.End);
        ReadExactly(file, trailer);
        for (int i = 0; i < Magic.Length; i++)
        {
            if (trailer[8 + i] != Magic[i])
            {
                return false;
            }
        }

        zipLength = BitConverter.ToInt64(trailer, 0);
        programLength = file.Length - TrailerSize - zipLength;
        return zipLength > 0 && programLength > 0;
    }

    public static FileStream OpenSelf() =>
        new FileStream(SelfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>A read-only window onto part of a file, so the zip can be read where it sits.</summary>
    public sealed class Slice : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public Slice(Stream inner, long start, long length)
        {
            _inner = inner;
            _start = start;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            _inner.Position = _start + _position;
            int read = _inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin: _position = offset; break;
                case SeekOrigin.Current: _position += offset; break;
                default: _position = _length + offset; break;
            }

            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            total += read;
        }
    }
}
