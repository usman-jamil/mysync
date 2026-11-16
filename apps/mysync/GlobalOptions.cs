namespace MySync.Cli;

/// <summary>
/// Options every command accepts. Parsed by ConsoleAppFramework before routing and read from
/// <c>ConsoleAppContext.GlobalOptions</c>.
/// </summary>
/// <param name="Parallel">Null when not given: fall back to <c>MYSYNC_PARALLELISM</c>, then the default.</param>
internal sealed record GlobalOptions(bool Verbose, bool Quiet, bool Json, int? Parallel);
