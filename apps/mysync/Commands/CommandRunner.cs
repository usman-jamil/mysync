using Microsoft.Extensions.DependencyInjection;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Storage;
using MySync.Cli.Rendering;
using MySync.Storage.Azure;
using SharedKernel;
using Spectre.Console;

namespace MySync.Cli.Commands;

/// <summary>
/// What every command does around its handler: resolve it from a fresh scope, check the
/// environment first, report failures (stderr, plus one JSON document with <c>--json</c>), and
/// turn an unexpected exception into exit 1 with a short message (the stack trace only with
/// <c>--verbose</c>, and never a secret).
/// </summary>
internal sealed class CommandRunner(IServiceProvider services, GlobalOptions options, CliEnvironment environment)
{
    public GlobalOptions Options => options;

    /// <summary>
    /// The settings a storage command needs: provider, parallelism, connection string, container.
    /// Checked before any work (and before any prompt), so a typo costs nothing.
    /// </summary>
    public Result CheckStorage()
    {
        Result settings = environment.Check();
        if (settings.IsFailure)
        {
            return settings;
        }

        using IServiceScope scope = services.CreateScope();
        IShareStore store = scope.ServiceProvider.GetService<IShareStore>()
            ?? throw new InvalidOperationException("No storage provider is registered for a known MYSYNC_PROVIDER.");

        Result<Domain.State.StorageLocation> location = store.GetLocation();
        return location.IsSuccess ? Result.Success() : Result.Failure(location.Error);
    }

    /// <summary>The publishing settings (<c>publish</c>, <c>unpublish</c>): checked before any work, like storage.</summary>
    public Result CheckPublishing()
    {
        using IServiceScope scope = services.CreateScope();
        Application.Abstractions.Publishing.IPublishTarget target = scope.ServiceProvider.GetRequiredService<Application.Abstractions.Publishing.IPublishTarget>();
        Result<Application.Abstractions.Publishing.PublishLocation> location = target.GetLocation();
        return location.IsSuccess ? Result.Success() : Result.Failure(location.Error);
    }

    public async Task<Result<TResponse>> SendAsync<TCommand, TResponse>(TCommand command, CancellationToken cancellationToken)
        where TCommand : ICommand<TResponse>
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResponse>>().Handle(command, cancellationToken);
    }

    public async Task<Result<TResponse>> QueryAsync<TQuery, TResponse>(TQuery query, CancellationToken cancellationToken)
        where TQuery : IQuery<TResponse>
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResponse>>().Handle(query, cancellationToken);
    }

    /// <summary>What was typed where a share is expected: a share ID or a name (exit 2 if it's neither). <c>null</c> stays <c>null</c>.</summary>
    public static Result<Domain.Shares.ShareReference?> ParseShare(string? text)
    {
        if (text is null)
        {
            return Result.Success<Domain.Shares.ShareReference?>(null);
        }

        Result<Domain.Shares.ShareReference> parsed = Domain.Shares.ShareReference.Parse(text);
        return parsed.IsSuccess ? parsed.Value : Result.Failure<Domain.Shares.ShareReference?>(parsed.Error);
    }

    /// <summary>Writes the failure and returns its exit code.</summary>
    public int Fail(Error error)
    {
        int exitCode = ConsoleOutput.Fail(error);
        return options.Json ? JsonOutput.Write(error, exitCode) : exitCode;
    }

    /// <summary>Runs a command body, mapping an unexpected exception to exit 1. Ctrl+C still propagates (exit 130).</summary>
    public async Task<int> RunAsync(Func<Task<int>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            return await body();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            string detail = ConnectionStringRedactor.Redact(options.Verbose ? exception.ToString() : exception.Message);
            ConsoleOutput.Errors.MarkupLine($"[red]Unexpected error: {Markup.Escape(detail)}[/]");
            if (!options.Verbose)
            {
                ConsoleOutput.Info("Run again with --verbose for details.");
            }

            if (options.Json)
            {
                JsonOutput.Write(Error.Failure("Unexpected", ConnectionStringRedactor.Redact(exception.Message)), ExitCodes.Unexpected);
            }

            return ExitCodes.Unexpected;
        }
    }
}
