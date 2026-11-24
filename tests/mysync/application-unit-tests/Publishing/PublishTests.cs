using System.Text;
using MySync.Application.Abstractions.Publishing;
using MySync.Application.Publishing.Publish;
using MySync.Application.UnitTests.TestSupport;
using MySync.Domain.Configuration;
using MySync.Domain.Publishing;
using MySync.Domain.Storage;
using MySync.Testing;
using SharedKernel;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Publishing;

/// <summary><c>mysync publish</c>: preflight, the upload order, the registry, cleanup and verification.</summary>
public class PublishTests
{
    private readonly SyncWorld _world = new();

    private async Task<Machine> SharedAsync(params (string Path, string Text)[] files)
    {
        Machine machine = _world.Machine("usman@DEV-LAPTOP");
        foreach ((string path, string text) in files.Length > 0 ? files : [("docs/readme.md", "# hi"), ("site/index.html", "<script>alert(1)</script>"), ("data/empty.csv", string.Empty)])
        {
            machine.Folder.WriteFile(path, text);
        }

        machine.Folder.AddDirectory("logs");
        (await machine.CreateAsync()).ShouldSucceed();
        return machine;
    }

    private string Text(string blobName) => Encoding.UTF8.GetString(_world.Publishing.Blob(blobName)!.Bytes);

    [Fact]
    public async Task Publish_Should_CopyEveryFile_WithItsHeaders_AndUploadIndexHtmlLast()
    {
        Machine machine = await SharedAsync();

        PublishReport report = (await machine.PublishAsync()).ShouldSucceed();

        var layout = PublicationLayout.For(report.PublishId);
        report.Url.AbsoluteUri.ShouldBe($"https://publishacct.z13.web.core.windows.net/p/{report.PublishId}/");
        report.Files.ShouldBe(3);
        report.Verified.ShouldBeTrue();
        report.Copies[CopyMethod.PutBlobFromUrl].ShouldBe(3);

        Text(layout.File(Path("docs/readme.md"))).ShouldBe("# hi");
        StoredBlob html = _world.Publishing.Blob(layout.File(Path("site/index.html")))!;
        html.Headers.ContentType.ShouldBe("text/html; charset=utf-8");
        html.Headers.ContentDisposition.ShouldBe("attachment; filename*=UTF-8''index.html");
        html.Headers.CacheControl.ShouldBe(PublishedBlob.Immutable);
        _world.Publishing.Blob(layout.File(Path("docs/readme.md")))!.Headers.ContentDisposition.ShouldStartWith("inline;");
        _world.Publishing.Blob(layout.Blob("assets/app.js"))!.Headers.ContentType.ShouldBe("text/javascript; charset=utf-8");
        Text(layout.Blob("site-data.js")).ShouldContain($"\"{report.PublishId}\"");
        Text(layout.Blob("SHA256SUMS.txt")).ShouldContain("  docs/readme.md\n");
        Text(layout.Blob("download.sh")).ShouldStartWith("#!/bin/sh\n");
        _world.Publishing.Blob(layout.Blob("download.ps1")).ShouldNotBeNull();
        _world.Publishing.BlobNames.ShouldContain("robots.txt");
        _world.Publishing.BlobNames.ShouldContain("404.html");

        // The commit point: index.html comes after every other write of the publication.
        IReadOnlyList<string> writes = [.. _world.Publishing.Operations.Where(op => op.StartsWith("put:p/", StringComparison.Ordinal) || op.StartsWith("copy:", StringComparison.Ordinal))];
        writes[^1].ShouldBe($"put:{layout.Index}");
    }

    [Fact]
    public async Task Publish_Should_RecordThePublication_PendingFirst_ThenPublished()
    {
        Machine machine = await SharedAsync();
        List<PublicationStatus> seen = [];
        _world.Publishing.BeforeWrite = name =>
        {
            if (name.StartsWith("p/", StringComparison.Ordinal) && name.Count(c => c == '/') == 2 && name.EndsWith("/index.html", StringComparison.Ordinal))
            {
                seen.Add(_world.Store.GetPublicationsAsync(machine.ShareId, SyncWorld.Ct).Result.Value!.Registry.Publications.Single().Status);
            }
        };

        PublishReport report = (await machine.PublishAsync(new PublishShareCommand(Title: "Q3 reports"))).ShouldSucceed();

        seen.ShouldBe([PublicationStatus.Pending]);
        Publication record = (await machine.PublicationsAsync()).ShouldSucceed().Publications.Single();
        record.Status.ShouldBe(PublicationStatus.Published);
        record.Id.ShouldBe(report.PublishId);
        record.Title.ShouldBe("Q3 reports");
        record.Files.ShouldBe(3);
        record.CreatedBy.ShouldBe("usman@DEV-LAPTOP");
        record.PublishAccount.ShouldBe("publishacct");
        record.SiteAssetsVersion.ShouldBe(_world.SiteAssets.Version);
        record.Url.ShouldBe(report.Url.AbsoluteUri);
    }

