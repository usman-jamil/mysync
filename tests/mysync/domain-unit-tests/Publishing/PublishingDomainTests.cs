using System.Collections.Immutable;
using System.Text;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Publishing;
using Shouldly;
using Xunit;
using static MySync.Domain.UnitTests.TestSupport.Fixtures;

namespace MySync.Domain.UnitTests.Publishing;

public class PublishIdTests
{
    [Fact]
    public void New_Should_Be22Base62Characters_AndDifferEveryTime()
    {
        HashSet<string> seen = [];
        for (int i = 0; i < 2000; i++)
        {
            var id = PublishId.New();
            id.Value.Length.ShouldBe(22);
            id.Value.All(char.IsAsciiLetterOrDigit).ShouldBeTrue();
            seen.Add(id.Value).ShouldBeTrue("128 random bits don't repeat");
        }
    }

    [Fact]
    public void FromBytes_Should_EncodeAll128Bits()
    {
        PublishId.FromBytes(new byte[16]).Value.ShouldBe(new string('0', 22));
        PublishId.FromBytes(Enumerable.Repeat((byte)0xFF, 16).ToArray()).Value.ShouldBe("7n42DGM5Tflk9n8mt7Fhc7"); // 2^128 - 1 in base62
        PublishId.FromBytes([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 62]).Value.ShouldBe("0000000000000000000010");
    }

    [Fact]
    public void New_Should_ComeFromTheCryptographicGenerator() =>
        typeof(PublishId).Assembly.GetReferencedAssemblies().ShouldNotContain(name => name.Name == "System.Random");

    [Theory]
    [InlineData("4fQ9xK2mT7bV1nR8sL3pZa", true)]
    [InlineData("4fQ9xK2mT7bV1nR8sL3pZ", false)]
    [InlineData("4fQ9xK2mT7bV1nR8sL3pZa0", false)]
    [InlineData("4fQ9xK2mT7bV1nR8sL3p-a", false)]
    [InlineData("../../../../../../etc/p", false)]
    [InlineData("0b6f3c1e-8f0a-4c43-9d3e", false)]
    [InlineData(null, false)]
    public void TryParse_Should_AcceptOnly22Base62Characters(string? text, bool valid) =>
        PublishId.TryParse(text, out _).ShouldBe(valid);
}

public class DispositionPolicyTests
{
    [Theory]
    [InlineData("a.html")]
    [InlineData("a.HTM")]
    [InlineData("a.svg")]
    [InlineData("a.xml")]
    [InlineData("a.js")]
    [InlineData("a.mjs")]
    [InlineData("a.xhtml")]
    [InlineData("dir.txt/a.Html")]
    [InlineData("noextension")]
    [InlineData(".bashrc")]
    [InlineData("tool.exe")]
    [InlineData("report.txt.html")]
    public void EverythingThatCouldRunScript_OrIsUnknown_Should_Download(string name) =>
        DispositionPolicy.For(name).ShouldBe(Disposition.Attachment);

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.md")]
    [InlineData("a.log")]
    [InlineData("a.csv")]
    [InlineData("a.json")]
    [InlineData("a.pdf")]
    [InlineData("a.PNG")]
    [InlineData("a.jpg")]
    [InlineData("a.jpeg")]
    [InlineData("a.gif")]
    [InlineData("a.webp")]
    public void PassiveTypes_Should_ShowInline(string name) => DispositionPolicy.For(name).ShouldBe(Disposition.Inline);

    [Theory]
    [InlineData("docs/plain.txt", "inline; filename*=UTF-8''plain.txt")]
    [InlineData("site/index.html", "attachment; filename*=UTF-8''index.html")]
    [InlineData("a b/c d.exe", "attachment; filename*=UTF-8''c%20d.exe")]
    [InlineData("x/\"quoted\";name.bin", "attachment; filename*=UTF-8''%22quoted%22%3Bname.bin")]
    [InlineData("x/café.pdf", "inline; filename*=UTF-8''caf%C3%A9.pdf")]
    [InlineData("x/a\r\nSet-Cookie: x.bin", "attachment; filename*=UTF-8''a%0D%0ASet-Cookie%3A%20x.bin")]
    public void ContentDisposition_Should_UseTheEncodedFileName(string path, string header) =>
        DispositionPolicy.ContentDisposition(path).ShouldBe(header);

