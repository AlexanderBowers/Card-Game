using System;
using System.Linq;
using Xunit;

/// user://run.json: what is written comes back, and every older save still loads.
public class SaveFormatTests
{
    /// A real save written by the Godot-Json version of the game (version 10, early ladder).
    private const string GodotWrittenV10 =
        "{\"active\":true,\"board\":\"classic\",\"cardsMet\":[\"+1\",\"+2\",\"+3\",\"+4\",\"-1\",\"-2\",\"-3\",\"-4\"],"
      + "\"collectorBack\":false,\"deck\":\"classic\",\"endless\":false,\"endlessBanked\":false,\"endlessBest\":0,"
      + "\"endlessScores\":[],\"endlessStreak\":0,\"furthest\":0,\"inventory\":["
      + "{\"effect\":0,\"flip\":false,\"value\":1},{\"effect\":0,\"flip\":false,\"value\":1},"
      + "{\"effect\":0,\"flip\":false,\"value\":2},{\"effect\":0,\"flip\":false,\"value\":2},"
      + "{\"effect\":0,\"flip\":false,\"value\":3},{\"effect\":0,\"flip\":false,\"value\":3},"
      + "{\"effect\":0,\"flip\":false,\"value\":4},{\"effect\":0,\"flip\":false,\"value\":-1},"
      + "{\"effect\":0,\"flip\":false,\"value\":-1},{\"effect\":0,\"flip\":false,\"value\":-2},"
      + "{\"effect\":0,\"flip\":false,\"value\":-2},{\"effect\":0,\"flip\":false,\"value\":-3},"
      + "{\"effect\":0,\"flip\":false,\"value\":-3},{\"effect\":0,\"flip\":false,\"value\":-4}],"
      + "\"matchRescueUsed\":false,\"medals\":0,\"ownedBoards\":[\"classic\",\"bronze\"],"
      + "\"ownedDecks\":[\"classic\",\"bronze\"],\"rolledEffects\":[],\"rolledStep\":-1,\"rolledTarget\":0,"
      + "\"sideDeck\":[0,1,2,3,4,5,6,7,8,9,10,11],\"step\":0,\"tutorialSeen\":true,\"version\":10}";

    /// A real version 3 save from the CardGame2 days: no cosmetics, no log, no endless keys.
    private const string GodotWrittenV3 =
        "{\"active\":false,\"furthest\":3,\"inventory\":["
      + "{\"effect\":0,\"flip\":false,\"value\":1},{\"effect\":0,\"flip\":false,\"value\":1},"
      + "{\"effect\":0,\"flip\":false,\"value\":2},{\"effect\":0,\"flip\":false,\"value\":2},"
      + "{\"effect\":0,\"flip\":false,\"value\":3},{\"effect\":0,\"flip\":false,\"value\":3},"
      + "{\"effect\":0,\"flip\":false,\"value\":4},{\"effect\":0,\"flip\":false,\"value\":-1},"
      + "{\"effect\":0,\"flip\":false,\"value\":-1},{\"effect\":0,\"flip\":false,\"value\":-2},"
      + "{\"effect\":0,\"flip\":false,\"value\":-2},{\"effect\":0,\"flip\":false,\"value\":-3},"
      + "{\"effect\":0,\"flip\":false,\"value\":-3},{\"effect\":0,\"flip\":false,\"value\":-4},"
      + "{\"effect\":0,\"flip\":true,\"value\":1},{\"effect\":0,\"flip\":true,\"value\":2},"
      + "{\"effect\":0,\"flip\":true,\"value\":1}],"
      + "\"medals\":6,\"sideDeck\":[3,4,5,6,8,9,10,11,0,15,14,16],\"step\":3,\"version\":3}";

    private static RunData Load(string json)
    {
        var run = new RunData(new Random(1));
        Assert.True(run.LoadSaveJson(json));
        return run;
    }

