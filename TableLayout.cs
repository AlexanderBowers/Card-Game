using Godot;
using System.Collections.Generic;

/// <summary>
/// The root of a table LAYOUT scene - table_landscape.tscn or table_portrait.tscn (pass 34).
///
/// A layout scene is only a picture: every node the table paints into, placed and sized in the
/// editor exactly as it appears in the game. It holds no rules and no state. GameManager (in
/// table_scene.tscn) instances whichever layout fits the screen under its LayoutHost, hands it to
/// TableUi, and swaps it for the other one when the phone is rotated - the match carries on,
/// because the match lives in GameManager and not here.
///
/// So to change how the table looks, open the layout scene and move things. Since pass 35 nothing
/// big is in a container: both sides, the board, the hand, the score box, the buttons, the deck
/// and the Menu button are each positioned freely on the Canvas - drag and resize them in the 2D
/// view. Only the small groups that arrange REPEATED things are containers (the 3x3 grid, the
/// hand row, the chips, the lines inside the score box), and those you still move
/// and size as one piece. The rules to keep:
///  - Every exported node below must exist (TableUi reads them; a missing one is skipped, not a
///    crash, but that part of the table then does nothing).
///  - A board is a GridContainer of 9 slot Panels. Their custom_minimum_size IS the board card size.
///  - Anything in the "editor_preview" group (sample hand cards, sample board cards) is only there
///    so the scene looks like a game in the editor. It is deleted the moment the game loads it.
///  - Solo and local 2-player share one layout: in solo, Player 2's button slot is hidden.
/// </summary>
[GlobalClass]
public partial class TableLayout : Control
{
    /// Which orientation this layout is for. TableUi picks the layout by this, and a few behaviours
    /// differ (portrait's 3x2 board, Flip Value standing in for Wins in the bar).
    [Export] public bool Portrait;

    /// The size of an ordinary card off the board: the one that flies from the deck, and the base
    /// the shop and deck screens scale from.
    [Export] public Vector2 CardSize = new Vector2(84, 114);

    /// The size of a card in either hand. The hand is rebuilt every refresh, so its size cannot be
    /// read off a node the way a board slot's can.
    [Export] public Vector2 HandCardSize = new Vector2(84, 114);

    /// On: Play and Put back are put exactly where Draw Card and Hold are (and sized the same),
    /// so only ActionRow's buttons need arranging - rearrange those and the pair that stands in
    /// for them follows. Off: ConfirmRow's buttons stay where you put them in the scene (the
    /// landscape default, because its ConfirmRow also has Flip Value to fit in).
    [Export] public bool ConfirmCopiesAction = true;

    /// How far a picked-up hand card rises (pass 40). Portrait crops the hand off the bottom of
    /// the screen, Pocket-style; a picked-up card lifts by about the cropped part, so the whole
    /// card shows while you decide. 0 = no lift.
    [Export] public float HandLiftOnPick;

    /// Pass 40: each side's score is a badge beside its own board instead of a "You  11/20" box
    /// (portrait). In face-to-face play the badge grows a second end, turned round, so the player
    /// across the table reads it the right way up too - a playing card's two corner indices,
    /// worked the same way. Uses ScoreValue / ScoreTarget and the ScoreFar* nodes below.
    [Export] public bool ScoreBadges;

    /// The design canvas every element is placed on (720x1560 portrait, 1560x720 landscape). The
    /// whole canvas is scaled to fit the screen and centred on it; the Background fills the rest.
    /// Keep everything inside it, or it can end up off the edge of a phone.
    [Export] public Control Canvas;

    [Export] public TextureRect Background;

