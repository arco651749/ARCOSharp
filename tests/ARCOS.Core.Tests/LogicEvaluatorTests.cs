using ARCOS.Core.Common;
using ARCOS.Core.Engines;
using Xunit;

namespace ARCOS.Core.Tests;

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

    [Fact]
    public void LogicEvaluator_RangeAndArrayOperators_ShouldEvaluateCorrectly()
    {
        var io = new IoEngine();
        var vars = new VariableEngine();
        var evaluator = new LogicEvaluator(io, vars);

        vars.Set("Count", "25", "Global");
        vars.Set("Temp", "105.5", "Global");
        vars.Set("EmptyList", "[]", "Global");
        vars.Set("PopulatedList", "[A;B;C]", "Global");
        io.SetPort("Ready?", true);

        // 1. 區間內 <>
        Assert.True(evaluator.Evaluate("Count <> 10~50"));
        Assert.False(evaluator.Evaluate("Count <> 30~50"));

        // 2. 區間外 </>
        Assert.True(evaluator.Evaluate("Temp </> 0~100"));
        Assert.False(evaluator.Evaluate("Temp </> 50~150"));

        // 3. 陣列判定 ==[] 與 !=[]
        Assert.True(evaluator.Evaluate("EmptyList == []"));
        Assert.False(evaluator.Evaluate("EmptyList != []"));
        Assert.True(evaluator.Evaluate("PopulatedList != []"));
        Assert.False(evaluator.Evaluate("PopulatedList == []"));

        // 4. 複合判斷：IO + 區間 + 陣列
        Assert.True(evaluator.Evaluate("Ready? & (Count <> 20~30) & (PopulatedList != [])"));
    }

    [Fact]
    public void LogicEvaluator_ValidateSyntax_ShouldCatchErrors()
    {
        // 合法表達式
        var (valid1, err1) = LogicEvaluator.ValidateSyntax("Ready? & (Count > 10 | ~Alarm?)");
        Assert.True(valid1);
        Assert.Null(err1);

        var (valid2, err2) = LogicEvaluator.ValidateSyntax("EXIST MyVar");
        Assert.True(valid2);
        Assert.Null(err2);

        var (valid3, err3) = LogicEvaluator.ValidateSyntax("Temp <> 20~80");
        Assert.True(valid3);
        Assert.Null(err3);

        // 不合法：缺少右括號
        var (invalid1, errInv1) = LogicEvaluator.ValidateSyntax("(Ready? & Alarm");
        Assert.False(invalid1);
        Assert.NotNull(errInv1);

        // 不合法：多餘右括號
        var (invalid2, errInv2) = LogicEvaluator.ValidateSyntax("Ready?)");
        Assert.False(invalid2);
        Assert.NotNull(errInv2);

        // 不合法：運算元缺失
        var (invalid3, errInv3) = LogicEvaluator.ValidateSyntax("Ready? & ");
        Assert.False(invalid3);
        Assert.NotNull(errInv3);
    }
}
