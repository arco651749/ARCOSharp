using Xunit;

// Avalonia UI 控制項於測試環境中之靜態屬性註冊 (AvaloniaProperty metadata dictionary) 非執行緒安全
// 停用 xUnit 測試類別並行執行以避免跨執行緒並行競爭
[assembly: CollectionBehavior(DisableTestParallelization = true)]
