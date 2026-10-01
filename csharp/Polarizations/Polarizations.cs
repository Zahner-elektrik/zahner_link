// Polarization measurements and data handling.
//
// Follows the Python Polarizations notebook: switch on (twice, to show what a rejected job looks
// like), measure single integral values, run a potentiostatic and a galvanostatic polarization,
// combine both datasets, write them to a Zahner measurement file and read them back.
//
//     dotnet run -- <host> [port] [user] [password]

using System;
using System.Collections.Generic;
using System.Globalization;
using Zahner.Link;
using Zahner.Link.Jobs;

internal static class Program
{
    private const string Potentiostat = "MAIN:1:POT";

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

        // Whatever ran before may have left the potentiostat on, and a passive OCV refuses to
        // start then. Cheap to make sure; harmless when it is already off.
        TrySwitchOff(link);

        // Setpoints are relative to where the cell rests -- see the basic introduction.
        double rest = MeasureRestPotential(link);
        Console.WriteLine($"rest potential: {Format(rest)} V");

        // --- Error handling: switching on twice --------------------------------------
        // The second switch-on is rejected because the potentiostat is already on. The job object
        // carries the device's own reason, which is what to look at when something fails.
        using (var onParameters = new SwitchOnParameters
               {
                   Potentiostat = Potentiostat,
                   Coupling = PotentiostatCoupling.Potentiostatic,
                   Bias = rest,
                   VoltageRangeIndex = 0,
                   ComplianceRangeIndex = 0,
               })
        using (var switchOn = new SwitchOnJob(onParameters))
        {
            Console.WriteLine("first switch on");
            link.DoJob(switchOn);

            try
            {
                Console.WriteLine("second switch on");
                link.DoJob(switchOn);
            }
            catch (ZahnerLinkException e)
            {
                Console.WriteLine($"caught exception: {e.Message}");
                Console.WriteLine($"job status: {switchOn.LastStatus}");
                Console.WriteLine($"job error message: {switchOn.LastErrorMessage}");
            }
        }

        // --- Single measurements of current and voltage -------------------------------
        // "voltage" and "current" address the active potentiostat; the URN forms name the channel
        // explicitly. The try/catch is a courtesy, not an expectation: MAIN:1:POT:U is the main
        // potentiostat and every workstation should resolve it. A station that answers "unknown
        // channel" here has a gap in its symbolic-path resolution -- measured against a simulator
        // that accepts MAIN:1:POT for SwitchOnJob and 30661:POT:U for this job, but not
        // MAIN:1:POT:U for this job.
        foreach (string channel in new[] { "voltage", "current", "MAIN:1:POT:U", "MAIN:1:POT:I" })
        {
            using var parameters = new MeasureIntegralParameters { Channel = channel, Duration = 0.3 };
            using var job = new MeasureIntegralJob(parameters);
            try
            {
                link.DoJob(job);
                Console.WriteLine($"single integral measurement for {channel}: {Format(job.GetResult())}");
            }
            catch (ZahnerLinkException e)
            {
                Console.WriteLine($"channel {channel} not available here: {e.Message}");
            }
        }

        // --- Potentiostatic polarization ----------------------------------------------
        DataSet potentiostatic;
        using (var parameters = new PogaParameters
               {
                   Bias = rest + 0.01,
                   Duration = 5.0,
                   OutputDataRate = 25,
                   Autorange = true,
                   CurrentRange = 0.1,
               })
        using (var job = new PogaJob(parameters))
        {
            link.DoJob(job);
            potentiostatic = link.GetJobResultData(job);
        }

        // --- Switching to galvanostatic mode -------------------------------------------
        SwitchOff(link);
        using (var onParameters = new SwitchOnParameters
               {
                   Potentiostat = Potentiostat,
                   Coupling = PotentiostatCoupling.Galvanostatic,
                   Bias = -0.001,               // -1 mA
                   VoltageRangeIndex = 0,
                   ComplianceRangeIndex = 0,
               })
        using (var switchOn = new SwitchOnJob(onParameters))
        {
            link.DoJob(switchOn);
        }

        // --- Galvanostatic polarization -------------------------------------------------
        DataSet galvanostatic;
        using (var parameters = new PogaParameters
               {
                   Bias = -0.005,               // -5 mA
                   Duration = 5.0,
                   OutputDataRate = 25,
                   Autorange = true,
                   CurrentRange = 0.1,
               })
        using (var job = new PogaJob(parameters))
        {
            link.DoJob(job);
            galvanostatic = link.GetJobResultData(job);
        }

        SwitchOff(link);
        link.Disconnect();

        // --- Exporting both datasets into one Zahner file ---------------------------------
        using (var measurement = new Measurement())
        {
            measurement.AppendDataset(potentiostatic);
            measurement.AppendDataset(galvanostatic);
            using var exporter = new ZXmlExporter { CompactXml = false };
            exporter.SaveAsFileStandalone(measurement, "polarization.zmx");
            Console.WriteLine("saved polarization.zmx");
        }

        // --- Combining both polarizations into one dataset ---------------------------------
        // Append joins the tracks of two datasets and continues the time axis; the time spent
        // switching between potentiostatic and galvanostatic mode is not counted. A timeOffset
        // would shift the appended rows further. The dataset is extended in place, which is why
        // this comes after the export above.
        var complete = (DcDataset)potentiostatic;
        complete.Append((DcDataset)galvanostatic, timeOffset: 0);
        Console.WriteLine($"complete data length: {complete.RowCount} points");

        potentiostatic.Dispose();
        galvanostatic.Dispose();

        // --- Reading it back --------------------------------------------------------------
        using (var importer = new ZXmlImporter())
        using (Measurement imported = importer.ImportMeasurementFromFile("polarization.zmx", out _))
        {
            IReadOnlyList<DataSet> datasets = imported.GetDatasets();
            Console.WriteLine($"imported measurement has {datasets.Count} datasets");

            var completeImported = (DcDataset)datasets[0];
            completeImported.Append((DcDataset)datasets[1], timeOffset: 0);
            Console.WriteLine($"imported complete data length: {completeImported.RowCount} points");

            // The type may change when data is appended, since any data can be merged.
            DatasetInfo info = completeImported.GetInfo();
            Console.WriteLine($"measurement type: {info.JobType}");
            Console.WriteLine($"measurement short type: {info.JobTypeShort}");
            Console.WriteLine("tracks:");
            foreach (ColumnHeader column in completeImported.Columns)
            {
                Console.WriteLine($"\t{column.Dimension} in '{column.Unit}' of channel urn '{column.Urn}'");
            }

            double[] time = completeImported.GetTrack(TrackNames.Time);
            double[] voltage = completeImported.GetTrack(TrackNames.Voltage);
            double[] current = completeImported.GetTrack(TrackNames.Current);
            Console.WriteLine($"{time.Length} points, "
                              + $"U[0]={Format(voltage[0])} V, I[0]={Format(current[0])} A");

            foreach (DataSet dataset in datasets)
            {
                dataset.Dispose();
            }
        }

        Console.WriteLine("done");
        return 0;
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

    /// <summary>Switches off, ignoring the rejection when it already is.</summary>
    private static void TrySwitchOff(ZahnerLink link)
    {
        try
        {
            SwitchOff(link);
        }
        catch (ZahnerLinkException)
        {
        }
    }

    private static void SwitchOff(ZahnerLink link)
    {
        using var parameters = new SwitchOffParameters { Potentiostat = Potentiostat };
        using var job = new SwitchOffJob(parameters);
        link.DoJob(job);
    }

    private static string Format(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}
