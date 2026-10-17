using System.Text.Json;
using DtsProbe;
using DurableTask.Core.History;
using Xunit;

namespace DtsProbeTests;

public sealed class HistoryObservationTests
{
    [Fact]
    public void ObservedStartsDoNotClaimAllGenerationsOrDiscloseEventPayloads()
    {
        const string secret = "sensitive-input-token";
        HistoryEvent[] history =
        [
            new ExecutionStartedEvent(0, secret) { Name = secret },
            new ExecutionStartedEvent(1, secret) { Name = secret },
            new ExecutionCompletedEvent(2, secret, DurableTask.Core.OrchestrationStatus.Completed),
        ];

        var observation = HistoryObservation.FromEvents(history);
        var serialized = JsonSerializer.Serialize(observation);

        Assert.Equal(3, observation.EventCount);
        Assert.Equal(2, observation.ExecutionStartedCount);
        Assert.Equal(1, observation.ExecutionCompletedCount);
        Assert.False(observation.AllGenerationsObserved);
        Assert.Equal("ReturnedInstanceHistoryOnly", observation.Scope);
        Assert.DoesNotContain(secret, serialized);
        using var json = JsonDocument.Parse(serialized);
        Assert.Equal(7, json.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void EmptyHistoryDoesNotClaimUniquenessOrCompleteness()
    {
        var observation = HistoryObservation.FromEvents([]);
        Assert.Equal(0, observation.EventCount);
        Assert.Equal(0, observation.ExecutionStartedCount);
        Assert.False(observation.AllGenerationsObserved);
    }
}
