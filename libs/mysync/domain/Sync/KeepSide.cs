namespace MySync.Domain.Sync;

/// <summary>Which side <c>mysync resolve --keep local|remote</c> keeps.</summary>
public enum KeepSide
{
    Local = 0,
    Remote = 1,
}
