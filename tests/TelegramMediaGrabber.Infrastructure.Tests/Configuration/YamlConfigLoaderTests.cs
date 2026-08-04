using TelegramMediaGrabber.Domain;
using TelegramMediaGrabber.Infrastructure.Configuration;

namespace TelegramMediaGrabber.Infrastructure.Tests.Configuration;

/// <summary>
/// Tests for <see cref="YamlConfigLoader"/> — parsing plus the "unknown
/// key fails loudly at load time" fail-fast requirement (AGENTS.md §7,
/// PROJECT_STATE.md §7).
/// </summary>
public sealed class YamlConfigLoaderTests
{
    private static string RepoExampleConfigPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config", "channels.example.yaml")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root containing config/channels.example.yaml from " + AppContext.BaseDirectory);
        return Path.Combine(dir!.FullName, "config", "channels.example.yaml");
    }

    [Fact]
    public void LoadFile_parses_the_repo_example_config_successfully()
    {
        var loader = new YamlConfigLoader();

        var options = loader.LoadFile(RepoExampleConfigPath());

        Assert.Equal("downloads", options.DownloadRoot);
        Assert.Equal(5, options.MaxConcurrentDownloads);
        Assert.Equal(9, options.Channels.Count);
        Assert.Equal(2, options.UploadJobs.Count);

        var audiobookChannel = Assert.Single(options.Channels, c => c.Name == "first_audiobook");
        Assert.True(audiobookChannel.AudiobookMode);
        Assert.NotNull(audiobookChannel.Metadata);
        Assert.Equal("Some Author", audiobookChannel.Metadata!.Author);
        Assert.Equal("Some Novel", audiobookChannel.Metadata.NovelTitle);
        Assert.Equal(new[] { MediaType.Audio, MediaType.Document }, audiobookChannel.MediaTypes);

        var generalDump = Assert.Single(options.Channels, c => c.Name == "general_dump");
        Assert.Equal(new[] { MediaType.Photo, MediaType.Video, MediaType.Document }, generalDump.MediaTypes);

        var recentOnly = Assert.Single(options.Channels, c => c.Name == "recent_only");
        Assert.Equal(new DateOnly(2026, 6, 1), recentOnly.MinDate);
        Assert.Equal(200, recentOnly.MaxMessages);

        var mirrored = Assert.Single(options.Channels, c => c.Name == "mirrored_channel");
        Assert.Equal("@my_backup_channel", mirrored.AutoUploadTarget);

        var episodeFiltered = Assert.Single(options.Channels, c => c.Name == "first_audiobook_ep_20_to_25_only");
        Assert.NotNull(episodeFiltered.EpisodeRange);
        Assert.Equal(20, episodeFiltered.EpisodeRange!.Start);
        Assert.Equal(25, episodeFiltered.EpisodeRange.End);

        var localOnly = Assert.Single(options.Channels, c => c.Name == "kept_local_only");
        Assert.True(localOnly.LocalOnly);
        Assert.Equal("Custom Folder Name", localOnly.MediaServerSubdir);
    }

    [Fact]
    public void Load_defaults_upload_interval_and_test_mode_when_omitted()
    {
        var options = new YamlConfigLoader().Load("channels: []");

        Assert.Equal(600, options.UploadIntervalSeconds);
        Assert.False(options.TestMode);
    }

    [Fact]
    public void Load_reads_explicit_upload_interval_and_test_mode()
    {
        const string yaml = """
            upload_interval_seconds: 60
            test_mode: true
            channels: []
            """;

        var options = new YamlConfigLoader().Load(yaml);

        Assert.Equal(60, options.UploadIntervalSeconds);
        Assert.True(options.TestMode);
    }

    [Fact]
    public void Load_rejects_unknown_top_level_key()
    {
        const string yaml = """
            download_root: downloads
            max_concurrent_downloads: 5
            typo_field: oops
            channels: []
            upload_jobs: []
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => new YamlConfigLoader().Load(yaml));
        Assert.Contains("typo_field", ex.Message);
    }

    [Fact]
    public void Load_rejects_unknown_key_inside_a_channel_block()
    {
        const string yaml = """
            channels:
              - id: "@foo"
                name: foo
                output_subdir: foo
                not_a_real_field: true
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => new YamlConfigLoader().Load(yaml));
        Assert.Contains("not_a_real_field", ex.Message);
    }

    [Fact]
    public void Load_rejects_unknown_key_inside_metadata_block()
    {
        const string yaml = """
            channels:
              - id: "@foo"
                name: foo
                output_subdir: foo
                audiobook_mode: true
                metadata:
                  author: Someone
                  novel_title: Some Book
                  narrator: Someone Else
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => new YamlConfigLoader().Load(yaml));
        Assert.Contains("narrator", ex.Message);
    }

    [Fact]
    public void Load_rejects_unknown_key_inside_an_override_entry()
    {
        const string yaml = """
            channels:
              - id: "@foo"
                name: foo
                output_subdir: foo
                overrides:
                  - match: "weird.mp3"
                    kind: chapter
                    number: 3
                    bogus_key: 1
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => new YamlConfigLoader().Load(yaml));
        Assert.Contains("bogus_key", ex.Message);
    }

    [Fact]
    public void Load_rejects_unknown_key_inside_an_upload_job()
    {
        const string yaml = """
            upload_jobs:
              - source_dir: uploads/x
                target_chat: "@x"
                recursive: true
                bogus: 1
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => new YamlConfigLoader().Load(yaml));
        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void Load_round_trips_a_full_valid_config_into_the_expected_shape()
    {
        const string yaml = """
            download_root: my_downloads
            max_concurrent_downloads: 10
            upload_jobs:
              - source_dir: uploads/photos
                target_chat: "@dest"
                recursive: true
              - source_dir: uploads/docs
                target_chat: -1001234567890
            channels:
              - id: -1009876543210
                name: my_audiobook
                media_types: [audio, document]
                output_subdir: staging
                min_date: 2024-01-15
                audiobook_mode: true
                metadata:
                  author: Some Author
                  novel_title: Some Title
                overrides:
                  - match: "weird_name.mp3"
                    kind: chapter
                    number: 12
                    subtitle: "A Weird Chapter"
                  - match: "compendium.m4a"
                    kind: volume
                    number: 2
                  - match: "dupe.mp3"
                    skip: true
            """;

        var options = new YamlConfigLoader().Load(yaml);

        Assert.Equal("my_downloads", options.DownloadRoot);
        Assert.Equal(10, options.MaxConcurrentDownloads);

        Assert.Equal(2, options.UploadJobs.Count);
        Assert.Equal(new UploadJobOptionsExpectation("uploads/photos", "@dest", true), UploadJobOptionsExpectation.From(options.UploadJobs[0]));
        Assert.Equal(new UploadJobOptionsExpectation("uploads/docs", "-1001234567890", false), UploadJobOptionsExpectation.From(options.UploadJobs[1]));

        var channel = Assert.Single(options.Channels);
        Assert.Equal("-1009876543210", channel.Id);
        Assert.Equal("my_audiobook", channel.Name);
        Assert.Equal(new[] { MediaType.Audio, MediaType.Document }, channel.MediaTypes);
        Assert.Equal("staging", channel.OutputSubdir);
        Assert.Equal(new DateOnly(2024, 1, 15), channel.MinDate);
        Assert.True(channel.AudiobookMode);
        Assert.Equal("Some Author", channel.Metadata!.Author);
        Assert.Equal("Some Title", channel.Metadata.NovelTitle);

        Assert.Equal(3, channel.Overrides.Count);
        Assert.Equal("weird_name.mp3", channel.Overrides[0].Match);
        Assert.Equal(ContentUnitKind.Chapter, channel.Overrides[0].Kind);
        Assert.Equal(12, channel.Overrides[0].Number);
        Assert.Equal("A Weird Chapter", channel.Overrides[0].Subtitle);

        Assert.Equal(ContentUnitKind.Volume, channel.Overrides[1].Kind);
        Assert.Equal(2, channel.Overrides[1].Number);
        Assert.Null(channel.Overrides[1].Subtitle);

        Assert.True(channel.Overrides[2].Skip);
    }

    [Fact]
    public void Load_defaults_download_root_and_max_concurrent_downloads_when_omitted()
    {
        var options = new YamlConfigLoader().Load("channels: []");

        Assert.Equal("downloads", options.DownloadRoot);
        Assert.Equal(5, options.MaxConcurrentDownloads);
    }

    [Fact]
    public void Load_surfaces_ChannelsOptions_Validate_failures()
    {
        const string yaml = """
            max_concurrent_downloads: 999
            channels: []
            """;

        Assert.Throws<InvalidOperationException>(() => new YamlConfigLoader().Load(yaml));
    }

    private sealed record UploadJobOptionsExpectation(string SourceDir, string TargetChat, bool Recursive)
    {
        public static UploadJobOptionsExpectation From(TelegramMediaGrabber.Application.Configuration.UploadJobOptions o) =>
            new(o.SourceDir, o.TargetChat, o.Recursive);
    }
}
