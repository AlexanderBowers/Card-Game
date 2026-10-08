using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using CriticalCount.Server;
using Microsoft.AspNetCore.RateLimiting;

// ----------------------------------------------------------------------------------------------
// Critical Count online server.
//
// PRIVACY, in the shape of the code: nothing in this file writes an IP address anywhere. Request
// logging is off, the console logs warnings and errors only, and the one place an address is
// read at all - the abuse limits on creating accounts and signing in - keeps it as an in-memory
// partition key that is forgotten when its window closes. See PRIVACY.md and server/DEPLOY.md.
//
// The server listens on localhost only. Phones reach it through a Cloudflare Tunnel, which
// terminates TLS at Cloudflare and carries traffic to this machine over an encrypted tunnel
// that the machine itself dials OUT - so it has no open inbound port and no public address.
// ----------------------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("CriticalCount", LogLevel.Information);

if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");

string dbPath = builder.Configuration["Db"] ?? Path.Combine(AppContext.BaseDirectory, "data", "criticalcount.db");
bool specials = builder.Configuration.GetValue("OnlineSpecials", false);

var store = new Store(dbPath);
var sessions = new Sessions();
var lobby = new Lobby(store, specials);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(sessions);
builder.Services.AddSingleton(lobby);
builder.Services.AddHostedService<Ticker>();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // New accounts: a handful an hour from one address is plenty for a family sharing Wi-Fi and
    // far too few to fill the friend search with junk.
    o.AddPolicy("create", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
    o.AddPolicy("signin", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Everything else is per ACCOUNT, not per address.
    o.AddPolicy("api", ctx => RateLimitPartition.GetTokenBucketLimiter(AccountKey(ctx, sessions),
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 30, TokensPerPeriod = 30, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));
});

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseRateLimiter();

var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CriticalCount");
log.LogInformation("Critical Count server starting (online specials: {Specials})", specials);

// ------------------------------------------------------------------
// Accounts and sessions
// ------------------------------------------------------------------

app.MapGet("/v1/health", () => Results.Ok(new { ok = true }));

app.MapPost("/v1/accounts", () =>
{
    var (id, secret) = store.CreateAccount();
    return Results.Ok(new { accountId = id, secret });
}).RequireRateLimiting("create");

app.MapPost("/v1/sessions", (SignIn body) =>
{
    if (body == null || !store.VerifySecret(body.AccountId, body.Secret)) return Results.Unauthorized();
    string token = sessions.Issue(body.AccountId);
    return Results.Ok(new { token, expiresIn = (int)Sessions.Lifetime.TotalSeconds, profile = ProfileJson(store.GetProfile(body.AccountId)) });
}).RequireRateLimiting("signin");

app.MapGet("/v1/me", (HttpContext ctx) =>
    WithAccount(ctx, me => Results.Ok(ProfileJson(store.GetProfile(me))))).RequireRateLimiting("api");

app.MapPut("/v1/me/name", (HttpContext ctx, NameChange body) => WithAccount(ctx, me =>
{
    NameFilter.Verdict verdict = NameFilter.Default.Check(body?.Name);
    if (verdict != NameFilter.Verdict.Ok)
        return Results.BadRequest(new { error = "name_" + verdict.ToString().ToLowerInvariant(), message = NameMessage(verdict) });
    return Results.Ok(ProfileJson(store.SetName(me, NameFilter.Clean(body.Name))));
})).RequireRateLimiting("api");

// Required by Google Play for any app with accounts: delete it from inside the app.
app.MapDelete("/v1/me", (HttpContext ctx) => WithAccount(ctx, me =>
{
    lobby.Remove(me);
    store.DeleteAccount(me);
    sessions.RevokeAll(me);
    return Results.NoContent();
})).RequireRateLimiting("api");

// ------------------------------------------------------------------
// Friends, search, blocks, reports
// ------------------------------------------------------------------

