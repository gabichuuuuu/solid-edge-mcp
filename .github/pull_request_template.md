## Qué cambia

<!-- Resumen y motivo. Enlaza el issue si lo hay. -->

## Validación

- [ ] `dotnet build SolidEdgeMcp.slnx` sin avisos
- [ ] Tests de `SolidEdgeMcp.Core.Tests` y `SolidEdgeMcp.Server.Tests` en verde
- [ ] Guiones de aceptación afectados (`run.ps1 A0x`) pasan contra Solid Edge real, con
      documentos temporales cerrados sin guardar
- [ ] Resultado medible comprobado (volumen, caja envolvente, número de caras, archivo exportado)

<!-- Indica qué guiones ejecutaste y con qué versión de Solid Edge. -->

## Herramientas nuevas o cambiadas

- [ ] Registradas en `Program.ToolTypes`, con casos de argumentos en `ToolRegistrationTests`
- [ ] Firmas y constantes tomadas de `reference/api` o de `se_com_describe` / `se_com_constants`
- [ ] Rutas por `PathGuard`; cada escritura se verifica y se deshace si falla

## Documentación

- [ ] `README.md`, `docs/COBERTURA.md` y `ESTADO.md` actualizados
- [ ] `CHANGELOG.md` actualizado

## Repositorio

- [ ] Sin interop, volcados del API, `publish/`, `policy.json`, logs ni secretos
- [ ] Commits convencionales en inglés
