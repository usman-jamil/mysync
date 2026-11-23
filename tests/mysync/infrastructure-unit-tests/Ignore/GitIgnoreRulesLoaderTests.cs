using MySync.Application.Abstractions.Ignore;
using MySync.Domain.Paths;
using MySync.Infrastructure.FileSystem;
using MySync.Infrastructure.Ignore;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Ignore;

/// <summary>The share root's <c>.mysyncignore</c> and <c>.gitignore</c>: either one alone, or both as a union.</summary>
public sealed class GitIgnoreRulesLoaderTests : IDisposable
{
    private readonly TempShareFolder _folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SharePath P(string value) => SharePath.Create(value).Value;

    private async Task<IIgnoreMatcher> LoadAsync() => (await new GitIgnoreRulesLoader(new ShareRoot(_folder.Path)).LoadAsync(Ct)).Value;

    [Fact]
    public async Task NeitherFile_Should_LeaveOnlyTheBuiltIns()
    {
        IIgnoreMatcher matcher = await LoadAsync();

        matcher.IsIgnored(P("debug.log"), isDirectory: false).ShouldBeFalse();
        matcher.IsIgnored(P(".DS_Store"), isDirectory: false).ShouldBeTrue();
        matcher.IsIgnored(P(".mysynctool"), isDirectory: false).ShouldBeTrue();
    }

    [Fact]
    public async Task OnlyAMysyncignore_Should_ApplyIt()
    {
        _folder.WriteFile(".mysyncignore", "*.log\n");

        IIgnoreMatcher matcher = await LoadAsync();

        matcher.IsIgnored(P("debug.log"), isDirectory: false).ShouldBeTrue();
        matcher.IsIgnored(P("bin"), isDirectory: true).ShouldBeFalse();
    }

    [Fact]
    public async Task OnlyAGitignore_Should_ApplyItTheSameWay()
    {
        _folder.WriteFile(".gitignore", "\uFEFFbin/\r\nobj/\r\n*.user\r\n");

        IIgnoreMatcher matcher = await LoadAsync();

        matcher.IsIgnored(P("bin"), isDirectory: true).ShouldBeTrue();
        matcher.IsIgnored(P("src/obj"), isDirectory: true).ShouldBeTrue();
        matcher.IsIgnored(P("app.csproj.user"), isDirectory: false).ShouldBeTrue();
        matcher.IsIgnored(P("bin"), isDirectory: false).ShouldBeFalse(); // bin/ names a folder only
        matcher.IsIgnored(P(".DS_Store"), isDirectory: false).ShouldBeTrue();
        matcher.IsIgnored(P(".gitignore"), isDirectory: false).ShouldBeFalse(); // synced, like .mysyncignore
    }

    [Fact]
    public async Task BothFiles_Should_IgnoreWhatEitherIgnores()
    {
        _folder.WriteFile(".mysyncignore", "*.log\n").WriteFile(".gitignore", "bin/\n");

        IIgnoreMatcher matcher = await LoadAsync();

        matcher.IsIgnored(P("debug.log"), isDirectory: false).ShouldBeTrue();
        matcher.IsIgnored(P("bin"), isDirectory: true).ShouldBeTrue();
        matcher.IsIgnored(P("src/app.cs"), isDirectory: false).ShouldBeFalse();
    }

    [Fact]
    public async Task ANegationInOneFile_Should_NotReincludeWhatTheOtherIgnores()
    {
        _folder.WriteFile(".mysyncignore", "*.log\n").WriteFile(".gitignore", "build/\n!keep.log\n");

        IIgnoreMatcher matcher = await LoadAsync();

        matcher.IsIgnored(P("keep.log"), isDirectory: false).ShouldBeTrue();
        matcher.IsIgnored(P("build"), isDirectory: true).ShouldBeTrue();
    }

    [Fact]
    public async Task AGitignoreInASubfolder_Should_NotBeRead()
    {
        _folder.WriteFile("sub/.gitignore", "*.txt\n");

        IIgnoreMatcher matcher = await LoadAsync();

        matcher.IsIgnored(P("sub/a.txt"), isDirectory: false).ShouldBeFalse();
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".mysyncignore")]
    public async Task AnOversizedFile_Should_BeRefused_NamingIt(string name)
    {
        _folder.WriteFile(name, new string('#', GitIgnoreRulesLoader.MaxBytes + 1));

        SharedKernel.Error error = (await new GitIgnoreRulesLoader(new ShareRoot(_folder.Path)).LoadAsync(Ct)).Error;

        error.Code.ShouldBe("Local.Unreadable");
        error.Description.ShouldContain(name);
    }

    [Fact]
    public void Union_Should_IgnoreWhenAnyMatcherDoes()
    {
        UnionIgnoreMatcher union = new([new GitIgnoreMatcher(["*.log"]), new GitIgnoreMatcher(["bin/"])]);

        union.IsIgnored(P("a.log"), isDirectory: false).ShouldBeTrue();
        union.IsIgnored(P("bin"), isDirectory: true).ShouldBeTrue();
        union.IsIgnored(P("a.txt"), isDirectory: false).ShouldBeFalse();
        new UnionIgnoreMatcher([]).IsIgnored(P("a.log"), isDirectory: false).ShouldBeFalse();
    }

    public void Dispose() => _folder.Dispose();
}
