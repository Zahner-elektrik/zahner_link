// Cycling a capacitor.
//
// Follows the Python CapacitorCycling notebook: charge and discharge repeatedly between two steps.
// Doing that with one polarization job per half cycle costs a round trip every time; FastCyclingJob
// performs the whole series on the instrument and returns one dataset, which is what makes ageing
// experiments over many cycles practical.
//
//     dotnet run -- <host> [port] [user] [password]

using System;
using System.Globalization;
using Zahner.Link;
using Zahner.Link.Jobs;

internal static class Program
{
    private const string Potentiostat = "MAIN:1:POT";

    /// <summary>Charging current. Small enough to be harmless on any cell.</summary>
    private const double Current = 200e-6;

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

        // --- One job per half cycle ------------------------------------------------------
        // The straightforward way: charge, discharge, repeat. Every step is a separate job, so
        // every step costs a round trip. Append joins the steps into one continuous dataset and
        // continues the time axis; the dead time between the jobs is not part of the data.
        SwitchOn(link, PotentiostatCoupling.Galvanostatic, 0.0);
        DcDataset? chained = null;
        for (int cycle = 0; cycle < 2; cycle++)
        {
            foreach (double step in new[] { +Current, -Current })
            {
                var data = (DcDataset)Polarize(link, step, 1.0);
                if (chained is null)
                {
                    chained = data;
                }
                else
                {
                    chained.Append(data);
                    data.Dispose();
                }
            }
        }

        TrySwitchOff(link);
        Console.WriteLine($"job per half cycle: {chained!.RowCount} rows in one dataset");
        measurement.AppendDataset(chained);
        chained.Dispose();

        // --- The same thing as one job ------------------------------------------------------
        // FastCyclingJob alternates between the two steps itself and ends at EndValue, which is
        // the state the cell is left in when the job finishes.
        SwitchOn(link, PotentiostatCoupling.Galvanostatic, 0.0);
        using (var parameters = new FastCyclingParameters
               {
                   FirstStep = +Current,
                   SecondStep = -Current,
                   EndValue = 0.0,              // leave the cell at zero current
                   StepTime = 1.0,
                   OutputDataRate = 50,
                   NumCycles = 3,
                   Autorange = true,
                   CurrentRange = 0.1,
                   // The cell must not be driven outside this band, whatever the steps ask for.
                   // With nothing connected the potential runs into the instrument's limit instead,
                   // which is what an open circuit does under a current step.
                   TurnLimitCheck = true,
                   LowerTurnBoundary = rest - 0.25,
                   UpperTurnBoundary = rest + 0.25,
               })
        using (var job = new FastCyclingJob(parameters))
        {
            link.DoJob(job);
            using DataSet data = link.GetJobResultData(job);
            var dc = (DcDataset)data;
            double[] time = dc.GetTrack(TrackNames.Time);
            double[] voltage = dc.GetTrack(TrackNames.Voltage);
            Console.WriteLine($"fast cycling: {time.Length} rows over {Format(time[time.Length - 1])} s, "
                              + $"U {Format(Min(voltage))} .. {Format(Max(voltage))} V");
            measurement.AppendDataset(data);
        }

        TrySwitchOff(link);

        using (var exporter = new ZXmlExporter { CompactXml = false })
        {
            exporter.SaveAsFileStandalone(measurement, "cycling.zmx");
            Console.WriteLine($"saved cycling.zmx with {measurement.DatasetCount} datasets");
        }

        link.Disconnect();
        Console.WriteLine("done");
        return 0;
    }

    private static DataSet Polarize(ZahnerLink link, double current, double seconds)
    {
        using var parameters = new PogaParameters
        {
            Bias = current,
            Duration = seconds,
            OutputDataRate = 50,
            Autorange = true,
            CurrentRange = 0.1,
        };
        using var job = new PogaJob(parameters);
        link.DoJob(job);
        return link.GetJobResultData(job);
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
