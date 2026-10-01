using Godot;
using System;

/// The words that float over the table: one status toast per player (what a picked-up effect
/// card would do, "Just recalled"...) and the effect banner in the middle (what a card just did).
///
/// The rules write the words (ITableUiHost.StatusFor, CardEffects); this only decides where they
/// sit, what colour they are and how long they stay.
public sealed class Toasts
{
    private readonly ITableUiHost _host;
    private readonly TableUi _ui;

    /// For the timers and the viewport - never asked a question about the game.
    private readonly Node _root;

    public Toasts(ITableUiHost host, TableUi ui, Node root)
    {
        _host = host;
        _ui = ui;
        _root = root;
    }

    private TableLayout L => _ui.Layout;
    private bool IsMirrored => _ui.IsMirrored;

    /// A call put off to the end of the frame, and dropped if the table has gone away in between.
    private void Defer(Action action) =>
        Callable.From(() => { if (GodotObject.IsInstanceValid(_root)) action(); }).CallDeferred();

    private Label _effectBanner => L?.EffectLabel;
    private PanelContainer _effectToast => L?.EffectToast;

    private Tween _effectFade;

    private uint _effectBannerToken;   // so a stale timer never wipes a newer message

    // ------------------------------------------------------------------
    // Status toasts (pass 36)
    //
    // The messages about a side - what a picked-up effect card would do, "Just recalled",
    // "Holding" - float over the middle of the table instead of taking a spot beside the score.
    // One toast per player, copied from the layout's EffectToast so they share its look: Player
    // 1's just below the middle, Player 2's just above (turned round with P2's side when
    // mirrored). A message about a picked-up card stays while the card is picked up; any other
    // message shows for a moment and fades.
    // ------------------------------------------------------------------
    public PanelContainer P1StatusToast => _p1StatusToast;
    public PanelContainer P2StatusToast => _p2StatusToast;

    private PanelContainer _p1StatusToast;
    private PanelContainer _p2StatusToast;
    private readonly string[] _statusShown = new string[2];
    private readonly uint[] _statusToken = new uint[2];

    private const float StatusToastSeconds = 2.5f;

    public void BuildStatusToasts()
    {
        _statusShown[0] = _statusShown[1] = null;
        _statusToken[0]++;
        _statusToken[1]++;
        _p1StatusToast = MakeStatusToast("P1StatusToast");
        _p2StatusToast = MakeStatusToast("P2StatusToast");
    }

    private PanelContainer MakeStatusToast(string name)
    {
        if (L.EffectToast == null) return null;
        PanelContainer toast = (PanelContainer)L.EffectToast.Duplicate();
        toast.Name = name;
        toast.TopLevel = true;
        toast.Visible = false;
        L.EffectToast.GetParent().AddChild(toast);
        return toast;
    }

    private static Label ToastLabel(Control toast)
    {
        foreach (Node child in toast.GetChildren())
            if (child is Label label) return label;
        return null;
    }

    public void UpdateStatusToast(int index, Player player, PanelContainer toast)
    {
        if (toast == null || player == null) return;
        Label label = ToastLabel(toast);
        if (label == null) return;

        string text = _host.StatusFor(player);
        if (string.IsNullOrEmpty(text))
        {
            if (_statusShown[index] != null)
            {
                _statusToken[index]++;
                toast.Visible = false;
            }
            _statusShown[index] = null;
            return;
        }

        bool lasting = _host.SelectedFor(player) != null; // about the card in hand: stays while held
        bool changed = text != _statusShown[index];
        _statusShown[index] = text;
        if (!changed && !lasting) return; // a timed message already had its moment

        label.Text = text;
        ApplyStatusColor(label, player);
        toast.Modulate = Colors.White;
        toast.Visible = true;
        PlaceStatusToast(toast, index == 1);
        Defer(() => PlaceStatusToast(toast, index == 1)); // again once the text has been shaped

        if (changed)
        {
            uint token = ++_statusToken[index];
            if (!lasting) FadeStatusToastLater(toast, index, token);
        }
    }

    private async void FadeStatusToastLater(PanelContainer toast, int index, uint token)
    {
        await _root.ToSignal(_root.GetTree().CreateTimer(StatusToastSeconds), SceneTreeTimer.SignalName.Timeout);
        if (!GodotObject.IsInstanceValid(toast) || token != _statusToken[index]) return;

        Tween fade = toast.CreateTween();
        fade.TweenProperty(toast, "modulate:a", 0f, 0.35f);
        await _root.ToSignal(fade, Tween.SignalName.Finished);
        if (!GodotObject.IsInstanceValid(toast) || token != _statusToken[index]) return;
        toast.Visible = false;
    }

