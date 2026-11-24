using System.Diagnostics.CodeAnalysis;

// The connections and responses in these tests wrap a substitute and in-memory streams: nothing to release.
[assembly: SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Connections and clients that live for one test, against the test bucket.")]