    [Theory]
    [InlineData("a.js", "text/javascript; charset=utf-8")]
    [InlineData("a.CSS", "text/css; charset=utf-8")]
    [InlineData("a.pdf", "application/pdf")]
    [InlineData("a.unknownext", "application/octet-stream")]
    [InlineData(".bashrc", "application/octet-stream")]
    [InlineData("a.tar.gz", "application/gzip")]
    public void ContentTypeMap_Should_FollowTheExtension(string name, string type) => ContentTypeMap.For(name).ShouldBe(type);
}

public class PublicationLayoutTests
{
    private static readonly PublishId Id = PublishId.TryParse("4fQ9xK2mT7bV1nR8sL3pZa", out PublishId? id) ? id : throw new InvalidOperationException();

    [Fact]
    public void BlobNames_Should_LiveUnderThePublicationsPrefix()
    {
        var layout = PublicationLayout.For(Id);

        layout.Prefix.ShouldBe("p/4fQ9xK2mT7bV1nR8sL3pZa/");
        layout.Index.ShouldBe("p/4fQ9xK2mT7bV1nR8sL3pZa/index.html");
        layout.Blob("assets/app.js").ShouldBe("p/4fQ9xK2mT7bV1nR8sL3pZa/assets/app.js");
        layout.File(Path("docs/a b.txt")).ShouldBe("p/4fQ9xK2mT7bV1nR8sL3pZa/files/docs/a b.txt");
    }

    [Theory]
    [InlineData("https://acct.z13.web.core.windows.net")]
    [InlineData("https://acct.z13.web.core.windows.net/")]
    public void Url_Should_NotDependOnATrailingSlash(string baseUrl) =>
        PublicationLayout.For(Id).Url(new Uri(baseUrl)).AbsoluteUri.ShouldBe("https://acct.z13.web.core.windows.net/p/4fQ9xK2mT7bV1nR8sL3pZa/");

    [Fact]
    public void Url_Should_KeepABasePath() =>
        PublicationLayout.For(Id).Url(new Uri("https://cdn.example.com/shared")).AbsoluteUri.ShouldBe("https://cdn.example.com/shared/p/4fQ9xK2mT7bV1nR8sL3pZa/");

    [Fact]
    public void CannotPublish_Should_RefuseWhatAzureCantName()
    {
        var layout = PublicationLayout.For(Id);

        layout.CannotPublish(Path("ends with dot.")).ShouldNotBeNull();
        layout.CannotPublish(Path(new string('a', 1000) + "/b")).ShouldNotBeNull();
        layout.CannotPublish(Path("fine/name.txt")).ShouldBeNull();
        layout.CannotPublish(Path("dir./fine")).ShouldBeNull();
    }
}

public class PublicationRegistryTests
{
    private static Publication Record(string id, PublicationStatus status = PublicationStatus.Pending) => new(
        PublishId.TryParse(id, out PublishId? parsed) ? parsed : throw new InvalidOperationException(),
        status, "t", Rev(1), 0, 0, Created, "me", "acct", "https://x/p/" + id + "/", "v");

    [Fact]
    public void With_Should_AddOrReplaceById_AndWithout_ShouldRemove()
    {
        PublicationRegistry registry = PublicationRegistry.Empty
            .With(Record("AAAAAAAAAAAAAAAAAAAAAA"))
            .With(Record("BBBBBBBBBBBBBBBBBBBBBB"))
            .With(Record("AAAAAAAAAAAAAAAAAAAAAA", PublicationStatus.Published));

        registry.Publications.Select(p => (p.Id.Value, p.Status)).ShouldBe(
            [("AAAAAAAAAAAAAAAAAAAAAA", PublicationStatus.Published), ("BBBBBBBBBBBBBBBBBBBBBB", PublicationStatus.Pending)]);
        registry.Without(registry.Publications[0].Id).Publications.Single().Id.Value.ShouldBe("BBBBBBBBBBBBBBBBBBBBBB");
        PublicationRegistry.Empty.Publications.ShouldBeEmpty();
    }
}

