using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace SocShared;

/// <summary>
/// Gestor global de excepciones compartido por las apps .NET MAUI de sOCratic (constitución General
/// §6.12): un error que no se esperaba nunca cierra la aplicación; se registra con la traza completa
/// en un fichero de la carpeta de datos de la app, se avisa al usuario en su idioma y la app sigue.
///
/// Enlace en el csproj (como AuthorNotes/ModernDialog):
///   &lt;Compile Include="..\Shared\CrashGuard.cs" Link="CrashGuard.cs" /&gt;
///
/// Uso: UNA llamada, la primera línea de <c>MauiProgram.CreateMauiApp()</c> (antes de crear el
/// builder; vale igual para Android y Windows, y se puede repetir sin efecto):
///   SocShared.CrashGuard.Install("TXT Reader");
///
/// Firma: <c>Install(string appName, Func&lt;string?&gt;? message = null, Func&lt;string?&gt;? language = null)</c>.
/// Idioma del aviso: por defecto es/en según <c>CultureInfo.CurrentUICulture</c>. Si la app deja
/// elegir idioma sin tocar la cultura, se le pasa cuál está en uso ("es"/"en"; se evalúa en cada
/// aviso, así sigue al cambio en caliente):
///   SocShared.CrashGuard.Install("PDF Reader", language: () =>
///       IPlatformApplication.Current?.Services.GetService&lt;ILocalizationService&gt;()?.CurrentLanguage);
/// Texto del aviso propio de la app (también se evalúa en cada aviso; null o vacío = el de por defecto):
///   SocShared.CrashGuard.Install("Family Together", message: () => Loc.Get("ErrUnexpected"));
///
/// Qué engancha:
///   - AppDomain.CurrentDomain.UnhandledException: solo registrar (no se puede frenar).
///   - TaskScheduler.UnobservedTaskException: SetObserved, registrar y avisar.
///   - Android: AndroidEnvironment.UnhandledExceptionRaiser: Handled = true, registrar y avisar.
///   - Windows: Microsoft.UI.Xaml.Application.Current.UnhandledException: Handled = true, registrar
///     y avisar (por eso la llamada va en CreateMauiApp: ahí la Application de WinUI ya existe).
///
/// Extras opcionales:
///   - <see cref="Report"/>(ex, "origen"): para un catch propio que quiera el mismo trato (registrar
///     y avisar) sin relanzar.
///   - <see cref="Log"/>(ex, "origen") / <see cref="Info"/>("texto"): solo registrar.
///   - <see cref="LogPath"/>: ruta del registro (p. ej. para enseñarlo o compartirlo desde Acerca de).
///   - <see cref="Alert"/>: sustituir cómo se enseña el aviso. Por defecto se usa
///     SocShared.ModernDialog si la app lo enlaza, y si no Page.DisplayAlert.
///
/// El registro es <c>FileSystem.AppDataDirectory/crash.log</c>; al pasar de 256 KB se aparta a
/// <c>crash.log.old</c> (pisando el anterior): nunca ocupa más de ~512 KB. El aviso va al hilo
/// principal y no se encadena: si ya hay uno a la vista o se enseñó hace menos de 10 s, el error
/// solo se registra. Sin dependencias fuera de MAUI.
/// </summary>
public static class CrashGuard
{
    private const long MaxBytes = 256 * 1024;
    private static readonly TimeSpan NoticeGap = TimeSpan.FromSeconds(10);
    private static readonly object Gate = new();

    private static int _installed;
    private static int _showing;
    private static DateTime _lastNotice = DateTime.MinValue;
    private static string _appName = "App";
    private static Func<string?>? _message;
    private static Func<string?>? _language;

    /// <summary>
    /// Cómo se enseña el aviso: (página, título, mensaje, botón). Por defecto ModernDialog si está
    /// enlazado, si no DisplayAlert. Se puede sustituir después de <see cref="Install"/>.
    /// </summary>
    public static Func<Page, string, string, string, Task>? Alert { get; set; }

    /// <summary>Ruta del fichero de registro.</summary>
    public static string LogPath
    {
        get
        {
            try
            {
                return Path.Combine(FileSystem.AppDataDirectory, "crash.log");
            }
            catch (Exception)
            {
                return Path.Combine(Path.GetTempPath(), "crash.log");
            }
        }
    }

    /// <summary>
    /// Engancha los gestores globales. Llamar una vez al principio de <c>MauiProgram.CreateMauiApp</c>.
    /// </summary>
    /// <param name="appName">Nombre de la app, para el registro.</param>
    /// <param name="message">Texto del aviso propio de la app (null o devolver null: el de por defecto).</param>
    /// <param name="language">Idioma en uso en la app, "es" o "en" (null o devolver null: CurrentUICulture).</param>
    public static void Install(string appName, Func<string?>? message = null, Func<string?>? language = null)
    {
        if (!string.IsNullOrWhiteSpace(appName))
            _appName = appName;
        if (message is not null)
            _message = message;
        if (language is not null)
            _language = language;

        if (Interlocked.Exchange(ref _installed, 1) == 1)
            return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Este no se puede frenar (el proceso ya se va): se registra, que es lo único que queda.
            Write("FATAL", "AppDomain.UnhandledException: " + e.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Report(e.Exception, "TaskScheduler.UnobservedTaskException");
        };

#if ANDROID
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            e.Handled = true;
            Report(e.Exception, "AndroidEnvironment.UnhandledExceptionRaiser");
        };
#endif

#if WINDOWS
        try
        {
            if (Microsoft.UI.Xaml.Application.Current is { } winApp)
            {
                winApp.UnhandledException += (_, e) =>
                {
                    e.Handled = true;
                    Report(e.Exception, "Microsoft.UI.Xaml.Application.UnhandledException");
                };
            }
        }
        catch (Exception ex)
        {
            Log(ex, "CrashGuard.Install (Windows)");
        }
#endif