    [Fact]
    public async Task NothingPublic_Should_LeadBackToTheShare()
    {
        Machine machine = await SharedAsync();

        PublishReport report = (await machine.PublishAsync()).ShouldSucceed();

        report.PublishId.Value.ShouldNotContain(machine.ShareId.ToString());
        foreach (string name in _world.Publishing.BlobNames)
        {
            name.ShouldNotContain(machine.ShareId.ToString());
            Encoding.UTF8.GetString(_world.Publishing.Blob(name)!.Bytes).ShouldNotContain(machine.ShareId.ToString());
            Encoding.UTF8.GetString(_world.Publishing.Blob(name)!.Bytes).ShouldNotContain(InMemoryShareStore.DefaultLocation.AccountName);
            Encoding.UTF8.GetString(_world.Publishing.Blob(name)!.Bytes).ShouldNotContain("usman@DEV-LAPTOP", customMessage: "who published stays in the private record");
        }
    }

    [Fact]
    public async Task Preflight_Should_StopAtTheFirstProblem_InOrder_BeforeWritingAnything()
    {
        Machine machine = await SharedAsync();
        _world.Publishing.LocationError = ConfigurationErrors.Missing(PublishErrors.PublishConnectionStringVariable);
        _world.Publishing.StaticWebsiteEnabled = false;
        _world.SiteAssets = new InMemorySiteAssetSource(isPlaceholder: true);

        (await machine.PublishAsync()).ShouldFail().Code.ShouldBe("Configuration.Missing");
        _world.Publishing.LocationError = null;
        (await machine.PublishAsync()).ShouldFail().Code.ShouldBe("Publish.StaticWebsiteDisabled");
        _world.Publishing.StaticWebsiteEnabled = true;
        _world.Publishing.IndexDocument = "home.html";
        (await machine.PublishAsync()).ShouldFail().Code.ShouldBe("Publish.IndexDocumentMismatch");
        _world.Publishing.IndexDocument = "index.html";
        _world.Publishing.CannotReadServiceProperties = true;
        (await machine.PublishAsync()).ShouldFail().Code.ShouldBe("Publish.CannotReadServiceProperties");
        _world.Publishing.CannotReadServiceProperties = false;
        (await machine.PublishAsync()).ShouldFail().Code.ShouldBe("Publish.SiteAssetsMissing");
        _world.SiteAssets = new InMemorySiteAssetSource();
        (await machine.PublishAsync(new PublishShareCommand(Revision: 9))).ShouldFail().Code.ShouldBe("Publish.RevisionNotFound");

        _world.Publishing.Operations.ShouldBeEmpty();
        (await _world.Store.GetPublicationsAsync(machine.ShareId, SyncWorld.Ct)).Value.ShouldBeNull();
    }

    [Fact]
    public async Task Publish_Should_NeedAShare()
    {
        Machine stranger = _world.Machine("stranger");

        (await stranger.PublishAsync()).ShouldFail().Code.ShouldBe("Shares.NotAShare");
        (await stranger.PublishAsync(new PublishShareCommand(Share: Domain.Shares.ShareReference.For(Domain.Shares.ShareId.New())))).ShouldFail().Code.ShouldBe("Shares.NotFound");
    }

    [Fact]
    public async Task Publish_Should_RefuseADeletedShare()
    {
        Machine machine = await SharedAsync();
        Domain.Shares.ShareId id = machine.ShareId;
        (await machine.DeleteAsync()).ShouldSucceed();

        (await _world.Machine("other").PublishAsync(new PublishShareCommand(Share: Domain.Shares.ShareReference.For(id)))).ShouldFail().Code.ShouldBe("Shares.Deleted");
    }

    [Fact]
    public async Task Publish_Should_WorkFromAnotherFolder_ByShareId_WithANeutralTitle()
    {
        Machine machine = await SharedAsync();

        PublishReport report = (await _world.Machine("other").PublishAsync(new PublishShareCommand(Share: Domain.Shares.ShareReference.For(machine.ShareId)))).ShouldSucceed();

        report.Title.ShouldBe("Shared files");
        report.Files.ShouldBe(3);
    }

