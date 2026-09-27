# Shared — código común de las apps sOCratic

Componentes compartidos por las apps .NET MAUI de sOCratic (enlazados con `<Compile Include="..\Shared\*.cs" />`):

- **ModernDialog.cs** — diálogos no nativos (tarjeta + velo, tema-aware).
- **CrashGuard.cs** — gestor global de excepciones (constitución General §6.12): un error inesperado
  se registra con traza en `AppDataDirectory/crash.log` (rotado a `crash.log.old`, ~512 KB como
  mucho), se avisa al usuario en es/en sin cerrar la app y sin encadenar avisos. Engancha
  `AppDomain.UnhandledException` (registrar), `TaskScheduler.UnobservedTaskException` (SetObserved),
  en Android `AndroidEnvironment.UnhandledExceptionRaiser` y en Windows
  `Microsoft.UI.Xaml.Application.UnhandledException` (Handled = true). Uso: enlazarlo en el csproj
  (`<Compile Include="..\Shared\CrashGuard.cs" Link="CrashGuard.cs" />`) y poner como primera línea
  de `MauiProgram.CreateMauiApp()`:
  `SocShared.CrashGuard.Install("Nombre de la app");` (firma completa:
  `Install(string appName, Func<string?>? message = null, Func<string?>? language = null)`). Si la
  app elige idioma sin tocar `CurrentUICulture`, se le dice cuál está en uso:
  `Install("PDF Reader", language: () => IPlatformApplication.Current?.Services.GetService<ILocalizationService>()?.CurrentLanguage)`;
  con el texto propio de la app: `Install("Nombre", message: () => Loc.Get("ErrUnexpected"))`. Para un `catch` propio
  que quiera el mismo trato: `SocShared.CrashGuard.Report(ex, "origen");`.
- **AuthorNotes.cs** — botón de notas de autor (solo Debug/dispositivos del autor).
- **signing.props** — configuración de firma Release compartida (la contraseña se pasa por CLI, nunca en el repo).

> El keystore (`socratic.keystore`) NO se versiona (ver `.gitignore`).

MIT.
