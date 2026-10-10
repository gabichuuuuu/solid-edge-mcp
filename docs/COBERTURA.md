# Cobertura de Solid Edge

Qué funciones de Solid Edge se pueden usar desde el MCP y por qué vía. Comprobado con Solid Edge
Community Edition 2026 (226.00.01.004). Fecha de la última revisión: 2026-10-10.

## Cómo leer esta tabla

Toda función de Solid Edge es accesible por alguna de estas vías:

- **Alto nivel**: una herramienta `se_*` específica, en mm y grados, con verificación del resultado
  y reversión si falla. Es la vía preferida.
- **COM**: la capa genérica (`se_com_describe` para ver miembros y firmas, `se_com_get`,
  `se_com_set`, `se_com_invoke`, `se_com_items`, `se_com_constants`). Llega a cualquier objeto y
  método del API, en metros y radianes, sin verificación automática: hay que comprobar el
  resultado con `se_geometry`, `se_physical` o `se_view capture`.
- **Orden**: `se_command` lanza cualquier orden de la interfaz (`find` busca por nombre y entorno).
  Las órdenes que piden selección o un cuadro de diálogo esperan a la persona.

Estado: **✔** probado en Solid Edge 2026; **·** disponible en el API pero sin probar;
**✘** falla o daña el documento en Solid Edge 2026 (no usar).

## Aplicación, documentos y datos

| Función | Vía | Estado |
|---|---|---|
| Conectar, arrancar Solid Edge con licencia, reconectar | Alto nivel `se_session` | ✔ |
| Crear pieza, chapa, conjunto, plano, soldadura (plantilla opcional; sin plantilla `.pwd`, como en Solid Edge 2026, la soldadura es un conjunto con la plantilla de soldadura) | `se_documents new` | ✔ |
| Abrir, activar, guardar, guardar como, guardar copia, cerrar | `se_documents` | ✔ |
| Deshacer, rehacer, recalcular | `se_documents undo/redo/recompute` | ✔ (solo lo que Solid Edge registra; ver nota) |
| Propiedades del documento (resumen, personalizadas) | `se_properties` | ✔ |
| Variables y cotas, fórmulas | `se_variables` | ✔ |
| Árbol de operaciones; suprimir, borrar, renombrar | `se_feature_tree`, `se_feature_edit` | ✔ |
| Geometría (cuerpo, caras, aristas con filtros) | `se_geometry` | ✔ |
| Medir distancias y ángulos (en pieza y en conjunto) | `se_measure` | ✔ |
| Masa, volumen, CDG, inercia | `se_physical` | ✔ |
| Selección activa (leer, cambiar) | `se_selection` | ✔ |
| Vista: encuadrar, orientación, modo, captura PNG | `se_view` | ✔ |
| Modos de visualización Boundary y SmoothBoundary | — | ✘ Solid Edge 2026 los cambia por Smooth (error `not_supported`) |
| Exportar STEP, IGES, Parasolid, STL, JT, SAT, 3MF, PDF, DXF, DWG | `se_export` | ✔ |
| Material: bibliotecas, propiedades, asignar (actualiza densidad y masa) | `se_material` | ✔ |
| Crear o editar materiales y bibliotecas; galgas de chapa | COM (`Application.GetMaterialTable()`; `GetMaterialList` y `GetMatLibFileName` dan E_NOTIMPL) | · |
| Estilos de cara, capas | COM (`FaceStyles`, `Layers`) | · |
| Familias de piezas (`FamilyMembers`), configuraciones (`PartConfigurations`) | COM | · |
| PMI 3D (`PMI`), vistas con nombre (`NamedViews`), secciones en vivo | COM | · |
| Simulación y estudios generativos | COM / Orden | · |

## Bocetos y planos de referencia

| Función | Vía | Estado |
|---|---|---|
| Boceto en plano principal, plano de referencia o cara plana | `se_sketch create` | ✔ |
| Línea, rectángulo, polilínea, círculo, arco, elipse, spline, punto, redondeo de esquina | `se_sketch` | ✔ |
| Eje de revolución (también un lado del perfil: se usa una copia y el perfil sigue cerrado) | `se_sketch axis` | ✔ |
| Validar perfil cerrado o cadena abierta | `se_sketch close` | ✔ |
| Proyectar aristas del modelo al boceto | `se_sketch project` | ✔ |
| Relaciones 2D (horizontal, paralela, tangente, igual, simétrica…) y cotas (longitud, distancia real, ángulo interior, radio, diámetro) | `se_sketch_constraint` | ✔ |
| Texto y chaflán de boceto | COM (`Profile.TextProfiles`, `Lines2d`…) | · |
| Bocetos 3D | COM (`Sketches3D`) | · |
| Planos desplazados y en ángulo | `se_ref_plane` | ✔ |
| Planos por 3 puntos, normal a curva, tangentes; ejes y sistemas de coordenadas | COM (`RefPlanes`, `RefAxes`, `CoordinateSystems`) | · |

