using System.IO;
using System.Security.Cryptography;

namespace TaskManagerPlus.Services;

/// <summary>Round 14, #841: SHA-256/MD5/SHA-1 for a file, computed on-demand only (never during a
/// scan/poll - callers gate this behind an explicit "Hash" button, see SecurityViewModel). A
/// single buffered pass over the file feeds all three algorithms at once via TransformBlock rather
/// than re-reading the file three times or loading the whole thing into memory - important since a
/// hashed file could be arbitrarily large.</summary>
public sealed record FileHashResult(string Path, string Sha256, string Md5, string Sha1);

public static class FileHashService
{
    private const int BufferSize = 1024 * 1024; // 1 MiB read chunks - never load a whole file at once

    public static FileHashResult ComputeHashes(string path)
    {
        using var sha256 = SHA256.Create();
        using var md5 = MD5.Create();
        using var sha1 = SHA1.Create();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha256.TransformBlock(buffer, 0, read, null, 0);
            md5.TransformBlock(buffer, 0, read, null, 0);
            sha1.TransformBlock(buffer, 0, read, null, 0);
        }
        sha256.TransformFinalBlock([], 0, 0);
        md5.TransformFinalBlock([], 0, 0);
        sha1.TransformFinalBlock([], 0, 0);

        return new FileHashResult(
            path,
            Convert.ToHexString(sha256.Hash!).ToLowerInvariant(),
            Convert.ToHexString(md5.Hash!).ToLowerInvariant(),
            Convert.ToHexString(sha1.Hash!).ToLowerInvariant());
    }
}
