using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Winche.Storage.Interfaces;
using Winche.Storage.Models;

namespace Winche.Cli.Emulator;

/// <summary>
/// Winche.Storage's object store for the emulator: files in a local folder, uploaded and downloaded through the
/// emulator's own <c>/_emulator/objects/…</c> URLs (what S3's presigned URLs are in production).
/// </summary>
internal sealed class LocalArchive(string root, Func<string> baseUrl) : IArchive
{
    public const string Prefix = "/_emulator/objects";
    private const string PartsPrefix = "/_emulator/parts";

    public string Root { get; } = Directory.CreateDirectory(root).FullName;

    private string FileOf(string path) => Path.Combine(Root, "objects", Safe(path));
    private string PartsOf(string uploadId) => Path.Combine(Root, "parts", Safe(uploadId));

    private static string Safe(string path)
    {
        var full = path.Replace('\\', '/').Trim('/');
        if (full.Split('/').Any(s => s is "" or "." or "..")) throw new ArgumentException($"Bad object path '{path}'.");
        return full;
    }

    private static string Url(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static UploadSession Session(string url) => new() { Url = url, ExpiresAt = DateTime.UtcNow.AddHours(1) };

    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) =>
        Task.FromResult(Session($"{baseUrl()}{Prefix}/{Url(path)}"));

    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new DownloadSession { Url = $"{baseUrl()}{Prefix}/{Url(path)}", ExpiresAt = DateTime.UtcNow.AddHours(1) });

    public async Task<string?> GetObjectETagAsync(string path, CancellationToken ct = default)
    {
        var file = FileOf(path);
        if (!File.Exists(file)) return null;
        await using var stream = File.OpenRead(file);
        return $"\"{Convert.ToHexStringLower(await MD5.HashDataAsync(stream, ct))}\"";
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        File.Delete(FileOf(path));
        return Task.CompletedTask;
    }

    public async Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        foreach (var p in paths) await DeleteAsync(p, ct);
    }

    public async IAsyncEnumerable<ArchivedObject> ListObjectsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var objects = Path.Combine(Root, "objects");
        if (!Directory.Exists(objects)) yield break;
        foreach (var file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
        {
            var key = Path.GetRelativePath(objects, file).Replace('\\', '/');
            if (prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
                yield return new ArchivedObject(key, File.GetLastWriteTimeUtc(file));
        }
    }

    public Task<string> CreateMultipartUploadAsync(string path, string mimeType, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(PartsOf(id));
        File.WriteAllText(Path.Combine(PartsOf(id), "path"), path);
        return Task.FromResult(id);
    }

    public Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default) =>
        Task.FromResult(Session($"{baseUrl()}{PartsPrefix}/{uploadId}/{partNumber}"));

    public async Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        var dir = PartsOf(uploadId);
        if (!Directory.Exists(dir)) return null;
        var file = FileOf(path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await using (var output = File.Create(file))
            foreach (var part in Parts(dir))
                await using (var input = File.OpenRead(Path.Combine(dir, part.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                    await input.CopyToAsync(output, ct);
        Directory.Delete(dir, recursive: true);
        return await GetObjectETagAsync(path, ct);
    }

    public Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default) =>
        Task.FromResult<IEnumerable<FilePart>>(Directory.Exists(PartsOf(uploadId)) ? Parts(PartsOf(uploadId)) : []);

    public Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default)
    {
        if (Directory.Exists(PartsOf(uploadId))) Directory.Delete(PartsOf(uploadId), recursive: true);
        return Task.CompletedTask;
    }

    private static List<FilePart> Parts(string dir) =>
        [.. Directory.EnumerateFiles(dir)
            .Select(f => (Name: Path.GetFileName(f), Size: new FileInfo(f).Length))
            .Where(f => int.TryParse(f.Name, out _))
            .Select(f => new FilePart { Number = int.Parse(f.Name, System.Globalization.CultureInfo.InvariantCulture), Size = f.Size })
            .OrderBy(p => p.Number)];

    /// <summary>The upload and download endpoints its URLs point at.</summary>
    public void Map(WebApplication app)
    {
        var types = new FileExtensionContentTypeProvider();
        app.MapPut(Prefix + "/{**path}", async (string path, HttpRequest request, CancellationToken ct) =>
        {
            var file = FileOf(Uri.UnescapeDataString(path));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await using (var output = File.Create(file)) await request.Body.CopyToAsync(output, ct);
            return Results.Ok();
        });
        app.MapGet(Prefix + "/{**path}", (string path) =>
        {
            var file = FileOf(Uri.UnescapeDataString(path));
            if (!File.Exists(file)) return Results.NotFound();
            return Results.File(file, types.TryGetContentType(file, out var type) ? type : "application/octet-stream");
        });
        app.MapPut(PartsPrefix + "/{uploadId}/{part:int}", async (string uploadId, int part, HttpRequest request, HttpResponse response, CancellationToken ct) =>
        {
            var dir = PartsOf(uploadId);
            if (!Directory.Exists(dir)) return Results.NotFound();
            var file = Path.Combine(dir, part.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await using (var output = File.Create(file)) await request.Body.CopyToAsync(output, ct);
            await using var stream = File.OpenRead(file);
            response.Headers.ETag = $"\"{Convert.ToHexStringLower(await MD5.HashDataAsync(stream, ct))}\"";
            return Results.Ok();
        });
    }
}
