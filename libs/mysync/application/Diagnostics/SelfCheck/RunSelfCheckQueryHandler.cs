using MySync.Application.Abstractions.Messaging;
using MySync.Domain.Paths;
using SharedKernel;

namespace MySync.Application.Diagnostics.SelfCheck;

internal sealed class RunSelfCheckQueryHandler : IQueryHandler<RunSelfCheckQuery, SelfCheckResponse>
{
    // "café" spelled with a combining acute accent (NFD, as macOS may return it) and with the
    // precomposed character (NFC, as manifests store it).
    private const string Decomposed = "café";
    private const string Composed = "café";

    public Task<Result<SelfCheckResponse>> Handle(RunSelfCheckQuery query, CancellationToken cancellationToken)
    {
        SelfCheckResponse response = new([CheckUnicodeNormalization(), DescribeGlobalization()]);

        return Task.FromResult(Result.Success(response));
    }

    private static SelfCheckResult CheckUnicodeNormalization()
    {
        const string name = "unicode-normalization";

        bool passed = string.Equals(PathRules.ToNfc(Decomposed), Composed, StringComparison.Ordinal);
        string detail = passed
            ? $"NFD -> NFC ok (Unicode {PathRules.UnicodeVersion})"
            : "NFD input was not composed to NFC";

        return new SelfCheckResult(name, passed, detail);
    }

    private static SelfCheckResult DescribeGlobalization()
    {
        // Informational only: tells a release smoke test which mode it actually exercised.
        bool invariant = AppContext.TryGetSwitch("System.Globalization.Invariant", out bool enabled) && enabled;

        return new SelfCheckResult("globalization", true, invariant ? "invariant mode" : "ICU / NLS");
    }
}