public class SiteDataBuilderTests
{
    private static readonly SiteDataPublication Publication = new(
        PublishId.New(), "Title", Created, Rev(7), "mysync test");

    private static Manifest Manifest(params (string Path, ManifestEntry Entry)[] entries) =>
        Ok(Domain.Manifests.Manifest.CreateNew(Share(), entries.Select(entry => Entry(entry.Path, entry.Entry)), Created, "me@HOST"));

    [Fact]
    public void Build_Should_ListEveryEntry_InOrdinalOrder_WithTotals()
    {
        Manifest manifest = Manifest(
            ("b.txt", File(size: 10)),
            ("a/z.html", File(size: 5)),
            ("a/Z.pdf", File(size: 1)),
            ("empty", DirectoryEntry.Instance),
            ("é.txt", File(size: 2)));

        (SiteData data, ImmutableArray<UnpublishablePath> skipped) = SiteDataBuilder.Build(manifest, Publication);

        data.Entries.Select(entry => entry.Path.Value).ShouldBe(["a/Z.pdf", "a/z.html", "b.txt", "empty", "é.txt"]);
        data.TotalFiles.ShouldBe(4);
        data.TotalBytes.ShouldBe(18);
        data.Entries.OfType<SiteDataDirectory>().Single().Path.Value.ShouldBe("empty");
        data.Entries.OfType<SiteDataFile>().Single(file => file.Path.Value == "a/z.html").Disposition.ShouldBe(Disposition.Attachment);
        data.Entries.OfType<SiteDataFile>().Single(file => file.Path.Value == "a/Z.pdf").Disposition.ShouldBe(Disposition.Inline);
        data.Publication.Revision.ShouldBe(Rev(7));
        skipped.ShouldBeEmpty();
    }

    [Fact]
    public void Build_Should_LeaveOutWhatCantBeABlob_AndSaySo()
    {
        (SiteData data, ImmutableArray<UnpublishablePath> skipped) = SiteDataBuilder.Build(Manifest(("ok.txt", File()), ("trailing.", File())), Publication);

        data.Entries.Select(entry => entry.Path.Value).ShouldBe(["ok.txt"]);
        data.TotalFiles.ShouldBe(1);
        skipped.Single().Path.Value.ShouldBe("trailing.");
    }
}

public class DownloadScriptsTests
{
    private static readonly Uri Url = new("https://acct.z13.web.core.windows.net/p/4fQ9xK2mT7bV1nR8sL3pZa/");

    private static readonly string[] Hostile =
    [
        "$(touch pwned).txt", "`touch pwned`.txt", "a;touch pwned.txt", "it's 'quoted'.txt", "\"double\".txt",
        "-rf.txt", "‘curly’ ’quotes‛.txt", "$HOME.txt", "a\\b.txt", "über/日本語 🎉.md", "sp ace/[x] #?%&+.bin",
    ];

    private static SiteData Data()
    {
        List<(string, ManifestEntry)> entries = [.. Hostile.Where(name => !name.Contains('\\')).Select(name => (name, (ManifestEntry)File()))];
        entries.Add(("zero.txt", File(HashOfEmpty, size: 0)));
        entries.Add(("emptydir", DirectoryEntry.Instance));
        Manifest manifest = Ok(Manifest.CreateNew(Share(), entries.Select(entry => Entry(entry.Item1, entry.Item2)), Created, "me"));
        return SiteDataBuilder.Build(manifest, new SiteDataPublication(PublishId.New(), "t", Created, Rev(1), "g")).Data;
    }

