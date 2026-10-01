// Current-voltage curves.
//
// Follows the Python CurrentVoltageCurves notebook: the three ways of sweeping an operating point.
// A linear ramp changes it continuously, a steady-state staircase steps and waits for the cell to
// settle, and cyclic voltammetry sweeps back and forth between two vertices.
//
//     dotnet run -- <host> [port] [user] [password]

using System;
using System.Globalization;
using Zahner.Link;
using Zahner.Link.Jobs;

internal static class Program
{
    private const string Potentiostat = "MAIN:1:POT";

    /// <summary>Sweep width around the rest potential. Small enough for any cell.</summary>
    private const double Window = 0.05;

    private static int Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "169.254.73.242";
        string port = args.Length > 1 ? args[1] : "1994";
        string user = args.Length > 2 ? args[2] : "";
        string password = args.Length > 3 ? args[3] : "";

        using var link = new ZahnerLink(host, port, ConnectionFlags.None, user, password);
        try
        {
            link.Connect();
            Console.WriteLine("connected successfully");
        }
        catch (ZahnerLinkException e)
        {
            Console.WriteLine($"failed to connect: {e.Message}");
            return 1;
        }

        TrySwitchOff(link);
        double rest = MeasureRestPotential(link);
        Console.WriteLine($"rest potential: {Format(rest)} V");

        using var measurement = new Measurement();

        // --- Linear ramp -----------------------------------------------------------------
        // The setpoint moves continuously at a fixed scan rate. The notebook ramps a current from
        // 0 A to 1 A; here the potential is swept across a narrow window around the rest
        // potential, which is safe on any cell.
        SwitchOn(link, PotentiostatCoupling.Potentiostatic, rest);
        using (var parameters = new RampParameters
               {
                   StartValue = rest,
                   EndValue = rest + Window,
                   ScanRate = 0.05,             // 50 mV per second
                   StepHeight = 0,              // let the instrument choose the smallest step
                   OutputDataRate = 10,
                   Autorange = true,
                   CurrentRange = 0.1,
               })
        using (var job = new RampJob(parameters))
        {
            link.DoJob(job);
            using DataSet data = link.GetJobResultData(job);
            Report("ramp", data);
            measurement.AppendDataset(data);
        }

        TrySwitchOff(link);

        // --- Steady-state staircase --------------------------------------------------------
        // Steps to the next value and waits there until the current stops changing, or until
        // StepTime runs out. Slower than a ramp, but each point is a settled one. A stop condition
        // ends the staircase early should the current ever exceed 1 A.
        SwitchOn(link, PotentiostatCoupling.Potentiostatic, rest);
        using StopCondition currentLimit = StopCondition.MinMaxLimit(TrackNames.Current, minimum: -1, maximum: 1);
        string[] stopConditionNames = { "|current| <= 1 A" };
        using (var parameters = new SteadyStairsParameters
               {
                   StartValue = rest,
                   EndValue = rest + Window,
                   StepTime = 2,                // give up waiting after 2 s
                   StepHeight = 0.01,           // 10 mV steps
                   HoldTime = 0.5,              // stay at least 0.5 s
                   StabilityTolerance = 0.01,   // "settled" means < 10 mA/s
                   OutputDataRate = 10,
                   Autorange = true,
                   CurrentRange = 0.1,
               })
        using (var job = new SteadyStairsJob(parameters))
        {
            job.AddStopCondition(currentLimit);
            try
            {
                link.DoJob(job);
            }
            catch (ZahnerLinkException e) when (e.ErrorCode == ErrorCode.StopConditionTriggered)
            {
                Console.WriteLine($"stopped on a stop condition: {e.Message}");
            }

            // When a stop condition ends the job, the first parameter of the job's error is the
            // index of the condition that triggered, in the order the conditions were added.
            ErrorObject? error = job.LastJobInfo?.Error;
            if (error is not null && error.Code == ErrorCode.StopConditionTriggered && error.Parameters.Count > 0)
            {
                Console.WriteLine($"   format string: {error.FormatString}");
                Console.WriteLine($"   parameters   : {string.Join(", ", error.Parameters)}");
                int index = Convert.ToInt32(error.Parameters[0], CultureInfo.InvariantCulture);
                Console.WriteLine($"   triggered    : {stopConditionNames[index]}");
            }

            using DataSet data = link.GetJobResultData(job);
            Report("steady staircase", data);
            measurement.AppendDataset(data);
        }

