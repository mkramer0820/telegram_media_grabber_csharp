using TelegramMediaGrabber.Application.Configuration;
using TelegramMediaGrabber.Application.Parsing;
using TelegramMediaGrabber.Domain;
using YamlDotNet.Serialization;

namespace TelegramMediaGrabber.Infrastructure.Configuration;

/// <summary>
/// Parses <c>channels.yaml</c> (PROJECT_STATE.md §7, CSHARP_PORT_GUIDE.md §2/§7)
/// into a validated <see cref="ChannelsOptions"/>.
/// </summary>
/// <remarks>
/// Deliberately does NOT use YamlDotNet's attribute-based POCO binding,
/// because that binds leniently by default (unknown keys are silently
/// ignored). Instead this walks YamlDotNet's untyped object graph
/// (nested <see cref="Dictionary{TKey,TValue}"/>/<see cref="List{T}"/>)
/// by hand, so every object level can be checked against its exact set
/// of allowed keys and fail loudly on a typo — the C# equivalent of the
/// Python predecessor's pydantic <c>extra="forbid"</c> (AGENTS.md §7).
/// </remarks>
public sealed class YamlConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    private static readonly string[] TopLevelKeys =
    {
        "download_root", "max_concurrent_downloads", "channels", "upload_jobs", "upload_interval_seconds",
        "test_mode",
    };

    private static readonly string[] ChannelKeys =
    {
        "id", "name", "media_types", "output_subdir", "min_date", "max_messages", "auto_upload_target",
        "episode_range", "audiobook_mode", "metadata", "overrides", "local_only", "media_server_subdir",
    };

    private static readonly string[] MetadataKeys = { "author", "novel_title" };

    private static readonly string[] EpisodeRangeKeys = { "start", "end" };

    private static readonly string[] OverrideKeys = { "match", "skip", "kind", "number", "subtitle" };

    private static readonly string[] UploadJobKeys = { "source_dir", "target_chat", "recursive" };

    /// <summary>Reads and parses a channels.yaml file from disk.</summary>
    /// <exception cref="InvalidOperationException">Unknown key, malformed value, or a failed <see cref="ChannelsOptions.Validate"/> check.</exception>
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.</exception>
    public ChannelsOptions LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file not found: '{path}'.", path);
        }

        return Load(File.ReadAllText(path));
    }

    /// <summary>Parses already-read channels.yaml content.</summary>
    /// <exception cref="InvalidOperationException">Unknown key, malformed value, or a failed <see cref="ChannelsOptions.Validate"/> check.</exception>
    public ChannelsOptions Load(string yaml)
    {
        var raw = Deserializer.Deserialize<object?>(yaml);
        var root = AsMap(raw, "document root") ?? new Dictionary<string, object?>();

        RejectUnknownKeys(root, "top-level config", TopLevelKeys);

        var downloadRoot = GetString(root, "download_root") ?? "downloads";
        var maxConcurrent = GetInt(root, "max_concurrent_downloads") ?? 5;
        var uploadIntervalSeconds = GetInt(root, "upload_interval_seconds") ?? 600;
        var testMode = GetBool(root, "test_mode") ?? false;

        var channels = GetList(root, "channels").Select(ParseChannel).ToList();
        var uploadJobs = GetList(root, "upload_jobs").Select(ParseUploadJob).ToList();

        var options = new ChannelsOptions(downloadRoot, maxConcurrent, channels, uploadJobs, uploadIntervalSeconds, testMode);
        options.Validate();
        return options;
    }

    /// <summary>Parses one entry of the <c>channels</c> list.</summary>
    private static ChannelOptions ParseChannel(object? node)
    {
        var map = AsMap(node, "channel entry") ?? throw new InvalidOperationException("Each 'channels' entry must be a mapping.");
        RejectUnknownKeys(map, "channel entry", ChannelKeys);

        var id = GetScalarAsString(map, "id") ?? throw new InvalidOperationException("Channel entry missing required 'id'.");
        var name = GetString(map, "name") ?? throw new InvalidOperationException($"Channel '{id}' missing required 'name'.");
        var outputSubdir = GetString(map, "output_subdir")
            ?? throw new InvalidOperationException($"Channel '{name}' missing required 'output_subdir'.");

        var mediaTypes = ParseMediaTypes(map, name);
        var minDate = GetDateOnly(map, "min_date", name);
        var maxMessages = GetInt(map, "max_messages");
        var autoUploadTarget = GetScalarAsString(map, "auto_upload_target");
        var episodeRange = ParseEpisodeRange(map, name);
        var audiobookMode = GetBool(map, "audiobook_mode") ?? false;
        var metadata = ParseMetadata(map, name);
        var overrides = GetList(map, "overrides").Select(o => ParseOverride(o, name)).ToList();
        var localOnly = GetBool(map, "local_only") ?? false;
        var mediaServerSubdir = GetString(map, "media_server_subdir");

        return new ChannelOptions(
            id, name, mediaTypes, outputSubdir, minDate, audiobookMode, metadata, overrides,
            maxMessages, autoUploadTarget, episodeRange, localOnly, mediaServerSubdir);
    }

    private static EpisodeRangeOptions? ParseEpisodeRange(IDictionary<string, object?> channelMap, string channelName)
    {
        if (!channelMap.TryGetValue("episode_range", out var raw) || raw is null)
        {
            return null;
        }

        var map = AsMap(raw, $"channel '{channelName}' episode_range") ?? throw new InvalidOperationException(
            $"Channel '{channelName}' 'episode_range' must be a mapping.");
        RejectUnknownKeys(map, $"channel '{channelName}' episode_range", EpisodeRangeKeys);

        var start = GetInt(map, "start") ?? throw new InvalidOperationException(
            $"Channel '{channelName}' 'episode_range' missing required 'start'.");
        var end = GetInt(map, "end") ?? throw new InvalidOperationException(
            $"Channel '{channelName}' 'episode_range' missing required 'end'.");

        return new EpisodeRangeOptions(start, end);
    }

    private static IReadOnlyList<MediaType> ParseMediaTypes(IDictionary<string, object?> map, string channelName)
    {
        if (!map.TryGetValue("media_types", out var raw) || raw is null)
        {
            return new[] { MediaType.Photo, MediaType.Video, MediaType.Document };
        }

        var list = AsList(raw, $"channel '{channelName}' media_types");
        if (list.Count == 0)
        {
            return new[] { MediaType.Photo, MediaType.Video, MediaType.Document };
        }

        return list
            .Select(item => ParseMediaType(item?.ToString() ?? string.Empty, channelName))
            .ToList();
    }

    private static MediaType ParseMediaType(string value, string channelName) => value.Trim().ToLowerInvariant() switch
    {
        "photo" => MediaType.Photo,
        "video" => MediaType.Video,
        "document" => MediaType.Document,
        "audio" => MediaType.Audio,
        _ => throw new InvalidOperationException(
            $"Channel '{channelName}' has unrecognized media_types entry '{value}'. Expected one of: photo, video, document, audio."),
    };

    private static AudiobookMetadata? ParseMetadata(IDictionary<string, object?> channelMap, string channelName)
    {
        if (!channelMap.TryGetValue("metadata", out var raw) || raw is null)
        {
            return null;
        }

        var map = AsMap(raw, $"channel '{channelName}' metadata") ?? throw new InvalidOperationException(
            $"Channel '{channelName}' 'metadata' must be a mapping.");
        RejectUnknownKeys(map, $"channel '{channelName}' metadata", MetadataKeys);

        var author = GetString(map, "author") ?? throw new InvalidOperationException($"Channel '{channelName}' metadata missing 'author'.");
        var novelTitle = GetString(map, "novel_title") ?? throw new InvalidOperationException($"Channel '{channelName}' metadata missing 'novel_title'.");

        return new AudiobookMetadata(author, novelTitle);
    }

    private static OverrideEntry ParseOverride(object? node, string channelName)
    {
        var map = AsMap(node, $"channel '{channelName}' override entry")
            ?? throw new InvalidOperationException($"Channel '{channelName}' has an override entry that isn't a mapping.");
        RejectUnknownKeys(map, $"channel '{channelName}' override entry", OverrideKeys);

        var match = GetString(map, "match") ?? throw new InvalidOperationException($"Channel '{channelName}' has an override missing 'match'.");
        var skip = GetBool(map, "skip") ?? false;
        var kind = ParseContentUnitKind(map, "kind", channelName, match);
        var number = GetInt(map, "number");
        var subtitle = GetString(map, "subtitle");

        return new OverrideEntry(match, skip, kind, number, subtitle);
    }

    private static ContentUnitKind? ParseContentUnitKind(IDictionary<string, object?> map, string key, string channelName, string match)
    {
        var raw = GetString(map, key);
        if (raw is null)
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "chapter" => ContentUnitKind.Chapter,
            "volume" => ContentUnitKind.Volume,
            _ => throw new InvalidOperationException(
                $"Channel '{channelName}' override for '{match}' has unrecognized kind '{raw}'. Expected 'chapter' or 'volume'."),
        };
    }

    private static UploadJobOptions ParseUploadJob(object? node)
    {
        var map = AsMap(node, "upload_jobs entry") ?? throw new InvalidOperationException("Each 'upload_jobs' entry must be a mapping.");
        RejectUnknownKeys(map, "upload_jobs entry", UploadJobKeys);

        var sourceDir = GetString(map, "source_dir") ?? throw new InvalidOperationException("Upload job missing required 'source_dir'.");
        var targetChat = GetScalarAsString(map, "target_chat")
            ?? throw new InvalidOperationException($"Upload job '{sourceDir}' missing required 'target_chat'.");
        var recursive = GetBool(map, "recursive") ?? false;

        return new UploadJobOptions(sourceDir, targetChat, recursive);
    }

    // ---- Untyped-YAML-graph helpers -------------------------------------------------

    /// <summary>Casts a YamlDotNet-deserialized node to a string-keyed map, or null if it isn't a mapping.</summary>
    private static IDictionary<string, object?>? AsMap(object? node, string context)
    {
        switch (node)
        {
            case null:
                return null;
            case IDictionary<object, object?> raw:
                var result = new Dictionary<string, object?>();
                foreach (var (key, value) in raw)
                {
                    if (key is not string stringKey)
                    {
                        throw new InvalidOperationException($"{context}: non-string key '{key}' is not supported.");
                    }

                    result[stringKey] = value;
                }

                return result;
            default:
                throw new InvalidOperationException($"{context}: expected a mapping, got '{node.GetType().Name}'.");
        }
    }

    /// <summary>Casts a YamlDotNet-deserialized node to a list, treating a missing/null node as empty.</summary>
    private static List<object?> AsList(object? node, string context)
    {
        return node switch
        {
            null => new List<object?>(),
            List<object?> list => list,
            System.Collections.IEnumerable enumerable and not string => enumerable.Cast<object?>().ToList(),
            _ => throw new InvalidOperationException($"{context}: expected a list, got '{node.GetType().Name}'."),
        };
    }

    private static List<object?> GetList(IDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var raw) ? AsList(raw, $"'{key}'") : new List<object?>();

    private static string? GetString(IDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw.ToString();
    }

    /// <summary>Like <see cref="GetString"/>, but accepts the Python schema's `id: int|str` shape.</summary>
    private static string? GetScalarAsString(IDictionary<string, object?> map, string key) => GetString(map, key);

    private static int? GetInt(IDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            int i => i,
            long l => checked((int)l),
            string s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"'{key}': expected an integer, got '{raw.GetType().Name}'."),
        };
    }

    private static bool? GetBool(IDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            bool b => b,
            string s => bool.Parse(s),
            _ => throw new InvalidOperationException($"'{key}': expected a boolean, got '{raw.GetType().Name}'."),
        };
    }

    private static DateOnly? GetDateOnly(IDictionary<string, object?> map, string key, string channelName)
    {
        if (!map.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            DateTime dt => DateOnly.FromDateTime(dt),
            string s => DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Channel '{channelName}' '{key}': expected an ISO-8601 date, got '{raw.GetType().Name}'."),
        };
    }

    /// <summary>
    /// Named key scopes elsewhere in the schema, checked when a key is
    /// rejected so the error can say "that's a real key, just not here" —
    /// e.g. a channel-level key like <c>max_messages</c> mistakenly nested
    /// one level too deep inside that channel's <c>metadata</c>.
    /// </summary>
    private static readonly (string Scope, string[] Keys)[] KnownScopes =
    {
        ("top-level config", TopLevelKeys),
        ("a channel entry", ChannelKeys),
        ("channel metadata", MetadataKeys),
        ("an episode_range", EpisodeRangeKeys),
        ("an override entry", OverrideKeys),
        ("an upload_jobs entry", UploadJobKeys),
    };

    private static void RejectUnknownKeys(IDictionary<string, object?> map, string context, IReadOnlyCollection<string> allowedKeys)
    {
        var unknown = map.Keys.Where(k => !allowedKeys.Contains(k)).ToList();
        if (unknown.Count == 0)
        {
            return;
        }

        var details = unknown.Select(k =>
        {
            var elsewhere = KnownScopes.FirstOrDefault(s => s.Keys.Contains(k) && !ReferenceEquals(s.Keys, allowedKeys));
            return elsewhere.Scope is null
                ? $"'{k}'"
                : $"'{k}' (this is a valid key, but belongs in {elsewhere.Scope} — check its indentation/nesting)";
        });

        throw new InvalidOperationException(
            $"Unknown key(s) in {context}: {string.Join(", ", details)}. " +
            $"Allowed keys here: {string.Join(", ", allowedKeys)}.");
    }
}