    [Fact]
    public void TheShellScript_Should_UseOnlyLfLineEndings_AndAPosixShebang()
    {
        string script = DownloadScripts.Shell(Data(), Url);

        script.ShouldStartWith("#!/bin/sh\n");
        script.ShouldNotContain("\r");
        script.ShouldContain("set -eu\n");
        script.ShouldContain("base='https://acct.z13.web.core.windows.net/p/4fQ9xK2mT7bV1nR8sL3pZa/'");
    }

    [Fact]
    public void TheShellScript_Should_SingleQuoteEveryPath_SoNoNameIsCode()
    {
        string script = DownloadScripts.Shell(Data(), Url);
        string[] calls = [.. script.Split('\n').Where(line => line.StartsWith("get ", StringComparison.Ordinal) || line.StartsWith("empty ", StringComparison.Ordinal) || line.StartsWith("mkdir -p '", StringComparison.Ordinal))];

        calls.Length.ShouldBe(Data().Entries.Length);
        foreach (string call in calls)
        {
            // What the shell reads as code, outside quotes, is only the command word, spaces and (for get) a hex hash.
            Unquoted(call).ShouldMatch("^(get|empty|mkdir -p)( +[0-9a-f]{64})? *$", call);
        }

        script.ShouldContain("get 'files/it%27s%20%27quoted%27.txt' './it'\\''s '\\''quoted'\\''.txt'");
        script.ShouldContain("empty './zero.txt'");
        script.ShouldContain("mkdir -p './emptydir'");
    }

    [Fact]
    public void ThePowerShellScript_Should_BeAscii_AndCarryPathsOnlyAsBase64()
    {
        string script = DownloadScripts.PowerShell(Data(), Url);

        script.All(c => c < 128).ShouldBeTrue("Windows PowerShell 5.1 may not read a script as UTF-8");
        foreach (string name in Hostile.Where(name => !name.Contains('\\')))
        {
            script.ShouldContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(name)));
            if (Uri.EscapeDataString(name) != name)
            {
                script.ShouldNotContain(name); // only a URL-safe name may appear, inside its quoted URL
            }
        }

        foreach (string line in script.Split("\r\n").Where(line => line.StartsWith("Get-MySyncFile ", StringComparison.Ordinal)))
        {
            line.ShouldMatch("^Get-MySyncFile 'files/[A-Za-z0-9%._~/-]+' '[A-Za-z0-9+/=]+' '[0-9a-f]{64}'$");
        }
    }

    /// <summary>The characters POSIX sh treats as code: outside single quotes, and not backslash-escaped.</summary>
    private static string Unquoted(string line)
    {
        StringBuilder code = new();
        bool quoted = false;
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (quoted)
            {
                quoted = c != '\'';
            }
            else if (c == '\'')
            {
                quoted = true;
            }
            else if (c == '\\')
            {
                i++; // the next character is escaped: data, not code
            }
            else
            {
                code.Append(c);
            }

            i++;
        }

        quoted.ShouldBeFalse($"unbalanced quotes: {line}");
        return code.ToString();
    }

    [Fact]
    public void Sha256Sums_Should_ListEveryFile_AsSha256sumReadsIt()
    {
        string sums = DownloadScripts.Sha256Sums(Data());

        sums.ShouldNotContain("\r");
        sums.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(Data().Entries.OfType<SiteDataFile>().Count());
        sums.ShouldContain($"{HashOfEmpty}  zero.txt\n");
        sums.ShouldNotContain("emptydir");
    }

    [Fact]
    public void FileHref_Should_PercentEncodeEachSegment() =>
        DownloadScripts.FileHref(new SiteDataFile(Path("sp ace/[x] #?%&+.bin"), 1, Hash(HashOfTest), Created, Disposition.Attachment))
            .ShouldBe("files/sp%20ace/%5Bx%5D%20%23%3F%25%26%2B.bin");
}
