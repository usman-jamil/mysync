using System.Diagnostics.CodeAnalysis;

// The connections and responses in these tests wrap a substitute and in-memory streams: nothing to release.
[assembly: SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test doubles over a substitute S3 and memory streams.")]
