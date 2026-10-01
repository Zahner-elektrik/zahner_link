// Extending measurements with stop conditions.
//
// Follows the Python StopConditions notebook: a galvanostatic charge that ends when a voltage
// limit is reached, and an open-circuit scan that ends once the potential has settled. A stop
// condition turns a fixed-duration measurement into one that ends when the cell says so. All
// steps are appended into one dataset, which is saved at the end.
//
//     dotnet run -- <host> [port] [user] [password]

using System;
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

        // --- Establishing consistent initial conditions --------------------------------
        TrySwitchOff(link);
        using (UserHardwareSettings config = HardwareSettingsHelper.GetConfigForMainPotentiostat())
        using (var job = new SetHardwareSettingsJob(config))
        {
            link.DoJob(job);
        }

        // --- Determining the open-circuit potential --------------------------------------
        // Two ways to the same number: a short scan, and a single integrating measurement. The
        // scan starts the dataset the following steps are appended to.
        double ocvFromScan;
        DcDataset measurementData;
        using (var parameters = new OcvParameters { Duration = 5.0, OutputDataRate = 10.0 })
        using (var job = new OcvJob(parameters))
        {
            link.DoJob(job);
            measurementData = (DcDataset)link.GetJobResultData(job);
            double[] voltage = measurementData.GetTrack(TrackNames.Voltage);
            ocvFromScan = voltage[voltage.Length - 1];
        }

        using (var parameters = new MeasureIntegralParameters { Channel = "voltage", Duration = 1.0 })
        using (var job = new MeasureIntegralJob(parameters))
        {
            link.DoJob(job);
            Console.WriteLine($"OCV from OcvJob         : {Format(ocvFromScan)} V");
            Console.WriteLine($"OCV from MeasureIntegral: {Format(job.GetResult())} V");
        }

        // --- Galvanostatic charge that ends at a voltage limit -----------------------------
        // The job is set up for 60 s, but the stop condition ends it as soon as the cell leaves
        // the window. Reaching the limit is the expected outcome, not a failure -- it arrives as
        // an exception carrying StopConditionTriggered.
        SwitchOn(link, PotentiostatCoupling.Galvanostatic, 0.0);
        using (var parameters = new PogaParameters
               {
                   Bias = 200e-6,               // 200 µA
                   Duration = 60.0,
                   OutputDataRate = 50,
                   Autorange = true,
                   CurrentRange = 0.1,
               })
        using (var job = new PogaJob(parameters))
        using (StopCondition window = StopCondition.MinMaxLimit(
                   TrackNames.Voltage, minimum: ocvFromScan - 0.05, maximum: ocvFromScan + 0.05))
        {
            job.AddStopCondition(window);

            try
            {
                link.DoJob(job);
                Console.WriteLine("charge ran to its full duration; the limit was never reached");
            }
            catch (ZahnerLinkException e) when (e.ErrorCode == ErrorCode.StopConditionTriggered)
            {
                Console.WriteLine($"stopped on the stop condition: {e.Message}");
            }

            using DataSet data = link.GetJobResultData(job);
            double[] time = ((DcDataset)data).GetTrack(TrackNames.Time);
            Console.WriteLine($"charge lasted {Format(time[time.Length - 1])} s of the 60 s requested");

            // The error of the last run says why the job ended. For a stop condition, its
            // parameters are the values that were substituted into the message.
            ErrorObject? error = job.LastJobInfo?.Error;
            if (error is not null && error.Code == ErrorCode.StopConditionTriggered)
            {
                Console.WriteLine($"   error code   : {error.Code}");
                Console.WriteLine($"   message      : {error.Message}");
                Console.WriteLine($"   format string: {error.FormatString}");
                Console.WriteLine($"   parameters   : {string.Join(", ", error.Parameters)}");
            }

            // Append continues the time axis of the data recorded so far.
            measurementData.Append((DcDataset)data);
        }

        TrySwitchOff(link);

        // --- Waiting for the potential to settle --------------------------------------------
        // Stop once the potential changes by less than 500 µV/s, but measure for at least 5 s.
        using (var parameters = new OcvParameters { Duration = 60.0, OutputDataRate = 10.0 })
        using (var job = new OcvJob(parameters))
        using (StopCondition settled = StopCondition.StabilityToleranceLimit(
                   TrackNames.Voltage, stabilityTolerance: 0.0005, minimumDuration: 5.0))
        {
            job.AddStopCondition(settled);

            try
            {
                link.DoJob(job);
                Console.WriteLine("relaxation ran its full duration; the potential never settled");
            }
            catch (ZahnerLinkException e) when (e.ErrorCode == ErrorCode.StopConditionTriggered)
            {
                Console.WriteLine($"potential settled: {e.Message}");
            }

            using DataSet data = link.GetJobResultData(job);
            double[] time = ((DcDataset)data).GetTrack(TrackNames.Time);
            Console.WriteLine($"relaxation lasted {Format(time[time.Length - 1])} s of the 60 s requested");
            measurementData.Append((DcDataset)data);
        }

        // --- Saving everything into one file ---------------------------------------------------
        using (var measurement = new Measurement())
        using (var exporter = new ZXmlExporter { CompactXml = false })
        {
            measurement.AppendDataset(measurementData);
            exporter.SaveAsFileStandalone(measurement, "charge.zmx");
            Console.WriteLine($"saved charge.zmx with {measurementData.RowCount} rows");
        }

        measurementData.Dispose();

        link.Disconnect();
        Console.WriteLine("done");
        return 0;
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
