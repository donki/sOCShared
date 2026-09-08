using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace SocShared;

/// <summary>
/// Diálogos NO nativos (modernos) compartidos por todas las apps sOCratic: una tarjeta con esquinas
/// redondeadas sobre un velo oscuro, con animación de entrada, en lugar del AlertDialog del sistema.
/// Reemplaza a Page.DisplayAlert / DisplayActionSheet.
///
/// Uso:
///   bool ok = await SocShared.ModernDialog.AlertAsync(this, "Título", "Mensaje", "Aceptar", "Cancelar");
///   string? sel = await SocShared.ModernDialog.ActionSheetAsync(this, "Título", "Cancelar", "A", "B", "C");
///
/// Tema-aware (claro/oscuro) y usa el color "Primary" de la app si existe.
/// </summary>
public static class ModernDialog
{
    const string OverlayId = "__modernDialogOverlay";

    static bool IsDark =>
        Application.Current?.RequestedTheme == AppTheme.Dark;

    static Color CardColor => IsDark ? Color.FromArgb("#1E2228") : Colors.White;
    static Color TextColor => IsDark ? Color.FromArgb("#ECEFF3") : Color.FromArgb("#1C2530");
    static Color MutedColor => IsDark ? Color.FromArgb("#9AA6B2") : Color.FromArgb("#5B6773");
    static Color ScrimColor => Color.FromRgba(0, 0, 0, IsDark ? 0.62 : 0.45);

    static Color Accent()
    {
        if (Application.Current?.Resources is not null &&
            Application.Current.Resources.TryGetValue("Primary", out var v) && v is Color c)
            return c;
        return Color.FromArgb("#3B82F6");
    }

    /// <summary>Aviso/confirmación. Devuelve true si se pulsa "accept"; false si "cancel" o se descarta.</summary>
    public static Task<bool> AlertAsync(Page page, string title, string message, string accept, string? cancel = null)
    {
        var tcs = new TaskCompletionSource<bool>();
        var accentBtn = MakeButton(accept, Accent(), Colors.White);
        Button? cancelBtn = cancel is null ? null : MakeButton(cancel, Color.FromArgb(IsDark ? "#2A2F37" : "#EDF0F3"), TextColor);

        // En una pantalla de movil los dos botones en fila solo caben si las etiquetas son cortas:
        // con "Grant access" ya se ocupaba el ancho justo de la tarjeta, y en espanol
        // ("Conceder acceso") el texto se salia y quedaba cortado. Cuando no caben, se apilan a lo
        // ancho, que es lo que hacen los dialogos del sistema.
        View buttons;
        if (cancelBtn is not null && accept.Length + cancel!.Length > 20)
        {
            accentBtn.HorizontalOptions = LayoutOptions.Fill;
            cancelBtn.HorizontalOptions = LayoutOptions.Fill;
            buttons = new VerticalStackLayout { Spacing = 8, Children = { accentBtn, cancelBtn } };
        }
        else
        {
            var row = new HorizontalStackLayout { Spacing = 10, HorizontalOptions = LayoutOptions.End };
            if (cancelBtn is not null) row.Add(cancelBtn);
            row.Add(accentBtn);
            buttons = row;
        }

        var card = BuildCard(title, message, buttons);
        var overlay = BuildOverlay(page, card, onScrim: () => Close(page, () => tcs.TrySetResult(false)));

        accentBtn.Clicked += (_, _) => Close(page, () => tcs.TrySetResult(true));
        if (cancelBtn is not null) cancelBtn.Clicked += (_, _) => Close(page, () => tcs.TrySetResult(false));

        Present(page, overlay, card);
        return tcs.Task;
    }

    /// <summary>Lista de opciones (reemplaza DisplayActionSheet). Devuelve la opción elegida o null.</summary>
    public static Task<string?> ActionSheetAsync(Page page, string? title, string cancel, params string[] options)
    {
        var tcs = new TaskCompletionSource<string?>();

        var list = new VerticalStackLayout { Spacing = 8 };
        foreach (var opt in options)
        {
            var b = MakeButton(opt, Color.FromArgb(IsDark ? "#2A2F37" : "#F1F4F7"), TextColor);
            b.HorizontalOptions = LayoutOptions.Fill;
            var captured = opt;
            b.Clicked += (_, _) => Close(page, () => tcs.TrySetResult(captured));
            list.Add(b);
        }
        var cancelBtn = MakeButton(cancel, Color.FromArgb(IsDark ? "#3A2A2E" : "#FBECEC"), Color.FromArgb("#C0392B"));
        cancelBtn.HorizontalOptions = LayoutOptions.Fill;
        cancelBtn.Clicked += (_, _) => Close(page, () => tcs.TrySetResult(null));
        list.Add(cancelBtn);

        var card = BuildCard(title, null, list);
        var overlay = BuildOverlay(page, card, onScrim: () => Close(page, () => tcs.TrySetResult(null)));

        Present(page, overlay, card);
        return tcs.Task;
    }