## Pieza (modelado ordenado)

| Función | Colección del API | Vía | Estado |
|---|---|---|---|
| Protrusión y vaciado por extrusión (finito, pasante, hasta siguiente, simétrico) | `ExtrudedProtrusions`, `ExtrudedCutouts` | `se_extrude` | ✔ |
| Revolución (protrusión y vaciado) | `RevolvedProtrusions`, `RevolvedCutouts` | `se_revolve` | ✔ |
| Agujeros (simple, avellanado, abocardado, roscado; pasante, hasta siguiente o finito) | `Holes`, `HoleDataCollection` | `se_hole` | ✔ |
| Redondeo y chaflán de aristas | `Rounds`, `Chamfers` | `se_round`, `se_chamfer` | ✔ |
| Patrón rectangular, circular; simetría | `Patterns`, `MirrorCopies` | `se_pattern` | ✔ |
| Vaciado de paredes (shell) | `Thinwalls` | `se_shell` | ✔ |
| Desmoldeo | `Drafts` | `se_draft` | ✔ |
| Solevado, barrido, hélice (protrusión y vaciado) | `LoftedProtrusions`, `SweptProtrusions`, `HelixProtrusions`… | `se_loft_sweep` | ✔ |
| Solevado con curvas guía o tangencias, barrido de varias trayectorias, hélice cónica o de paso variable, pared delgada | `LoftedProtrusions.Add`, `AddWithThinWall`, `HelixProtrusions.AddFinite` (opcionales) | COM | · |
| Rosca, nervio, red de nervios, labio, rejilla, saliente de montaje | `Threads`, `Ribs`, `WebNetworks`, `Lips`, `Vents`, `MountingBossCollection` | COM | · |
| Engrosar, dividir, operaciones booleanas entre cuerpos | `Thickens`, `Splits`, `Unions`, `Subtracts`, `Intersects`, `BooleanFeatures` | COM | · |
| Primitivas (caja, cilindro, esfera), tubos, ranuras | `BoxFeatures`, `CylinderFeatures`, `SphereFeatures`, `TubeFeatures`, `Slots` | COM | · |
| Pieza copiada, simetría de pieza, escalar cuerpo | `CopiedParts`, `MirrorParts`, `ScaleBodyFeatures` | COM | · |
| Patrón definido por el usuario, a lo largo de curva | `UserDefinedPatterns`, `PatternParts` | COM | · |

## Modelado síncrono y superficies

| Función | Vía | Estado |
|---|---|---|
| Cambiar entre síncrono y ordenado | COM (`ModelingMode` 1 síncrono, 2 ordenado) | ✔ |
| Mover, girar, desplazar, borrar y sustituir caras | COM (`FaceMoves`, `FaceRotates`, `FaceOffsets`, `DeleteFaces`, `ReplaceFaces`) | · |
| Superficies por extrusión, revolución, barrido, solevado, límites, desplazadas, cosidas | COM (`Constructions.ExtrudedSurfaces`, `SweptSurfaces`, `SurfaceByBoundaries`, `StitchSurfaces`…) | · |
| Curvas (intersección, proyectadas, helicoidales, por tabla) | COM (`Constructions.IntersectionCurves`, `ProjectCurves`, `HelicalCurves`…) | · |

## Chapa

