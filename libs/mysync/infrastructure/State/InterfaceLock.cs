using System.Globalization;
using System.Text;
using MySync.Application.Abstractions.State;
using MySync.Domain.Paths;
using MySync.Domain.Session;
using MySync.Domain.State;
using MySync.Infrastructure.FileSystem;
using SharedKernel;

namespace MySync.Infrastructure.State;

/// <summary>
/// <c>.mysynctool.ui</c> held exclusively (as <see cref="FolderLock"/> holds <c>.mysynctool.lock</c>),
/// with the process ID written inside. The operating system releases it when the process ends, so
/// a file left by a crash is simply taken over. Deleted again on release.
/// </summary>
internal sealed class InterfaceLock(ShareRoot root) : IInterfaceLock
{
    public async Task<Result<IAsyncDisposable>> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = root.Combine(StateFileNames.InterfaceLock);

#pragma warning disable CA2000 // Ownership passes to the caller, who disposes the held lock to release it.
        FileStream? handle = null;
        try
        {
            handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            handle.SetLength(0);
            byte[] pid = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            await handle.WriteAsync(pid, cancellationToken);
            await handle.FlushAsync(cancellationToken);
            Held held = new(handle, path);
            handle = null;
            return Result.Success<IAsyncDisposable>(held);
#pragma warning restore CA2000
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return Result.Failure<IAsyncDisposable>(LocalFileErrors.WriteFailed(StateFileNames.InterfaceLock, exception.Message));
        }
        catch (IOException)
        {
            return Result.Failure<IAsyncDisposable>(TuiErrors.AlreadyRunning(ReadProcessId(path)));
        }
        finally
        {
            if (handle is not null)
            {
                await handle.DisposeAsync();
            }
        }
    }

    // Best effort: a platform that locks the file against reads too gives no ID.
    private static int? ReadProcessId(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.ASCII);
            return int.TryParse(reader.ReadToEnd().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) ? pid : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class Held(FileStream handle, string path) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await handle.DisposeAsync();
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Taken by another interface meanwhile: theirs now.
            }
        }
    }
}
