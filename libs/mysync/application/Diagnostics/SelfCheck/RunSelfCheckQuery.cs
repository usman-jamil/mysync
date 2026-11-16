using MySync.Application.Abstractions.Messaging;

namespace MySync.Application.Diagnostics.SelfCheck;

/// <summary>
/// Checks the runtime can do what mysync relies on, inside the binary that ships. The release
/// workflow runs it in the published Linux binaries (CLAUDE.md §8, §16): release builds use
/// <c>InvariantGlobalization</c>, and Unicode normalization must still work there.
/// </summary>
public sealed record RunSelfCheckQuery : IQuery<SelfCheckResponse>;
