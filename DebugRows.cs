using Godot;
using System;
using System.Collections.Generic;

/// The debug rows in the table menu: stage jumps, a save wipe, and the monetisation and consent
/// switches (monetization-spec.md).
///
/// Behind OS.IsDebugBuild(), so it cannot ship: an exported build never builds these buttons.
/// Solo scene only - local 2-player has no run to jump around in. Options > Debug shows or hides
/// the rows Build returns.
public static class DebugRows
{
    /// Builds the rows into the menu's debug slot and returns them (empty in an exported build).
    /// The two actions are the game's: jumping a stage restarts into it, and a wiped save has no
    /// match left to go back to.
    public static List<Control> Build(Container column, Action<int> jumpStage, Action restartToMenu)
    {
        List<Control> rows = new List<Control>();
        if (!OS.IsDebugBuild() || column == null) return rows;

        HBoxContainer Row()
        {
            HBoxContainer row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            row.AddThemeConstantOverride("separation", 6);
            column.AddChild(row);
            rows.Add(row);
            row.Visible = GameSettings.ShowDebugButtons;   // Options > Debug
            return row;
        }

        // The run.
        HBoxContainer runRow = Row();
        runRow.AddChild(OverlayUi.MakeLabel("debug", 12, OverlayUi.Muted));
        AddButton(runRow, "< Stage", () => jumpStage(-1));
        AddButton(runRow, "Stage >", () => jumpStage(+1));
        AddButton(runRow, "Wipe Save", () =>
        {
            RunData.Instance?.DebugWipeSave();
            GD.Print("DEBUG: save wiped - collection, deck, medals and run are gone");
            restartToMenu();
        });

        // Monetization switches, on a second row so the first stays narrow.
        HBoxContainer adRow = Row();
        AddToggle(adRow, () => Prompts.DebugAlwaysRescue ? "Rescue 100%" : "Rescue 15%",
                  () => Prompts.DebugAlwaysRescue = !Prompts.DebugAlwaysRescue);
        AddToggle(adRow, () => AdService.DebugSimulateNoFill ? "Ads: no fill" : "Ads: fill",
                  () => AdService.DebugSimulateNoFill = !AdService.DebugSimulateNoFill);
        AddToggle(adRow, () => PurchaseService.OwnsNoAds ? "No Ads: owned" : "No Ads: not owned",
                  () => PurchaseService.DebugSetOwned(!PurchaseService.OwnsNoAds));

        // Consent testing, on a third row: both take effect on the NEXT launch, when consent is
        // gathered.
        HBoxContainer consentRow = Row();
        AddToggle(consentRow, () => AdMobBackend.DebugConsentEea ? "Consent: EEA test" : "Consent: real",
                  () => AdMobBackend.DebugConsentEea = !AdMobBackend.DebugConsentEea);
        AddButton(consentRow, "Reset consent", () =>
        {
            AdMobBackend.DebugResetConsent();
            GD.Print("DEBUG: consent reset - relaunch to be asked again");
        });

        return rows;
    }

    private static void AddButton(Container row, string text, Action pressed)
    {
        Button button = new Button { Text = text };
        button.Pressed += pressed;
        row.AddChild(button);
    }

    /// A button whose text says the switch's current state, and flips it when pressed.
    private static void AddToggle(Container row, Func<string> text, Action toggle)
    {
        Button button = new Button { Text = text() };
        button.Pressed += () => { toggle(); button.Text = text(); };
        row.AddChild(button);
    }
}
