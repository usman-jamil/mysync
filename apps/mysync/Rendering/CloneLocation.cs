namespace MySync.Cli.Rendering;

/// <summary>
/// How <c>mysync clone</c> names the folder it cloned into: relative (<c>./app-config</c>) when it is
/// inside the current folder, else in full; and the <c>cd</c> hint, none when it is the current
/// folder. BCL only, so the tests can link it in.
/// </summary>
internal static class CloneLocation
{
    /// <returns>The folder as shown, and what to <c>cd</c> to (quoted for the shell), or <c>null</c> when it is the current folder.</returns>
    public static (string Shown, string? Cd) Of(string folder, string current, bool powerShell)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(current);

        string relative = Path.GetRelativePath(current, folder);
        if (relative == ".")
        {
            return (".", null);
        }

        bool inside = !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);

        return inside
            ? ("." + Path.DirectorySeparatorChar + relative, ShellQuote.Quote(relative, powerShell))
            : (folder, ShellQuote.Quote(folder, powerShell));
    }
}