    [Fact]
    public async Task Publish_Should_TakeAnOlderRevision_FromHistory()
    {
        Machine machine = await SharedAsync(("a.txt", "first"));
        machine.Folder.WriteFile("a.txt", "second");
        (await machine.SyncAsync()).ShouldSucceed();

        PublishReport report = (await machine.PublishAsync(new PublishShareCommand(Revision: 1))).ShouldSucceed();

        report.Revision.Value.ShouldBe(1);
        Text(PublicationLayout.For(report.PublishId).File(Path("a.txt"))).ShouldBe("first");
    }

    [Fact]
    public async Task Publish_Should_WarnAboutUnsyncedChanges_AndPublishTheLastSyncedState()
    {
        Machine machine = await SharedAsync(("a.txt", "synced"));
        machine.Folder.WriteFile("a.txt", "edited, not synced");

        PublishReport report = (await machine.PublishAsync()).ShouldSucceed();

        report.Warnings.ShouldContain(warning => warning.Contains("aren't synced", StringComparison.Ordinal));
        Text(PublicationLayout.For(report.PublishId).File(Path("a.txt"))).ShouldBe("synced");
    }

    [Fact]
    public async Task Publish_Should_WarnWhenThePublishingAccountIsThePrivateOne()
    {
        Machine machine = await SharedAsync();
        (await machine.PublishAsync()).ShouldSucceed().Warnings.ShouldNotContain(warning => warning.Contains("private shares", StringComparison.Ordinal));

        _world.Publishing = new InMemoryPublishTarget(_world.Store, InMemoryShareStore.DefaultLocation.AccountName);
        _world.Verifier = new InMemoryPublicationVerifier(_world.Publishing);

        PublishReport report = (await machine.PublishAsync()).ShouldSucceed();
        report.Warnings.ShouldContain(warning => warning.Contains("the account that holds your private shares", StringComparison.Ordinal));
        report.Verified.ShouldBeTrue();
    }

    [Fact]
    public async Task Publish_Should_StreamThroughThisMachine_WhenTheStoreCantMakeReadUrls()
    {
        Machine machine = await SharedAsync();
        _world.Store.CanMakeReadUrls = false;

        PublishReport report = (await machine.PublishAsync()).ShouldSucceed();

        report.Copies[CopyMethod.Streamed].ShouldBe(3);
        Text(PublicationLayout.For(report.PublishId).File(Path("docs/readme.md"))).ShouldBe("# hi");
    }

    [Fact]
    public async Task ANativeCopySource_Should_BeCopiedNatively_WhenTheTargetSharesItsCredentials()
    {
        Machine machine = await SharedAsync();
        _world.Store.CanMakeReadUrls = false;
        _world.Store.NativeCredentialScope = "scope-a";
        _world.Publishing.NativeCredentialScope = "scope-a";

        PublishReport report = (await machine.PublishAsync()).ShouldSucceed();

        report.Copies.ShouldBe(new Dictionary<CopyMethod, int> { [CopyMethod.CopyObject] = 3 });
        Text(PublicationLayout.For(report.PublishId).File(Path("docs/readme.md"))).ShouldBe("# hi");
    }

    [Fact]
    public async Task ANativeCopySource_FromOtherCredentials_Should_UseItsUrl_OrStream()
    {
        Machine machine = await SharedAsync();
        _world.Store.NativeCredentialScope = "scope-a";
        _world.Publishing.NativeCredentialScope = "scope-b"; // another profile: it may not read the share's bucket

        (await machine.PublishAsync()).ShouldSucceed().Copies.ShouldBe(new Dictionary<CopyMethod, int> { [CopyMethod.PutBlobFromUrl] = 3 });

        _world.Publishing.CopiesFromUrls = false;
        (await machine.PublishAsync()).ShouldSucceed().Copies.ShouldBe(new Dictionary<CopyMethod, int> { [CopyMethod.Streamed] = 3 });
    }

