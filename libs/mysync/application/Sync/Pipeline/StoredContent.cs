using MySync.Application.Abstractions.Storage;
using SharedKernel;

namespace MySync.Application.Sync.Pipeline;

/// <summary>Temporary bridge while readers move to one decoding component: the stored stream of an opened object.</summary>
internal static class StoredContent
{
    public static Result<Stream> ToContent(this Result<StoredObject> opened) =>
        opened.IsSuccess ? Result.Success(opened.Value.Content) : Result.Failure<Stream>(opened.Error);
}
