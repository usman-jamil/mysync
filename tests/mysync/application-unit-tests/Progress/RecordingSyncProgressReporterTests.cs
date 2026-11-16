using MySync.Application.Abstractions.Progress;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Application.UnitTests.Progress;

public class RecordingSyncProgressReporterTests
{
    [Fact]
    public void Lines_Should_RecordEveryCallInOrder_InAStableFormat()
    {
        RecordingSyncProgressReporter progress = new();

        progress.PhaseStarted(SyncPhase.Uploading);
        progress.TransfersPlanned(TransferDirection.Upload, 2, 1500);
        progress.BytesTransferred(TransferDirection.Upload, 1000);
        progress.FileTransferred(TransferDirection.Upload, ManifestBuilder.PathOf("a.txt"));

        progress.Lines.ShouldBe([
            "phase Uploading",
            "planned Upload 2 files 1500 bytes",
            "bytes Upload 1000",
            "file Upload a.txt",
        ]);
    }

    [Fact]
    public void NullReporter_Should_AcceptEveryCall()
    {
        NullSyncProgressReporter progress = NullSyncProgressReporter.Instance;

        Should.NotThrow(() =>
        {
            progress.PhaseStarted(SyncPhase.Downloading);
            progress.BytesTransferred(TransferDirection.Download, 1);
        });
    }
}
