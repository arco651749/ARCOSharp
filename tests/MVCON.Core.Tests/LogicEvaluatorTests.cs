using MVCON.Core.Common;
using MVCON.Core.Engines;
using Xunit;

namespace MVCON.Core.Tests;

public class LogicEvaluatorTests
{
    [Fact]
    public void ArgParaHelper_ParseKeyValueAndErrorLabel_ShouldWork()
    {
        string param = "Cam0, TimeOut=5, Shift=2 [[ErrorHandler]]Camera snapped failed";

        var dict = ArgParaHelper.ParseKeyValueArgs(param, out var posArgs);

        Assert.Single(posArgs);
        Assert.Equal("Cam0", posArgs[0]);
        Assert.Equal("5", dict["TimeOut"]);
        Assert.Equal("2", dict["Shift"]);
        Assert.Equal("ErrorHandler", dict["_ErrorLabel"]);
        Assert.Equal("Camera snapped failed", dict["_ErrorMsg"]);
    }

    [Fact]
    public void LogicEvaluator_BasicBooleanAndParenthesis_ShouldEvaluate()
    {
        var io = new IoEngine();
        var vars = new VariableEngine();
        var evaluator = new LogicEvaluator(io, vars);

        io.SetPort("A?", true);
        io.SetPort("B?", false);
        io.SetPort("C?", true);

        Assert.True(evaluator.Evaluate("A? & C?"));
        Assert.False(evaluator.Evaluate("A? & B?"));
        Assert.True(evaluator.Evaluate("A? | B?"));
        Assert.True(evaluator.Evaluate("~B?"));
        Assert.True(evaluator.Evaluate("(A? | B?) & C?"));
        Assert.False(evaluator.Evaluate("(A? & B?) | ~C?"));
    }

    [Fact]
    public void LogicEvaluator_VariableComparison_ShouldCompareNumericAndString()
    {
        var io = new IoEngine();
        var vars = new VariableEngine();
        var evaluator = new LogicEvaluator(io, vars);

        vars.Set("Count", "15", "Global");
        vars.Set("State", "RUNNING", "Global");

        Assert.True(evaluator.Evaluate("$Count > 10"));
        Assert.False(evaluator.Evaluate("$Count < 5"));
        Assert.True(evaluator.Evaluate("$State == RUNNING"));
        Assert.False(evaluator.Evaluate("$State == STOPPED"));

        // 複合邏輯：變數比對 + IO 旗標
        io.SetPort("SafetyOK?", true);
        Assert.True(evaluator.Evaluate("SafetyOK? & ($Count >= 15)"));
    }

    [Fact]
    public void LogicEvaluator_ExistAndNull_ShouldCheckVariablePresence()
    {
        var io = new IoEngine();
        var vars = new VariableEngine();
        var evaluator = new LogicEvaluator(io, vars);

        vars.Set("ExistingVar", "Hello", "Global");

        Assert.True(evaluator.Evaluate("EXIST ExistingVar"));
        Assert.False(evaluator.Evaluate("EXIST NonExistentVar"));

        Assert.True(evaluator.Evaluate("NULL NonExistentVar"));
        Assert.False(evaluator.Evaluate("NULL ExistingVar"));
    }
}
