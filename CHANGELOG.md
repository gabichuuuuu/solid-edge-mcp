# Registro de cambios

Los cambios relevantes de este proyecto se anotan aquí. El formato sigue
[Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y el proyecto usa
[versionado semántico](https://semver.org/lang/es/).

## [0.1.0-alpha] - sin publicar

Primera versión alfa, en repositorio privado. Validada contra Solid Edge Community Edition 2026
(detalle en [ESTADO.md](ESTADO.md)).

### Añadido

- Servidor MCP local por stdio (C#/.NET 10, SDK `ModelContextProtocol` 2.2.0) que controla Solid
  Edge por COM, con 40 herramientas `se_*`: sesión, documentos, datos, consulta, vista, bocetos,
  pieza, chapa, conjuntos, planos, exportación y acceso genérico al API (`se_com_*`, `se_command`).
- Política por `policy.json` y `PathGuard`: solo se abren, guardan, exportan e insertan archivos en
  las carpetas permitidas.
- Verificación de cada operación de modelado (estado y cambio de volumen) y reversión si falla.
- `se_material`: bibliotecas, propiedades y asignación de materiales.
- `se_loft_sweep`: solevados, barridos y hélices, como protrusión o vaciado.
- `se_sketch`: elipses, splines, puntos, redondeo de esquina y proyección de aristas;
  `se_sketch_constraint`: relaciones y cotas de boceto.
- `se_geometry`: resumen de una ocurrencia de conjunto en coordenadas del conjunto.
- Batería de aceptación contra Solid Edge real (`tests/SolidEdgeMcp.Acceptance`, guiones A01–A10
  y `run.ps1`), también ejecutable contra otra build del servidor con `-Server`.
- Publicación autocontenida win-x64 con archivos de bloqueo comunes a la compilación y la
  publicación.

### Corregido

- COM: traducción de los errores que .NET convierte en otras excepciones y `null` en los
  argumentos de objeto sin uso.
- Bocetos: cotas de distancia real y de ángulo interior, aristas proyectadas devueltas y perfil
  cerrado cuando un lado es el eje de revolución.
- Agujeros: ángulo de avellanado en grados y roscado de agujeros finitos.
- Vista: se rechazan los modos de visualización que Solid Edge no aplica.
- Material: las caras y aristas conservan sus handles tras aplicar un material.
- Conjuntos: handles de cara y arista propios de cada ocurrencia, y relación plana recreada en el
  sentido contrario cuando Solid Edge la deja insatisfecha.
- Documentos: se liberan los handles de los documentos descargados al cerrar otro; deshacer y
  rehacer se comprueban con el contador de transacciones de Solid Edge; las soldaduras se crean con
  la plantilla de conjunto soldado cuando no hay plantilla `.pwd`.
- Sesión: `status` ya no vuelve a engancharse tras `disconnect`.
- Chapa: la brida de contorno devuelve todas las operaciones que crea y rechaza el sentido
  inverso, que Solid Edge ignora; la pestaña solo ofrece las direcciones normal e inversa.
- Planos: encuadre y captura de la hoja entera, y aviso al exportar un plano sin guardar.
- Propiedades: se listan todos los conjuntos de propiedades aunque haya propiedades sin nombre.
- Aceptación: los guiones admiten puntos dentro de los valores de filtro de las rutas.

### Documentación

- `README.md`, `AGENTS.md`, política de ejemplo y matriz de cobertura
  ([docs/COBERTURA.md](docs/COBERTURA.md)).
- `ESTADO.md` con resultados de aceptación, hechos del API comprobados y ejecuciones de extremo a
  extremo con Claude Code y Codex.
- README reescrito, este registro de cambios y plantillas de issues y pull requests.