    [Fact]
    public void The_market_and_flip_lessons_survive_a_save_and_old_saves_have_neither()
    {
        RunData old = Load(GodotWrittenV10);
        Assert.False(old.MarketLessonSeen);
        Assert.False(old.FlipLessonPending);

        old.CompleteMarketLesson(flipCardInDeck: true);
        RunData back = Load(old.ToSaveJson());
        Assert.True(back.MarketLessonSeen);
        Assert.True(back.FlipLessonPending);

        back.CompleteFlipLesson();
        Assert.False(Load(back.ToSaveJson()).FlipLessonPending);

        // Replay the tutorial: the Modifier Shop lesson comes round again.
        back.CompleteMarketLesson(flipCardInDeck: true);
        back.ReplayTutorial();
        Assert.False(back.MarketLessonSeen);
        Assert.False(back.FlipLessonPending);
    }

    /// A profile with something in every field the save holds.
    private static RunData BusyProfile()
    {
        var run = new RunData(new Random(5)) { Clock = () => 1_700_000_000 };
        run.StartNewRun();
        run.MarkTutorialSeen();
        run.StartEndless();
        run.CompleteMatch(3, won: true);
        run.CompleteMatch(3, won: true);
        run.CompleteMatch(0, won: false);    // banks a streak of 2
        run.StartNewRun();
        run.DebugJumpToStep(Ladder.EndlessStepIndex); // the finale, rolled
        run.AddToInventory(new ModifierDef(0, false, CardEffect.Veto));
        run.AddToInventory(new ModifierDef(5, canFlipValue: true));
        run.SetSideDeck(Enumerable.Range(3, RunData.SideDeckSize));
        run.CompleteMatch(3, won: true);      // beats stage 10: medals, endless unlocked
        run.StartNewRun();
        run.DebugJumpToStep(Ladder.EndlessStepIndex);
        run.BuyCosmetic("bronze", board: true);
        run.UseMatchRescue();
        return run;
    }

    [Fact]
    public void A_save_loads_back_to_the_same_profile_and_writes_the_same_text()
    {
        RunData original = BusyProfile();
        string json = original.ToSaveJson();
        RunData loaded = Load(json);

        Assert.Equal(json, loaded.ToSaveJson());

        Assert.Equal(original.RunActive, loaded.RunActive);
        Assert.Equal(original.Medals, loaded.Medals);
        Assert.Equal(original.StepIndex, loaded.StepIndex);
        Assert.Equal(original.FurthestStep, loaded.FurthestStep);
        Assert.True(loaded.TutorialSeen);
        Assert.True(loaded.MatchRescueUsed);
        Assert.Equal(original.EndlessBest, loaded.EndlessBest);
        Assert.Equal(original.EndlessScores, loaded.EndlessScores);
        Assert.Equal(original.Inventory, loaded.Inventory);
        Assert.Equal(original.SideDeck, loaded.SideDeck);
        Assert.Equal(original.CardsMet.OrderBy(k => k), loaded.CardsMet.OrderBy(k => k));
        Assert.Equal(original.OwnedBoards.OrderBy(k => k), loaded.OwnedBoards.OrderBy(k => k));
        Assert.Equal("bronze", loaded.SelectedBoard);
        Assert.Equal(original.CurrentTarget, loaded.CurrentTarget);
        Assert.Equal(original.CurrentRolledEffects, loaded.CurrentRolledEffects);
    }

    [Fact]
    public void A_save_written_by_the_Godot_Json_version_loads_unchanged()
    {
        RunData run = Load(GodotWrittenV10);
        Assert.True(run.RunActive);
        Assert.True(run.TutorialSeen);
        Assert.Equal(14, run.Inventory.Count);
        Assert.Equal(12, run.SideDeck.Count);
        Assert.Contains("bronze", run.OwnedDecks);
        Assert.Equal(Cosmetics.Default, run.SelectedDeck);
        Assert.Equal(-1, run.RolledStep);
    }

