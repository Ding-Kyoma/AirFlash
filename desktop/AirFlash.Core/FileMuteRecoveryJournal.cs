namespace AirFlash.Core;

/// <summary>
/// A local, per-user journal protected by a lifetime file lease. Flush(true) requests
/// stable file data; replacement/directory metadata and physical power-loss behavior
/// still depend on Windows, the filesystem and storage hardware.
/// </summary>
public sealed class FileMuteRecoveryJournal(string directory) : IMuteRecoveryJournal
{
    private const int MaximumBytes = 64 * 1024;
    private readonly string _directory = Path.GetFullPath(directory);
    private FileStream? _lease;
    private string JournalPath => Path.Combine(_directory, "mute-recovery.json");

    public IDisposable AcquireLease()
    {
        if (_lease is not null) throw new MuteRecoveryBusyException("Mute recovery is already owned by this journal instance.");
        Directory.CreateDirectory(_directory);
        try
        {
            // Constant path: replacing or deleting the lease would permit two owners.
            // Managed FileStream handles are not inheritable by child processes.
            var stream = new FileStream(Path.Combine(_directory, "mute-recovery.lease"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _lease = stream;
            return new Lease(this, stream);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            throw new MuteRecoveryBusyException("Another process owns mute recovery. Close it before retrying.", error);
        }
    }

    public byte[]? Read()
    {
        RequireLease();
        FileStream stream;
        try { stream = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (stream)
        {
            // Only failure of the initial open can establish absence. A later read
            // error, even one reporting NotFound, remains an uncertain read.
            if (stream.Length > MaximumBytes) throw new InvalidDataException("Mute recovery journal exceeds 64 KiB.");
            var bytes = new byte[MaximumBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) return bytes[..count];
                count += read;
            }
            throw new InvalidDataException("Mute recovery journal exceeds 64 KiB.");
        }
    }

    public void Write(byte[] bytes)
    {
        RequireLease();
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Mute recovery journal exceeds 64 KiB.");
        var temporary = Path.Combine(_directory, "mute-recovery." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            DurableWrite(temporary, bytes);
            File.Move(temporary, JournalPath, overwrite: true);
            // Also flush the committed handle. A failure here is reported even if
            // replacement already happened; the coordinator reconciles exact bytes.
            using var committed = new FileStream(JournalPath, FileMode.Open,
                FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            committed.Flush(flushToDisk: true);
        }
        finally
        {
            // Removing this unique, same-directory temporary cannot affect the
            // current journal or another owner's operation.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Archive(byte[] expectedBytes)
    {
        RequireLease();
        ArgumentNullException.ThrowIfNull(expectedBytes);
        VerifyArchiveSource(expectedBytes);
        var archive = Path.Combine(_directory, "mute-recovery.archive." + Guid.NewGuid().ToString("N") + ".bin");
        // Keep the original until an exact unique backup has been flushed. A
        // failed backup can leave a partial archive, but never consumes the source.
        DurableWrite(archive, expectedBytes);
        VerifyArchiveSource(expectedBytes);
        File.Delete(JournalPath);
    }

    private void VerifyArchiveSource(byte[] expected)
    {
        var current = Read();
        if (current is null || !current.AsSpan().SequenceEqual(expected))
            throw new IOException("Mute recovery journal changed before it could be archived. Refresh and review it again.");
    }
    private static void DurableWrite(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
    private void RequireLease()
    {
        if (_lease is null) throw new InvalidOperationException("Mute recovery requires its lifetime lease.");
    }
    private sealed class Lease(FileMuteRecoveryJournal owner, FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _stream, null);
            if (current is null) return;
            try { current.Dispose(); }
            finally { if (ReferenceEquals(owner._lease, current)) owner._lease = null; }
        }
    }
}
