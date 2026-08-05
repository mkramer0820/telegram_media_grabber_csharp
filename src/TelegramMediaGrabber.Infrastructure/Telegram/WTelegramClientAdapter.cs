using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using TelegramMediaGrabber.Application.Telegram;
using TL;
using WTelegram;

namespace TelegramMediaGrabber.Infrastructure.Telegram;

/// <summary>
/// <see cref="ITelegramClient"/> implemented against WTelegramClient
/// (CSHARP_PORT_GUIDE.md §9 point 3 — the highest-risk piece of the port).
/// </summary>
/// <remarks>
/// <para>
/// <b>Verification status</b>: this class compiles against the real
/// WTelegramClient 4.4.7 public API surface (verified by reflection
/// against the installed package, not guessed from memory/Telethon
/// familiarity) but has NOT been exercised against a live Telegram
/// connection — no API credentials are available in this environment.
/// The FloodWait retry shape is extracted into <see cref="FloodWaitRetry"/>
/// and is independently unit tested; everything else here is
/// structurally-implemented-but-unverified and should be treated as the
/// first place to look if real-world behavior diverges.
/// </para>
/// <para>
/// <b>Known design gap</b>: <see cref="TelegramMessage"/> is a thin
/// projection (Application must not depend on WTelegramClient's <c>TL</c>
/// types), but downloading requires the original <c>TL.Document</c>/
/// <c>TL.Photo</c> object (access hash, file reference). This adapter
/// bridges the gap with an internal cache of raw messages populated by
/// <see cref="IterMessagesAsync"/>/<see cref="GetMessagesAsync"/>, keyed
/// by (chat, message) id — <see cref="DownloadMediaAsync"/> only works for
/// messages obtained that way, on this same adapter instance. This is an
/// Infrastructure-internal implementation detail, not part of the
/// <see cref="ITelegramClient"/> contract.
/// </para>
/// </remarks>
public sealed class WTelegramClientAdapter : ITelegramClient
{
    private readonly Client _client;
    private readonly ConcurrentDictionary<long, InputPeer> _peers = new();
    private readonly ConcurrentDictionary<long, InputChannelBase> _channels = new();
    private readonly ConcurrentDictionary<(long ChatId, int MessageId), MessageBase> _rawMessages = new();

    /// <summary>
    /// Builds a client configured from the environment variables documented
    /// in PROJECT_STATE.md §7: <c>TG_API_ID</c>, <c>TG_API_HASH</c>,
    /// <c>TG_PHONE</c>, <c>TG_SESSION_NAME</c>.
    /// </summary>
    public WTelegramClientAdapter()
        : this(BuildEnvironmentConfigProvider())
    {
    }

    /// <summary>Builds a client using an explicit WTelegramClient config-callback (see the library's README "Non-interactive configuration" section).</summary>
    public WTelegramClientAdapter(Func<string, string?> configProvider)
    {
        ArgumentNullException.ThrowIfNull(configProvider);

        SuppressRawProtocolLoggingUnlessOptedIn();

        // WTelegramClient's own delegate type is Func<string,string> (not
        // nullable-annotated by that library), but null is its documented
        // "use the default" signal for most config keys — this wrapper is
        // not lying to the compiler, just bridging an unannotated external
        // API.
        _client = new Client(what => configProvider(what)!);
    }

    /// <summary>
    /// WTelegramClient's own <c>Helpers.Log</c> defaults to writing every
    /// raw MTProto frame ("Sending Contacts_ResolveUsername", "Receiving
    /// RpcResult", ...) straight to <see cref="Console"/> -- unrelated to,
    /// and never configured through, this app's own Serilog/file logging.
    /// Left alone it fights the Spectre.Console dashboard for the same
    /// terminal exactly the way AGENTS.md §4.2 warns a console logging
    /// provider would, just via a third-party path instead of our own
    /// <c>ILogger</c>. Silenced by default; set <c>TG_VERBOSE=1</c> to
    /// restore it for low-level protocol debugging.
    /// </summary>
    private static void SuppressRawProtocolLoggingUnlessOptedIn()
    {
        if (Environment.GetEnvironmentVariable("TG_VERBOSE") == "1")
        {
            return;
        }

        WTelegram.Helpers.Log = static (level, message) => { };
    }

    private static Func<string, string?> BuildEnvironmentConfigProvider() => what => what switch
    {
        "api_id" => Environment.GetEnvironmentVariable("TG_API_ID"),
        "api_hash" => Environment.GetEnvironmentVariable("TG_API_HASH"),
        "phone_number" => Environment.GetEnvironmentVariable("TG_PHONE"),
        "session_pathname" => SessionPathname(),
        _ => null,
    };

