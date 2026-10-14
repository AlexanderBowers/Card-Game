using System;
using System.Text;
using Xunit;

/// The cloud save's blob and its "which one is further along" rule (CloudSave, 2026-10-09).
public class CloudBundleTests
{
    private static RunData NewRun(int seed = 1)
    {
        RunData run = new RunData(new Random(seed)) { Clock = () => 1_000 };
        run.StartNewRun();
        return run;
    }

    [Fact]
    public void A_profile_and_its_online_account_round_trip_through_the_bundle()
    {
        RunData run = NewRun();
        run.CompleteMatch(3, won: true);
        byte[] data = CloudBundle.Pack(run.ToSaveJson(), "acct-1", "s3cret");

        Assert.True(CloudBundle.TryUnpack(data, out string json, out string id, out string secret));
        Assert.Equal(run.ToSaveJson(), json);
        Assert.Equal("acct-1", id);
        Assert.Equal("s3cret", secret);

        RunData back = new RunData(new Random(2));
        Assert.True(back.LoadSaveJson(json));
        Assert.Equal(run.FurthestStep, back.FurthestStep);
    }

    [Fact]
    public void A_profile_without_an_online_account_carries_none()
    {
        byte[] data = CloudBundle.Pack(NewRun().ToSaveJson(), null, null);
        Assert.True(CloudBundle.TryUnpack(data, out _, out string id, out string secret));
        Assert.Null(id);
        Assert.Null(secret);
        Assert.DoesNotContain("online", Encoding.UTF8.GetString(data));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"v\":99,\"run\":\"{}\"}")]   // a newer game's bundle
    [InlineData("{\"v\":1}")]
    public void Anything_that_is_not_a_readable_bundle_reads_as_an_empty_cloud(string text)
    {
        byte[] data = text == null ? null : Encoding.UTF8.GetBytes(text);
        Assert.False(CloudBundle.TryUnpack(data, out _, out _, out _));
    }

    [Fact]
    public void The_save_further_up_the_ladder_wins_whatever_the_medals()
    {
        RunData far = NewRun(), near = NewRun(2);
        for (int i = 0; i < 12; i++) far.CompleteMatch(3, won: true);
        far.SpendMedals(far.Medals);                       // spent everything
        for (int i = 0; i < 3; i++) near.CompleteMatch(3, won: true);

        Assert.True(CloudBundle.Compare(far, near) > 0);
        Assert.True(CloudBundle.Compare(near, far) < 0);
        Assert.True(CloudBundle.ProgressValue(far) > CloudBundle.ProgressValue(near));
    }

    [Fact]
    public void Two_copies_of_the_same_profile_are_level()
    {
        RunData run = NewRun();
        run.CompleteMatch(3, won: true);
        RunData copy = new RunData(new Random(9));
        copy.LoadSaveJson(run.ToSaveJson());
        Assert.Equal(0, CloudBundle.Compare(run, copy));
    }

    [Fact]
    public void A_fresh_phone_is_behind_any_played_profile()
    {
        RunData fresh = new RunData(new Random(3));
        RunData played = NewRun();
        played.AddToInventory(new ModifierDef(5));
        Assert.True(CloudBundle.Compare(played, fresh) > 0);
    }

    [Fact]
    public void Replacing_a_profile_writes_it_and_refuses_junk()
    {
        RunData cloud = NewRun();
        for (int i = 0; i < 5; i++) cloud.CompleteMatch(3, won: true);

        RunData phone = NewRun(4);
        int saves = 0;
        phone.Changed += () => saves++;
        Assert.False(phone.ReplaceFrom("not json"));
        Assert.Equal(0, saves);

        Assert.True(phone.ReplaceFrom(cloud.ToSaveJson()));
        Assert.Equal(1, saves);
        Assert.Equal(5, phone.FurthestStep);
    }

    [Fact]
    public void The_summary_names_the_climb_medals_and_cards()
    {
        RunData run = NewRun();
        for (int i = 0; i < 22; i++) run.CompleteMatch(3, won: true);
        string line = CloudBundle.Summary(run);
        Assert.StartsWith("Stage 23 reached", line);
        Assert.Contains($"{run.Medals} medals", line);
        Assert.Contains($"{run.Inventory.Count} cards", line);
    }
}
