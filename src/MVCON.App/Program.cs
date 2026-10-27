using System;
using System.IO;
using System.Runtime.Loader;
using Avalonia;

namespace MVCON.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 註冊全域組件解析器，確保 MODULES/ 與 DRIVER/ 載入的外掛可直接共用主目錄下的核心 DLL
        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            string candidatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{assemblyName.Name}.dll");
            if (File.Exists(candidatePath))
            {
                return context.LoadFromAssemblyPath(candidatePath);
            }
            return null;
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