    /// <summary>
    /// Resolved to an absolute path deliberately — a bare relative path
    /// like "data/downloader.session" leaves it up to WTelegramClient's
    /// own internal file I/O to decide what "relative" resolves against,
    /// which is not guaranteed to be <see cref="Environment.CurrentDirectory"/>
    /// (Program.cs's working-directory anchor only controls that one).
    /// Observed live: the session file existed at a consistent path across
    /// runs, but the account was still asked to re-verify on every run —
    /// consistent with WTelegramClient resolving the relative pathname to
    /// somewhere other than where it was actually written each time.
    /// </summary>
    private static string? SessionPathname()
    {
        var name = Environment.GetEnvironmentVariable("TG_SESSION_NAME");
        return string.IsNullOrEmpty(name) ? null : Path.GetFullPath($"{name}.session");
    }

    /// <inheritdoc/>
    public async Task ConnectAndAuthenticateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // LoginUserIfNeeded has no CancellationToken overload upstream; the
        // check above is the best this adapter can offer before handing
        // control to the library's own (potentially interactive) login flow.
        await _client.LoginUserIfNeeded().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<TelegramEntity> ResolveEntityAsync(string chatIdOrUsername, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatIdOrUsername);
        cancellationToken.ThrowIfCancellationRequested();

        var inviteHash = ExtractInviteHash(chatIdOrUsername);
        if (inviteHash is not null)
        {
            return await ResolveInviteLinkAsync(inviteHash, chatIdOrUsername, cancellationToken).ConfigureAwait(false);
        }

        var username = ExtractUsername(chatIdOrUsername);
        if (username is not null)
        {
            return await ResolveUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        }

        if (long.TryParse(chatIdOrUsername, out var rawId))
        {
            return await ResolveByIdAsync(rawId, cancellationToken).ConfigureAwait(false);
        }