app.MapGet("/v1/friends", (HttpContext ctx) => WithAccount(ctx, me =>
{
    Store.FriendLists lists = store.ListFriends(me);
    return Results.Ok(new
    {
        friends = lists.Friends.Select(p => new { id = p.Id, name = p.Display, online = lobby.IsOnline(p.Id), inMatch = lobby.IsInMatch(p.Id) }),
        incoming = lists.Incoming.Select(p => new { id = p.Id, name = p.Display }),
        outgoing = lists.Outgoing.Select(p => new { id = p.Id, name = p.Display }),
    });
})).RequireRateLimiting("api");

app.MapPost("/v1/friends/code", (HttpContext ctx, CodeBody body) => WithAccount(ctx, me =>
{
    string other = store.FindByFriendCode(body?.Code);
    return other == null ? Results.NotFound(new { error = "no_such_code" }) : FriendResultJson(store.RequestFriend(me, other));
})).RequireRateLimiting("api");

app.MapPost("/v1/friends/{id}", (HttpContext ctx, string id) =>
    WithAccount(ctx, me => FriendResultJson(store.RequestFriend(me, id)))).RequireRateLimiting("api");

app.MapPost("/v1/friends/{id}/accept", (HttpContext ctx, string id) =>
    WithAccount(ctx, me => store.AcceptFriend(me, id) ? Results.Ok(new { result = "accepted" }) : Results.NotFound())).RequireRateLimiting("api");

app.MapDelete("/v1/friends/{id}", (HttpContext ctx, string id) => WithAccount(ctx, me =>
{
    store.RemoveFriend(me, id);
    return Results.NoContent();
})).RequireRateLimiting("api");

app.MapGet("/v1/players/search", (HttpContext ctx, string name) => WithAccount(ctx, me =>
    Results.Ok(store.SearchByName(me, name ?? string.Empty).Select(p => new { id = p.Id, name = p.Display })))).RequireRateLimiting("api");

app.MapGet("/v1/blocks", (HttpContext ctx) => WithAccount(ctx, me =>
    Results.Ok(store.ListBlocked(me).Select(p => new { id = p.Id, name = p.Display })))).RequireRateLimiting("api");

app.MapPost("/v1/blocks/{id}", (HttpContext ctx, string id) => WithAccount(ctx, me =>
{
    store.Block(me, id);
    return Results.NoContent();
})).RequireRateLimiting("api");

app.MapDelete("/v1/blocks/{id}", (HttpContext ctx, string id) => WithAccount(ctx, me =>
{
    store.Unblock(me, id);
    return Results.NoContent();
})).RequireRateLimiting("api");

app.MapPost("/v1/reports/{id}", (HttpContext ctx, string id, ReportBody body) => WithAccount(ctx, me =>
{
    if (Array.IndexOf(Store.ReportReasons, body?.Reason) < 0) return Results.BadRequest(new { error = "bad_reason" });
    store.Report(me, id, body.Reason);
    return Results.NoContent();
})).RequireRateLimiting("api");

// ------------------------------------------------------------------
// Endless leaderboard
// ------------------------------------------------------------------

app.MapGet("/v1/leaderboard/endless", (HttpContext ctx) => WithAccount(ctx, me =>
{
    Store.Profile mine = store.GetProfile(me);
    return Results.Ok(new
    {
        top = store.TopEndless(me).Select(r => new { rank = r.Rank, name = $"{r.Name}#{r.Tag:D4}", streak = r.Streak, you = r.You }),
        you = new { rank = store.RankOf(me), streak = mine?.EndlessBest ?? 0, name = mine?.Display },
    });
})).RequireRateLimiting("api");

app.MapPost("/v1/leaderboard/endless", (HttpContext ctx, ScoreBody body) => WithAccount(ctx, me =>
{
    if (body == null || body.Streak < 0 || body.Streak > Store.MaxPlausibleStreak) return Results.BadRequest(new { error = "bad_streak" });
    int best = store.SubmitEndless(me, body.Streak);
    return Results.Ok(new { best, rank = store.RankOf(me) });
})).RequireRateLimiting("api");

