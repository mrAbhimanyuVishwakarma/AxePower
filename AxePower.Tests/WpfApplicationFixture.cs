using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace AxePower.Tests;

/// <summary>
/// Owns the one WPF <see cref="Application"/> the view tests share.
///
/// WPF's Application is a process-wide singleton and xUnit runs this whole assembly inside a
/// single test host, so every test that needs one needs the *same* one: constructing a second
/// throws, and shutting the first down while later tests still want it makes their
/// <c>InitializeComponent</c> fail with "The Application object is being shut down".
///
/// Worse, the Application keeps a reference to the dispatcher of the thread that created it.
/// If that thread exits — which is what happens when a test spins up an STA thread, builds a
/// view and lets the thread end — <c>Application.Current</c> is left pointing at a dispatcher
/// whose thread is gone. Anything that later reads it (<see cref="AxePower.App.ViewModels.MainViewModel"/>
/// does) takes the entire test host down with it, which reads as "Test host process crashed"
/// with no failing test named.
///
/// So the Application here lives on one STA thread that keeps its dispatcher running for the
/// duration of the run, and tests marshal onto it via <see cref="Invoke"/>.
/// </summary>
public sealed class WpfApplicationFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly Exception? _startupFailure;

    /// <summary>The dispatcher of the thread that owns the Application. Stays running.</summary>
    public Dispatcher Dispatcher { get; private set; } = null!;

    /// <summary>The application's DI container, when the real <see cref="AxePower.App.App"/> could be created.</summary>
    public IServiceProvider? Services { get; private set; }

    public WpfApplicationFixture()
    {
        using var ready = new ManualResetEventSlim(false);
        Exception? failure = null;

        _thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                {
                    var app = new AxePower.App.App();
                    app.InitializeComponent();

                    // App.xaml wires Startup to AxePower's real startup, and WPF raises it as soon
                    // as this thread's dispatcher runs. That would do the single-instance check
                    // against a running AxePower, relaunch this test host elevated via UAC when
                    // "Run as administrator" is on (exiting the host mid-run), show the tray icon
                    // and write the user's settings. The tests only need App's resources.
                    app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(
                        typeof(StartupEventHandler), app, "Application_Startup");

                    // App.xaml declares OnMainWindowClose; no window is ever shown here, and an
                    // explicit shutdown mode keeps WPF from tearing the Application down on its own.
                    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    Services = app.Services;
                }

                // The app ships as AxePower.exe (see <AssemblyName> in AxePower.App.csproj),
                // so the pack URI must name the assembly, not the project.
                EnsureMergedDictionary("/AxePower;component/Resources/Styles.xaml");

                Dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                ready.Set();
            }

            // Keeps this thread — and therefore the Application's dispatcher — alive until Dispose.
            if (failure == null)
            {
                Dispatcher.Run();
            }
        });

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(60)))
        {
            failure ??= new TimeoutException("The WPF application thread did not start within 60 seconds.");
        }

        _startupFailure = failure;
    }

    private static void EnsureMergedDictionary(string packUri)
    {
        if (Application.Current == null) return;

        var uri = new Uri(packUri, UriKind.RelativeOrAbsolute);
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.Source == uri) return;
        }

        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the Application's STA thread and rethrows anything it
    /// threw on the caller's thread — an assertion that fires on the worker thread would
    /// otherwise be swallowed and the test would pass regardless.
    /// </summary>
    public void Invoke(Action action)
    {
        Assert.True(_startupFailure is null, $"WPF application fixture failed to start: {_startupFailure}");

        Exception? captured = null;
        Dispatcher.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
            // Generous on purpose: this is a deadlock guard, not a performance budget. Building
            // the view-model graph touches WMI and the hardware sensor stack, which can take tens
            // of seconds on a cold machine, and failing the test for that would be a false alarm.
        }, DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromMinutes(10));

        if (captured != null)
        {
            throw new InvalidOperationException($"[STA thread] {captured.Message}", captured);
        }
    }

    public void Dispose()
    {
        try
        {
            Dispatcher?.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Nothing useful to do while the host is tearing down.
        }
    }
}

/// <summary>
/// Every test class that touches WPF types joins this collection, so xUnit runs them
/// sequentially against the single shared <see cref="WpfApplicationFixture"/>.
/// </summary>
[CollectionDefinition(Name)]
public class WpfCollection : ICollectionFixture<WpfApplicationFixture>
{
    public const string Name = "WPF";
}
