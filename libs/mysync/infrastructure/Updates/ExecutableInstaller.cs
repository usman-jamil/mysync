using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using MySync.Application.Abstractions.Updates;
using MySync.Domain.Updates;
using SharedKernel;

namespace MySync.Infrastructure.Updates;

/// <summary>
/// The running mysync binary as a file (CLAUDE.md "self-update"). Everything platform-specific about
/// an update is here: where the binary is, whether this user may replace it, and how a running
/// executable is swapped (a rename on Unix; rename-aside on Windows, which can't overwrite a running .exe).
/// </summary>
internal sealed class ExecutableInstaller : IExecutableInstaller
{
    internal const string WorkspacePrefix = ".mysync-update-";

    public InstalledExecutable Locate()
    {
        string runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        ReleaseVersion version = CurrentVersion();
        string? process = Environment.ProcessPath;

        if (process is null)
        {
            return new InstalledExecutable(string.Empty, runtimeIdentifier, version, "the path of the running program is unknown");
        }

        if (string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return new InstalledExecutable(process, runtimeIdentifier, version, "it runs as 'dotnet mysync.dll', a development build");
        }

        if (!ReleaseAssets.RuntimeIdentifiers.Contains(runtimeIdentifier, StringComparer.Ordinal))
        {
            return new InstalledExecutable(process, runtimeIdentifier, version, $"no release is built for {runtimeIdentifier}");
        }

        // /usr/local/bin/mysync may be a symlink to the real file; that file is what gets replaced.
        string real = File.ResolveLinkTarget(process, returnFinalTarget: true)?.FullName ?? process;
        return new InstalledExecutable(real, runtimeIdentifier, version, null);
    }

    public Result CheckReplaceable(InstalledExecutable installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        string folder = Path.GetDirectoryName(installed.Path)!;
        string probe = Path.Combine(folder, $"{WorkspacePrefix}probe-{Guid.NewGuid():N}");

        try
        {
            // Replacing a file is a rename in its folder, so the folder is what must be writable.
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return Result.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            string advice = OperatingSystem.IsWindows()
                ? "its folder isn't writable by you. Run the terminal as administrator, or install mysync in a folder you own."
                : "its folder isn't writable by you. Run 'sudo mysync update', or install mysync in a folder you own, such as ~/.local/bin.";
            return Result.Failure(UpdateErrors.NotWritable(installed.Path, advice));
        }
    }

    public Task<Result<IUpdateWorkspace>> CreateWorkspaceAsync(InstalledExecutable installed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installed);
        cancellationToken.ThrowIfCancellationRequested();

        // Next to the executable: the final move is then a rename on one volume.
        string folder = Path.Combine(Path.GetDirectoryName(installed.Path)!, WorkspacePrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
#pragma warning disable CA2000 // Ownership passes to the caller, whose 'await using' deletes the workspace.
            return Task.FromResult(Result.Success<IUpdateWorkspace>(new UpdateWorkspace(folder)));
#pragma warning restore CA2000
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Failure<IUpdateWorkspace>(UpdateErrors.NotWritable(installed.Path, exception.Message)));
        }
    }

    private static ReleaseVersion CurrentVersion()
    {
        string? informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (ReleaseVersion.TryParse(informational, out ReleaseVersion? version))
        {
            return version;
        }

        // A build with no version attribute counts as older than any release.
        return ReleaseVersion.TryParse("0.0.0", out ReleaseVersion? unknown) ? unknown : throw new InvalidOperationException();
    }

    private sealed class UpdateWorkspace(string folder) : IUpdateWorkspace
    {
        private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

        public Stream CreateFile(string name) =>
            new FileStream(Path.Combine(folder, Path.GetFileName(name)), FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

        public async Task<Result<string>> ExtractExecutableAsync(string archiveName, string executableName, CancellationToken cancellationToken)
        {
            string archive = Path.Combine(folder, Path.GetFileName(archiveName));
            string extracted = Path.Combine(folder, "new", executableName);
            Directory.CreateDirectory(Path.GetDirectoryName(extracted)!);

            try
            {
                bool found = archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? ExtractFromZip(archive, executableName, extracted)
                    : await ExtractFromTarGzAsync(archive, executableName, extracted, cancellationToken);
                if (!found)
                {
                    return Result.Failure<string>(UpdateErrors.NewBinaryFailed($"{archiveName} doesn't contain {executableName}"));
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return Result.Failure<string>(UpdateErrors.NewBinaryFailed($"{archiveName} could not be unpacked: {exception.Message}"));
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(extracted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return extracted;
        }

        public async Task<Result<ProcessOutcome>> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            ProcessStartInfo start = new(executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                using Process process = Process.Start(start)!;
                process.StandardInput.Close();
                Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
                Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RunTimeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                    return Result.Failure<ProcessOutcome>(UpdateErrors.NewBinaryFailed($"'{string.Join(' ', arguments)}' didn't finish within {RunTimeout.TotalSeconds} seconds"));
                }

                await errors;
                return new ProcessOutcome(process.ExitCode, await output);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                return Result.Failure<ProcessOutcome>(UpdateErrors.NewBinaryFailed($"it could not be started: {exception.Message}"));
            }
        }

        public Task<Result> InstallAsync(string executable, InstalledExecutable target, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(target);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    // An atomic rename: the running process keeps its open copy of the old file.
                    File.Move(executable, target.Path, overwrite: true);
                    return Task.FromResult(Result.Success());
                }

                // Windows can rename a running .exe but not overwrite it. The renamed one is removed on a later start.
                string aside = $"{target.Path}.old-{Guid.NewGuid():N}";
                File.Move(target.Path, aside);
                try
                {
                    File.Move(executable, target.Path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    File.Move(aside, target.Path); // put the working one back
                    throw;
                }

                return Task.FromResult(Result.Success());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(Result.Failure(UpdateErrors.NotWritable(target.Path, exception.Message)));
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Removed on a later start (UpdateLeftovers).
            }

            return ValueTask.CompletedTask;
        }

        private static bool ExtractFromZip(string archive, string executableName, string destination)
        {
            using ZipArchive zip = ZipFile.OpenRead(archive);
            ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(candidate => Normalize(candidate.FullName) == executableName);
            if (entry is null)
            {
                return false;
            }

            entry.ExtractToFile(destination, overwrite: true); // our path, not the entry's: nothing can land elsewhere
            return true;
        }

        private static async Task<bool> ExtractFromTarGzAsync(string archive, string executableName, string destination, CancellationToken cancellationToken)
        {
            await using FileStream file = File.OpenRead(archive);
            await using GZipStream gzip = new(file, CompressionMode.Decompress);
            await using TarReader tar = new(gzip);
            while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Normalize(entry.Name) == executableName)
                {
                    await entry.ExtractToFileAsync(destination, overwrite: true, cancellationToken);
                    return true;
                }
            }

            return false;
        }

        // The release archives hold "./mysync"; accept "mysync" too. Anything in a folder is not the executable.
        private static string Normalize(string name) => name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name;
    }
}
