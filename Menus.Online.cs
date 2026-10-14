using Godot;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

/// The Online pages of the start menu (2026-10-08): name, quick match, friends, challenges and the
/// endless leaderboard. Pages of the same menu panel, like Local 2-Player and Endless Scores - Back
/// is a refill, not another overlay.
///
/// Nothing here runs until the player presses Online. That first press is what creates the
/// anonymous account (OnlineService); a player who never presses it never touches the network.
public sealed partial class Menus
{
    /// "Play Again" on the online match-end screen: the next start menu opens on the queue.
    public static bool PendingOnlineQueue;

    private bool _onlineActive;      // an Online page is up and listening to the socket
    private int _onlinePage;          // bumped on every page change; a late async answer for an old page is dropped
    private Label _onlineStatus;      // the line a page uses for "Looking for..." and errors
    private string _pendingInvite;    // an incoming challenge, while its page is up
    private Action _onlineQueueOnReady;

    private static OnlineService Svc => OnlineService.Instance;

    private int NewOnlinePage(string heading)
    {
        _onlinePage++;
        _onlineStatus = null;
        OverlayUi.ClearChildren(_startMenuBox);
        _startMenuBox.AddChild(OverlayUi.MakeLabel(heading, MenuHeadingFont));
        _startMenuBox.AddChild(MenuSpacer());
        AttachOnline();
        return _onlinePage;
    }

    private bool StillOn(int page) =>
        page == _onlinePage && _onlineActive && GodotObject.IsInstanceValid(_startMenuBox) && _startMenuBox.IsInsideTree();

    private Label AddOnlineNote(string text, Color? color = null, int size = 0)
    {
        Label label = OverlayUi.MakeLabel(text, size > 0 ? size : MenuNoteFont, color ?? OverlayUi.Muted);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
        _startMenuBox.AddChild(label);
        return label;
    }

    private Label AddOnlineStatus(string text)
    {
        _onlineStatus = AddOnlineNote(text, OverlayUi.Ink, 22);
        return _onlineStatus;
    }

    private void SetOnlineStatus(string text, bool warning = false)
    {
        if (_onlineStatus == null || !GodotObject.IsInstanceValid(_onlineStatus)) return;
        _onlineStatus.Text = text;
        _onlineStatus.AddThemeColorOverride("font_color", warning ? OverlayUi.Warning : OverlayUi.Ink);
    }

