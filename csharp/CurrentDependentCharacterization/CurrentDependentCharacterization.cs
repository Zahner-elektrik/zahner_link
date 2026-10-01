// Characterising a device that changes while you measure it.
//
// Follows the Python CurrentDependentCharacterization notebook. The same CvJob is run twice --
// once potentiostatic, once galvanostatic -- to show that the job does not change, only the mode
// the potentiostat is in. Then impedance is measured repeatedly at one frequency, which tracks a
// device whose properties drift as current warms it up.
//
//     dotnet run -- <host> [port] [user] [password]

using System;
using System.Globalization;
using System.Numerics;
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

        // --- Potentiostatic cyclic voltammetry -----------------------------------------
        SwitchOn(link, PotentiostatCoupling.Potentiostatic, rest);
        using (DataSet data = RunCv(link, rest))
        {
            Report("potentiostatic CV", data);
            measurement.AppendDataset(data);
        }

        TrySwitchOff(link);

        // --- The same job, galvanostatic ------------------------------------------------
        // Nothing about the job changes. The potentiostat's coupling decides whether the sweep
        // values are volts or amperes, so the same code characterises a device either way.
        SwitchOn(link, PotentiostatCoupling.Galvanostatic, 0.0);
        using (DataSet data = RunCv(link, 0.0))
        {
            Report("galvanostatic CV", data);
            measurement.AppendDataset(data);
        }

        TrySwitchOff(link);

        // --- Impedance tracking at a single frequency --------------------------------------
        // A frequency table whose every entry is the same frequency turns an EIS job into a time
        // series: each entry is one impedance measurement, and the series shows how the device
        // changes while current flows through it.
        SwitchOn(link, PotentiostatCoupling.Potentiostatic, rest);
        using (var parameters = new EisFrequencyTableParameters { Bias = rest })
        {
            for (int i = 0; i < 20; i++)
            {
                parameters.AddEntry(frequency: 1e3, amplitude: 0.01, preDuration: 0.0,
                                    preWaves: 1, measDuration: 0.1, measWaves: 1);
            }

            using var job = new EisFrequencyTableJob(parameters);
            link.DoJob(job);
            using DataSet data = link.GetJobResultData(job);
            var eis = (EisDataset)data;

            Complex[] impedances = eis.Impedances[0].GetComplexImpedanceTrack();
            double[] times = eis.Times;
            Console.WriteLine($"impedance tracking: {impedances.Length} measurements over "
                              + $"{Format(times[times.Length - 1])} s");
            Console.WriteLine($"   first |Z| = {Format(impedances[0].Magnitude)} Ohm, "
                              + $"last |Z| = {Format(impedances[impedances.Length - 1].Magnitude)} Ohm");
            measurement.AppendDataset(data);
        }

        TrySwitchOff(link);

        using (var exporter = new ZXmlExporter { CompactXml = false })
        {
            exporter.SaveAsFileStandalone(measurement, "characterization.zmx");
            Console.WriteLine($"saved characterization.zmx with {measurement.DatasetCount} datasets");
        }

        link.Disconnect();
        Console.WriteLine("done");
        return 0;
    }

    private static DataSet RunCv(ZahnerLink link, double centre)
    {
        using var parameters = new CvParameters
        {
            StartValue = centre,
            FirstVertex = centre + Window,
            SecondVertex = centre - Window,
            EndValue = centre,
            ScanRate = 0.05,
            OutputDataRate = 50,
            NumCycles = 0.5,
            Autorange = true,
            CurrentRange = 0.1,
            StepHeight = 0,
            IrDrop = 0,
            TurnLimitCheck = true,
            LowerTurnBoundary = centre - 2 * Window,
            UpperTurnBoundary = centre + 2 * Window,
        };
        using var job = new CvJob(parameters);
        link.DoJob(job);
        return link.GetJobResultData(job);
    }

    private static void Report(string label, DataSet data)
    {
        var dc = (DcDataset)data;
        double[] voltage = dc.GetTrack(TrackNames.Voltage);
        double[] current = dc.GetTrack(TrackNames.Current);
        Console.WriteLine($"{label}: {voltage.Length} points, "
                          + $"U {Format(Min(voltage))} .. {Format(Max(voltage))} V, "
                          + $"I {Format(Min(current))} .. {Format(Max(current))} A");
    }

    private static double Min(double[] v)
    {
        double m = v[0];
        foreach (double x in v) { if (x < m) { m = x; } }
        return m;
    }

    private static double Max(double[] v)
    {
        double m = v[0];
        foreach (double x in v) { if (x > m) { m = x; } }
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
            using var job = new SwitchOffJob(new SwitchOffParameters { Potentiostat = Potentiostat });
            link.DoJob(job);
        }
        catch (ZahnerLinkException)
        {
        }
    }

    private static string Format(double value) => value.ToString("G4", CultureInfo.InvariantCulture);
}
