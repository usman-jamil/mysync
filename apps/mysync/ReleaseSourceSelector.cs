using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySync.Application.Maintenance.Update;
using MySync.Domain.Updates;
using MySync.Infrastructure;
using MySync.Updates.GitHub;

namespace MySync.Cli;

/// <summary>
/// Where <c>mysync update</c> gets releases, from <c>MYSYNC_UPDATE_SOURCE</c>:
/// unset → the repository this binary was released from; <c>github:owner/repo</c> → that
/// repository; anything else → a local folder (a path, or a <c>file://</c> URL) laid out like GitHub
/// releases. Whatever the source, releases must be signed with the trusted key.
/// </summary>
/// <remarks>
/// <b>Forks:</b> the release workflow builds every binary with
/// <c>-p:MySyncReleaseRepository=${{ github.repository }}</c> and the public half of that repository's
/// <c>MYSYNC_RELEASE_SIGNING_KEY</c> (<c>-p:MySyncReleasePublicKey</c>). So a fork's binaries update
/// from the fork and trust the fork's key, with no code change. A local build (neither property set)
/// uses <see cref="ReleaseAssets.DefaultRepository"/> and <see cref="ReleaseSignature.ProductionPublicKeyPem"/>.
/// </remarks>
internal static class ReleaseSourceSelector
{
    public const string SourceVariable = "MYSYNC_UPDATE_SOURCE";
    public const string TokenVariable = "GITHUB_TOKEN";

    /// <summary>
    /// The key releases must be signed with, fixed at build time (<c>-p:MySyncReleasePublicKey=&lt;base64
    /// SubjectPublicKeyInfo&gt;</c>): the release workflow sets it from its own signing key, the update
    /// end-to-end test from a throwaway one. Nothing at run time can change it, so an environment
    /// variable can never make mysync trust a different key.
    /// </summary>
    public const string PublicKeyMetadata = "MySyncReleasePublicKey";

    /// <summary>The GitHub repository this binary was released from (<c>-p:MySyncReleaseRepository=owner/repo</c>).</summary>
    public const string RepositoryMetadata = "MySyncReleaseRepository";

    private const string GitHubPrefix = "github:";

    public static void Register(IServiceCollection services, IConfiguration configuration, string version)
    {
        string? source = configuration[SourceVariable];
        if (string.IsNullOrWhiteSpace(source))
        {
            services.AddGitHubReleaseSource(BuiltIn(RepositoryMetadata) ?? ReleaseAssets.DefaultRepository, version, configuration[TokenVariable]);
        }
        else if (source.StartsWith(GitHubPrefix, StringComparison.OrdinalIgnoreCase))
        {
            services.AddGitHubReleaseSource(source[GitHubPrefix.Length..], version, configuration[TokenVariable]);
        }
        else
        {
            string folder = Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && uri.IsFile ? uri.LocalPath : source;
            services.AddDirectoryReleaseSource(folder);
        }

        services.AddSingleton(TrustedKey());
    }

    private static UpdateOptions TrustedKey() =>
        BuiltIn(PublicKeyMetadata) is { } embedded
            ? new UpdateOptions($"-----BEGIN PUBLIC KEY-----\n{embedded}\n-----END PUBLIC KEY-----")
            : UpdateOptions.Production;

    private static string? BuiltIn(string key)
    {
        string? value = Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
