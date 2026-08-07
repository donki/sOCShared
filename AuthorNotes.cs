using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

namespace SocShared;

/// <summary>
/// Notas de autor integradas en la app (mismo criterio que sOC): un botón flotante 📝
/// visible SOLO en los dispositivos del autor (tablet Samsung / Xiaomi). Al pulsarlo se
/// escribe una nota que se GUARDA SOLA en los datos de la app (author_notes.json),
/// etiquetada con la pantalla actual y la fecha/hora. Se recupera por adb (run-as).
///
/// Implementación: se inyecta un Button real en el árbol visual de cada ContentPage
/// (envolviendo su contenido en un Grid), lo que es mucho más fiable que un IWindowOverlay.
/// Uso: en App.CreateWindow -> var w = new Window(shell); AuthorNotes.Attach(w); return w;
/// </summary>
public static class AuthorNotes
{
    // Dispositivos autorizados (por modelo). Tablet Samsung y Xiaomi/Redmi del autor.
    static readonly string[] AllowedModels = { "SM-X130", "24090RA29G" };

    public static bool DeviceAllowed
    {
        get
        {
            try { foreach (var m in AllowedModels) if (string.Equals(DeviceInfo.Model, m, StringComparison.OrdinalIgnoreCase)) return true; }
            catch { }
            return false;
        }
    }

    const string Marker = "__authorNotesRoot";
    static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "author_notes.json");

    static Page? _hookedPage;

    public static void Attach(Window window)
    {
        if (!DeviceAllowed) return;
        window.Created += (_, _) => Hook(window);
        // La página raíz puede reasignarse después (p.ej. TXTReader arranca con un SplashPage y
        // luego cambia window.Page a un Shell): hay que volver a enganchar cuando eso ocurre.
        window.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Window.Page)) Hook(window);
        };
        if (window.Handler != null) Hook(window);
    }

    static void Hook(Window window)
    {
        try
        {
            var page = window.Page;
            // Evita re-suscribir eventos sobre la misma página (PropertyChanged puede repetir).
            if (page is null || ReferenceEquals(page, _hookedPage)) return;
            _hookedPage = page;
            switch (page)
            {
                case Shell shell:
                    shell.Navigated += (_, _) => EnsureButton(shell.CurrentPage);
                    EnsureButton(shell.CurrentPage);
                    break;
                case NavigationPage nav:
                    nav.Pushed += (_, e) => EnsureButton(e.Page);
                    nav.Popped += (_, _) => EnsureButton(nav.CurrentPage);
                    EnsureButton(nav.CurrentPage);
                    break;
                default:
                    EnsureButton(page);
                    break;
            }
        }
        catch { }
    }

    /// <summary>Envuelve el contenido de la página en un Grid y añade el botón 📝 encima (una sola vez).</summary>
    static void EnsureButton(Page? page)
    {
        try
        {
            if (page is not ContentPage cp || cp.Content is null) return;
            if (cp.Content is Grid g && g.StyleId == Marker) return;   // ya inyectado

            var content = cp.Content;
            cp.Content = null;
            var grid = new Grid { StyleId = Marker };
            grid.Add(content);

            var btn = new Button
            {
                Text = "📝",
                FontSize = 22,
                WidthRequest = 54,
                HeightRequest = 54,
                CornerRadius = 14,
                Padding = 0,
                BackgroundColor = Color.FromRgba(20, 22, 30, 230),
                TextColor = Colors.White,
                BorderColor = Color.FromRgba(230, 180, 90, 235),
                BorderWidth = 1,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.End,
                Margin = new Thickness(0, 0, 16, 90),
                ZIndex = 999,
            };
            btn.Clicked += async (_, _) => await ShowEditor(cp);
            grid.Add(btn);
            cp.Content = grid;
        }
        catch { }
    }

    static async Task ShowEditor(Page page)
    {
        try
        {
            var ctx = page.GetType().Name;
            var txt = await SocShared.ModernDialog.PromptAsync(
                page,
                "📝 Nota — " + ctx,
                "Se guarda sola al pulsar Guardar (pantalla y hora incluidas).",
                accept: "Guardar", cancel: "Cancelar", placeholder: "Escribe aquí…");
            if (!string.IsNullOrWhiteSpace(txt))
                Save(ctx, txt);
        }
        catch { }
    }

    public static void Save(string context, string text)
    {
        try
        {
            var data = Load();
            data.notes.Add(new Note { time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), context = context, text = text });
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    static NotesFile Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<NotesFile>(File.ReadAllText(FilePath)) ?? Fresh(); }
        catch { }
        return Fresh();
    }

    static NotesFile Fresh()
    {
        var app = "?";
        try { app = AppInfo.Name; } catch { }
        return new NotesFile { app = app };
    }

    public class Note { public string time { get; set; } = ""; public string context { get; set; } = ""; public string text { get; set; } = ""; }
    class NotesFile { public string app { get; set; } = "?"; public List<Note> notes { get; set; } = new(); }
}
