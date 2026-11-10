// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Collections;
using System.Reflection;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tests;

/// <summary>The tables the services keep beside the enumerations of the tools must follow them.</summary>
public sealed class ArgumentTablesTests
{
    public static TheoryData<Type, IReadOnlyList<string>> NameLists => new()
    {
        { typeof(DimensionTolerance), DimensionService.Kinds },
        { typeof(LiveRule), FaceRelationService.LiveRules },
        { typeof(DocumentStatus), FilePropertyService.StatusNames },
        { typeof(AnnotationKind), PmiAnnotationService.Kinds },
        { typeof(SensorOperator), SensorService.Operators },
        { typeof(SimplifyAction), SimplifyService.Actions },
        { typeof(SimulationConstraint), SimulationService.ConstraintKinds },
        { typeof(SimulationLoad), SimulationService.LoadKinds },
        { typeof(DrawOrder), SketchService.Orders },
        { typeof(WindowStateKind), UiService.WindowStates },
    };

    [Theory]
    [MemberData(nameof(NameLists))]
    public void A_list_of_names_has_the_values_of_its_enumeration(Type enumeration, IReadOnlyList<string> names) =>
        Assert.Equal(Names(enumeration).Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));

    [Fact]
    public void A_table_keyed_by_an_enumeration_has_every_value()
    {
        var missing = new List<string>();
        int tables = 0;
        foreach (Type service in typeof(DrawingService).Assembly.GetTypes())
        {
            foreach (FieldInfo field in service.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Type? key = field.FieldType.GetInterfaces().Append(field.FieldType)
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                    .Select(i => i.GetGenericArguments()[0])
                    .FirstOrDefault(k => k.IsEnum && k.Namespace == typeof(ArgumentNames).Namespace);
                if (key is null || field.GetValue(null) is not IEnumerable table)
                {
                    continue;
                }

                tables++;
                var keys = new HashSet<object>(table.Cast<object>().Select(pair => pair.GetType().GetProperty("Key")!.GetValue(pair)!));
                missing.AddRange(Enum.GetValues(key).Cast<object>().Where(value => !keys.Contains(value))
                    .Select(value => $"{service.Name}.{field.Name}: {key.Name}.{value}"));
            }
        }

        // 'printer' is the paper of the printer itself: it has no Solid Edge constant.
        missing.Remove("PrintService.Papers: PrintPaper.Printer");

        Assert.True(tables > 0);
        Assert.Empty(missing);
    }

    [Fact]
    public void A_feature_control_frame_knows_every_characteristic()
    {
        foreach (GeometricCharacteristic characteristic in Enum.GetValues<GeometricCharacteristic>())
        {
            Assert.Contains(ArgumentNames.Snake(characteristic), DrawingService.Characteristics.Keys);
        }
    }

    private static IEnumerable<string> Names(Type enumeration) => Enum.GetValues(enumeration).Cast<Enum>().Select(ArgumentNames.Snake);
}
