using System.Runtime.InteropServices;

using Horizon.Platform.Android.Native;

namespace Horizon.Platform.Android;

/// <summary>
/// One asset opened out of the APK, read straight from it with AAsset_read and moved about with AAsset_seek64. Opened
/// for random access so seeking back works on compressed assets too. Not shared between threads, like any FileStream,
/// and the AAsset is closed by Dispose or, when somebody forgot, by the handle's finaliser.
/// </summary>
internal sealed unsafe class AssetStream : Stream
{
    private readonly AssetHandle handle;
    private readonly string path;
    private readonly long length;
    private long position;

    private AssetStream(AssetHandle handle, string path)
    {
        this.handle = handle;
        this.path = path;
        length = NativeAssets.Length(handle.DangerousGetHandle());
        if (length < 0)
        {
            handle.Dispose();
            throw new IOException($"Content '{path}' has no length the asset manager can say.");
        }
    }

    /// <summary>Opens an asset by its path from the assets root, throwing the way IndexedContent does when it isn't there.</summary>
    public static AssetStream Open(nint manager, string path)
    {
        nint asset = NativeAssets.Open(manager, path, NativeAssets.MODE_RANDOM);
        if (asset == 0) throw new FileNotFoundException($"Content '{path}' was not found in the APK's assets.", path);
        return new AssetStream(new AssetHandle(asset), path);
    }

    public override bool CanRead => !handle.IsClosed;
    public override bool CanSeek => !handle.IsClosed;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            return length;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            return position;
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    // As much as the asset manager hands over in one go, which can be less than asked, zero only at the end
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (buffer.IsEmpty || position >= length) return 0;

        int read;
        fixed (byte* bytes = buffer) read = NativeAssets.Read(handle.DangerousGetHandle(), bytes, (nuint)buffer.Length);
        if (read < 0) throw new IOException($"Reading content '{path}' out of the APK failed ({read}).");
        position += read;
        return read;
    }

    public override int ReadByte()
    {
        byte one = 0;
        return Read(new Span<byte>(ref one)) == 1 ? one : -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0) throw new IOException("An attempt was made to move the position before the beginning of the stream.");

        // Past the end is fine for a stream, it just reads nothing, but AAsset says no to it so it's kept here only
        long native = NativeAssets.Seek(handle.DangerousGetHandle(), Math.Min(target, length), NativeAssets.SEEK_SET);
        if (native < 0) throw new IOException($"Seeking in content '{path}' failed.");
        position = target;
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException("APK assets can't be written.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("APK assets can't be written.");

    protected override void Dispose(bool disposing)
    {
        if (disposing) handle.Dispose();
        base.Dispose(disposing);
    }

    // Closes the AAsset once, whether it's disposed or finalised
    private sealed class AssetHandle : SafeHandle
    {
        public AssetHandle(nint asset) : base(0, ownsHandle: true) => SetHandle(asset);
        public override bool IsInvalid => handle == 0;

        protected override bool ReleaseHandle()
        {
            NativeAssets.Close(handle);
            return true;
        }
    }
}
