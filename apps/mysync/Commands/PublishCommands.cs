using ConsoleAppFramework;
using MySync.Application.Publishing.List;
using MySync.Application.Publishing.Publish;
using MySync.Application.Publishing.Unpublish;
using MySync.Cli.Rendering;
using SharedKernel;

namespace MySync.Cli.Commands;

/// <summary><c>mysync publish</c>, <c>publications</c> and <c>unpublish</c> (CLAUDE.md, Publishing).</summary>
internal sealed class PublishCommands(CommandRunner runner)
{
    /// <summary>
    /// Publish a snapshot of this share as a public, read-only website anyone with the link can browse and download from.
    /// </summary>
    /// <param name="share">The share to publish (default: this folder's).</param>
    /// <param name="revision">A revision from the share's history (default: the latest).</param>
    /// <param name="title">The page's title (default: this folder's name).</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("publish")]
    public Task<int> Publish(string? share = null, long? revision = null, string? title = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            Result settings = runner.CheckStorage();
            if (settings.IsSuccess)
            {
                settings = runner.CheckPublishing();
            }

            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<PublishReport> published = await runner.SendAsync<PublishShareCommand, PublishReport>(
                new PublishShareCommand(share, revision, title, runner.Options.Parallel), cancellationToken);
            if (published.IsFailure)
            {
                return runner.Fail(published.Error);
            }

            int exitCode = ExitCodes.FromReport(published.Value);
            if (runner.Options.Json)
            {
                return JsonOutput.Write(published.Value, exitCode);
            }

            PublicationView.Render(published.Value);
            return exitCode;
        }, cancellationToken);

    /// <summary>List this share's publications: ID, status, URL, revision, when, files and size.</summary>
    /// <param name="share">The share (default: this folder's).</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("publications")]
    public Task<int> Publications(string? share = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            Result settings = runner.CheckStorage();
            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<PublicationListResponse> listed = await runner.QueryAsync<ListPublicationsQuery, PublicationListResponse>(
                new ListPublicationsQuery(share), cancellationToken);
            if (listed.IsFailure)
            {
                return runner.Fail(listed.Error);
            }

            if (runner.Options.Json)
            {
                return JsonOutput.Write(listed.Value, ExitCodes.Success);
            }

            PublicationView.Render(listed.Value);
            return ExitCodes.Success;
        }, cancellationToken);

    /// <summary>Take a publication down: its link stops working at once, then its files are removed.</summary>
    /// <param name="publishId">The publication's ID, as `mysync publications` lists it.</param>
    /// <param name="share">The share it belongs to (default: this folder's).</param>
    /// <param name="cancellationToken">Ctrl+C.</param>
    [Command("unpublish")]
    public Task<int> Unpublish([Argument] string publishId, string? share = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync(async () =>
        {
            Result settings = runner.CheckStorage();
            if (settings.IsSuccess)
            {
                settings = runner.CheckPublishing();
            }

            if (settings.IsFailure)
            {
                return runner.Fail(settings.Error);
            }

            Result<UnpublishReport> removed = await runner.SendAsync<UnpublishCommand, UnpublishReport>(
                new UnpublishCommand(publishId, share), cancellationToken);
            if (removed.IsFailure)
            {
                return runner.Fail(removed.Error);
            }

            if (runner.Options.Json)
            {
                return JsonOutput.Write(removed.Value, ExitCodes.Success);
            }

            PublicationView.Render(removed.Value);
            return ExitCodes.Success;
        }, cancellationToken);
}