    [ExportGroup("Player 1 (bottom / left)")]
    [Export] public Control P1ScoreBox;
    [Export] public Label P1ScorePrefix;
    [Export] public Label P1ScoreValue;
    [Export] public Label P1ScoreThem;
    /// Score badge (ScoreBadges): the small "/20" under the number.
    [Export] public Label P1ScoreTarget;
    /// Score badge, face to face only: the end turned round for the player across the table,
    /// and the line between the two ends.
    [Export] public Control P1ScoreFarEnd;
    [Export] public Label P1ScoreFarValue;
    [Export] public Label P1ScoreFarTarget;
    [Export] public Control P1ScoreDivider;
    /// The OTHER player's score, beside this player's own ("Them  14"). Pass 36: this spot used to
    /// hold the status line; its messages now float as a toast.
    [Export] public Label P1OpponentScore;
    /// The box around it, styled like the player's own score box (and red the same way).
    [Export] public Control P1OpponentBox;
    [Export] public Control P1WinsRow;
    [Export] public Label P1WinsLabel;
    [Export] public Container P1Chips;
    [Export] public GridContainer P1Board;  // a PerspectiveBoard in both scenes (pass 39)
    [Export] public Container P1Hand;
    [Export] public Control P1ButtonSlot;
    [Export] public Control P1ActionRow;
    [Export] public Button P1DrawCard;
    [Export] public Button P1Hold;
    [Export] public Control P1ConfirmRow;
    [Export] public Button P1Play;
    [Export] public Button P1PutBack;
    [Export] public Button P1FlipValue;

    [ExportGroup("Player 2 (top / right)")]
    /// Player 2's whole side. Turned 180 degrees about its centre for face-to-face play; in
    /// landscape its children are also mirrored left-to-right, so that once turned the score still
    /// faces the middle of the table. In portrait without the mirror (against the bot) it is not
    /// turned but mirrored top-to-bottom instead (pass 40), so author it exactly like Player 1's
    /// side - as its own player sees it - and every mode comes out right.
    [Export] public Control P2Side;
    [Export] public Control P2ScoreBox;
    [Export] public Label P2ScorePrefix;
    [Export] public Label P2ScoreValue;
    [Export] public Label P2ScoreThem;
    /// Score badge (ScoreBadges): the small "/20" under the number.
    [Export] public Label P2ScoreTarget;
    /// Score badge, face to face only: the end turned round for the player across the table,
    /// and the line between the two ends.
    [Export] public Control P2ScoreFarEnd;
    [Export] public Label P2ScoreFarValue;
    [Export] public Label P2ScoreFarTarget;
    [Export] public Control P2ScoreDivider;
    /// The OTHER player's score, beside this player's own ("Them  14"). Pass 36: this spot used to
    /// hold the status line; its messages now float as a toast.
    [Export] public Label P2OpponentScore;
    /// The box around it, styled like the player's own score box (and red the same way).
    [Export] public Control P2OpponentBox;
    [Export] public Control P2WinsRow;
    [Export] public Label P2WinsLabel;
    [Export] public Container P2Chips;
    [Export] public GridContainer P2Board;  // a PerspectiveBoard in both scenes (pass 39)
    [Export] public Container P2Hand;
    [Export] public Control P2ButtonSlot;
    [Export] public Control P2ActionRow;
    [Export] public Button P2DrawCard;
    [Export] public Button P2Hold;
    [Export] public Control P2ConfirmRow;
    [Export] public Button P2Play;
    [Export] public Button P2PutBack;
    [Export] public Button P2FlipValue;

    [ExportGroup("Middle")]
    [Export] public Label SetInfoLabel;
    [Export] public Label TargetLabel;
    /// The face-down deck. In portrait it is turned on its side inside a holder (DeckFootprint).
    [Export] public TextureRect Deck;
    /// What the tutorial's spotlight frames for the deck: the holder in portrait, the deck itself
    /// in landscape.
    [Export] public Control DeckFootprint;
    [Export] public Button MenuButton;
    /// The effect banner. Top-level, so it floats over the middle without taking room in it.
    [Export] public PanelContainer EffectToast;
    [Export] public Label EffectLabel;

    public const string EditorPreviewGroup = "editor_preview";

    public override void _Ready()
    {
        if (Engine.IsEditorHint()) return;

        // The sample cards exist for the editor only. Detached at once rather than just queued,
        // so they are never measured as part of the hand or the board.
        List<Node> previews = new List<Node>();
        foreach (Node node in GetTree().GetNodesInGroup(EditorPreviewGroup))
            if (IsAncestorOf(node)) previews.Add(node);
        foreach (Node node in previews)
        {
            node.GetParent()?.RemoveChild(node);
            node.QueueFree();
        }
    }
}