        Info($"{_appName} {SafeVersion()} arrancada; gestor de excepciones enganchado.");
    }

    /// <summary>Registra el error y avisa al usuario (sin encadenar avisos). Nunca lanza.</summary>
    public static void Report(Exception? ex, string source)
    {
        Log(ex, source);
        NotifyUser();
    }

    /// <summary>Solo registra el error con su traza. Nunca lanza.</summary>
    public static void Log(Exception? ex, string source) =>
        Write("ERROR", $"{source}: {ex?.ToString() ?? "(sin excepción)"}");

    /// <summary>Solo registra una línea informativa. Nunca lanza.</summary>
    public static void Info(string message) => Write("INFO", message);

    private static void NotifyUser()
    {
        try
        {
            lock (Gate)
            {
                // Una ráfaga de errores no puede convertirse en una ráfaga de diálogos.
                if (_showing == 1 || DateTime.UtcNow - _lastNotice < NoticeGap)
                    return;
                _showing = 1;
                _lastNotice = DateTime.UtcNow;
            }

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    var page = CurrentPage();
                    if (page is null)
                        return; // sin ventana a la que avisar: ya está en el registro

                    var es = IsSpanish();
                    var title = es ? "Algo ha fallado" : "Something went wrong";
                    string? custom = null;
                    try { custom = _message?.Invoke(); } catch (Exception) { }
                    var text = !string.IsNullOrWhiteSpace(custom)
                        ? custom!
                        : es
                            ? "Ha ocurrido un error inesperado. La aplicación sigue abierta y el error ha quedado registrado."
                            : "An unexpected error occurred. The app is still open and the error has been logged.";
                    var ok = es ? "Aceptar" : "OK";

                    var show = Alert ?? DefaultAlert;
                    await show(page, title, text, ok);
                }
                catch (Exception ex)
                {
                    Log(ex, "CrashGuard.NotifyUser");
                }
                finally
                {
                    lock (Gate)
                    {
                        _showing = 0;
                        _lastNotice = DateTime.UtcNow;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            lock (Gate) _showing = 0;
            Log(ex, "CrashGuard.NotifyUser");
        }
    }

    private static bool IsSpanish()
    {
        string? lang = null;
        try { lang = _language?.Invoke(); } catch (Exception) { }
        if (string.IsNullOrWhiteSpace(lang))
            lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return lang!.StartsWith("es", StringComparison.OrdinalIgnoreCase);
    }

    private static MethodInfo? _modernAlert;
    private static bool _modernLooked;

    private static Task DefaultAlert(Page page, string title, string message, string ok)
    {
        // ModernDialog es opcional: si la app lo enlaza (mismo ensamblado) se usa; si no, el nativo.
        if (!_modernLooked)
        {
            _modernLooked = true;
            try
            {
                _modernAlert = typeof(CrashGuard).Assembly.GetType("SocShared.ModernDialog")?
                    .GetMethod("AlertAsync", new[] { typeof(Page), typeof(string), typeof(string), typeof(string), typeof(string) });
            }
            catch (Exception)
            {
                _modernAlert = null;
            }
        }

        if (_modernAlert?.Invoke(null, new object?[] { page, title, message, ok, null }) is Task t)
            return t;

#if NET10_0_OR_GREATER
        return page.DisplayAlertAsync(title, message, ok);
#else
        return page.DisplayAlert(title, message, ok);
#endif
    }

    /// <summary>La página que está a la vista (modal encima, página actual del Shell o de la pila).</summary>
    private static Page? CurrentPage()
    {
        var root = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (root is null)
            return null;

        var modal = root.Navigation?.ModalStack.LastOrDefault();
        if (modal is not null)
            return Deepest(modal);

        return Deepest(root);
    }

    private static Page Deepest(Page page) => page switch
    {
        Shell shell when shell.CurrentPage is { } p => p,
        NavigationPage nav when nav.CurrentPage is { } p => Deepest(p),
        FlyoutPage flyout when flyout.Detail is { } p => Deepest(p),
        TabbedPage tabs when tabs.CurrentPage is { } p => Deepest(p),
        _ => page,
    };

    private static string SafeVersion()
    {
        try { return AppInfo.Current.VersionString; }
        catch (Exception) { return "?"; }
    }

    private static void Write(string level, string message)
    {
        System.Diagnostics.Debug.WriteLine($"[{_appName}] {level} {message}");
#if ANDROID
        try
        {
            if (level != "INFO")
                Android.Util.Log.Error(_appName, message);
        }
        catch (Exception) { }
#endif
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true); // nunca crece sin fin

                File.AppendAllText(path,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // El registro nunca puede tumbar lo que se estaba registrando.
        }
    }
}