// ------------------------------------------------------------------
// The live socket: matchmaking, challenges and matches
// ------------------------------------------------------------------

app.Map("/v1/play", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
    using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await SocketClient.Run(socket, store, sessions, lobby, ctx.RequestAborted);
    return Results.Empty;
});

app.Run();

// ------------------------------------------------------------------
// Helpers
// ------------------------------------------------------------------

IResult WithAccount(HttpContext ctx, Func<string, IResult> action)
{
    string me = sessions.Resolve(BearerToken(ctx));
    return me == null ? Results.Unauthorized() : action(me);
}

static string BearerToken(HttpContext ctx)
{
    string header = ctx.Request.Headers.Authorization;
    return header != null && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..].Trim() : null;
}

/// The address an abuse limit is counted against. Behind the tunnel every request arrives from
/// cloudflared on this machine, so the visitor's address is in CF-Connecting-IP - trusted ONLY
/// when the request really did come from localhost. Held in memory as a limiter key, never logged.
static string ClientKey(HttpContext ctx)
{
    IPAddress remote = ctx.Connection.RemoteIpAddress;
    if (remote != null && IPAddress.IsLoopback(remote))
    {
        string cf = ctx.Request.Headers["CF-Connecting-IP"];
        if (!string.IsNullOrEmpty(cf)) return cf;
    }
    return remote?.ToString() ?? "unknown";
}

static string AccountKey(HttpContext ctx, Sessions sessions) =>
    sessions.Resolve(BearerToken(ctx)) ?? "anon:" + ClientKey(ctx);

static object ProfileJson(Store.Profile p) => p == null ? null : new
{
    accountId = p.Id,
    name = p.Name,
    tag = p.Tag,
    display = p.Display,
    friendCode = Store.FormatFriendCode(p.FriendCode),
    endlessBest = p.EndlessBest,
    named = p.Name != Store.DefaultName,
};

static IResult FriendResultJson(Store.FriendResult r) => r switch
{
    Store.FriendResult.Requested => Results.Ok(new { result = "requested" }),
    Store.FriendResult.Accepted => Results.Ok(new { result = "accepted" }),
    Store.FriendResult.AlreadyFriends => Results.Ok(new { result = "already_friends" }),
    Store.FriendResult.AlreadyRequested => Results.Ok(new { result = "already_requested" }),
    Store.FriendResult.NotFound => Results.NotFound(new { error = "no_such_player" }),
    Store.FriendResult.TooMany => Results.BadRequest(new { error = "too_many_friends" }),
    // Blocked reads exactly like "no such player": a blocked person learns nothing from trying.
    Store.FriendResult.Blocked => Results.NotFound(new { error = "no_such_player" }),
    _ => Results.BadRequest(new { error = "self" }),
};

static string NameMessage(NameFilter.Verdict v) => v switch
{
    NameFilter.Verdict.TooShort => $"Names need at least {NameFilter.MinLength} characters",
    NameFilter.Verdict.TooLong => $"Names can be up to {NameFilter.MaxLength} characters",
    NameFilter.Verdict.BadCharacters => "Use letters, numbers, spaces, _ - or .",
    NameFilter.Verdict.NeedsLetters => "Names need at least two letters",
    _ => "That name isn't allowed. Try another",
};

record SignIn(string AccountId, string Secret);
record NameChange(string Name);
record CodeBody(string Code);
record ReportBody(string Reason);
record ScoreBody(int Streak);

/// Drives every match's clock and expires challenges, four times a second.
sealed class Ticker : BackgroundService
{
    private readonly Lobby _lobby;
    private readonly Sessions _sessions;
    private readonly ILogger<Ticker> _log;

