using FluentValidation;
using MySync.Application.Abstractions.Messaging;
using MySync.Application.Abstractions.Publishing;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using SharedKernel;

namespace MySync.Application.Publishing.Unpublish;

/// <summary>
/// <c>mysync unpublish &lt;publishId&gt; [--share &lt;id&gt;]</c>: <c>index.html</c> first (the link dies
/// at once), then the rest of the publication, then its record.
/// </summary>
public sealed record UnpublishCommand(string PublishId, string? ShareId = null) : ICommand<UnpublishReport>;

public sealed record UnpublishReport(ShareId ShareId, PublishId PublishId, string Url, int BlobsDeleted);

internal sealed class UnpublishCommandValidator : AbstractValidator<UnpublishCommand>
{
    public UnpublishCommandValidator()
    {
        RuleFor(command => command.PublishId)
            .Must(id => Domain.Publishing.PublishId.TryParse(id, out _))
            .WithErrorCode(PublishErrors.InvalidId().Code)
            .WithMessage(PublishErrors.InvalidId().Description);

        RuleFor(command => command.ShareId)
            .Must(id => id is null || Domain.Shares.ShareId.TryParse(id, out _))
            .WithErrorCode(ShareErrors.InvalidId().Code)
            .WithMessage(ShareErrors.InvalidId().Description);
    }
}

internal sealed class UnpublishCommandHandler(
    PublishingShare share, PublicationRegistryUpdater registry, PublicationRemover remover, IPublishTarget target)
    : ICommandHandler<UnpublishCommand, UnpublishReport>
{
    public async Task<Result<UnpublishReport>> Handle(UnpublishCommand command, CancellationToken cancellationToken)
    {
        Result<ResolvedShare> resolved = await share.ResolveAsync(command.ShareId, cancellationToken);
        if (resolved.IsFailure)
        {
            return Result.Failure<UnpublishReport>(resolved.Error);
        }

        Result<PublishLocation> location = target.GetLocation();
        if (location.IsFailure)
        {
            return Result.Failure<UnpublishReport>(location.Error);
        }

        Result<PublicationRegistry> read = await registry.ReadAsync(resolved.Value.ShareId, cancellationToken);
        if (read.IsFailure)
        {
            return Result.Failure<UnpublishReport>(read.Error);
        }

        PublishId id = Domain.Publishing.PublishId.TryParse(command.PublishId, out PublishId? parsed) ? parsed : throw new ArgumentException("Not validated.", nameof(command));
        if (read.Value.Find(id) is not { } publication)
        {
            return Result.Failure<UnpublishReport>(PublishErrors.NotFound(id));
        }

        Result<int> removed = await remover.RemoveAsync(resolved.Value.ShareId, publication, location.Value, cancellationToken);
        return removed.IsSuccess
            ? new UnpublishReport(resolved.Value.ShareId, id, publication.Url, removed.Value)
            : Result.Failure<UnpublishReport>(removed.Error);
    }
}
