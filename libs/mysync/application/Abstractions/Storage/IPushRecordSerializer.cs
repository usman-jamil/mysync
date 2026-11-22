using MySync.Domain.Sync.Push;

namespace MySync.Application.Abstractions.Storage;

/// <summary><c>_mysync-push.json</c> as bytes: source-generated, deterministic (Infrastructure).</summary>
public interface IPushRecordSerializer
{
    byte[] Serialize(PushRecord record);
}
