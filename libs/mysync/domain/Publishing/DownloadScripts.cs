using System.Globalization;
using System.Text;

namespace MySync.Domain.Publishing;

/// <summary>
/// The "download everything" files of a publication: <c>SHA256SUMS.txt</c>, <c>download.sh</c>
/// and <c>download.ps1</c>. Pure string building. Every path is untrusted, so neither script
/// ever lets one become code:
/// <list type="bullet">
/// <item><c>download.sh</c> single-quotes each path (<c>'</c> becomes <c>'\''</c>); POSIX <c>sh</c>, LF line endings.</item>
/// <item><c>download.ps1</c> carries each path as base64 of its UTF-8 bytes: PowerShell also takes
/// curly quotes as string delimiters, and base64 keeps the script plain ASCII for Windows PowerShell 5.1.</item>
/// </list>
/// Both download into the current folder, verify each file's SHA-256, and stop at the first mismatch.
/// </summary>
public static class DownloadScripts
{
    /// <summary><c>&lt;sha256&gt;  &lt;path&gt;</c> per file, ordinal path order: what <c>sha256sum -c</c> reads.</summary>
    public static string Sha256Sums(SiteData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        StringBuilder text = new();
        foreach (SiteDataFile file in data.Entries.OfType<SiteDataFile>())
        {
            text.Append(file.Sha256.Value).Append("  ").Append(file.Path.Value).Append('\n');
        }

        return text.ToString();
    }

    public static string Shell(SiteData data, Uri publicationUrl)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(publicationUrl);

        StringBuilder script = new();
        void Line(string text) => script.Append(text).Append('\n');

        Line("#!/bin/sh");
        Line($"# Downloads mysync publication {data.Publication.Id} ({Count(data)}) into the current folder");
        Line("# and checks every file's SHA-256, stopping at the first that doesn't match.");
        Line("#");
        Line($"#   curl -fsSL {publicationUrl.AbsoluteUri}{PublicationLayout.ShellScript} | sh");
        Line(string.Empty);
        Line("set -eu");
        Line($"base={ShellQuote(publicationUrl.AbsoluteUri)}");
        Line(string.Empty);
        Line("command -v curl >/dev/null 2>&1 || { echo 'mysync: curl is required' >&2; exit 1; }");
        Line("if command -v sha256sum >/dev/null 2>&1; then sha256() { sha256sum \"$1\" | cut -d' ' -f1; }");
        Line("elif command -v shasum >/dev/null 2>&1; then sha256() { shasum -a 256 \"$1\" | cut -d' ' -f1; }");
        Line("else echo 'mysync: sha256sum or shasum is required' >&2; exit 1; fi");
        Line(string.Empty);
        Line("get() { # <url> <path> <sha256>");
        Line("  mkdir -p \"$(dirname \"$2\")\"");
        Line("  curl -fSL --retry 3 -o \"$2\" \"$base$1\"");
        Line("  if [ \"$(sha256 \"$2\")\" != \"$3\" ]; then printf 'mysync: %s does not match its SHA-256; stopping.\\n' \"$2\" >&2; exit 1; fi");
        Line("}");
        Line("empty() { mkdir -p \"$(dirname \"$1\")\"; : > \"$1\"; }");
        Line(string.Empty);

        foreach (SiteDataEntry entry in data.Entries)
        {
            string local = ShellQuote("./" + entry.Path.Value);
            Line(entry switch
            {
                SiteDataFile { Size: 0 } => $"empty {local}",
                SiteDataFile file => $"get {ShellQuote(FileHref(file))} {local} {file.Sha256.Value}",
                _ => $"mkdir -p {local}",
            });
        }

        Line(string.Empty);
        Line($"echo 'Downloaded and verified {Count(data)}.'");
        return script.ToString();
    }

    public static string PowerShell(SiteData data, Uri publicationUrl)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(publicationUrl);

        StringBuilder script = new();
        void Line(string text) => script.Append(text).Append("\r\n");

        Line($"# Downloads mysync publication {data.Publication.Id} ({Count(data)}) into the current folder");
        Line("# and checks every file's SHA-256, stopping at the first that doesn't match.");
        Line("#");
        Line($"#   irm {publicationUrl.AbsoluteUri}{PublicationLayout.PowerShellScript} | iex");
        Line(string.Empty);
        Line("$ErrorActionPreference = 'Stop'");
        Line("$ProgressPreference = 'SilentlyContinue'");
        Line("[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12");
        Line($"$base = '{publicationUrl.AbsoluteUri}'");
        Line("$root = (Get-Location).ProviderPath");
        Line(string.Empty);
        Line("# Paths are base64 of their UTF-8 bytes; .NET calls take them literally ([ and ] aren't wildcards).");
        Line("function Get-Target([string] $Path64) {");
        Line("    $relative = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Path64))");
        Line("    $target = [IO.Path]::Combine($root, $relative)");
        Line("    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))");
        Line("    return $target");
        Line("}");
        Line("function Get-MySyncFile([string] $Url, [string] $Path64, [string] $Sha256) {");
        Line("    $target = Get-Target $Path64");
        Line("    $temp = [IO.Path]::GetTempFileName()");
        Line("    Invoke-WebRequest -UseBasicParsing -Uri ($base + $Url) -OutFile $temp");
        Line("    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $temp).Hash.ToLowerInvariant()");
        Line("    if ($actual -ne $Sha256) { Remove-Item -LiteralPath $temp; throw \"mysync: $target does not match its SHA-256; stopping.\" }");
        Line("    if ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }");
        Line("    [IO.File]::Move($temp, $target)");
        Line("}");
        Line("function New-EmptyFile([string] $Path64) { [IO.File]::WriteAllBytes((Get-Target $Path64), [byte[]]@()) }");
        Line("function New-Folder([string] $Path64) {");
        Line("    [void][IO.Directory]::CreateDirectory([IO.Path]::Combine($root, [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Path64))))");
        Line("}");
        Line(string.Empty);

        foreach (SiteDataEntry entry in data.Entries)
        {
            string path64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Path.Value));
            Line(entry switch
            {
                SiteDataFile { Size: 0 } => $"New-EmptyFile '{path64}'",
                SiteDataFile file => $"Get-MySyncFile '{FileHref(file)}' '{path64}' '{file.Sha256.Value}'",
                _ => $"New-Folder '{path64}'",
            });
        }

        Line(string.Empty);
        Line($"Write-Host 'Downloaded and verified {Count(data)}.'");
        return script.ToString();
    }

    /// <summary>The relative link to a file: <c>files/</c> + each segment percent-encoded (RFC 3986). ASCII only.</summary>
    public static string FileHref(SiteDataFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return "files/" + string.Join('/', file.Path.Segments.Select(Uri.EscapeDataString));
    }

    /// <summary>POSIX single quotes: the only character that needs care inside them is <c>'</c> itself.</summary>
    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Count(SiteData data) =>
        data.TotalFiles == 1 ? "1 file" : string.Create(CultureInfo.InvariantCulture, $"{data.TotalFiles} files");
}
