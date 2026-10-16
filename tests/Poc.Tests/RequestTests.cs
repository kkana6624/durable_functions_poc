using Poc.Core;
using Xunit;

namespace Poc.Tests;

public class RequestTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AcceptsPocTaskCounts(int count)
    {
        new WorkflowRequest("feasibility-001", TaskCount: count).Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void RejectsTaskCountsOutsidePocRange(int count)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new WorkflowRequest("feasibility-001", TaskCount: count).Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("id; exit 1")]
    [InlineData("safe-id\n")]
    public void RejectsUnsafeRequestIds(string id)
    {
        Assert.ThrowsAny<ArgumentException>(() => new WorkflowRequest(id).Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void RejectsFailureIndexOutsideTasks(int index)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new WorkflowRequest("feasibility-001", FailTaskIndex: index).Validate());
    }

    [Fact]
    public void RejectsUnknownClassification()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new WorkflowRequest("feasibility-001", Classification: "unknown").Validate());
    }
}
