using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using BorgStudio.App.Services;

namespace BorgStudio.Screenshots;

/// <summary>
/// Renders the product page's screenshots in one language:
/// <c>dotnet run -c Release --project tools/BorgStudio.Screenshots -- en site/images/en</c> (and <c>de</c>).
/// Optional further arguments name the pictures to render (default: all).
/// Every picture is a real BorgStudio window, filled with made-up example data and fake borg.
/// </summary>
public static class Program
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is not [var language, var folder, ..])
        {
            Console.Error.WriteLine("Usage: BorgStudio.Screenshots <en|de> <output folder> [picture ...]");
            return 2;
        }
        // Texts are bound when the windows load, so the language comes first.
        AppLanguages.Apply(language);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = Texts.Culture(language);
        Directory.CreateDirectory(folder);
        BuildAvaloniaApp().SetupWithoutStarting();
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());

        // The pictures are made on the UI thread; its loop runs until they are done.
        using var done = new CancellationTokenSource();
        var work = RenderAllAsync(folder, args.Skip(2).ToHashSet(StringComparer.OrdinalIgnoreCase));
        work.ContinueWith(_ => done.Cancel(), TaskScheduler.Default);
        Dispatcher.UIThread.MainLoop(done.Token);
        return work.GetAwaiter().GetResult();
    }

    private static async Task<int> RenderAllAsync(string folder, HashSet<string> only)
    {
        var failed = 0;
        foreach (var shot in Shots.All)
        {
            if (only.Count > 0 && !only.Contains(shot.Name))
                continue;
            try
            {
                using var scene = new Scene { Output = Path.Combine(folder, shot.Name + ".png") };
                if (await shot.Render(scene) is { } window)
                {
                    scene.Save(window);
                    window.Close();
                }
                Console.WriteLine($"{shot.Name}.png");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"{shot.Name}: {exception}");
            }
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Lets bindings, layout and rendering catch up.</summary>
    public static void Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Renders the window and writes its frame.</summary>
    public static void Save(Window window, string path)
    {
        Settle();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing rendered.");
#pragma warning disable CS0618 // The encoder options overload needs a concrete encoder; PNG is the default anyway.
        frame.Save(path);
#pragma warning restore CS0618
    }
}
