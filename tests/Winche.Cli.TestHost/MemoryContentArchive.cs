using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Winche.Storage.Interfaces;
using Winche.Storage.Locations;
using Winche.Storage.Models;

namespace Winche.Cli.TestHost;

/// <summary>Assets' contents in memory: bytes by key, no links (files are streamed).</summary>
public sealed class MemoryContentArchive : IArchive, IArchiveContent
{
    public static ConcurrentDictionary<string, byte[]> Objects { get; } = new();

    public async Task WriteAsync(string path, Stream content, string? contentType, CancellationToken ct = default)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);
        Objects[path] = copy.ToArray();
    }
    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<Stream?>(Objects.TryGetValue(path, out var b) ? new MemoryStream(b) : null);
    public Task<ArchiveObjectInfo?> HeadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<ArchiveObjectInfo?>(Objects.TryGetValue(path, out var b) ? new ArchiveObjectInfo(b.Length, null) : null);
    public Task DeleteAsync(string path, CancellationToken ct = default) { Objects.TryRemove(path, out _); return Task.CompletedTask; }
    public Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default) { foreach (var p in paths) Objects.TryRemove(p, out _); return Task.CompletedTask; }
    public async IAsyncEnumerable<ArchivedObject> ListObjectsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, DownloadHeaders? headers = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string> CreateMultipartUploadAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => throw new NotSupportedException();
}
