using System.Text;
using System.Text.Json;

namespace FileOp.Core.Storage;

public interface IStorageOptimizationThresholdPreferenceStore
{
    ValueTask<StorageOptimizationThresholdPreference?> LoadAsync(
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        StorageOptimizationThresholdPreference preference,
        CancellationToken cancellationToken = default);
}

public sealed class JsonFileStorageOptimizationThresholdPreferenceStore :
    IStorageOptimizationThresholdPreferenceStore
{
    private const int SchemaVersion = 1;
    private const long MaximumPreferenceBytes = 4_096;

    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonFileStorageOptimizationThresholdPreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async ValueTask<StorageOptimizationThresholdPreference?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var info = new FileInfo(_path);
            if (info.Length <= 0 || info.Length > MaximumPreferenceBytes)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            if (bytes.LongLength <= 0 || bytes.LongLength > MaximumPreferenceBytes)
            {
                return null;
            }

            return Parse(bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask SaveAsync(
        StorageOptimizationThresholdPreference preference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preference);
        if (!StorageOptimizationThresholdPreferencePolicy.IsSupported(preference))
        {
            throw new ArgumentException(
                "Only supported policy-relative Optimize threshold multipliers can be persisted.",
                nameof(preference));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new PreferenceDocument(
                SchemaVersion,
                preference.LargeFileMultiplier,
                preference.SameSizeMultiplier,
                preference.StaleAgeMultiplier);
            var json = JsonSerializer.Serialize(document) + "\n";
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.LongLength > MaximumPreferenceBytes)
            {
                throw new InvalidOperationException("Serialized Optimize threshold preference exceeded its hard size bound.");
            }

            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static StorageOptimizationThresholdPreference? Parse(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int? schemaVersion = null;
        int? largeFileMultiplier = null;
        int? sameSizeMultiplier = null;
        int? staleAgeMultiplier = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name) || property.Value.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            if (!property.Value.TryGetInt32(out var value))
            {
                return null;
            }

            switch (property.Name)
            {
                case "SchemaVersion":
                    schemaVersion = value;
                    break;
                case "LargeFileMultiplier":
                    largeFileMultiplier = value;
                    break;
                case "SameSizeMultiplier":
                    sameSizeMultiplier = value;
                    break;
                case "StaleAgeMultiplier":
                    staleAgeMultiplier = value;
                    break;
                default:
                    return null;
            }
        }

        if (names.Count != 4 ||
            schemaVersion != SchemaVersion ||
            !largeFileMultiplier.HasValue ||
            !sameSizeMultiplier.HasValue ||
            !staleAgeMultiplier.HasValue)
        {
            return null;
        }

        var preference = new StorageOptimizationThresholdPreference(
            largeFileMultiplier.Value,
            sameSizeMultiplier.Value,
            staleAgeMultiplier.Value);
        return StorageOptimizationThresholdPreferencePolicy.IsSupported(preference)
            ? preference
            : null;
    }

    private sealed record PreferenceDocument(
        int SchemaVersion,
        int LargeFileMultiplier,
        int SameSizeMultiplier,
        int StaleAgeMultiplier);
}
