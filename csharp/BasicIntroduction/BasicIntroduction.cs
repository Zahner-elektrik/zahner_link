// Getting started with the ZahnerLink.Net package.
//
// Follows the Python BasicIntroduction notebook: connect, measure, polarize, save the result,
// switch off. Two deliberate differences from the notebook are marked in the code below.
//
//     dotnet run -- <host> [port] [user] [password]

using System;
using System.Globalization;
using Zahner.Link;
using Zahner.Link.Jobs;

internal static class Program
{
    private const string Potentiostat = "MAIN:1:POT";

    /// <summary>Excursion from the measured rest potential. Small enough for any cell.</summary>
    private const double OffsetVolts = 0.01;

    private static int Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "169.254.73.242";
        string port = args.Length > 1 ? args[1] : "1994";
        string user = args.Length > 2 ? args[2] : "";
        string password = args.Length > 3 ? args[3] : "";

        // --- Connecting to the IM7 --------------------------------------------------
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

        // The notebook runs a DC calibration here. The published package does not contain the
        // calibration jobs -- they exist only in builds with ZL_WITH_INTERNAL_COMPONENTS, and
        // calling one raises EntryPointNotFoundException.

        // --- Measuring the rest potential --------------------------------------------
        // Open-circuit voltage: passive, so it needs no potentiostat. The polarization below is
        // expressed relative to this. The notebook uses an absolute 1 V, which suits the cell it
        // was written for; against a different cell an absolute setpoint is a large step, and the
        // potentiostat rejects it ("only reaches ... instead of ...").
        double restPotential;
        using (var parameters = new OcvParameters { Duration = 1.0, OutputDataRate = 10.0 })
        using (var ocv = new OcvJob(parameters))
        {
            link.DoJob(ocv);
            using DataSet data = link.GetJobResultData(ocv);
            double[] voltage = ((DcDataset)data).GetTrack(TrackNames.Voltage);
            restPotential = voltage[voltage.Length - 1];
        }

        Console.WriteLine($"rest potential: {restPotential.ToString("G6", CultureInfo.InvariantCulture)} V");

        // --- Switching on the potentiostat -------------------------------------------
        using (var parameters = new SwitchOnParameters
               {
                   Potentiostat = Potentiostat,
                   Coupling = PotentiostatCoupling.Potentiostatic,
                   Bias = restPotential,
                   VoltageRangeIndex = 0,
                   ComplianceRangeIndex = 0,
               })
        using (var switchOn = new SwitchOnJob(parameters))
        {
            link.DoJob(switchOn);
        }

        // --- Running a measurement ----------------------------------------------------
        using (var parameters = new PogaParameters
               {
                   Bias = restPotential + OffsetVolts,
                   Duration = 5.0,
                   OutputDataRate = 25,
                   Autorange = true,
                   CurrentRange = 0.1,
               })
        using (var polarization = new PogaJob(parameters))
        {
            link.DoJob(polarization);

            // --- Saving measurement data ----------------------------------------------
            using DataSet data = link.GetJobResultData(polarization);
            using var measurement = new Measurement();
            measurement.AppendDataset(data);

            using var exporter = new ZXmlExporter { CompactXml = false };
            exporter.SaveAsFileStandalone(measurement, "polarization.zmx");
            Console.WriteLine("saved polarization.zmx");
        }

        // --- Switching off the potentiostat -------------------------------------------
        using (var parameters = new SwitchOffParameters { Potentiostat = Potentiostat })
        using (var switchOff = new SwitchOffJob(parameters))
        {
            link.DoJob(switchOff);
        }

        // --- Disconnecting from the IM7 -----------------------------------------------
        link.Disconnect();
        Console.WriteLine("done");
        return 0;
    }
}
