using System.Text.Json.Serialization;
using DurableTask.Core.History;

namespace DtsProbe;

// Aggregate only the history returned by one instance-ID query. These counts cannot
// establish how many prior executions were replaced, continued, retained or purged.
public sealed record HistoryObservation(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("allGenerationsObserved")] bool AllGenerationsObserved,
    [property: JsonPropertyName("eventCount")] int EventCount,
    [property: JsonPropertyName("executionStartedCount")] int ExecutionStartedCount,
    [property: JsonPropertyName("executionCompletedCount")] int ExecutionCompletedCount,
    [property: JsonPropertyName("executionTerminatedCount")] int ExecutionTerminatedCount,
    [property: JsonPropertyName("continueAsNewCount")] int ContinueAsNewCount)
{
    public static HistoryObservation FromEvents(IEnumerable<HistoryEvent> events)
    {
        int total = 0, started = 0, completed = 0, terminated = 0, continued = 0;
        foreach (var item in events)
        {
            checked { total++; }
            switch (item.EventType)
            {
                case EventType.ExecutionStarted: started++; break;
                case EventType.ExecutionCompleted: completed++; break;
                case EventType.ExecutionTerminated: terminated++; break;
                case EventType.ContinueAsNew: continued++; break;
            }
        }
        return new("ReturnedInstanceHistoryOnly", false, total, started, completed, terminated, continued);
    }
}
