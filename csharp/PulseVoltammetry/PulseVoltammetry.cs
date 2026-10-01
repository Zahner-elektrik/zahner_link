// Pulse voltammetry.
//
// Follows the Python PulseVoltammetry notebook: differential pulse voltammetry steps the potential
// and superimposes a short pulse on each step; normal pulse voltammetry returns to a base value
// between pulses. Both are sensitive to faradaic current because the capacitive part of the
// response has decayed by the time the sample is taken.
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

        // --- Setting the operating point ------------------------------------------------
        // SetBiasJob moves the output of an already-running potentiostat without switching it off
        // and on again.
        SwitchOn(link, rest);
        using (var parameters = new SetBiasParameters { Potentiostat = Potentiostat, Bias = rest })
        using (var job = new SetBiasJob(parameters))
        {
            link.DoJob(job);
            Console.WriteLine($"bias set to {Format(rest)} V");
        }

        // --- Differential pulse voltammetry ------------------------------------------------
        // The staircase advances by StepValue; on top of every step sits a pulse of PulseValue
        // for PulseTime. The measured quantity is the difference between the current just before
        // and just after the pulse.
        using (var parameters = new DpvParameters
               {
                   StartValue = rest,
                   StepValue = 0.005,           // 5 mV per step
                   PulseValue = 0.02,           // 20 mV pulse on each step
                   EndValue = rest + Window,
                   StepTime = 0.5,
                   PulseTime = 0.1,
                   InvertPulse = false,
                   OutputDataRate = 200,
                   CurrentRange = 0.1,
               })
        using (var job = new DpvJob(parameters))
        {
            link.DoJob(job);
            using DataSet data = link.GetJobResultData(job);
            Report("differential pulse", data);
            measurement.AppendDataset(data);
        }

        // --- Normal pulse voltammetry ---------------------------------------------------------
        // Every pulse starts from the same base value, so the cell relaxes between pulses instead
        // of accumulating the history of the whole sweep.
        using (var parameters = new NpvParameters
               {
                   BaseValue = rest,
                   StartValue = rest,
                   StepValue = 0.005,
                   EndValue = rest + Window,
                   StepTime = 0.5,
                   PulseTime = 0.1,
                   OutputDataRate = 200,
                   CurrentRange = 0.1,
               })
        using (var job = new NpvJob(parameters))
        {
            link.DoJob(job);
            using DataSet data = link.GetJobResultData(job);
            Report("normal pulse", data);
            measurement.AppendDataset(data);
        }

        TrySwitchOff(link);

        using (var exporter = new ZXmlExporter { CompactXml = false })
        {
            exporter.SaveAsFileStandalone(measurement, "pulse.zmx");
            Console.WriteLine($"saved pulse.zmx with {measurement.DatasetCount} datasets");
        }

        link.Disconnect();
        Console.WriteLine("done");
        return 0;
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

    private static void SwitchOn(ZahnerLink link, double bias)
    {
        using var parameters = new SwitchOnParameters
        {
            Potentiostat = Potentiostat,
            Coupling = PotentiostatCoupling.Potentiostatic,
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