    public void PlaceStatusToast(PanelContainer toast, bool playerTwo)
    {
        if (toast == null || !toast.Visible || !toast.IsInsideTree() || L?.Canvas == null) return;
        Label label = ToastLabel(toast);
        Vector2 vp = _root.GetViewport().GetVisibleRect().Size;
        float width = Mathf.Min(520f, vp.X - 32f);
        if (label != null) label.CustomMinimumSize = new Vector2(Mathf.Max(80f, width - 40f), 0f);
        toast.ResetSize();
        Vector2 size = toast.GetCombinedMinimumSize();
        toast.Size = size;

        // Clear of the effect banner, which sits on the middle itself.
        Vector2 centre = L.Canvas.GetGlobalRect().GetCenter();
        float offset = size.Y / 2f + 70f;
        Vector2 at = centre + new Vector2(-size.X / 2f, (playerTwo ? -offset : offset) - size.Y / 2f);
        at.X = Mathf.Clamp(at.X, 16f, Mathf.Max(16f, vp.X - size.X - 16f));
        at.Y = Mathf.Clamp(at.Y, 16f, Mathf.Max(16f, vp.Y - size.Y - 16f));
        toast.GlobalPosition = at;

        // Player 2's reads from the other side of the table when the side is turned round.
        toast.PivotOffset = size / 2f;
        toast.RotationDegrees = (playerTwo && IsMirrored) ? 180f : 0f;
    }

    /// Colours the status line for a picked-up EFFECT card (green: playable, red: not). A plain
    /// Modifier's preview is on the score line instead (SetScoreLines).
    private void ApplyStatusColor(Label label, Player player)
    {
        if (label == null) return;

        Card picked = _host.SelectedFor(player);
        if (picked == null)
        {
            label.RemoveThemeColorOverride("font_color");
            return;
        }

        // An effect card is not added to this player's score, so "would this bust me" is the wrong
        // question: colour it by whether it can be played at all.
        if (picked.Effect != CardEffect.None)
        {
            label.AddThemeColorOverride("font_color", _host.CanPlayEffect(player, picked)
                ? new Color(0.55f, 0.95f, 0.60f)
                : new Color(1f, 0.45f, 0.42f));
            return;
        }

        // A plain Modifier's preview lives on the score line (ScorePreviewColor), not here.
        label.RemoveThemeColorOverride("font_color");
    }

    /// Centres the floating banner on the middle panel. Its width is chosen here (autowrap needs
    /// one), and its height follows from the text.
    public void PlaceEffectToast()
    {
        if (_effectToast == null || _effectBanner == null || !_effectToast.Visible || !_effectToast.IsInsideTree()) return;
        Control column = L?.Canvas ?? _effectToast.GetParent() as Control;
        if (column == null) return;

        Vector2 vp = _root.GetViewport().GetVisibleRect().Size;
        float width = Mathf.Min(560f, vp.X - 32f);
        _effectBanner.CustomMinimumSize = new Vector2(Mathf.Max(80f, width - 40f), 0f);
        _effectToast.ResetSize(); // shrink back to the new text before measuring
        Vector2 size = _effectToast.GetCombinedMinimumSize();
        _effectToast.Size = size;

        Vector2 centre = column.GetGlobalRect().GetCenter();
        Vector2 at = centre - size / 2f;
        at.X = Mathf.Clamp(at.X, 16f, Mathf.Max(16f, vp.X - size.X - 16f));
        at.Y = Mathf.Clamp(at.Y, 16f, Mathf.Max(16f, vp.Y - size.Y - 16f));
        _effectToast.GlobalPosition = at;
    }


    /// What an effect card just did, on the table. The explanation already existed - CardEffects
    /// writes one for every card it resolves - but it only ever went to the log, so at the table
    /// a score simply changed and nothing said why.
    public async void ShowEffectBanner(string text)
    {
        if (_effectBanner == null || string.IsNullOrEmpty(text)) return;

        uint token = ++_effectBannerToken;
        _effectBanner.Text = text;
        if (_effectToast != null)
        {
            _effectFade?.Kill(); // an older banner's fade must not carry on into this one
            _effectToast.Modulate = Colors.White;
            _effectToast.Visible = true;
            PlaceEffectToast();
            Defer(PlaceEffectToast); // again once the new text has been shaped
        }

        await _root.ToSignal(_root.GetTree().CreateTimer(5.0f), SceneTreeTimer.SignalName.Timeout);
        if (!_root.IsInsideTree() || token != _effectBannerToken) return; // a newer card owns the line

        // A short fade rather than a blink, so a player who was looking elsewhere sees it leave.
        if (_effectToast != null)
        {
            Tween fade = _effectFade = _effectToast.CreateTween();
            fade.TweenProperty(_effectToast, "modulate:a", 0f, 0.35f);
            await _root.ToSignal(fade, Tween.SignalName.Finished);
            if (!_root.IsInsideTree() || token != _effectBannerToken) return;
            _effectToast.Visible = false;
        }
        _effectBanner.Text = string.Empty;
    }

    public void ClearEffectBanner()
    {
        _effectBannerToken++;
        if (_effectBanner != null) _effectBanner.Text = string.Empty;
        _effectFade?.Kill();
        if (_effectToast != null) _effectToast.Visible = false;
    }
}
