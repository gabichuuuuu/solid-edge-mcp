// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class VariableService
{
    private const int VarTypePhysical = 16;
    private static readonly string[] PhysicalTargets = ["Mass", "Volume", "Surface_Area"];

    /// <summary>
    /// The physical property variables of a part (Mass, Volume, Surface_Area; type 16 in Variables.Query) do not work as the
    /// target of Solid Edge's Goal Seek. Verified in Solid Edge 2026 on a 100 x 60 x 20 steel block: "Mass" failed with E_FAIL
    /// after one step and left the depth changed, and "Volume" stopped after 0 iterations, because those variables change only
    /// when the physical properties are computed. Find also returns them by name. For them the server searches itself (see
    /// <see cref="MeasuredGoalSeek"/>), unless only a user variable has that name.
    /// </summary>
    private static string? PhysicalTarget(SolidEdgeDocument document, string name)
    {
        if (document is not (SolidEdgePart.PartDocument or SolidEdgePart.SheetMetalDocument)
            || PhysicalTargets.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) is not { } target)
        {
            return null;
        }

        try
        {
            if (Late.Items(Query(document, "*", VarTypePhysical)).Any(v => string.Equals(NameOf(v), target, StringComparison.OrdinalIgnoreCase)))
            {
                return target;
            }

            Find(document, name);
            return null;
        }
        catch (ToolException ex) when (ex.Code == "not_found")
        {
            return target;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return target;
        }
    }

    /// <summary>
    /// Goal Seek on the volume, area or mass (with the density of InspectService.Density) measured as se_physical does:
    /// secant steps on 'change', each one set in the model and measured, starting 5 % away from its value. A value Solid Edge
    /// rejects, or a model it cannot measure, halves the step. Reached when within 1e-7 of the value; otherwise 'change' is
    /// put back.
    /// </summary>
    private static object MeasuredGoalSeek(SeContext context, SolidEdgeDocument document, string target, object changed, double value,
        double seconds, int limit)
    {
        ModelDocument model = ModelDocument.From(document);
        double density = 0;
        if (target == "Mass")
        {
            density = InspectService.Density(context, model, model.RequireModel()).Density;
            if (density <= 0)
            {
                throw new ToolException("no_density", "The part has no density, so its mass cannot be measured.",
                    "Give it a material (se_material) or a density first.");
            }
        }

        int units = target switch
        {
            "Volume" => (int)UnitTypeConstants.igUnitVolume,
            "Surface_Area" => (int)UnitTypeConstants.igUnitArea,
            _ => (int)UnitTypeConstants.igUnitMass,
        };
        double goal = ToDatabase(value, units);
        if (goal <= 0)
        {
            throw ToolException.InvalidArgument($"'value' must be positive for {target}.");
        }

        // Measured as se_physical measures (Body.Volume reads up to 0.5 % high on elliptical edges).
        double Measure()
        {
            (double volume, double area, double mass) = InspectService.Measure(model, density > 0 ? density : 1);
            return target switch
            {
                "Volume" => volume,
                "Surface_Area" => area,
                _ => mass,
            };
        }

        double? TrySet(double x)
        {
            try
            {
                Late.Set(changed, "Value", x);
                double now = Convert.ToDouble(Late.Get(changed, "Value"), CultureInfo.InvariantCulture);
                return Math.Abs(now - x) > 1e-9 * Math.Max(1, Math.Abs(x)) ? null : Measure();
            }
            catch (Exception ex) when (ex is COMException or ToolException && !ComErrors.IsGone(ex))
            {
                return null;
            }
        }

        string changeName = NameOf(changed)!;
        double start = Convert.ToDouble(Late.Get(changed, "Value"), CultureInfo.InvariantCulture);
        double volumeBefore = model.Volume();
        double tolerance = 1e-7 * goal;
        Stopwatch clock = Stopwatch.StartNew();
        double x0 = start, f0 = Measure() - goal;
        double bestX = x0, bestF = f0;
        double x1 = start != 0 ? start * 1.05 : ToDatabase(1, UnitsOf(changed));
        int steps = 0;
        bool flat = false;
        while (Math.Abs(bestF) > tolerance && steps < limit && clock.Elapsed.TotalSeconds < seconds)
        {
            steps++;
            if (TrySet(x1) is not { } measured)
            {
                x1 = (x0 + x1) / 2;
                continue;
            }

            double f1 = measured - goal;
            if (Math.Abs(f1) < Math.Abs(bestF))
            {
                (bestX, bestF) = (x1, f1);
            }

            if (f1 == f0)
            {
                flat = true;
                break;
            }

            // A flat stretch would throw the next value far away: at most four times the last step. A dimension stays positive.
            double step = -f1 * (x1 - x0) / (f1 - f0);
            double most = 4 * Math.Abs(x1 - x0);
            double next = x1 + Math.Clamp(step, -most, most);
            if (changed is Dimension && next <= 0)
            {
                next = x1 / 2;
            }

            (x0, f0, x1) = (x1, f1, next);
        }

        if (Math.Abs(bestF) > tolerance || TrySet(bestX) is not { } reached || Math.Abs(reached - goal) > tolerance)
        {
            PutBack(changed, start);
            context.ModelChanged(document, $"Goal Seek put '{changeName}' back");
            string why = flat ? $"{target} does not change with '{changeName}'"
                : steps >= limit ? "iteration limit reached"
                : clock.Elapsed.TotalSeconds >= seconds ? "time limit reached"
                : "no closer value found";
            throw new ToolException("goal_not_reached",
                $"The search stopped with {target} at {Math.Round(FromDatabase(bestF + goal, units), 9)} {UnitLabel(units)} after {steps} "
                + $"steps ({why}); '{changeName}' was put back.",
                "Check that the value can be reached by changing that variable, or raise timeLimit or iterations.");
        }

        context.ModelChanged(document, $"Goal Seek changed '{changeName}'");
        RefreshPhysical(context, document);
        return new
        {
            target = new { name = target, value = Math.Round(FromDatabase(reached, units), 9), units = UnitLabel(units) },
            changed = Describe(context, document, changed),
            iterations = steps,
            seconds = Math.Round(clock.Elapsed.TotalSeconds, 3),
            measured = true,
            volumeChange = Geo.Mm3(model.Volume() - volumeBefore),
        };
    }

    /// <summary>
    /// The physical property variables keep the values of the last stored computation, and exist only once there was one (a
    /// material applied). Verified in Solid Edge 2026: Volume stayed 120000 mm3 after the depth of a block went from 20 to 15,
    /// also after se_physical (which does not store); Model.ComputePhysicalProperties made it 90000. Refreshed with the density
    /// and accuracy already stored (or the material's density, when none was stored yet); a part without either is left as it is.
    /// </summary>
    private static void RefreshPhysical(SeContext context, SolidEdgeDocument document)
    {
        if (document is not (SolidEdgePart.PartDocument or SolidEdgePart.SheetMetalDocument)
            || ModelDocument.From(document) is not { } part || part.TryModel() is not { } model)
        {
            return;
        }

        double accuracy = InspectService.StoredSettings(model).Accuracy;
        double density = InspectService.Density(context, part, model).Density;
        if (density <= 0)
        {
            return;
        }

        Array cog = new double[3];
        Array cov = new double[3];
        Array global = new double[6];
        Array principal = new double[3];
        Array axes = new double[9];
        Array radii = new double[3];
        try
        {
            model.ComputePhysicalProperties(density, accuracy is > 0 and < 1 ? accuracy : 0.99, out _, out _, out _, ref cog, ref cov,
                ref global, ref principal, ref axes, ref radii, out _, out _);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // The variables keep their last values; the physical properties tool still measures the model.
        }
    }

    private static bool IsPhysical(SolidEdgeDocument document, string name)
    {
        try
        {
            return document is SolidEdgePart.PartDocument or SolidEdgePart.SheetMetalDocument
                && Late.Items(Query(document, "*", VarTypePhysical)).Any(v => string.Equals(NameOf(v), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return false;
        }
    }
}
