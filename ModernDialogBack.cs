using System;
using System.Linq;
using System.Reflection;
using Microsoft.Maui.Controls;

namespace SocShared;

/// <summary>
/// Botón de atrás con un <see cref="ModernDialog"/> abierto (constitución Mobile §7: si hay un
/// diálogo encima, atrás lo cierra antes de salir de la pantalla). El diálogo es una capa dentro de
/// la página, no una página modal, así que sin esto atrás se iba de la pantalla con el diálogo a la
/// vista, o escondía la aplicación y al volver seguía ahí.
///
/// Enlace en el csproj, junto a ModernDialog:
///   &lt;Compile Include="..\Shared\ModernDialogBack.cs" Link="ModernDialogBack.cs" /&gt;
///
/// Uso: lo primero en el <c>OnBackButtonPressed</c> del Shell (con la página visible) y en el de
/// las páginas modales, que el Shell no ve:
///   if (SocShared.ModernDialogBack.TryDismiss(CurrentPage)) return true;
/// </summary>
public static class ModernDialogBack
{
    // El mismo identificador que pone ModernDialog a su capa.
    private const string OverlayId = "__modernDialogOverlay";

    /// <summary>
    /// Cierra el diálogo abierto en la página como si se tocara fuera de él (lo mismo que
    /// «Cancelar»: el que lo esperaba recibe false o null). Devuelve true si había uno.
    /// </summary>
    public static bool TryDismiss(Page? page)
    {
        if (page is not ContentPage { Content: Grid host })
            return false;

        var overlay = host.Children.OfType<Grid>().LastOrDefault(g => g.StyleId == OverlayId);
        if (overlay is null)
            return false;

        // El fondo oscuro lleva el gesto que cierra el diálogo y completa la tarea que lo espera.
        var scrim = overlay.Children.OfType<BoxView>().FirstOrDefault();
        var tap = scrim?.GestureRecognizers.OfType<TapGestureRecognizer>().FirstOrDefault();
        var send = typeof(TapGestureRecognizer).GetMethod("SendTapped",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (scrim is not null && tap is not null && send is not null)
        {
            try
            {
                var parameters = send.GetParameters();
                var args = new object?[parameters.Length];
                args[0] = scrim;
                for (var i = 1; i < parameters.Length; i++)
                    args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
                send.Invoke(tap, args);
                return true;
            }
            catch (Exception)
            {
                // Si MAUI cambia el método, al menos se quita la capa (abajo).
            }
        }

        host.Remove(overlay);
        return true;
    }
}
