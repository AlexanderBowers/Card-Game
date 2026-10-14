using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// The phone's side of the online server (server/CriticalCount.Server; see server/DEPLOY.md).
///
/// An autoload ("Online"), so it lives above the scene and survives every ReloadCurrentScene the
/// table does - a match in progress, the socket and the sign-in all outlast a restart of the table.
///
/// PRIVACY, as the code sees it:
///   - Nothing here runs until the player opens an online screen. Offline play never creates an
///     account, never opens a connection and never sends a byte.
///   - The account is anonymous: a random id and a random secret, made by the server and kept in
///     user://online.cfg. No name, email or device id is ever sent; the display name is whatever the
///     player types, and only after the server's filter accepts it.
///   - Everything goes over TLS to play.cloudydaygames.com (Godot's HTTPRequest and WebSocketPeer,
///     which bring their own certificate bundle, so this is the same on Android, iOS and desktop).
///
/// REST calls are async (await ToSignal), one short-lived HTTPRequest node each. The live socket is
/// polled in _Process and every message is raised on the main thread as Message(type, json).
public partial class OnlineService : Node
{
    public const string ProductionUrl = "https://play.cloudydaygames.com";

    /// Debug builds only: "-- --online-server=http://127.0.0.1:5080" points the game at a server
    /// running on this PC (dotnet run --project server/CriticalCount.Server). A release build always
    /// talks to production.
    public static string BaseUrl { get; } = ResolveBaseUrl();
    private static string SocketUrl => BaseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/v1/play";

    private static string ResolveBaseUrl()
    {
        if (OS.IsDebugBuild())
            foreach (string arg in OS.GetCmdlineUserArgs())
                if (arg.StartsWith("--online-server=")) return arg["--online-server=".Length..].TrimEnd('/');
        return ProductionUrl;
    }
    /// Must match the server's Protocol.Version range (Protocol.MinClientVersion).
    public const int ProtocolVersion = 1;
    private const string AccountPath = "user://online.cfg";

