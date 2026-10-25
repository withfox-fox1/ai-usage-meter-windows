using System.Text.Json;
using AIUsageMeter.Core;

namespace AIUsageMeter.Tests;

public class NotificationEvaluatorTests
{
    private static readonly DateTimeOffset Reset1 = new(2026, 10, 25, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset2 = Reset1.AddHours(5);

    private static UsageWindow W(double percent, DateTimeOffset? reset = null) => new(percent, reset ?? Reset1);

    [Fact]
    public void FirstObservation_DoesNotNotify_EvenIfAbove()
    {
        var state = new ThresholdState();

        Assert.Empty(NotificationEvaluator.Evaluate(state, W(85)));
        Assert.Empty(NotificationEvaluator.Evaluate(state, W(88)));
        Assert.Equal([80], state.Crossed);
    }

    [Fact]
    public void CrossingFromBelow_NotifiesOnlyOnce()
    {
        var state = new ThresholdState();
        NotificationEvaluator.Evaluate(state, W(70));

        Assert.Equal([80], NotificationEvaluator.Evaluate(state, W(81)));
        Assert.Empty(NotificationEvaluator.Evaluate(state, W(85)));
        Assert.Empty(NotificationEvaluator.Evaluate(state, W(90)));
        Assert.Equal([95], NotificationEvaluator.Evaluate(state, W(95)));
        Assert.Empty(NotificationEvaluator.Evaluate(state, W(100)));
    }

    [Fact]
    public void JumpOverBothThresholds_ReturnsBoth()
    {
        var state = new ThresholdState();
        NotificationEvaluator.Evaluate(state, W(10));

        Assert.Equal([80, 95], NotificationEvaluator.Evaluate(state, W(97)));
    }

    [Fact]
    public void ResetChanges_ClearsState_AndAllowsNotifyAgain()
    {
        var state = new ThresholdState();
        NotificationEvaluator.Evaluate(state, W(50));
        Assert.Equal([80], NotificationEvaluator.Evaluate(state, W(82)));

        // リセット後、新しい枠で再び 80% を超えたら通知される。
        Assert.Empty(NotificationEvaluator.Evaluate(state, W(3, Reset2)));
        Assert.Empty(state.Crossed);
        Assert.Equal([80], NotificationEvaluator.Evaluate(state, W(80, Reset2)));
    }

    [Fact]
    public void ResetChanges_WhileStillAbove_NotifiesForNewWindow()
    {
        var state = new ThresholdState();
        NotificationEvaluator.Evaluate(state, W(50));
        NotificationEvaluator.Evaluate(state, W(85));

        Assert.Equal([80], NotificationEvaluator.Evaluate(state, W(85, Reset2)));
    }

    [Fact]
    public void SmallResetJitter_IsNotTreatedAsReset()
    {
        var state = new ThresholdState();
        NotificationEvaluator.Evaluate(state, W(50));
        NotificationEvaluator.Evaluate(state, W(85));

        Assert.Empty(NotificationEvaluator.Evaluate(state, W(86, Reset1.AddSeconds(20))));
    }

    [Fact]
    public void DroppingBelowThreshold_AllowsNotifyOnNextCrossing()
    {
        var state = new ThresholdState();
        NotificationEvaluator.Evaluate(state, W(50));
        NotificationEvaluator.Evaluate(state, W(85));
        NotificationEvaluator.Evaluate(state, W(60));

        Assert.Equal([80], NotificationEvaluator.Evaluate(state, W(81)));
    }

    [Fact]
    public void NullResetToValue_IsTreatedAsNewWindow()
    {
        var state = new ThresholdState();
        NotificationEvaluator.Evaluate(state, new UsageWindow(0, null));

        Assert.Equal([80], NotificationEvaluator.Evaluate(state, W(90)));
    }

    [Fact]
    public void StateSurvivesSerialization_NoDuplicateAfterRestart()
    {
        var state = new ThresholdState();
        NotificationEvaluator.Evaluate(state, W(50));
        Assert.Equal([80], NotificationEvaluator.Evaluate(state, W(81)));

        var restored = JsonSerializer.Deserialize<ThresholdState>(JsonSerializer.Serialize(state))!;

        Assert.Empty(NotificationEvaluator.Evaluate(restored, W(83)));
    }
}
