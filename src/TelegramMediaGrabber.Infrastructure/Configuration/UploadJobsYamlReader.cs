using YamlDotNet.Serialization;

namespace TelegramMediaGrabber.Infrastructure.Configuration;

/// <summary>
/// Reads just the <c>source_dir</c> values out of an <c>upload_jobs:</c>
/// YAML block — deliberately much smaller than <see cref="YamlConfigLoader"/>:
/// this is for reading a standalone snippet (e.g. one
/// <c>--mode links-to-jobs</c> generated, possibly hand-edited afterward
/// to rename entries), not the full <c>channels.yaml</c> schema, so it
/// tolerates any other keys present rather than rejecting them.
/// </summary>
public static class UploadJobsYamlReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.</exception>
    /// <exception cref="InvalidOperationException">The file isn't a mapping with an <c>upload_jobs</c> list, or an entry's <c>source_dir</c> isn't a string.</exception>
    public static IReadOnlyList<string> ReadSourceDirs(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"YAML file not found: '{path}'.", path);
        }

        var raw = Deserializer.Deserialize<object?>(File.ReadAllText(path));
        if (raw is not IDictionary<object, object?> root)
        {
            throw new InvalidOperationException($"'{path}': expected a mapping at the document root.");
        }

        if (!root.TryGetValue("upload_jobs", out var jobsRaw) || jobsRaw is null)
        {
            return [];
        }

        if (jobsRaw is not System.Collections.IEnumerable jobs || jobsRaw is string)
        {
            throw new InvalidOperationException($"'{path}': 'upload_jobs' must be a list.");
        }

        var sourceDirs = new List<string>();
        foreach (var job in jobs)
        {
            if (job is not IDictionary<object, object?> jobMap)
            {
                throw new InvalidOperationException($"'{path}': each 'upload_jobs' entry must be a mapping.");
            }

            if (!jobMap.TryGetValue("source_dir", out var sourceDirRaw) || sourceDirRaw is not string sourceDir)
            {
                throw new InvalidOperationException($"'{path}': an 'upload_jobs' entry is missing a string 'source_dir'.");
            }

            sourceDirs.Add(sourceDir);
        }

        return sourceDirs;
    }
}
