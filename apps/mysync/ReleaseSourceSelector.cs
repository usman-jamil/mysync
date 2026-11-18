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
/// unset → GitHub <see cref="ReleaseAssets.DefaultRepository"/>; <c>github:owner/repo</c> → that
/// repository; anything else → a local folder (a path, or a <c>file://</c> URL) laid out like GitHub
/// releases. Whatever the source, releases must be signed with the trusted key.
/// </summary>
internal static class ReleaseSourceSelector
{
    public const string SourceVariable = "MYSYNC_UPDATE_SOURCE";
    public const string TokenVariable = "GITHUB_TOKEN";

    /// <summary>
    /// The assembly metadata a <b>test build</b> can carry to trust another key
    /// (<c>-p:MySyncReleasePublicKey=&lt;base64 SubjectPublicKeyInfo&gt;</c>). Release builds don't set it,
    /// and nothing at run time can change it, so an environment variable can never make mysync trust a different key.
    /// </summary>
    public const string PublicKeyMetadata = "MySyncReleasePublicKey";

    private const string GitHubPrefix = "github:";

    public static void Register(IServiceCollection services, IConfiguration configuration, string version)
    {
        string? source = configuration[SourceVariable];
        if (string.IsNullOrWhiteSpace(source))
        {
            services.AddGitHubReleaseSource(ReleaseAssets.DefaultRepository, version, configuration[TokenVariable]);
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

    private static UpdateOptions TrustedKey()
    {
        string? embedded = Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == PublicKeyMetadata)?.Value;

        return string.IsNullOrWhiteSpace(embedded)
            ? UpdateOptions.Production
            : new UpdateOptions($"-----BEGIN PUBLIC KEY-----\n{embedded}\n-----END PUBLIC KEY-----");
    }
}
