namespace MySync.Domain.Publishing;

/// <summary>
/// The Content-Type a published file is served with, by extension (case-insensitive). The static
/// website serves whatever the blob says; unknown types are <c>application/octet-stream</c>.
/// </summary>
public static class ContentTypeMap
{
    public const string Default = "application/octet-stream";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain; charset=utf-8",
        [".log"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".xhtml"] = "application/xhtml+xml",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".zip"] = "application/zip",
        [".gz"] = "application/gzip",
        [".tar"] = "application/x-tar",
        [".7z"] = "application/x-7z-compressed",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".sh"] = "text/x-shellscript; charset=utf-8",
        [".ps1"] = "text/plain; charset=utf-8",
        [".yml"] = "text/yaml; charset=utf-8",
        [".yaml"] = "text/yaml; charset=utf-8",
    };

    public static string For(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return Types.TryGetValue(Extension(fileName), out string? type) ? type : Default;
    }

    internal static string Extension(string fileName)
    {
        int slash = fileName.LastIndexOf('/');
        int dot = fileName.LastIndexOf('.');
        return dot > slash + 1 ? fileName[dot..] : string.Empty; // ".bashrc" has no extension
    }
}
