using System.Security.Cryptography;

namespace TelegramMediaGrabber.Application.Files;

/// <summary>Content-hash helpers used for cross-message download dedup and upload dedup keys.</summary>
public static class ContentHash
{
    /// <summary>Full SHA-256 hex digest of a fully-written, closed file — used for cross-message duplicate detection after a download completes.</summary>
    public static string OfFile(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// A stable, fast upload dedup key: filename + size + SHA-256 of only
    /// the first <paramref name="prefixBytes"/> bytes — deliberately NOT a
    /// full-file hash, to avoid re-hashing potentially large media on
    /// every upload-directory scan. See PROJECT_STATE.md §5.
    /// </summary>
    public static string ComputeUploadDedupKey(string path, int prefixBytes = 1024 * 1024)
    {
        var info = new FileInfo(path);
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        var buffer = new byte[prefixBytes];
        var read = stream.Read(buffer, 0, buffer.Length);
        var hash = sha256.ComputeHash(buffer, 0, read);
        return $"{info.Name}:{info.Length}:{Convert.ToHexStringLower(hash)}";
    }
}