    public Ticker(Lobby lobby, Sessions sessions, ILogger<Ticker> log)
    {
        _lobby = lobby;
        _sessions = sessions;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        DateTime nextSweep = DateTime.UtcNow;
        while (await timer.WaitForNextTickAsync(stop))
        {
            try
            {
                _lobby.Tick();
                if (DateTime.UtcNow >= nextSweep)
                {
                    _sessions.Sweep();
                    nextSweep = DateTime.UtcNow.AddMinutes(10);
                }
            }
            catch (Exception e)
            {
                _log.LogError(e, "Tick failed");
            }
        }
    }
}

/// One phone's socket. The first message must be {type:"auth", token}; after that every message
/// goes to the lobby. Sends go through a small queue so a slow phone never blocks a match - and a
/// phone too slow to keep up with even that is disconnected rather than buffered forever.
sealed class SocketClient : Lobby.IClient
{
    private readonly WebSocket _socket;
    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly CancellationTokenSource _cts;

    public string AccountId { get; }
    public string DisplayName { get; }

    private SocketClient(WebSocket socket, string accountId, string displayName, CancellationToken aborted)
    {
        _socket = socket;
        AccountId = accountId;
        DisplayName = displayName;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
    }

    public void Send(object message)
    {
        string json = JsonSerializer.Serialize(message, message.GetType(), Protocol.Json);
        if (!_outbox.Writer.TryWrite(json)) Close();
    }

    public void Close()
    {
        _outbox.Writer.TryComplete();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public static async Task Run(WebSocket socket, Store store, Sessions sessions, Lobby lobby, CancellationToken aborted)
    {
        var buffer = new byte[Protocol.MaxInboundBytes];

        // Sign in first, within ten seconds.
        string accountId;
        using (var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(aborted))
        {
            authTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            Inbound first;
            try { first = await Receive(socket, buffer, authTimeout.Token); }
            catch (OperationCanceledException) { first = null; }
            accountId = first?.Type == "auth" ? sessions.Resolve(first.Token) : null;
            if (accountId != null && first.Version < Protocol.MinClientVersion)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    type = "error", code = "update_required", message = "Update Critical Count to play online.",
                }, Protocol.Json));
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, aborted);
                await SafeClose(socket, WebSocketCloseStatus.PolicyViolation, "update");
                return;
            }
            if (accountId == null)
            {
                await SafeClose(socket, WebSocketCloseStatus.PolicyViolation, "auth");
                return;
            }
        }

        Store.Profile profile = store.GetProfile(accountId);
        if (profile == null)
        {
            await SafeClose(socket, WebSocketCloseStatus.PolicyViolation, "auth");
            return;
        }

        var client = new SocketClient(socket, accountId, profile.Display, aborted);
        Task pump = client.PumpOutbox();
        lobby.Connected(client);

        // A phone sends a few messages a second at most; twenty in one second is not a person.
        int burst = 0;
        DateTime window = DateTime.UtcNow;
        try
        {
            while (!client._cts.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                Inbound msg;
                try { msg = await Receive(socket, buffer, client._cts.Token); }
                catch (JsonException)
                {
                    client.Send(new { type = "error", code = "bad_json" });
                    continue;
                }
                if (msg == null) break;

                if ((DateTime.UtcNow - window).TotalSeconds >= 1) { window = DateTime.UtcNow; burst = 0; }
                if (++burst > 20) break;

                lobby.Handle(client, msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            lobby.Disconnected(client);
            client.Close();
            try { await pump; } catch { }
            await SafeClose(socket, WebSocketCloseStatus.NormalClosure, "bye");
        }
    }

    private async Task PumpOutbox()
    {
        try
        {
            await foreach (string json in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    /// One whole text message, or null when the socket closed. Anything over the size limit
    /// closes the connection.
    private static async Task<Inbound> Receive(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        int count = 0;
        while (true)
        {
            if (count >= buffer.Length) throw new OperationCanceledException("message too large");
            WebSocketReceiveResult r = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            count += r.Count;
            if (r.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize<Inbound>(new ReadOnlySpan<byte>(buffer, 0, count), Protocol.Json);
    }

    private static async Task SafeClose(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(status, reason, CancellationToken.None);
        }
        catch { }
    }
}
