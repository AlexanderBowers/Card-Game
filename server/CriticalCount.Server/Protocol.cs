using System.Text.Json;
using System.Text.Json.Serialization;

namespace CriticalCount.Server;

/// The messages on the /v1/play WebSocket. Every message is one JSON object with a "type".
///
/// PHONE -> SERVER
///   {type:"auth", token, version}                first message, always; nothing else is read before it
///   {type:"queue"} / {type:"leaveQueue"}         quick match
///   {type:"invite", to}                          challenge an online friend (account id)
///   {type:"cancelInvite"}
///   {type:"inviteReply", inviteId, accept}
///   {type:"play", cardId, value?, chosenId?}     value: a +/- card's chosen sign; chosenId: Recall's pick
///   {type:"draw"} / {type:"hold"}                Draw Card / Hold
///   {type:"forfeit"}
///   {type:"ping"}
///
/// SERVER -> PHONE
///   {type:"authed", you:{id,name}}
///   {type:"queued"} / {type:"queueLeft"}
///   {type:"invited", inviteId, from:{id,name}}    someone challenged you (expires in 30 s)
///   {type:"inviteSent", inviteId, to}
///   {type:"inviteClosed", inviteId, reason}       declined / expired / cancelled / unavailable
///   {type:"matchStart", matchId, opponent:{id,name}, target, setsToWin, turnSeconds}
///   {type:"state", ...}                           the whole table as THIS player may see it
///   {type:"setEnd", winner, yourScore, theirScore}   winner: "you" | "them" | "tie"
///   {type:"matchEnd", winner, reason}             reason: "sets" | "forfeit" | "timeouts" | "disconnect"
///   {type:"error", code, message}               code "update_required" right after auth: the app is too old
///   {type:"pong"}
public static class Protocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// No message from a phone is ever larger than this. Anything bigger is dropped with the connection.
    public const int MaxInboundBytes = 2048;

    /// Bumped whenever the rules or the messages change in a way an older app would get wrong.
    /// The app sends its own in "auth"; one older than MinClientVersion is told to update rather
    /// than being let into a match it would play by different rules.
    public const int Version = 1;
    public const int MinClientVersion = 1;
}

public sealed class Inbound
{
    public string Type { get; set; }
    public string Token { get; set; }
    public int Version { get; set; }
    public string To { get; set; }
    public string InviteId { get; set; }
    public bool Accept { get; set; }
    public int CardId { get; set; }
    public int? Value { get; set; }
    public int? ChosenId { get; set; }
}

public sealed record PlayerRef(string Id, string Name);

public sealed record CardView(int Id, int Value, bool Flip, string Effect, string Text);

public sealed record SideView(
    string Name,
    int Score,
    bool Holding,
    bool Ended,
    bool CanAct,
    int Wins,
    List<CardView> Board,
    int HandCount,
    List<CardView> Hand,      // yours only; null for the opponent
    List<CardView> Spent,     // yours only: what Recall could bring back
    int? RecallLocked,        // yours only
    bool PlayedEffect,
    bool Connected,
    int DeckLeft);

public sealed record EventView(string Effect, bool ByYou, string Narration, int? DestroyedId);

public sealed record StateMessage(
    string Type,
    string MatchId,
    int Target,
    int SetsToWin,
    long TurnMsLeft,
    string Phase,             // "playing" | "setEnd" | "over"
    SideView You,
    SideView Them,
    EventView Event);