    /// <summary>Entrada de texto (reemplaza DisplayPromptAsync). Devuelve el texto o null si se cancela.</summary>
    public static Task<string?> PromptAsync(Page page, string title, string? message, string accept = "OK",
        string cancel = "Cancel", string? initialValue = null, string? placeholder = null)
    {
        var tcs = new TaskCompletionSource<string?>();

        var entry = new Entry
        {
            Text = initialValue ?? string.Empty,
            Placeholder = placeholder ?? string.Empty,
            TextColor = TextColor,
            BackgroundColor = Color.FromArgb(IsDark ? "#12151A" : "#F1F4F7"),
        };
        var accentBtn = MakeButton(accept, Accent(), Colors.White);
        var cancelBtn = MakeButton(cancel, Color.FromArgb(IsDark ? "#2A2F37" : "#EDF0F3"), TextColor);
        var buttons = new HorizontalStackLayout { Spacing = 10, HorizontalOptions = LayoutOptions.End };
        buttons.Add(cancelBtn);
        buttons.Add(accentBtn);

        var body = new VerticalStackLayout { Spacing = 12 };
        body.Add(entry);
        body.Add(buttons);

        var card = BuildCard(title, message, body);
        var overlay = BuildOverlay(page, card, onScrim: () => Close(page, () => tcs.TrySetResult(null)));

        accentBtn.Clicked += (_, _) => Close(page, () => tcs.TrySetResult(entry.Text ?? string.Empty));
        cancelBtn.Clicked += (_, _) => Close(page, () => tcs.TrySetResult(null));

        Present(page, overlay, card);
        return tcs.Task;
    }

    // ---- construcción ----

    static Border BuildCard(string? title, string? message, View content)
    {
        var stack = new VerticalStackLayout { Spacing = 14 };
        if (!string.IsNullOrEmpty(title))
            stack.Add(new Label { Text = title, FontSize = 19, FontAttributes = FontAttributes.Bold, TextColor = TextColor });
        if (!string.IsNullOrEmpty(message))
            stack.Add(new Label { Text = message, FontSize = 15, TextColor = MutedColor });
        stack.Add(content);

        return new Border
        {
            Content = stack,
            BackgroundColor = CardColor,
            Stroke = Color.FromRgba(255, 255, 255, IsDark ? 0.08 : 0.0),
            StrokeThickness = IsDark ? 1 : 0,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            Padding = new Thickness(22, 20),
            Margin = new Thickness(28, 0),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            MaximumWidthRequest = 420,
            Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Opacity = 0.35f, Radius = 24, Offset = new Point(0, 8) },
            Opacity = 0,
            Scale = 0.92,
        };
    }

    static Grid BuildOverlay(Page page, Border card, System.Action onScrim)
    {
        var scrim = new BoxView { Color = ScrimColor, Opacity = 0 };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => onScrim();
        scrim.GestureRecognizers.Add(tap);

        var overlay = new Grid { StyleId = OverlayId };
        overlay.Add(scrim);
        overlay.Add(card);
        return overlay;
    }

    static Button MakeButton(string text, Color bg, Color fg) => new()
    {
        Text = text,
        BackgroundColor = bg,
        TextColor = fg,
        FontSize = 15,
        FontAttributes = FontAttributes.Bold,
        CornerRadius = 12,
        Padding = new Thickness(18, 10),
        MinimumHeightRequest = 44,
    };

    static void Present(Page page, Grid overlay, Border card)
    {
        var host = HostGrid(page);
        if (host is null) return;

        // Nunca dos dialogos superpuestos: si ya hay uno, se retira antes de poner el nuevo. Sin
        // esto, dos avisos disparados a la vez (tipico al volver de un dialogo del sistema) dejaban
        // el de debajo colgado en pantalla, sin nadie que lo cerrara.
        foreach (var stale in host.Children.OfType<Grid>().Where(g => g.StyleId == OverlayId).ToList())
            host.Children.Remove(stale);

        // El overlay ocupa toda la rejilla anfitriona.
        if (host.RowDefinitions.Count > 0) Grid.SetRowSpan(overlay, host.RowDefinitions.Count);
        if (host.ColumnDefinitions.Count > 0) Grid.SetColumnSpan(overlay, host.ColumnDefinitions.Count);
        host.Add(overlay);

        // Animación de entrada.
        var scrim = (BoxView)overlay.Children[0];
        scrim.FadeTo(1, 160, Easing.CubicOut);
        card.FadeTo(1, 180, Easing.CubicOut);
        card.ScaleTo(1, 200, Easing.CubicOut);
    }

    static async void Close(Page page, System.Action complete)
    {
        var host = HostGrid(page);
        var overlay = host?.Children.OfType<Grid>().FirstOrDefault(g => g.StyleId == OverlayId);
        if (overlay is not null)
        {
            var card = overlay.Children.OfType<Border>().FirstOrDefault();
            if (card is not null)
            {
                card.ScaleTo(0.92, 120, Easing.CubicIn);
                await card.FadeTo(0, 120, Easing.CubicIn);
            }
            host!.Remove(overlay);
        }
        complete();
    }

    /// <summary>Rejilla anfitriona: envuelve el contenido de la página en un Grid (una vez) para poder
    /// superponer el overlay. Reutiliza el Grid si ya lo hay (incluido el de AuthorNotes).</summary>
    static Grid? HostGrid(Page page)
    {
        if (page is not ContentPage cp)
            return null;
        if (cp.Content is Grid g)
            return g;
        var content = cp.Content;
        var grid = new Grid();
        cp.Content = null;
        if (content is not null)
            grid.Add(content);
        cp.Content = grid;
        return grid;
    }
}