        TrySwitchOff(link);

        // --- Cyclic voltammetry ---------------------------------------------------------------
        // Sweeps from the start value to the first vertex, back to the second, and ends at
        // EndValue. NumCycles = 0.5 is a single pass rather than a full there-and-back cycle.
        SwitchOn(link, PotentiostatCoupling.Potentiostatic, rest);
        using (var parameters = new CvParameters
               {
                   StartValue = rest,
                   FirstVertex = rest + Window,
                   SecondVertex = rest - Window,
                   EndValue = rest,
                   ScanRate = 0.05,
                   OutputDataRate = 10,
                   NumCycles = 0.5,
                   Autorange = true,
                   CurrentRange = 0.1,
                   StepHeight = 0,              // continuous sweep rather than a staircase
                   IrDrop = 0,                  // no compensation for the cell's series resistance
                   // A safety net independent of the vertices: if the potential leaves this band
                   // the sweep turns around early. It has to be a real interval, so it cannot be
                   // left at its default of 0 .. 0 when the check is on.
                   TurnLimitCheck = true,
                   LowerTurnBoundary = rest - 2 * Window,
                   UpperTurnBoundary = rest + 2 * Window,
               })
        using (var job = new CvJob(parameters))
        {
            link.DoJob(job);
            using DataSet data = link.GetJobResultData(job);
            Report("cyclic voltammetry", data);
            measurement.AppendDataset(data);
        }

        TrySwitchOff(link);

        using (var exporter = new ZXmlExporter { CompactXml = false })
        {
            exporter.SaveAsFileStandalone(measurement, "curves.zmx");
            Console.WriteLine($"saved curves.zmx with {measurement.DatasetCount} datasets");
        }

        link.Disconnect();
        Console.WriteLine("done");
        return 0;
    }

    /// <summary>Prints the span a sweep actually covered -- what the notebook plots.</summary>
    private static void Report(string label, DataSet data)
    {
        var dc = (DcDataset)data;
        double[] voltage = dc.GetTrack(TrackNames.Voltage);
        double[] current = dc.GetTrack(TrackNames.Current);
        Console.WriteLine($"{label}: {voltage.Length} points, "
                          + $"U {Format(Min(voltage))} .. {Format(Max(voltage))} V, "
                          + $"I {Format(Min(current))} .. {Format(Max(current))} A");
    }

    private static double Min(double[] values)
    {
        double m = values[0];
        foreach (double v in values) { if (v < m) { m = v; } }
        return m;
    }

    private static double Max(double[] values)
    {
        double m = values[0];
        foreach (double v in values) { if (v > m) { m = v; } }
        return m;
    }

    private static double MeasureRestPotential(ZahnerLink link)
    {
        using var parameters = new OcvParameters { Duration = 1.0, OutputDataRate = 10.0 };
        using var job = new OcvJob(parameters);
        link.DoJob(job);
        using DataSet data = link.GetJobResultData(job);
        double[] voltage = ((DcDataset)data).GetTrack(TrackNames.Voltage);
        return voltage[voltage.Length - 1];
    }

    private static void SwitchOn(ZahnerLink link, PotentiostatCoupling coupling, double bias)
    {
        using var parameters = new SwitchOnParameters
        {
            Potentiostat = Potentiostat,
            Coupling = coupling,
            Bias = bias,
            VoltageRangeIndex = 0,
            ComplianceRangeIndex = 0,
        };
        using var job = new SwitchOnJob(parameters);
        link.DoJob(job);
    }

    private static void TrySwitchOff(ZahnerLink link)
    {
        try
        {
            using var parameters = new SwitchOffParameters { Potentiostat = Potentiostat };
            using var job = new SwitchOffJob(parameters);
            link.DoJob(job);
        }
        catch (ZahnerLinkException)
        {
        }
    }

    private static string Format(double value) => value.ToString("G4", CultureInfo.InvariantCulture);
}