| Función | Vía | Estado |
|---|---|---|
| Espesor, radio, alivios y factor neutro | `se_sheetmetal settings/set_settings` | ✔ |
| Pestaña base y adicional | `se_sheetmetal tab` | ✔ |
| Brida de contorno base (siempre hacia la normal del boceto) | `se_sheetmetal contour_flange` | ✔ |
| Brida (varias aristas, ángulo, lado, radio) | `se_sheetmetal flange` | ✔ |
| Dobladillo cerrado, abierto, en S, bucle cerrado | `se_sheetmetal hem` | ✔ |
| Rotura de esquina redonda o en chaflán | `se_sheetmetal break_corner` | ✔ |
| Desplegado y tamaño de corte; DXF del desplegado | `se_sheetmetal flatten`, `se_export flatPattern` | ✔ |
| Embutido, recorte embutido, recorte normal, grabado, desvío, pliegue, cordón U/V | `se_sheetmetal_form` | ✔ |
| Brida de contorno sobre arista, brida solevada, brida multiarista | COM (`ContourFlanges.AddSync`, `LoftedFlanges`, `MultiEdgeFlanges`) | · |
| Desdoblar y redoblar, cambiar ángulo de pliegue, redimensionar pliegue | COM (`Unbends`, `Rebends`, `ChangeBendAngles`, `ResizeBends`) | · |
| Pestaña y ranura, cerramientos, rasgado de aristas, convertir a chapa | COM (`TabAndSlotFeatures`, `Enclosures`, `RipEdges`, `ConvToSMs`) | · |
| Cerrar esquinas (`CloseCorners`) | — | ✘ RPC_E_SERVERFAULT; gráficos rotos hasta reiniciar |
| Cartelas (`Gussets`) | — | ✘ corrompe el documento |
| Persianas (`Louvers`) | — | ✘ no se resuelve |
| Dobladillos rizo, bucle abierto y bucle centrado | — | ✘ fallan (RPC_E_SERVERFAULT) |
| Dobladillo en S o bucle sobre una arista contigua a otro dobladillo | — | ✘ RPC_E_SERVERFAULT (se deshace) |
| Cordón circular; extremos punzonado y conformado | — | ✘ sin efecto o E_FAIL |
| Brida en modelado ordenado; `DimSide`; recorte normal `igBoth` o finito | — | ✘ |

## Conjuntos

| Función | Vía | Estado |
|---|---|---|
| Insertar, mover, girar, fijar, reemplazar, mostrar/ocultar, suprimir, borrar componentes | `se_assembly` | ✔ |
| Relaciones plana, axial, inserción, ángulo, tangencia; listar y borrar | `se_relations` | ✔ |
| Caras y aristas de cada ocurrencia (handles propios; la ocurrencia se deduce en relaciones y medidas) | `se_geometry source` = ocurrencia | ✔ |
| Interferencias | `se_interference` | ✔ |
| Lista de materiales | `se_bom` | ✔ |
| Patrones y simetrías de componentes | COM (`AssemblyPatterns`, `AssemblyMirrors`) | · |
| Operaciones de conjunto, piezas guiadas por el conjunto | COM (`AssemblyFeatures`, `AssemblyDrivenPartFeatures`) | · |
| Configuraciones, vistas explosionadas, grupos | COM (`Configurations`) / Orden | · |
| Bastidores, tuberías, cableado, sujeciones, soldadura | COM (`StructuralFrames`, `Pipes`, `Harnesses`, `FastenerSystems`, `WeldmentAssembly`) / Orden | · |

## Planos

| Función | Vía | Estado |
|---|---|---|
| Hojas: añadir, configurar, activar, borrar | `se_drawing` | ✔ |
| Vistas base, proyectada, auxiliar, sección y detalle; mover, escalar, borrar | `se_drawing` | ✔ |
| Cotas (longitud, distancia, diámetro, radio, ángulo) con colocación automática; recuperar cotas | `se_drawing` | ✔ |
| Lista de piezas | `se_drawing parts_list` | ✔ |
| Actualizar vistas | `se_drawing update` | ✔ |
| Globos, ejes, marcas de centro, cajas de texto, notas | COM (`Sheet.Balloons`, `CenterLines`, `CenterMarks`, `TextBoxes`, `Leaders`) | · |
| Acabado superficial, soldadura, tolerancias geométricas, referencias | COM (`SurfaceFinishSymbols`, `WeldSymbols`, `FeatureControlFrames`, `DatumFrames`) | · |
| Tablas de agujeros, de pliegues, bloques | COM (`HoleTables`, `DraftBendTables`, `Blocks`) | · |
| Geometría 2D en la hoja | COM (`Sheet.Lines2d`, `Circles2d`, `Arcs2d`…) | · |
| Exportar PDF, DXF, DWG | `se_export` | ✔ |

## Notas

- **Deshacer**: Solid Edge no registra como paso deshacible una operación creada por el API a partir
  de un boceto, y crear un boceto vacía el historial; tras eso, en esa sesión del documento tampoco
  registra agujeros ni borrados (en un documento reabierto sí). `se_documents undo/redo` consultan
  `UndoTransactionCount`/`RedoTransactionCount`, dan `nothing_to_undo` si no hay pasos y comprueban
  que el contador baja. Para quitar una operación, `se_feature_edit delete/suppress`.