    [Fact]
    public void A_version_3_save_keeps_its_cards_and_meets_everything_it_owns()
    {
        RunData run = Load(GodotWrittenV3);
        Assert.False(run.RunActive);
        Assert.Equal(6, run.Medals);
        Assert.Equal(3, run.FurthestStep);
        Assert.False(run.TutorialSeen);
        Assert.Equal(17, run.Inventory.Count);
        Assert.Equal(3, run.Inventory.Count(d => d.CanFlipValue));
        Assert.Contains("flip2", run.CardsMet);
        Assert.Equal(new[] { Cosmetics.Default }, run.OwnedDecks);
        Assert.Empty(run.EndlessScores);
        Assert.False(run.CollectorBack);
    }

    [Fact]
    public void A_version_9_save_keeps_the_Bronze_set_it_was_given_but_moves_onto_Classic()
    {
        RunData run = Load("{\"version\":9,\"ownedDecks\":[\"classic\"],\"deck\":\"bronze\",\"board\":\"bronze\"}");
        Assert.Contains("bronze", run.OwnedDecks);
        Assert.Contains("bronze", run.OwnedBoards);
        Assert.Equal(Cosmetics.Default, run.SelectedDeck);
        Assert.Equal(Cosmetics.Default, run.SelectedBoard);
    }

    [Fact]
    public void Numbers_written_as_doubles_still_load()
    {
        RunData run = Load("{\"version\":10.0,\"medals\":12.0,\"step\":2.0,\"active\":false,"
                         + "\"inventory\":[{\"value\":-3.0,\"flip\":false,\"effect\":0.0}]}");
        Assert.Equal(12, run.Medals);
        Assert.Equal(2, run.StepIndex);
        Assert.Equal(new ModifierDef(-3), run.Inventory.Single());
    }

    [Fact]
    public void A_scrapped_Push_in_a_saved_collection_loads_as_Copy()
    {
        RunData run = Load($"{{\"inventory\":[{{\"value\":3,\"effect\":{(int)CardEffect.Push}}}]}}");
        Assert.Equal(new ModifierDef(0, false, CardEffect.Copy), run.Inventory.Single());
    }

    [Fact]
    public void An_endless_save_is_parked_on_stage_ten()
    {
        RunData run = Load("{\"endless\":true,\"step\":2,\"endlessStreak\":4}");
        Assert.Equal(Ladder.EndlessStepIndex, run.StepIndex);
        Assert.Equal(4, run.EndlessStreak);
    }

    [Fact]
    public void A_run_that_lost_its_cards_loads_as_no_run()
    {
        RunData run = Load("{\"active\":true,\"inventory\":[{\"value\":2}],\"sideDeck\":[5,9]}");
        Assert.Single(run.Inventory);
        Assert.Empty(run.SideDeck);
        Assert.False(run.RunActive);
    }

    [Fact]
    public void A_half_written_finale_roll_is_dropped_to_be_rolled_again()
    {
        RunData run = Load("{\"step\":9,\"rolledStep\":9,\"rolledTarget\":23,\"rolledEffects\":[999]}");
        Assert.Equal(-1, run.RolledStep);
        Assert.Empty(run.RolledEffects);
    }

    [Fact]
    public void The_scoreboard_loads_sorted_and_without_empty_rows()
    {
        RunData run = Load("{\"endlessScores\":[{\"streak\":2,\"at\":5},{\"streak\":0,\"at\":9},"
                         + "{\"streak\":7,\"at\":1},\"junk\"]}");
        Assert.Equal(new[] { 7, 2 }, run.EndlessScores.Select(s => s.Streak));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    public void Text_that_is_not_a_save_is_refused_and_changes_nothing(string text)
    {
        RunData run = BusyProfile();
        string before = run.ToSaveJson();
        Assert.False(run.LoadSaveJson(text));
        Assert.Equal(before, run.ToSaveJson());
    }

    [Fact]
    public void Wrongly_typed_keys_read_as_their_defaults()
    {
        RunData run = Load("{\"medals\":\"lots\",\"active\":\"yes\",\"cardsMet\":{\"a\":1},\"deck\":7}");
        Assert.Equal(0, run.Medals);
        Assert.False(run.RunActive);
        Assert.Empty(run.CardsMet);
        Assert.Equal(Cosmetics.Default, run.SelectedDeck);
    }
}
