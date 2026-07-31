# Shared — código común de las apps sOCratic

Componentes compartidos por las apps .NET MAUI de sOCratic (enlazados con `<Compile Include="..\Shared\*.cs" />`):

- **ModernDialog.cs** — diálogos no nativos (tarjeta + velo, tema-aware).
- **AuthorNotes.cs** — botón de notas de autor (solo Debug/dispositivos del autor).
- **signing.props** — configuración de firma Release compartida (la contraseña se pasa por CLI, nunca en el repo).

> El keystore (`socratic.keystore`) NO se versiona (ver `.gitignore`).

MIT.