        // Not an invite link, not "@name"/a t.me URL, not numeric -- by
        // elimination this can only be a bare username with no "@" prefix
        // (e.g. "some_audiobook_channel"), since Telegram usernames can never be
        // purely numeric. Telethon accepts this shape directly; matching
        // that leniency here rather than forcing every config value to
        // carry an explicit "@".
        return await ResolveUsernameAsync(chatIdOrUsername, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves a bare username (no "@") via <c>Contacts_ResolveUsername</c>.</summary>
    private async Task<TelegramEntity> ResolveUsernameAsync(string username, CancellationToken cancellationToken)
    {
        var resolved = await FloodWaitRetry.ExecuteAsync(
            () => _client.Contacts_ResolveUsername(username),
            SelectFloodWaitSeconds,
            DelayAsync,
            cancellationToken).ConfigureAwait(false);

        return CacheResolvedPeer(resolved.peer, resolved.chats, resolved.users);
    }

    /// <summary>Extracts the hash from a private invite link ("https://t.me/+&lt;hash&gt;" or the older "t.me/joinchat/&lt;hash&gt;" form); null if not that shape.</summary>
    private static string? ExtractInviteHash(string input)
    {
        foreach (var prefix in new[] { "https://t.me/", "http://t.me/", "t.me/" })
        {
            if (!input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = input[prefix.Length..];
            if (rest.StartsWith('+'))
            {
                return rest[1..];
            }

            if (rest.StartsWith("joinchat/", StringComparison.OrdinalIgnoreCase))
            {
                return rest["joinchat/".Length..];
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// Resolves a private invite link via <c>Messages_CheckChatInvite</c> —
    /// mirrors the Python predecessor's <c>resolve_entity</c>: returns the
    /// underlying chat only if this account has already joined it
    /// (<c>ChatInviteAlready</c>/<c>ChatInvitePeek</c>), and deliberately
    /// never joins on the caller's behalf. Joining is an account action
    /// visible to other members, so it must be a decision made explicitly
    /// in the Telegram app, not something a config entry silently triggers.
    /// </summary>
    /// <remarks>
    /// A specific invite link's check can report "not already joined" even
    /// when the account genuinely is a member — a channel can have several
    /// active invite links, and Telegram's per-link check doesn't always
    /// reflect actual membership for a link other than the one originally
    /// used to join. When that happens, this falls back to a best-effort
    /// title match against the account's own joined-chat list
    /// (<c>Messages_GetAllChats</c>) before giving up — still never joins,
    /// just checks membership a second way.
    /// </remarks>
    private async Task<TelegramEntity> ResolveInviteLinkAsync(string hash, string originalInput, CancellationToken cancellationToken)
    {
        var invite = await FloodWaitRetry.ExecuteAsync(
            () => _client.Messages_CheckChatInvite(hash),
            SelectFloodWaitSeconds,
            DelayAsync,
            cancellationToken).ConfigureAwait(false);

        switch (invite)
        {
            case ChatInviteAlready already:
                return CacheChat(already.chat);
            case ChatInvitePeek peek:
                return CacheChat(peek.chat);
            case ChatInvite plain:
                var matchByTitle = await TryResolveByTitleAsync(plain.title, cancellationToken).ConfigureAwait(false);
                if (matchByTitle is not null)
                {
                    return matchByTitle;
                }

                throw new InvalidOperationException(
                    $"Invite link '{originalInput}' is valid but this account has not joined that channel yet " +
                    $"(checked both the invite link itself and the account's joined-chat list for '{plain.title}'). " +
                    "Join it from the Telegram app first — this app never auto-joins channels on your behalf.");
            default:
                throw new InvalidOperationException(
                    $"Invite link '{originalInput}' is valid but this account has not joined that channel yet. " +
                    "Join it from the Telegram app first — this app never auto-joins channels on your behalf.");
        }
    }

    /// <inheritdoc/>
    public async Task<TelegramEntity?> TryResolveByTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        var allChats = await FloodWaitRetry.ExecuteAsync(
            () => _client.Messages_GetAllChats(),
            SelectFloodWaitSeconds,
            DelayAsync,
            cancellationToken).ConfigureAwait(false);

        var match = allChats.chats.Values.FirstOrDefault(
            chat => string.Equals(chat.Title, title, StringComparison.Ordinal));
        return match is null ? null : CacheChat(match);
    }

    /// <summary>Extracts a bare username from an "@username" or "https://t.me/username" form; null if not that shape (including the invite-link shape, handled separately by <see cref="ExtractInviteHash"/>).</summary>
    private static string? ExtractUsername(string input)
    {
        if (input.StartsWith('@'))
        {
            return input[1..];
        }

        foreach (var prefix in new[] { "https://t.me/", "http://t.me/", "t.me/" })
        {
            if (!input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = input[prefix.Length..];
            if (rest.StartsWith('+') || rest.StartsWith("joinchat/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return rest;
        }

        return null;
    }

    private async Task<TelegramEntity> ResolveByIdAsync(long rawId, CancellationToken cancellationToken)
    {
        // Bot-API-style channel IDs are offset by -1_000_000_000_000 from
        // the "raw" MTProto channel id used internally.
        var normalizedChannelId = rawId <= -1_000_000_000_000 ? -rawId - 1_000_000_000_000 : (long?)null;

        var allChats = await FloodWaitRetry.ExecuteAsync(
            () => _client.Messages_GetAllChats(),
            SelectFloodWaitSeconds,
            DelayAsync,
            cancellationToken).ConfigureAwait(false);

        foreach (var (id, chat) in allChats.chats)
        {
            if (id == normalizedChannelId || id == -rawId || id == rawId)
            {
                return CacheChat(chat);
            }
        }

        throw new InvalidOperationException(
            $"Chat ID {rawId} was not found among this account's known chats. WTelegramClient can only " +
            "resolve chats/channels the account has already joined — this adapter never auto-joins.");
    }

    private TelegramEntity CacheResolvedPeer(Peer peer, IReadOnlyDictionary<long, ChatBase> chats, IReadOnlyDictionary<long, User> users)
    {
        switch (peer)
        {
            case PeerChannel pc when chats.TryGetValue(pc.channel_id, out var chat):
                return CacheChat(chat);
            case PeerChat pch when chats.TryGetValue(pch.chat_id, out var chat):
                return CacheChat(chat);
            case PeerUser pu when users.TryGetValue(pu.user_id, out var user):
                return CacheUser(user);
            default:
                throw new InvalidOperationException($"Resolved peer '{peer}' has no matching chat/user metadata in the response.");
        }
    }

    private TelegramEntity CacheChat(ChatBase chat)
    {
        _peers[chat.ID] = chat.ToInputPeer();
        if (chat is Channel channel)
        {
            _channels[chat.ID] = channel;
            var kind = channel.flags.HasFlag(Channel.Flags.megagroup) ? "group" : "channel";
            return new TelegramEntity(chat.ID, chat.Title, channel.username, kind);
        }

        return new TelegramEntity(chat.ID, chat.Title, Username: null, Kind: "group");
    }

    private TelegramEntity CacheUser(User user)
    {
        _peers[user.ID] = user.ToInputPeer();
        var displayName = user.MainUsername ?? user.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new TelegramEntity(user.ID, displayName, user.MainUsername, Kind: "user");
    }

    private InputPeer GetCachedPeer(TelegramEntity entity) =>
        _peers.TryGetValue(entity.Id, out var peer)
            ? peer
            : throw new InvalidOperationException(
                $"Entity {entity.Id} ('{entity.DisplayName}') was not resolved via ResolveEntityAsync on this client instance.");

    /// <inheritdoc/>
    public async IAsyncEnumerable<TelegramMessage> IterMessagesAsync(
        TelegramEntity entity, int minId = 0, int? limit = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var peer = GetCachedPeer(entity);
        var offsetId = 0;
        var yielded = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var history = await FloodWaitRetry.ExecuteAsync(
                () => _client.Messages_GetHistory(peer, offset_id: offsetId, limit: 100),
                SelectFloodWaitSeconds,
                DelayAsync,
                cancellationToken).ConfigureAwait(false);

            if (history.Messages.Length == 0)
            {
                yield break;
            }

            foreach (var raw in history.Messages)
            {
                if (raw.ID <= minId)
                {
                    yield break;
                }

                _rawMessages[(entity.Id, raw.ID)] = raw;
                yield return ToTelegramMessage(entity.Id, raw);
                yielded++;
                if (limit is { } max && yielded >= max)
                {
                    yield break;
                }
            }

            offsetId = history.Messages[^1].ID;
            if (history.Messages.Length < 100)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TelegramMessage?>> GetMessagesAsync(
        TelegramEntity entity, IReadOnlyList<int> messageIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        GetCachedPeer(entity); // validates the entity was resolved; the ID array below is peer-agnostic on the wire.

        var inputs = messageIds.Select(id => (InputMessage)new InputMessageID { id = id }).ToArray();

        var result = await FloodWaitRetry.ExecuteAsync(
            () => _channels.TryGetValue(entity.Id, out var channel)
                ? _client.Channels_GetMessages(channel, inputs)
                : _client.Messages_GetMessages(inputs),
            SelectFloodWaitSeconds,
            DelayAsync,
            cancellationToken).ConfigureAwait(false);

        var byId = new Dictionary<int, MessageBase>();
        foreach (var raw in result.Messages)
        {
            if (raw is not MessageEmpty)
            {
                byId[raw.ID] = raw;
                _rawMessages[(entity.Id, raw.ID)] = raw;
            }
        }

        return messageIds
            .Select(id => byId.TryGetValue(id, out var raw) ? ToTelegramMessage(entity.Id, raw) : null)
            .ToList();
    }

    /// <inheritdoc/>
    public async Task DownloadMediaAsync(
        TelegramEntity entity,
        TelegramMessage message,
        string destinationPath,
        IProgress<(long BytesDone, long BytesTotal)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destinationPath);

        if (!_rawMessages.TryGetValue((entity.Id, message.Id), out var raw) || raw is not Message { media: MessageMedia media })
        {
            throw new InvalidOperationException(
                $"No downloadable media cached for message {message.Id} in chat {entity.Id}. Messages must be " +
                "obtained via IterMessagesAsync/GetMessagesAsync on this same adapter instance before downloading.");
        }

        void OnProgress(long transmitted, long total) => progress?.Report((transmitted, total));

        await FloodWaitRetry.ExecuteAsync(
            async () =>
            {
                await using var stream = File.Create(destinationPath);
                switch (media)
                {
                    case MessageMediaDocument { document: Document document }:
                        await _client.DownloadFileAsync(document, stream, thumbSize: null, progress: OnProgress).ConfigureAwait(false);
                        break;
                    case MessageMediaPhoto { photo: Photo photo }:
                        await _client.DownloadFileAsync(photo, stream, photoSize: null, progress: OnProgress).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException($"Message {message.Id} has no supported downloadable media (document/photo).");
                }

                return true;
            },
            SelectFloodWaitSeconds,
            DelayAsync,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TelegramMessage> WatchNewMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var queue = System.Threading.Channels.Channel.CreateUnbounded<TelegramMessage>();

        Task OnUpdates(UpdatesBase updates)
        {
            foreach (var update in FlattenUpdates(updates))
            {
                var raw = update switch
                {
                    UpdateNewChannelMessage u => u.message,
                    UpdateNewMessage u => u.message,
                    _ => null,
                };

                if (raw is not Message message || ExtractChatId(message.peer_id) is not { } chatId)
                {
                    continue;
                }

                _rawMessages[(chatId, message.ID)] = message;
                queue.Writer.TryWrite(ToTelegramMessage(chatId, message));
            }

            return Task.CompletedTask;
        }

        _client.OnUpdates += OnUpdates;
        try
        {
            await foreach (var message in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            _client.OnUpdates -= OnUpdates;
        }
    }

    /// <summary>Unwraps the individual <see cref="TL.Update"/> entries carried by a raw <see cref="UpdatesBase"/> push.</summary>
    private static IEnumerable<Update> FlattenUpdates(UpdatesBase updates) => updates switch
    {
        Updates u => u.updates,
        UpdatesCombined u => u.updates,
        UpdateShort u => [u.update],
        _ => [],
    };

    /// <summary>Extracts the chat ID a message's <see cref="Peer"/> refers to, matching the ID convention <see cref="CacheChat"/> stores entities under.</summary>
    private static long? ExtractChatId(Peer? peer) => peer switch
    {
        PeerChannel p => p.channel_id,
        PeerChat p => p.chat_id,
        PeerUser p => p.user_id,
        _ => null,
    };

    /// <inheritdoc/>
    public async Task<TelegramMessage> UploadDocumentAsync(
        TelegramEntity entity,
        string filePath,
        string caption = "",
        IProgress<(long BytesDone, long BytesTotal)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var peer = GetCachedPeer(entity);
        void OnProgress(long transmitted, long total) => progress?.Report((transmitted, total));

        var message = await FloodWaitRetry.ExecuteAsync(
            async () =>
            {
                var uploaded = await _client.UploadFileAsync(filePath, OnProgress).ConfigureAwait(false);
                return await _client.SendMediaAsync(peer, caption, uploaded).ConfigureAwait(false);
            },
            SelectFloodWaitSeconds,
            DelayAsync,
            cancellationToken).ConfigureAwait(false);

        _rawMessages[(entity.Id, message.ID)] = message;
        return ToTelegramMessage(entity.Id, message);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TelegramMessage>> UploadMediaGroupAsync(
        TelegramEntity entity,
        IReadOnlyList<string> filePaths,
        string caption = "",
        IProgress<(long BytesDone, long BytesTotal)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count is 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(filePaths), filePaths.Count, "A media group must contain between 1 and 10 files.");
        }

        var peer = GetCachedPeer(entity);
        void OnProgress(long transmitted, long total) => progress?.Report((transmitted, total));

        var messages = await FloodWaitRetry.ExecuteAsync(
            async () =>
            {
                var medias = new List<InputMedia>(filePaths.Count);
                foreach (var path in filePaths)
                {
                    var uploaded = await _client.UploadFileAsync(path, OnProgress).ConfigureAwait(false);
                    medias.Add(new InputMediaUploadedDocument
                    {
                        file = uploaded,
                        mime_type = GuessMimeType(path),
                        attributes = new DocumentAttribute[] { new DocumentAttributeFilename { file_name = Path.GetFileName(path) } },
                    });
                }

                return await _client.SendAlbumAsync(peer, medias, caption).ConfigureAwait(false);
            },
            SelectFloodWaitSeconds,
            DelayAsync,
            cancellationToken).ConfigureAwait(false);

        foreach (var message in messages)
        {
            _rawMessages[(entity.Id, message.ID)] = message;
        }

        return messages.Select(m => ToTelegramMessage(entity.Id, m)).ToList();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private static TelegramMessage ToTelegramMessage(long chatId, MessageBase raw)
    {
        string? fileName = null;
        var hasAudio = false;
        var hasVideo = false;
        var hasPhoto = false;
        var hasDocument = false;

        if (raw is Message { media: MessageMedia media })
        {
            switch (media)
            {
                case MessageMediaDocument { document: Document document }:
                    hasDocument = true;
                    fileName = document.Filename;
                    hasAudio = document.attributes.Any(a => a is DocumentAttributeAudio);
                    hasVideo = document.attributes.Any(a => a is DocumentAttributeVideo);
                    break;
                case MessageMediaPhoto { photo: Photo }:
                    hasPhoto = true;
                    break;
            }
        }

        var date = new DateTimeOffset(DateTime.SpecifyKind(raw.Date, DateTimeKind.Utc));
        return new TelegramMessage(raw.ID, chatId, date, fileName, hasAudio, hasVideo, hasPhoto, hasDocument);
    }

    /// <summary>Minimal extension-based MIME guess for upload — Telegram mostly cares about "photo"/"video"/generic document handling, not exact MIME accuracy.</summary>
    private static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".m4a" or ".m4b" => "audio/mp4",
        ".mp4" => "video/mp4",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    private static int? SelectFloodWaitSeconds(Exception ex) =>
        ex is RpcException { Code: 420 } rpc && rpc.X >= 0 ? rpc.X : null;

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
