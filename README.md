# solid_edge_mcp

Servidor [MCP](https://modelcontextprotocol.io) local que permite a Claude Code, Claude Desktop,
Codex u otro cliente MCP manejar **Solid Edge** por COM con la amplitud de una persona: consultar
modelos, dibujar bocetos, crear operaciones de pieza y chapa, montar conjuntos, hacer planos y
exportar. Es de uso general: no depende de unos archivos, un equipo ni un flujo concretos.

Probado con Solid Edge **Community Edition 2026** en Windows 11 x64.

## Herramientas

Cada herramienta agrupa sus operaciones en un parámetro `action`. Longitudes en mm y ángulos en
grados; los objetos se devuelven como *handles* (`face:12`, `sketch:5`…) reutilizables en otras
llamadas. Cada operación de modelado se verifica (estado y cambio de volumen) y, si falla o no
tiene efecto, se deshace y se informa del motivo y del siguiente paso.

| Área | Herramientas |
|---|---|
| Servidor y sesión | `se_server_info`, `se_session` (conectar, arrancar Solid Edge, estado) |
| Documentos | `se_documents` (crear pieza, chapa, conjunto, plano o soldadura; abrir, guardar, guardar como, cerrar, deshacer, recalcular) |
| Datos | `se_properties`, `se_variables` (cotas y variables, fórmulas), `se_material` (bibliotecas, propiedades, asignar material) |
| Consulta | `se_feature_tree`, `se_geometry` (cuerpo, caras, aristas), `se_measure`, `se_physical` (masa, volumen, CDG, inercia), `se_selection` |
| Vista | `se_view` (encuadre, orientación, modo de visualización, **captura PNG**) |
| Bocetos | `se_sketch` (líneas, rectángulos, polilíneas, círculos, arcos, eje, cierre validado), `se_ref_plane` |
| Pieza | `se_extrude`, `se_revolve`, `se_hole`, `se_round`, `se_chamfer`, `se_pattern` (rectangular, circular, simetría), `se_shell`, `se_draft`, `se_loft_sweep` (solevado, barrido, hélice; protrusión y vaciado), `se_feature_edit` |
| Chapa | `se_sheetmetal` (ajustes, pestaña, brida de contorno, brida, dobladillo, rotura de esquina, desplegado), `se_sheetmetal_form` (embutido, recorte embutido, recorte normal, grabado, desvío, pliegue, cordón) |
| Conjuntos | `se_assembly` (insertar, mover, girar, fijar, reemplazar, mostrar, suprimir, borrar), `se_relations`, `se_interference`, `se_bom` |
| Planos | `se_drawing` (hojas; vistas base, proyectada, auxiliar, sección y detalle; cotas; recuperar cotas; lista de piezas; actualizar) |
| Exportación | `se_export` (STEP, IGES, Parasolid, STL, JT, SAT, 3MF, PDF, DXF, DWG y desplegado de chapa) |
| Acceso total | `se_com_describe`, `se_com_get`, `se_com_set`, `se_com_invoke`, `se_com_items`, `se_com_constants` (cualquier objeto y método del API) y `se_command` (cualquier orden de la interfaz) |

Lo que no tiene herramienta específica se hace con la capa `se_com_*` o con `se_command`. El
detalle de qué cubre cada vía está en [docs/COBERTURA.md](docs/COBERTURA.md).

## Requisitos

- Windows x64 con Solid Edge instalado y con licencia (la Community Edition sirve).
- SDK de .NET 10 para compilar (`global.json`).

## Instalación

```powershell
# 1. Interop desde las type libraries de tu Solid Edge (no se distribuye; va a lib\interop)
.\scripts\generate-interop.ps1
# 2. Compilar y publicar
dotnet publish src\SolidEdgeMcp.Server -c Release -r win-x64 --self-contained -o publish
# 3. Política: copia el ejemplo y ajusta las carpetas permitidas
Copy-Item config\policy.example.json "$env:LOCALAPPDATA\SolidEdgeMcp\policy.json"
```

Opcional: `.\scripts\dump-api.ps1` vuelca el API a `reference\api\*.txt` para consultar firmas.

## Configuración

`policy.json` (admite comentarios) se busca en `--policy <ruta>`, en la variable
`SOLID_EDGE_MCP_POLICY` o en `%LocalAppData%\SolidEdgeMcp\policy.json`:

- `allowedRoots`: únicas carpetas donde se abren, guardan, exportan e insertan archivos. La carpeta
  de trabajo `%LocalAppData%\SolidEdgeMcp\work` se permite siempre.
- `startSolidEdgeIfNotRunning`, `operationTimeoutSeconds`, `startupTimeoutSeconds` y `limits`.

Ver [config/policy.example.json](config/policy.example.json). Los logs van a
`%LocalAppData%\SolidEdgeMcp\logs`.

## Registro en clientes

Claude Code:

```powershell
claude mcp add -s user solid-edge -- "<ruta>\publish\SolidEdgeMcp.Server.exe" --policy "<ruta>\policy.json"
```

Claude Desktop (`claude_desktop_config.json`):

```json
{ "mcpServers": { "solid-edge": { "command": "<ruta>\\publish\\SolidEdgeMcp.Server.exe", "args": ["--policy", "<ruta>\\policy.json"] } } }
```

Codex (`~/.codex/config.toml`):

```toml
[mcp_servers.solid_edge]
command = "<ruta>\\publish\\SolidEdgeMcp.Server.exe"
args = ["--policy", "<ruta>\\policy.json"]
tool_timeout_sec = 300
```

## Seguridad

La única restricción es de carpetas: toda ruta de archivo pasa por un filtro que rechaza lo que
quede fuera de `allowedRoots` (también rutas `\\?\`, flujos alternativos, nombres de dispositivo y
enlaces que salgan de la carpeta). La capa genérica (`se_com_*`) y `se_command` pueden hacer
cualquier cosa que permita el API, por eso se anuncian como destructivas para que el cliente pida
aprobación. El texto leído de los documentos se trata como datos, no como instrucciones.

## Limitaciones conocidas

- Algunas funciones del API de Solid Edge 2026 fallan o dañan el documento (cerrar esquinas,
  cartelas, persianas, varios tipos de dobladillo…); están listadas en
  [docs/COBERTURA.md](docs/COBERTURA.md).
- Las órdenes de `se_command` que piden selección o un cuadro de diálogo esperan a la persona.

## Desarrollo

```powershell
dotnet build SolidEdgeMcp.slnx
dotnet test --project tests\SolidEdgeMcp.Core.Tests\SolidEdgeMcp.Core.Tests.csproj
dotnet test --project tests\SolidEdgeMcp.Server.Tests\SolidEdgeMcp.Server.Tests.csproj
```

Los tests no necesitan Solid Edge; la validación de las herramientas se hace contra Solid Edge
real. Reglas del proyecto en [AGENTS.md](AGENTS.md); estado y decisiones en
[ESTADO.md](ESTADO.md); plan en [PLAN.md](PLAN.md).

Solid Edge es una marca de Siemens Digital Industries Software. Este proyecto no está afiliado a
Siemens ni distribuye ninguno de sus archivos: la interop se genera en cada equipo desde la
instalación local.