    public static OnlineService Instance { get; private set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public override void _EnterTree() => Instance = this;
    public override void _ExitTree() { if (Instance == this) Instance = null; }

    // ------------------------------------------------------------------
    // Account and sign-in
    // ------------------------------------------------------------------

    private string _accountId;
    private string _secret;
    private string _token;
    private DateTime _tokenExpires;

    /// The signed-in player's profile, refreshed by every sign-in and name change.
    public Profile Me { get; private set; }

    public sealed class Profile
    {
        public string AccountId { get; set; }
        public string Name { get; set; }
        public int Tag { get; set; }
        public string Display { get; set; }
        public string FriendCode { get; set; }
        public int EndlessBest { get; set; }
        public bool Named { get; set; }
    }

    /// True once this phone has an online account (it has opened an online screen before).
    public bool HasAccount
    {
        get
        {
            LoadAccount();
            return !string.IsNullOrEmpty(_accountId);
        }
    }

    private bool _accountLoaded;

    private void LoadAccount()
    {
        if (_accountLoaded) return;
        _accountLoaded = true;
        ConfigFile cfg = new ConfigFile();
        if (cfg.Load(AccountPath) != Error.Ok) return;
        _accountId = cfg.GetValue("account", "id", "").AsString();
        _secret = cfg.GetValue("account", "secret", "").AsString();
    }

    private void SaveAccount()
    {
        ConfigFile cfg = new ConfigFile();
        cfg.SetValue("account", "id", _accountId ?? "");
        cfg.SetValue("account", "secret", _secret ?? "");
        Error err = cfg.Save(AccountPath);
        if (err != Error.Ok) GD.PushWarning($"Could not save the online account: {err}");
    }

    /// Makes the account on first use, then signs in. Null on success, or a sentence to show.
    public async Task<string> EnsureSignedIn()
    {
        if (!string.IsNullOrEmpty(_token) && DateTime.UtcNow < _tokenExpires) return null;
        LoadAccount();

        if (string.IsNullOrEmpty(_accountId))
        {
            ApiResult made = await Send(HttpClient.Method.Post, "/v1/accounts", null, auth: false);
            if (!made.Ok) return made.Status == 429 ? "Too many new accounts from this network. Try again later" : made.Problem;
            _accountId = made.Body.GetProperty("accountId").GetString();
            _secret = made.Body.GetProperty("secret").GetString();
            SaveAccount();
        }

        ApiResult signed = await Send(HttpClient.Method.Post, "/v1/sessions",
            new { accountId = _accountId, secret = _secret }, auth: false);
        if (signed.Status == 401)
        {
            // The server no longer knows this account (deleted elsewhere). Start a fresh one.
            ForgetAccount();
            return await EnsureSignedIn();
        }
        if (!signed.Ok) return signed.Problem;

        _token = signed.Body.GetProperty("token").GetString();
        int seconds = signed.Body.GetProperty("expiresIn").GetInt32();
        _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 300));
        Me = signed.Body.GetProperty("profile").Deserialize<Profile>(Json);
        return null;
    }

    /// The account this phone signs in with, for the cloud save to carry to a new phone (CloudSave).
    /// Nulls when there is none yet.
    public (string Id, string Secret) ExportAccount()
    {
        LoadAccount();
        return (_accountId, _secret);
    }

    /// Takes over an account carried in from the cloud save: the same player on a new phone keeps
    /// their name, friends and leaderboard place instead of starting a stranger.
    public void ImportAccount(string id, string secret)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)) return;
        _accountLoaded = true;
        _accountId = id;
        _secret = secret;
        _token = null;
        Me = null;
        SaveAccount();
    }

    private void ForgetAccount()
    {
        _accountId = null;
        _secret = null;
        _token = null;
        Me = null;
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(AccountPath));
    }

    // ------------------------------------------------------------------
    // REST
    // ------------------------------------------------------------------

    public readonly struct ApiResult
    {
        public readonly int Status;          // HTTP status, or 0 when the request never got an answer
        public readonly JsonElement Body;    // the parsed JSON body (Undefined when there was none)
        public readonly string Problem;      // a sentence for the player when !Ok

        public ApiResult(int status, JsonElement body, string problem)
        {
            Status = status;
            Body = body;
            Problem = problem;
        }

        public bool Ok => Status >= 200 && Status < 300;

        /// The server's machine-readable error ("name_rejected", "no_such_code"...), or null.
        public string ErrorCode =>
            Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("error", out JsonElement e) ? e.GetString() : null;

        public string ServerMessage =>
            Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("message", out JsonElement m) ? m.GetString() : null;
    }

    public Task<ApiResult> Get(string path) => Call(HttpClient.Method.Get, path, null);
    public Task<ApiResult> Post(string path, object body = null) => Call(HttpClient.Method.Post, path, body);
    public Task<ApiResult> Put(string path, object body) => Call(HttpClient.Method.Put, path, body);
    public Task<ApiResult> Delete(string path) => Call(HttpClient.Method.Delete, path, null);

    /// An authenticated call: signs in first if needed, and once more if the token has lapsed.
    private async Task<ApiResult> Call(HttpClient.Method method, string path, object body)
    {
        string problem = await EnsureSignedIn();
        if (problem != null) return new ApiResult(0, default, problem);

        ApiResult result = await Send(method, path, body, auth: true);
        if (result.Status == 401)
        {
            _token = null;
            problem = await EnsureSignedIn();
            if (problem != null) return new ApiResult(0, default, problem);
            result = await Send(method, path, body, auth: true);
        }
        return result;
    }

    private async Task<ApiResult> Send(HttpClient.Method method, string path, object body, bool auth)
    {
        HttpRequest request = new HttpRequest { Timeout = 12.0 };
        AddChild(request);

        List<string> headers = new List<string> { "Accept: application/json" };
        string payload = string.Empty;
        if (body != null)
        {
            headers.Add("Content-Type: application/json");
            payload = JsonSerializer.Serialize(body, body.GetType(), Json);
        }
        else if (method == HttpClient.Method.Post || method == HttpClient.Method.Put)
        {
            headers.Add("Content-Type: application/json");
            payload = "{}";
        }
        if (auth && !string.IsNullOrEmpty(_token)) headers.Add("Authorization: Bearer " + _token);

        Error err = request.Request(BaseUrl + path, headers.ToArray(), method, payload);
        if (err != Error.Ok)
        {
            request.QueueFree();
            return new ApiResult(0, default, "Couldn't reach the server");
        }

        Variant[] done = await ToSignal(request, HttpRequest.SignalName.RequestCompleted);
        request.QueueFree();

        long outcome = done[0].AsInt64();
        int status = (int)done[1].AsInt64();
        byte[] bytes = done[3].AsByteArray();

        if (outcome != (long)HttpRequest.Result.Success)
            return new ApiResult(0, default, "No connection to the server. Check your internet");

        JsonElement parsed = default;
        if (bytes != null && bytes.Length > 0)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(bytes);
                parsed = doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }

        string problem = null;
        if (status < 200 || status >= 300)
        {
            problem = status switch
            {
                429 => "Slow down a moment and try again",
                >= 500 => "The server had a problem. Try again shortly",
                _ => "That didn't work",
            };
        }
        return new ApiResult(status, parsed, problem);
    }

    // ------------------------------------------------------------------
    // Profile, friends, leaderboard
    // ------------------------------------------------------------------

    /// Null on success (Me is updated), or the sentence explaining why the name was refused.
    public async Task<string> SetName(string name)
    {
        ApiResult r = await Put("/v1/me/name", new { name });
        if (r.Ok)
        {
            Me = r.Body.Deserialize<Profile>(Json);
            return null;
        }
        return r.ServerMessage ?? r.Problem;
    }

    /// Deletes the online account on the server and forgets it here. Offline progress is untouched.
    public async Task<string> DeleteAccount()
    {
        if (!HasAccount) return null;
        ApiResult r = await Delete("/v1/me");
        if (!r.Ok && r.Status != 401) return r.Problem;
        CloseSocket();
        ForgetAccount();
        return null;
    }

    /// Sends the best endless streak this phone knows about (the server keeps the higher one).
    public async Task SubmitEndlessBest(int streak)
    {
        if (streak <= 0) return;
        await Post("/v1/leaderboard/endless", new { streak });
    }

    // ------------------------------------------------------------------
    // The live socket: queue, challenges, matches
    // ------------------------------------------------------------------

    /// Every message from the server, on the main thread: its "type" and the whole object.
    public event Action<string, JsonElement> Message;

    /// Raised when the socket closes for any reason (network lost, signed in elsewhere, closed).
    public event Action Closed;

    /// Raised when the socket has signed in and is ready for messages.
    public event Action Ready;

    private WebSocketPeer _socket;
    private bool _authSent;
    private bool _authed;
    private double _pingTimer;
    private bool _wantSocket;
    private double _reconnectIn = -1;

    public bool SocketReady => _authed && _socket != null && _socket.GetReadyState() == WebSocketPeer.State.Open;

    /// The opponent's account id while a match is on, for Block/Report from the table.
    public string CurrentOpponentId { get; set; }

    /// True from matchStart to matchEnd: a dropped connection is re-made at once, because the
    /// server holds the seat for 45 seconds.
    public bool InMatch { get; set; }

    /// Opens the socket (signing in first). Null when it is on its way, or a sentence to show.
    public async Task<string> OpenSocket()
    {
        _wantSocket = true;
        if (_socket != null && _socket.GetReadyState() != WebSocketPeer.State.Closed) return null;

        string problem = await EnsureSignedIn();
        if (problem != null) return problem;

        _socket = new WebSocketPeer();
        _socket.InboundBufferSize = 256 * 1024;
        _authSent = false;
        _authed = false;
        Error err = _socket.ConnectToUrl(SocketUrl);
        if (err != Error.Ok)
        {
            _socket = null;
            return "Couldn't reach the server";
        }
        return null;
    }

    public void CloseSocket()
    {
        _wantSocket = false;
        InMatch = false;
        _reconnectIn = -1;
        if (_socket != null && _socket.GetReadyState() == WebSocketPeer.State.Open) _socket.Close(1000, "bye");
        _socket = null;
        _authed = false;
    }

    /// Sends one message ({type, ...}). False when the socket is not ready.
    public bool SendMessage(object message)
    {
        if (!SocketReady) return false;
        return _socket.SendText(JsonSerializer.Serialize(message, message.GetType(), Json)) == Error.Ok;
    }

    public override void _Process(double delta)
    {
        if (_reconnectIn > 0)
        {
            _reconnectIn -= delta;
            if (_reconnectIn <= 0)
            {
                _reconnectIn = -1;
                if (_wantSocket) _ = OpenSocket();
            }
        }

        if (_socket == null) return;
        _socket.Poll();
        WebSocketPeer.State state = _socket.GetReadyState();

        if (state == WebSocketPeer.State.Open)
        {
            if (!_authSent)
            {
                _authSent = true;
                _socket.SendText(JsonSerializer.Serialize(new { type = "auth", token = _token, version = ProtocolVersion }, Json));
            }

            while (_socket.GetAvailablePacketCount() > 0)
            {
                byte[] packet = _socket.GetPacket();
                Dispatch(packet);
            }

            _pingTimer += delta;
            if (_authed && _pingTimer > 20)
            {
                _pingTimer = 0;
                SendMessage(new { type = "ping" });
            }
        }
        else if (state == WebSocketPeer.State.Closed)
        {
            _socket = null;
            bool wasAuthed = _authed;
            _authed = false;
            Closed?.Invoke();
            // A match in progress is worth getting back to; the server keeps the seat for 45 s.
            if (_wantSocket && InMatch) _reconnectIn = 2.0;
            else if (!wasAuthed) _wantSocket = false;
        }
    }

    private void Dispatch(byte[] packet)
    {
        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(packet);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement typeEl)) return;
        string type = typeEl.GetString();

        switch (type)
        {
            case "authed":
                _authed = true;
                _pingTimer = 0;
                Ready?.Invoke();
                break;
            case "pong":
                return;
            case "matchStart":
                InMatch = true;
                if (root.TryGetProperty("opponent", out JsonElement opp) && opp.TryGetProperty("id", out JsonElement oid))
                    CurrentOpponentId = oid.GetString();
                break;
            case "matchEnd":
                InMatch = false;
                break;
            case "error":
                string code = root.TryGetProperty("code", out JsonElement c) ? c.GetString() : null;
                if (code == "signed_in_elsewhere" || code == "update_required")
                {
                    _wantSocket = false;
                    InMatch = false;
                }
                break;
        }

        Message?.Invoke(type, root);
    }

    // ------------------------------------------------------------------
    // Small helpers for the screens
    // ------------------------------------------------------------------

    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static int Int(JsonElement e, string name, int fallback = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : fallback;

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}
