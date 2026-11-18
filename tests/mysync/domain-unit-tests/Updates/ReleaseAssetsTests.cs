using MySync.Domain.Content;
using MySync.Domain.Updates;
using Shouldly;
using Xunit;

namespace MySync.Domain.UnitTests.Updates;

/// <summary>
/// The release names every installed mysync looks for. They must match what
/// <c>.github/workflows/release-mysync.yml</c> publishes; changing them breaks <c>mysync update</c>.
/// </summary>
public class ReleaseAssetsTests
{
    private static readonly ReleaseVersion V = ReleaseVersion.TryParse("1.2.3", out ReleaseVersion? version) ? version : null!;

    [Theory]
    [InlineData("linux-x64", "mysync-1.2.3-linux-x64.tar.gz", "mysync")]
    [InlineData("linux-arm64", "mysync-1.2.3-linux-arm64.tar.gz", "mysync")]
    [InlineData("linux-musl-x64", "mysync-1.2.3-linux-musl-x64.tar.gz", "mysync")]
    [InlineData("osx-x64", "mysync-1.2.3-osx-x64.tar.gz", "mysync")]
    [InlineData("osx-arm64", "mysync-1.2.3-osx-arm64.tar.gz", "mysync")]
    [InlineData("win-x64", "mysync-1.2.3-win-x64.zip", "mysync.exe")]
    [InlineData("win-arm64", "mysync-1.2.3-win-arm64.zip", "mysync.exe")]
    public void Names_Should_BeExactlyWhatTheReleaseWorkflowPublishes(string rid, string archive, string executable)
    {
        ReleaseAssets.ArchiveName(V, rid).ShouldBe(archive);
        ReleaseAssets.ExecutableName(rid).ShouldBe(executable);
        ReleaseAssets.RuntimeIdentifiers.ShouldContain(rid);
    }

    [Fact]
    public void Tags_ChecksumsAndSignature_Should_KeepTheirNames()
    {
        ReleaseAssets.Tag(V).ShouldBe("mysync-1.2.3");
        ReleaseAssets.ChecksumsName.ShouldBe("SHA256SUMS.txt");
        ReleaseAssets.SignatureName.ShouldBe("SHA256SUMS.txt.sig");
        ReleaseAssets.RuntimeIdentifiers.Count.ShouldBe(7);
    }

    [Fact]
    public void TheReleaseWorkflow_Should_PublishEveryRuntimeAndTheSignature()
    {
        string workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release-mysync.yml"));

        foreach (string rid in ReleaseAssets.RuntimeIdentifiers)
        {
            workflow.ShouldContain($"rid: {rid}");
        }

        workflow.ShouldContain(ReleaseAssets.SignatureName);
        workflow.ShouldContain("name=\"mysync-$VERSION-$RID\"");
    }

    [Fact]
    public void TheReleaseWorkflow_Should_BuildEveryBinaryWithItsOwnRepositoryAndKey()
    {
        string workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release-mysync.yml"));
        string csproj = File.ReadAllText(Path.Combine(RepositoryRoot(), "apps", "mysync", "MySync.Cli.csproj"));

        foreach (string property in new[] { "MySyncReleaseRepository", "MySyncReleasePublicKey" })
        {
            workflow.ShouldContain($"-p:{property}=");
            csproj.ShouldContain($"<AssemblyMetadata Include=\"{property}\" Value=\"$({property})\" />");
        }

        workflow.ShouldContain("RELEASE_REPOSITORY: ${{ github.repository }}");
        workflow.ShouldContain($"UPSTREAM: {ReleaseAssets.DefaultRepository}");
    }

    [Fact]
    public void ParseChecksums_Should_ReadSha256sumOutput_LikeARealRelease()
    {
        const string Sums = """
            cdafed3b93242953bb90978a28990bcfd81e6eed24728a96a0895bc084f66e92  mysync-0.3.0-linux-arm64.tar.gz
            0a76086961647e6691efe0953b7eda6b05eaf86fbaf1e55b1b312a44f908a0b9 *mysync-0.3.0-linux-x64.tar.gz
            not a checksum line
            zz  broken.tar.gz

            """;

        IReadOnlyDictionary<string, ContentHash> parsed = ReleaseAssets.ParseChecksums(Sums.Replace("\n", "\r\n", StringComparison.Ordinal));

        parsed.Keys.ShouldBe(["mysync-0.3.0-linux-arm64.tar.gz", "mysync-0.3.0-linux-x64.tar.gz"], ignoreOrder: true);
        parsed["mysync-0.3.0-linux-x64.tar.gz"].Value.ShouldBe("0a76086961647e6691efe0953b7eda6b05eaf86fbaf1e55b1b312a44f908a0b9");
    }

    internal static string RepositoryRoot()
    {
        DirectoryInfo? folder = new(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "mysync.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new DirectoryNotFoundException("mysync.slnx not found above the test output.");
    }
}