    [Fact]
    public async Task MixedProviders_Should_StreamEveryFile()
    {
        // An Azure share (URL sources) published to S3 (copies natively from S3 only).
        Machine machine = await SharedAsync();
        _world.Publishing.CopiesFromUrls = false;
        _world.Publishing.NativeCredentialScope = "s3-scope";

        PublishReport report = (await machine.PublishAsync()).ShouldSucceed();

        report.Copies.ShouldBe(new Dictionary<CopyMethod, int> { [CopyMethod.Streamed] = 3 });
        Text(PublicationLayout.For(report.PublishId).File(Path("docs/readme.md"))).ShouldBe("# hi");
        _world.Publishing.Operations.ShouldNotContain(operation => operation.StartsWith("copy:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHostWithoutIndexDocuments_Should_LinkToIndexHtml_ButKeepTheScriptsInTheFolder()
    {
        Machine machine = await SharedAsync();
        _world.Publishing.ServesIndexDocuments = false;

        PublishReport report = (await machine.PublishAsync()).ShouldSucceed();

        string folder = $"https://publishacct.z13.web.core.windows.net/p/{report.PublishId}/";
        report.Url.AbsoluteUri.ShouldBe(folder + "index.html");
        report.FolderUrl.AbsoluteUri.ShouldBe(folder);
        report.ShellCommand.ShouldBe($"curl -fsSL {folder}download.sh | sh");
        report.PowerShellCommand.ShouldBe($"irm {folder}download.ps1 | iex");
        (await machine.PublicationsAsync()).ShouldSucceed().Publications.Single().Url.ShouldBe(folder + "index.html");
        var layout = PublicationLayout.For(report.PublishId);
        Text(layout.Blob("download.sh")).ShouldContain($"base='{folder}'");
        Text(layout.Blob("download.ps1")).ShouldContain($"$base = '{folder}'");
        _world.Verifier.Requests.Single().AbsoluteUri.ShouldBe(folder + "site-data.js");
    }

    [Fact]
    public async Task AHostThatServesIndexDocuments_Should_LinkToTheFolder()
    {
        Machine machine = await SharedAsync();

        PublishReport report = (await machine.PublishAsync()).ShouldSucceed();

        report.Url.ShouldBe(report.FolderUrl);
        report.Url.AbsoluteUri.ShouldEndWith($"/p/{report.PublishId}/");
    }

    [Fact]
    public async Task Verification_Should_BeToldWhatTheTargetCallsNotServed()
    {
        Machine machine = await SharedAsync();
        _world.Publishing.NotServedStatusCodes = new HashSet<int> { 403, 404 };

        (await machine.PublishAsync()).ShouldSucceed();

        _world.Verifier.NotServedStatusCodes.ShouldBe(new HashSet<int> { 403, 404 });
    }

    [Fact]
    public async Task APreflightFailure_Should_StopBeforeAnythingIsWritten()
    {
        Machine machine = await SharedAsync();
        _world.Publishing.PreflightError = PublishErrors.NotPublic("team-publish", "https://team-publish.s3.eu-west-1.amazonaws.com");

        (await machine.PublishAsync()).ShouldFail().Code.ShouldBe("Publish.NotPublic");

        _world.Publishing.Operations.ShouldBeEmpty();
        (await _world.Store.GetPublicationsAsync(machine.ShareId, SyncWorld.Ct)).Value.ShouldBeNull();
    }

    [Fact]
    public async Task TheSameAccountWarning_Should_BeTheTargetsCall()
    {
        Machine machine = await SharedAsync();
        _world.Publishing.SameAccount = location => location.Container == "mysync"; // e.g. S3: the same bucket

        (await machine.PublishAsync()).ShouldSucceed().Warnings.ShouldContain(warning => warning.Contains("private shares", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStreamedCopyWhoseBytesDontMatchTheirHash_Should_FailThePublication_AndPublishNothing()
    {
        Machine machine = await SharedAsync();
        _world.Store.CanMakeReadUrls = false;
        _world.Store.DownloadStream = (_, bytes) => new MemoryStream([.. bytes, (byte)'!']);

        (await machine.PublishAsync()).ShouldFail().Code.ShouldBe("Storage.CorruptObject");

        _world.Publishing.BlobNames.ShouldNotContain(name => name.StartsWith("p/", StringComparison.Ordinal));
        (await machine.PublicationsAsync()).ShouldSucceed().Publications.Single().Status.ShouldBe(PublicationStatus.Failed);
    }

    [Fact]
    public async Task AFailureBeforeTheCommitPoint_Should_RemoveWhatWasUploaded_AndMarkTheRecordFailed()
    {
        Machine machine = await SharedAsync();
        _world.Publishing.FailOn = name => name.EndsWith("download.ps1", StringComparison.Ordinal) ? StorageErrors.Unreachable : null;

        (await machine.PublishAsync()).ShouldFail().Code.ShouldBe("Storage.Unreachable");

        _world.Publishing.BlobNames.ShouldNotContain(name => name.StartsWith("p/", StringComparison.Ordinal));
        Publication record = (await machine.PublicationsAsync()).ShouldSucceed().Publications.Single();
        record.Status.ShouldBe(PublicationStatus.Failed);
        _world.Publishing.Operations.ShouldNotContain($"put:{PublicationLayout.For(record.Id).Index}");
    }

    [Fact]
    public async Task ACopyFailure_Should_StopTheOtherCopies_AndClean()
    {
        Machine machine = await SharedAsync();
        _world.Publishing.FailOn = name => name.EndsWith("readme.md", StringComparison.Ordinal) ? StorageErrors.Timeout : null;

        (await machine.PublishAsync()).ShouldFail().Code.ShouldBe("Storage.Timeout");

        _world.Publishing.BlobNames.ShouldNotContain(name => name.StartsWith("p/", StringComparison.Ordinal));
        (await machine.PublicationsAsync()).ShouldSucceed().Publications.Single().Status.ShouldBe(PublicationStatus.Failed);
    }

    [Fact]
    public async Task CtrlC_BeforeTheCommitPoint_Should_Clean_AndMarkTheRecordFailed()
    {
        Machine machine = await SharedAsync();
        using CancellationTokenSource cancel = new();
        _world.Publishing.BeforeWrite = name =>
        {
            if (name.EndsWith("site-data.js", StringComparison.Ordinal))
            {
                cancel.Cancel();
            }
        };

        await Should.ThrowAsync<OperationCanceledException>(() => machine.PublishAsync(cancellationToken: cancel.Token));

        _world.Publishing.BlobNames.ShouldNotContain(name => name.StartsWith("p/", StringComparison.Ordinal));
        (await machine.PublicationsAsync()).ShouldSucceed().Publications.Single().Status.ShouldBe(PublicationStatus.Failed);
    }

    [Fact]
    public async Task ARegistryWrittenByAnotherMachineFirst_Should_BeReadAgain_AndKeepTheirRecord()
    {
        Machine machine = await SharedAsync();
        Machine other = _world.Machine("other@SERVER01");
        bool raced = false;
        _world.Store.BeforePutPublications = async shareId =>
        {
            if (!raced)
            {
                raced = true;
                _world.Store.BeforePutPublications = null;
                (await other.PublishAsync(new PublishShareCommand(Share: Domain.Shares.ShareReference.For(shareId), Title: "theirs"))).ShouldSucceed();
            }
        };

        (await machine.PublishAsync(new PublishShareCommand(Title: "mine"))).ShouldSucceed();

        (await machine.PublicationsAsync()).ShouldSucceed().Publications.Select(p => p.Title).Order().ShouldBe(["mine", "theirs"]);
        _world.Time.Delays.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task AFailedVerification_Should_LeaveThePublicationUp_AndSayWhy()
    {
        Machine machine = await SharedAsync();
        _world.Verifier.Unreachable = true;

        PublishReport report = (await machine.PublishAsync()).ShouldSucceed();

        report.Verified.ShouldBeFalse();
        report.VerificationDetail.ShouldBe("HTTP 404 Not Found");
        _world.Publishing.Blob(PublicationLayout.For(report.PublishId).Index).ShouldNotBeNull();
        (await machine.PublicationsAsync()).ShouldSucceed().Publications.Single().Status.ShouldBe(PublicationStatus.Published);
        _world.Verifier.Requests.Single().AbsoluteUri.ShouldBe(report.Url.AbsoluteUri + "site-data.js");
    }

    [Fact]
    public async Task APathAzureCantName_Should_BeLeftOut_AndReported()
    {
        Machine machine = await SharedAsync(("ok.txt", "ok"), ("trailing.", "dot"));

        PublishReport report = (await machine.PublishAsync()).ShouldSucceed();

        report.Files.ShouldBe(1);
        report.Skipped.Single().Path.Value.ShouldBe("trailing.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public async Task ABadTitle_Should_BeAUsageError(string title)
    {
        Machine machine = await SharedAsync();

        (await machine.PublishAsync(new PublishShareCommand(Title: title))).ShouldFail().Type.ShouldBe(ErrorType.Validation);
    }

    private static Domain.Paths.SharePath Path(string value) => ManifestBuilder.PathOf(value);
}