    private LineEdit AddOnlineInput(string placeholder, string text = "", int maxLength = 32)
    {
        LineEdit input = new LineEdit
        {
            PlaceholderText = placeholder,
            Text = text ?? string.Empty,
            MaxLength = maxLength,
            CustomMinimumSize = new Vector2(MenuButtonWidth, MenuButtonHeight),
            Alignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        input.AddThemeFontSizeOverride("font_size", MenuButtonFont);
        _startMenuBox.AddChild(input);
        return input;
    }

    // ------------------------------------------------------------------
    // Listening to the socket while an Online page is up
    // ------------------------------------------------------------------

    private void AttachOnline()
    {
        if (_onlineActive || Svc == null) return;
        _onlineActive = true;
        Svc.Message += OnMenuOnlineMessage;
        Svc.Ready += OnMenuOnlineReady;
    }

    private void DetachOnlineMenu()
    {
        if (!_onlineActive) return;
        _onlineActive = false;
        if (Svc == null) return;
        Svc.Message -= OnMenuOnlineMessage;
        Svc.Ready -= OnMenuOnlineReady;
    }

    /// Back to the main menu from the Online pages. The socket closes: no presence, no challenges,
    /// nothing open while the player is off doing something else.
    private void LeaveOnline()
    {
        DetachOnlineMenu();
        Svc?.CloseSocket();
        FillStartMenu();
    }

    private void OnMenuOnlineReady()
    {
        if (!_onlineActive || !GodotObject.IsInstanceValid(_startMenuBox)) { DetachOnlineMenu(); return; }
        Action next = _onlineQueueOnReady;
        _onlineQueueOnReady = null;
        next?.Invoke();
    }

    private void OnMenuOnlineMessage(string type, JsonElement msg)
    {
        // The scene was reloaded under us: stop listening.
        if (!GodotObject.IsInstanceValid(_startMenuBox) || !_startMenuBox.IsInsideTree())
        {
            DetachOnlineMenu();
            return;
        }
        if (!_onlineActive) return;

        switch (type)
        {
            case "queued":
                SetOnlineStatus("Looking for an opponent...");
                break;
            case "matchStart":
                DetachOnlineMenu();
                HideStartMenu();
                _host.StartOnlineMatch(msg);
                break;
            case "invited":
                FillInvited(OnlineService.Str(msg, "inviteId"),
                    msg.TryGetProperty("from", out JsonElement from) ? OnlineService.Str(from, "name") : "Someone");
                break;
            case "inviteSent":
                break;
            case "inviteClosed":
                string reason = OnlineService.Str(msg, "reason");
                if (_pendingInvite != null && OnlineService.Str(msg, "inviteId") == _pendingInvite)
                {
                    _pendingInvite = null;
                    FillOnline();
                    return;
                }
                SetOnlineStatus(reason switch
                {
                    "declined" => "They said not right now.",
                    "expired" => "No answer - the challenge ran out.",
                    "cancelled" => "Challenge cancelled.",
                    _ => "They're not available right now.",
                }, warning: true);
                break;
            case "error":
                SetOnlineStatus(OnlineService.Str(msg, "code") switch
                {
                    "friend_unavailable" => "They're offline or already playing.",
                    "not_friends" => "You can only challenge friends.",
                    "already_in_match" => "You're already in a match.",
                    "signed_in_elsewhere" => "You signed in on another device.",
                    "update_required" => "Update Critical Count to play online.",
                    "bad_deck" => "Your deck couldn't be checked. Update Critical Count and try again.",
                    _ => "Something went wrong. Try again.",
                }, warning: true);
                break;
        }
    }

    // ------------------------------------------------------------------
    // The hub
    // ------------------------------------------------------------------

    private async void FillOnline()
    {
        int page = NewOnlinePage("Online");
        if (Svc == null) { AddOnlineNote("Online isn't available in this build."); AddMenuButton("Back", null, LeaveOnline); return; }

        AddOnlineStatus("Connecting...");
        string problem = await Svc.EnsureSignedIn();
        if (!StillOn(page)) return;
        if (problem != null)
        {
            SetOnlineStatus(problem, warning: true);
            AddOnlineNote("Everything else in the game works offline.");
            AddMenuButton("Try Again", null, FillOnline);
            AddMenuButton("Back", null, LeaveOnline);
            return;
        }

        // A first visit chooses a name before anything else: nobody plays "Player".
        if (Svc.Me != null && !Svc.Me.Named)
        {
            FillOnlineName(firstTime: true);
            return;
        }

        // Listening for challenges from here on: friends see this phone as online.
        _ = Svc.OpenSocket();

        OverlayUi.ClearChildren(_startMenuBox);
        _startMenuBox.AddChild(OverlayUi.MakeLabel("Online", MenuHeadingFont));
        _startMenuBox.AddChild(OverlayUi.MakeLabel(Svc.Me?.Display ?? "", 30, OverlayUi.Ink));
        AddOnlineNote($"Friend code: {Svc.Me?.FriendCode}", OverlayUi.MedalGold, 20);
        _startMenuBox.AddChild(MenuSpacer());
        _onlineStatus = null;

        Button quick = AddMenuButton("Quick Match", null, FillOnlineQuickMatch);
        OverlayUi.StyleButton(quick, primary: true);
        AddMenuButton("Friends", null, FillFriends);
        AddMenuButton("Endless Leaderboard", null, FillLeaderboard);
        AddMenuButton("Change Name", null, () => FillOnlineName(firstTime: false));
        AddMenuButton("Back", null, LeaveOnline);
        _startMenuBox.AddChild(MenuSpacer());
        AddOnlineNote("Online needs the internet. Everything else plays offline.");
    }

    // ------------------------------------------------------------------
    // Name
    // ------------------------------------------------------------------

    private void FillOnlineName(bool firstTime)
    {
        int page = NewOnlinePage(firstTime ? "Choose a Name" : "Change Name");
        AddOnlineNote("Other players see it with four numbers after it, like Alex#4821. "
                      + "3 to 16 letters, numbers or spaces.");
        LineEdit input = AddOnlineInput("Your name", Svc?.Me != null && Svc.Me.Named ? Svc.Me.Name : "", 16);
        AddOnlineStatus(string.Empty);

        async void Save()
        {
            string name = input.Text.Trim();
            SetOnlineStatus("Saving...");
            string problem = await Svc.SetName(name);
            if (!StillOn(page)) return;
            if (problem != null)
            {
                SetOnlineStatus(problem, warning: true);
                return;
            }
            FillOnline();
        }

        input.TextSubmitted += _ => Save();
        Button save = AddMenuButton("Save", null, Save);
        OverlayUi.StyleButton(save, primary: true);
        AddMenuButton(firstTime ? "Not Now" : "Back", null, firstTime ? LeaveOnline : FillOnline);
    }

    // ------------------------------------------------------------------
    // Quick match
    // ------------------------------------------------------------------

    private async void FillOnlineQuickMatch()
    {
        int page = NewOnlinePage("Quick Match");
        AddOnlineStatus("Connecting...");
        AddOnlineNote("First to win 3 sets. 30 seconds a turn.");
        AddMenuButton("Cancel", null, () =>
        {
            Svc?.SendMessage(new { type = "leaveQueue" });
            FillOnline();
        });

        if (Svc == null) return;
        string problem = await Svc.OpenSocket();
        if (!StillOn(page)) return;
        if (problem != null)
        {
            SetOnlineStatus(problem, warning: true);
            return;
        }

        void Join()
        {
            if (!StillOn(page)) return;
            if (Svc.SendMessage(new { type = "queue", deck = MyOnlineDeck() })) SetOnlineStatus("Looking for an opponent...");
        }
        if (Svc.SocketReady) Join();
        else _onlineQueueOnReady = Join;
    }

    // ------------------------------------------------------------------
    // Friends
    // ------------------------------------------------------------------

    private async void FillFriends()
    {
        int page = NewOnlinePage("Friends");
        if (Svc == null) return;
        AddOnlineNote($"Your friend code: {Svc.Me?.FriendCode}", OverlayUi.MedalGold, 20);

        LineEdit find = AddOnlineInput("Friend code or name", "", 24);
        Button add = AddMenuButton("Add Friend", null, null);
        AddOnlineStatus(string.Empty);
        VBoxContainer results = new VBoxContainer();
        results.AddThemeConstantOverride("separation", 8);
        _startMenuBox.AddChild(results);

        async void Find()
        {
            string text = find.Text.Trim();
            if (text.Length < 2) return;
            OverlayUi.ClearChildren(results);
            SetOnlineStatus("Looking...");

            // A friend code is tried first: it is exact, and it never lists anybody.
            if (LooksLikeCode(text))
            {
                OnlineService.ApiResult byCode = await Svc.Post("/v1/friends/code", new { code = text });
                if (!StillOn(page)) return;
                if (byCode.Ok)
                {
                    SetOnlineStatus(FriendResultText(OnlineService.Str(byCode.Body, "result")));
                    FillFriendLists(page);
                    return;
                }
            }

            OnlineService.ApiResult found = await Svc.Get("/v1/players/search?name=" + Uri.EscapeDataString(text));
            if (!StillOn(page)) return;
            if (!found.Ok) { SetOnlineStatus(found.Problem, warning: true); return; }
            if (found.Body.ValueKind != JsonValueKind.Array || found.Body.GetArrayLength() == 0)
            {
                SetOnlineStatus("Nobody by that name or code.", warning: true);
                return;
            }
            SetOnlineStatus("Tap a name to send a friend request.");
            foreach (JsonElement p in found.Body.EnumerateArray())
            {
                string id = OnlineService.Str(p, "id");
                string name = OnlineService.Str(p, "name");
                Button pick = new Button { Text = name, CustomMinimumSize = new Vector2(MenuButtonWidth, 52) };
                pick.AddThemeFontSizeOverride("font_size", 22);
                pick.Pressed += async () =>
                {
                    OnlineService.ApiResult r = await Svc.Post("/v1/friends/" + id);
                    if (!StillOn(page)) return;
                    SetOnlineStatus(r.Ok ? FriendResultText(OnlineService.Str(r.Body, "result")) : "Couldn't send that request.", !r.Ok);
                    OverlayUi.ClearChildren(results);
                    FillFriendLists(page);
                };
                results.AddChild(pick);
            }
        }

        add.Pressed += Find;
        find.TextSubmitted += _ => Find();

        _startMenuBox.AddChild(MenuSpacer());
        _friendLists = new VBoxContainer();
        _friendLists.AddThemeConstantOverride("separation", 10);
        _startMenuBox.AddChild(_friendLists);
        _startMenuBox.AddChild(MenuSpacer());
        AddMenuButton("Blocked Players", null, FillBlocked);
        AddMenuButton("Back", null, FillOnline);

        _ = Svc.OpenSocket(); // so challenges can be sent and received from here
        await Task.CompletedTask;
        FillFriendLists(page);
    }

    private VBoxContainer _friendLists;

    private async void FillFriendLists(int page)
    {
        OnlineService.ApiResult r = await Svc.Get("/v1/friends");
        if (!StillOn(page) || _friendLists == null || !GodotObject.IsInstanceValid(_friendLists)) return;
        OverlayUi.ClearChildren(_friendLists);
        if (!r.Ok)
        {
            _friendLists.AddChild(OverlayUi.MakeLabel(r.Problem, MenuNoteFont, OverlayUi.Warning));
            return;
        }

        JsonElement incoming = r.Body.GetProperty("incoming");
        if (incoming.GetArrayLength() > 0)
        {
            _friendLists.AddChild(OverlayUi.MakeLabel("Friend Requests", MenuSectionFont));
            foreach (JsonElement p in incoming.EnumerateArray())
            {
                string id = OnlineService.Str(p, "id");
                HBoxContainer row = FriendRow(OnlineService.Str(p, "name"), null);
                row.AddChild(SmallButton("Accept", async () =>
                {
                    await Svc.Post($"/v1/friends/{id}/accept");
                    if (StillOn(page)) FillFriendLists(page);
                }, primary: true));
                row.AddChild(SmallButton("No", async () =>
                {
                    await Svc.Delete($"/v1/friends/{id}");
                    if (StillOn(page)) FillFriendLists(page);
                }));
            }
        }

        JsonElement friends = r.Body.GetProperty("friends");
        _friendLists.AddChild(OverlayUi.MakeLabel(friends.GetArrayLength() > 0 ? "Your Friends" : "No friends yet", MenuSectionFont));
        if (friends.GetArrayLength() == 0)
        {
            Label tip = OverlayUi.MakeLabel("Share your friend code, or type theirs above.", MenuNoteFont, OverlayUi.Muted);
            tip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            tip.CustomMinimumSize = new Vector2(MenuButtonWidth, 0);
            _friendLists.AddChild(tip);
        }
        foreach (JsonElement p in friends.EnumerateArray())
        {
            string id = OnlineService.Str(p, "id");
            string name = OnlineService.Str(p, "name");
            bool online = OnlineService.Bool(p, "online");
            bool playing = OnlineService.Bool(p, "inMatch");
            string status = playing ? "In a match" : online ? "Online" : "Offline";
            HBoxContainer row = FriendRow(name, status);
            if (online && !playing) row.AddChild(SmallButton("Challenge", () => FillChallenge(id, name), primary: true));
            row.AddChild(SmallButton("...", () => FillFriendActions(id, name, isFriend: true)));
        }

        JsonElement outgoing = r.Body.GetProperty("outgoing");
        if (outgoing.GetArrayLength() > 0)
        {
            _friendLists.AddChild(OverlayUi.MakeLabel("Requests Sent", MenuSectionFont));
            foreach (JsonElement p in outgoing.EnumerateArray())
            {
                string id = OnlineService.Str(p, "id");
                HBoxContainer row = FriendRow(OnlineService.Str(p, "name"), "Waiting");
                row.AddChild(SmallButton("Cancel", async () =>
                {
                    await Svc.Delete($"/v1/friends/{id}");
                    if (StillOn(page)) FillFriendLists(page);
                }));
            }
        }
    }

    private HBoxContainer FriendRow(string name, string status)
    {
        HBoxContainer row = new HBoxContainer { CustomMinimumSize = new Vector2(MenuButtonWidth, 0) };
        row.AddThemeConstantOverride("separation", 8);
        VBoxContainer who = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        Label n = OverlayUi.MakeLabel(name, 20, OverlayUi.Ink);
        n.HorizontalAlignment = HorizontalAlignment.Left;
        n.ClipText = true;
        who.AddChild(n);
        if (status != null)
        {
            Label s = OverlayUi.MakeLabel(status, 14, status == "Online" ? OverlayUi.AccentDeep : OverlayUi.Muted);
            s.HorizontalAlignment = HorizontalAlignment.Left;
            who.AddChild(s);
        }
        row.AddChild(who);
        _friendLists.AddChild(row);
        return row;
    }

    private static Button SmallButton(string text, Action pressed, bool primary = false)
    {
        Button b = new Button { Text = text, CustomMinimumSize = new Vector2(text.Length <= 3 ? 64 : 120, 52) };
        b.AddThemeFontSizeOverride("font_size", 20);
        if (primary) OverlayUi.StyleButton(b, primary: true);
        b.Pressed += pressed;
        return b;
    }

    private static bool LooksLikeCode(string text)
    {
        string t = text.Replace("-", "").Replace(" ", "");
        if (t.Length != 8) return false;
        foreach (char c in t) if (!char.IsLetterOrDigit(c)) return false;
        return true;
    }

    private static string FriendResultText(string result) => result switch
    {
        "requested" => "Friend request sent.",
        "accepted" => "You're now friends!",
        "already_friends" => "You're already friends.",
        "already_requested" => "Request already sent.",
        _ => "Done.",
    };

    // ------------------------------------------------------------------
    // One friend: challenge, remove, block, report
    // ------------------------------------------------------------------

    private void FillFriendActions(string id, string name, bool isFriend)
    {
        int page = NewOnlinePage(name);
        AddOnlineStatus(string.Empty);
        if (isFriend) AddMenuButton("Challenge", null, () => FillChallenge(id, name));

        bool removeArmed = false, blockArmed = false;
        Button remove = null, block = null;
        if (isFriend)
        {
            remove = AddMenuButton("Remove Friend", null, async () =>
            {
                if (!removeArmed) { removeArmed = true; remove.Text = "Remove - tap again to confirm"; return; }
                await Svc.Delete($"/v1/friends/{id}");
                if (StillOn(page)) FillFriends();
            });
        }
        block = AddMenuButton("Block", "They can't find you, add you, challenge you or be matched with you.", async () =>
        {
            if (!blockArmed) { blockArmed = true; block.Text = "Block - tap again to confirm"; return; }
            await Svc.Post($"/v1/blocks/{id}");
            if (StillOn(page)) FillFriends();
        });
        AddMenuButton("Report Name", null, async () =>
        {
            OnlineService.ApiResult r = await Svc.Post($"/v1/reports/{id}", new { reason = "name" });
            if (StillOn(page)) SetOnlineStatus(r.Ok ? "Reported. Thank you." : r.Problem, !r.Ok);
        });
        AddMenuButton("Back", null, FillFriends);
    }

    private async void FillBlocked()
    {
        int page = NewOnlinePage("Blocked Players");
        AddOnlineStatus("Loading...");
        _friendLists = new VBoxContainer();
        _friendLists.AddThemeConstantOverride("separation", 10);
        _startMenuBox.AddChild(_friendLists);
        AddMenuButton("Back", null, FillFriends);

        OnlineService.ApiResult r = await Svc.Get("/v1/blocks");
        if (!StillOn(page)) return;
        if (!r.Ok) { SetOnlineStatus(r.Problem, warning: true); return; }
        SetOnlineStatus(r.Body.GetArrayLength() == 0 ? "Nobody is blocked." : string.Empty);
        foreach (JsonElement p in r.Body.EnumerateArray())
        {
            string id = OnlineService.Str(p, "id");
            HBoxContainer row = FriendRow(OnlineService.Str(p, "name"), null);
            row.AddChild(SmallButton("Unblock", async () =>
            {
                await Svc.Delete($"/v1/blocks/{id}");
                if (StillOn(page)) FillBlocked();
            }));
        }
    }

    // ------------------------------------------------------------------
    // Challenges
    // ------------------------------------------------------------------

    private async void FillChallenge(string id, string name)
    {
        int page = NewOnlinePage("Challenge");
        AddOnlineStatus($"Asking {name}...");
        AddOnlineNote("First to win 3 sets. 30 seconds a turn.");
        AddMenuButton("Cancel", null, () =>
        {
            Svc?.SendMessage(new { type = "cancelInvite" });
            FillFriends();
        });

        string problem = await Svc.OpenSocket();
        if (!StillOn(page)) return;
        if (problem != null) { SetOnlineStatus(problem, warning: true); return; }

        void Send()
        {
            if (!StillOn(page)) return;
            if (Svc.SendMessage(new { type = "invite", to = id, deck = MyOnlineDeck() })) SetOnlineStatus($"Waiting for {name} to answer...");
        }
        if (Svc.SocketReady) Send();
        else _onlineQueueOnReady = Send;
    }

    /// The 12-card deck from the deck screen, as card names for the server to check and build
    /// (server: OnlineDeck). Null - a random hand - until a full deck has been confirmed.
    private static string[] MyOnlineDeck()
    {
        RunData run = RunData.Instance;
        if (run == null || run.SideDeck.Count != RunData.SideDeckSize) return null;
        string[] keys = new string[RunData.SideDeckSize];
        for (int i = 0; i < keys.Length; i++)
        {
            int index = run.SideDeck[i];
            if (index < 0 || index >= run.Inventory.Count) return null;
            keys[i] = run.Inventory[index].LogKey;
            if (keys[i] == null) return null;
        }
        return keys;
    }

    private void FillInvited(string inviteId, string from)
    {
        _pendingInvite = inviteId;
        NewOnlinePage("Challenge!");
        _startMenuBox.AddChild(OverlayUi.MakeLabel($"{from} wants to play", 28, OverlayUi.Ink));
        AddOnlineStatus(string.Empty);
        Button accept = AddMenuButton("Play", null, () =>
        {
            Svc?.SendMessage(new { type = "inviteReply", inviteId, accept = true, deck = MyOnlineDeck() });
            SetOnlineStatus("Starting...");
        });
        OverlayUi.StyleButton(accept, primary: true);
        AddMenuButton("Not Now", null, () =>
        {
            Svc?.SendMessage(new { type = "inviteReply", inviteId, accept = false });
            _pendingInvite = null;
            FillOnline();
        });
    }

    // ------------------------------------------------------------------
    // The endless leaderboard
    // ------------------------------------------------------------------

    private async void FillLeaderboard()
    {
        int page = NewOnlinePage("Endless Leaderboard");
        AddOnlineNote("Longest Endless streaks, from every player.");
        AddOnlineStatus("Loading...");

        // This phone's best goes up first, so the board you are about to read includes you.
        await Svc.SubmitEndlessBest(RunData.Instance?.EndlessBest ?? 0);
        OnlineService.ApiResult r = await Svc.Get("/v1/leaderboard/endless");
        if (!StillOn(page)) return;
        if (!r.Ok) { SetOnlineStatus(r.Problem, warning: true); AddMenuButton("Back", null, FillOnline); return; }

        JsonElement top = r.Body.GetProperty("top");
        SetOnlineStatus(top.GetArrayLength() == 0 ? "No streaks yet. Clear the ladder to unlock Endless." : string.Empty);
        foreach (JsonElement row in top.EnumerateArray())
            AddLeaderRow(OnlineService.Int(row, "rank"), OnlineService.Str(row, "name"), OnlineService.Int(row, "streak"),
                         OnlineService.Bool(row, "you"));

        JsonElement you = r.Body.GetProperty("you");
        int myRank = OnlineService.Int(you, "rank");
        _startMenuBox.AddChild(MenuSpacer());
        AddOnlineNote(myRank > 0 ? $"You: #{myRank}, best streak {OnlineService.Int(you, "streak")}"
                                 : "You're not on the board yet.", OverlayUi.Ink, 20);
        AddMenuButton("Back", null, FillOnline);
    }

    private void AddLeaderRow(int rank, string name, int streak, bool you)
    {
        HBoxContainer row = new HBoxContainer { CustomMinimumSize = new Vector2(MenuButtonWidth, 0) };
        row.AddThemeConstantOverride("separation", 10);
        Color ink = you ? OverlayUi.AccentDeep : rank == 1 ? OverlayUi.MedalGold : OverlayUi.Ink;
        Label place = OverlayUi.MakeLabel($"{rank}.", 20, OverlayUi.Muted);
        place.CustomMinimumSize = new Vector2(48, 0);
        place.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(place);
        Label who = OverlayUi.MakeLabel(name, 20, ink);
        who.HorizontalAlignment = HorizontalAlignment.Left;
        who.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        who.ClipText = true;
        row.AddChild(who);
        Label score = OverlayUi.MakeLabel($"{streak}", 24, ink);
        score.CustomMinimumSize = new Vector2(56, 0);
        score.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(score);
        _startMenuBox.AddChild(row);
    }
}
