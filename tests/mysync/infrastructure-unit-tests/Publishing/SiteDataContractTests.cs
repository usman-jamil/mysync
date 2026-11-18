using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using MySync.Domain.Content;
using MySync.Domain.Manifests;
using MySync.Domain.Paths;
using MySync.Domain.Publishing;
using MySync.Domain.Shares;
using MySync.Infrastructure.Publishing;
using MySync.Testing;
using Shouldly;
using Xunit;

namespace MySync.Infrastructure.UnitTests.Publishing;

/// <summary>
/// <c>site-data.js</c> as mysync writes it, against the page's own contract,
/// <c>apps/mysync-site/contract/site-data.schema.json</c>: the C# side of the test the site runs on its fixture.
/// </summary>
public sealed class SiteDataContractTests
{
    private static readonly string[] AwkwardNames =
    [
        "README.md", "a b/c d.txt", "docs/[draft] plan #3?.md", "docs/100% done & more+plus.txt", "docs/\"quoted\" 'name'.txt",
        "docs/a;b `c` $(d).md", "photos/über café/IMG_0001.jpg", "photos/日本語/写真.png", "photos/🎉 party.webp",
        "site/</script><script>alert(1)</script>.html", "deep/a/b/c/d/e/f/g/notes.txt", "data/empty.csv", "bin/tool.exe",
    ];

    private readonly SiteDataSerializer _serializer = new();

    [Fact]
    public void SiteDataJs_Should_MatchTheSchema_ForEveryAwkwardName()
    {
        JsonNode data = Parse(_serializer.Serialize(Build(Fixture())));

        EvaluationResults results = Schema().Evaluate(JsonSerializer.SerializeToElement(data), new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });

        results.IsValid.ShouldBeTrue(string.Join("\n", (results.Details ?? []).Where(detail => detail.Errors is { Count: > 0 }).Select(detail => $"{detail.InstanceLocation}: {string.Join("; ", detail.Errors!.Values)}")));
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("publication", "{}")]
    [InlineData("shareId", "\"leaks the share\"")]
    public void TheSchemaCheck_Should_CatchADocumentThatBreaksIt(string property, string json)
    {
        JsonNode data = Parse(_serializer.Serialize(Build(Fixture())));
        data[property] = JsonNode.Parse(json);

        Schema().Evaluate(JsonSerializer.SerializeToElement(data), new EvaluationOptions { RequireFormatValidation = true }).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void SiteDataJs_Should_AssignOneObject_AndEscapeEverythingThatCouldEndTheScript()
    {
        string text = Encoding.UTF8.GetString(_serializer.Serialize(Build(Fixture())));

        text.ShouldStartWith("window.__MYSYNC_SITE__ = {");
        text.ShouldEndWith("};\n");
        text.ShouldNotContain("</script>");
        text.ShouldNotContain("<");
        text.ShouldNotContain("`");
        text.All(c => c < 128).ShouldBeTrue("every non-ASCII character is written as \\uXXXX");
    }

    [Fact]
    public void SiteDataJs_Should_CarryEveryEntry_InOrdinalOrder_WithItsDisposition()
    {
        JsonNode data = Parse(_serializer.Serialize(Build(Fixture())));
        JsonArray entries = data["entries"]!.AsArray();

        string[] paths = [.. entries.Select(entry => entry!["path"]!.GetValue<string>())];
        paths.ShouldBe([.. paths.Order(StringComparer.Ordinal)]);
        paths.ShouldContain("photos/über café/IMG_0001.jpg");
        entries.Single(entry => entry!["path"]!.GetValue<string>() == "logs")!["kind"]!.GetValue<string>().ShouldBe("dir");
        entries.Single(entry => entry!["path"]!.GetValue<string>().StartsWith("site/", StringComparison.Ordinal))!["disposition"]!.GetValue<string>().ShouldBe("attachment");
        entries.Single(entry => entry!["path"]!.GetValue<string>() == "README.md")!["disposition"]!.GetValue<string>().ShouldBe("inline");
        data["totals"]!["files"]!.GetValue<long>().ShouldBe(AwkwardNames.Length);
        data["downloads"]!["shell"]!.GetValue<string>().ShouldBe("download.sh");
    }

    [Fact]
    public void SiteDataJs_Should_NeverMentionTheShare()
    {
        Manifest manifest = Fixture();
        string text = Encoding.UTF8.GetString(_serializer.Serialize(Build(manifest)));

        text.ShouldNotContain(manifest.ShareId.ToString());
    }

    internal static Manifest Fixture()
    {
        ManifestBuilder builder = new();
        foreach (string name in AwkwardNames)
        {
            builder.WithFile(name, name == "data/empty.csv" ? string.Empty : "content of " + name);
        }

        return builder.WithDirectory("logs").Build();
    }

    internal static SiteData Build(Manifest manifest) =>
        SiteDataBuilder.Build(manifest, new SiteDataPublication(
            PublishId.New(), "Q3 reports — <b>über</b>", new DateTimeOffset(2026, 11, 18, 7, 30, 0, TimeSpan.Zero), manifest.Revision, "mysync 0.5.0")).Data;

    private static JsonNode Parse(byte[] siteDataJs)
    {
        string text = Encoding.UTF8.GetString(siteDataJs);
        return JsonNode.Parse(text[SiteDataSerializer.Prefix.Length..^2])!;
    }

    // Loaded once: JsonSchema.Net registers a schema globally by its $id.
    private static readonly Lazy<JsonSchema> LoadedSchema = new(() =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(RepositoryRoot(), "apps", "mysync-site", "contract", "site-data.schema.json"))));

    private static JsonSchema Schema() => LoadedSchema.Value;

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "mysync.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("mysync.slnx not found above the test binaries.");
    }
}
