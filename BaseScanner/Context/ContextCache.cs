using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace BaseScanner.Context;

/// <summary>
/// Two-level cache for CodeContext (memory + disk).
/// Provides fast access to previously computed context data.
/// </summary>
public class ContextCache : IDisposable
{
    private readonly MemoryCache _memoryCache;
    private readonly string _cacheDirectory;
    private readonly TimeSpan _memoryCacheExpiration = TimeSpan.FromMinutes(30);
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ContextCache(string? cacheDirectory = null)
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 10 // Max 10 contexts in memory
        });

        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BaseScanner",
            "ContextCache");

        if (!Directory.Exists(_cacheDirectory))
            Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Try to get a cached context for the given project.
    /// </summary>
    public async Task<CodeContext?> TryGetAsync(string projectPath, IEnumerable<string> documentPaths)
    {
        var key = ComputeCacheKey(projectPath, documentPaths);

        // Try L1 (memory)
        if (_memoryCache.TryGetValue(key, out CodeContext? context))
            return context;

        // Try L2 (disk)
        var diskPath = GetDiskCachePath(key);
        if (File.Exists(diskPath))
        {
            try
            {
                var loaded = await LoadFromDiskAsync(diskPath);
                if (loaded != null)
                {
                    // Promote to L1
                    SetInMemory(key, loaded);
                    return loaded;
                }
            }
            catch
            {
                // Cache corrupted, delete it
                try { File.Delete(diskPath); } catch { }
            }
        }

        return null;
    }

    /// <summary>
    /// Store a context in the cache.
    /// </summary>
    public async Task SetAsync(string projectPath, IEnumerable<string> documentPaths, CodeContext context)
    {
        var key = ComputeCacheKey(projectPath, documentPaths);

        // Store in L1
        SetInMemory(key, context);

        // Store in L2 (async, don't block)
        _ = Task.Run(async () =>
        {
            try
            {
                await SaveToDiskAsync(GetDiskCachePath(key), context);
            }
            catch
            {
                // Ignore disk cache failures
            }
        });
    }

    /// <summary>
    /// Invalidate cache for a project.
    /// </summary>
    public void Invalidate(string projectPath)
    {
        // We can't easily invalidate by project in memory cache without tracking keys
        // For now, just clear old disk cache files
        try
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var files = Directory.GetFiles(_cacheDirectory, $"{projectName}-*.cache");
            foreach (var file in files)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Clear all cached data.
    /// </summary>
    public void Clear()
    {
        _memoryCache.Compact(1.0); // Remove everything

        try
        {
            foreach (var file in Directory.GetFiles(_cacheDirectory, "*.cache"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Clean up old cache files.
    /// </summary>
    public void CleanupOldEntries(TimeSpan maxAge)
    {
        try
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.GetFiles(_cacheDirectory, "*.cache"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private void SetInMemory(string key, CodeContext context)
    {
        var options = new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = _memoryCacheExpiration
        };
        _memoryCache.Set(key, context, options);
    }

    private string ComputeCacheKey(string projectPath, IEnumerable<string> documentPaths)
    {
        var sb = new StringBuilder();
        sb.Append(projectPath);
        sb.Append('|');

        // Sort documents for consistent hashing
        foreach (var doc in documentPaths.OrderBy(d => d))
        {
            sb.Append(doc);
            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(doc);
                sb.Append(':');
                sb.Append(lastWrite.Ticks);
            }
            catch { }
            sb.Append('|');
        }

        var hash = ComputeHash(sb.ToString());
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        return $"{projectName}-{hash}";
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private string GetDiskCachePath(string key) =>
        Path.Combine(_cacheDirectory, $"{key}.cache");

    private async Task<CodeContext?> LoadFromDiskAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CodeContext>(stream, JsonOptions);
    }

    private async Task SaveToDiskAsync(string path, CodeContext context)
    {
        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, context, JsonOptions);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _memoryCache.Dispose();
            _disposed = true;
        }
    }
}
