using ARCOS.Core.Model;
using Xunit;

namespace ARCOS.Core.Tests;

public class CommandStateTests
{
    [Fact]
    public void CommandState_ShouldBehaveAsInteger_ByDefault()
    {
        CommandState state = 100;
        Assert.Equal(100, (int)state);
        Assert.Equal(100, state.Value);
        Assert.False(state.HasData);

        state.Value += 50;
        Assert.Equal(150, (int)state);
    }

    [Fact]
    public void CommandState_ShouldStoreAndRetrieveTransientData_ViaJsonIndexer()
    {
        var state = new CommandState { Value = 2 };

        state["step"] = 1;
        state["retryCount"] = 3;
        state["token"] = "ABC_123";

        Assert.True(state.HasData);
        Assert.Equal(1, state.Get<int>("step"));
        Assert.Equal(3, state.Get<int>("retryCount"));
        Assert.Equal("ABC_123", state.Get<string>("token"));
    }

    [Fact]
    public void CommandState_Reset_ShouldClearTransientDataAndResetValue()
    {
        var state = new CommandState { Value = 500 };
        state["step"] = 2;

        Assert.True(state.HasData);

        state.Reset();

        Assert.Equal(0, state.Value);
        Assert.False(state.HasData);
        Assert.Null(state["step"]);
    }
}
