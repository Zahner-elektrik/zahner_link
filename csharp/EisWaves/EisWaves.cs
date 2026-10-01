// The waveforms behind an impedance spectrum.
//
// Follows the Python EisWaves notebook. An impedance point is not measured directly: the
// instrument applies a sine, records the voltage and current waveforms, and derives the impedance
// from them. Those raw waveforms come back with the result, which is what to look at when a
// spectrum has a point that makes no sense -- distortion, clipping or noise is visible there and
// nowhere else.
//
// Waves belong to a wave id, not to a row: a multisine excitation measures several frequencies at
// once, so all of its rows share one wave. A path therefore holds fewer waves than the spectrum
// has points, and the way from a row to its wave goes through GetWaveForRow.
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
        SwitchOn(link, rest);

        using (var parameters = new EisGenerateParameters
               {
                   Bias = rest,
                   MinFrequency = 100,
                   MaxFrequency = 10e3,
                   StartFrequency = 1e3,
                   PointsPerDecadeUpper = 8,
                   PointsPerDecadeLower = 5,
                   PreDuration = 0.1,
                   PreWaves = 1,
                   MeasDuration = 0.1,
                   MeasWaves = 5,               // at least five full sines per point
                   Amplitude = 0.02,            // 20 mV
               })
        using (var job = new EisGenerateJob(parameters))
        {
            link.DoJob(job);
            using DataSet data = link.GetJobResultData(job);
            var eis = (EisDataset)data;

            // --- The spectrum ----------------------------------------------------------
            double[] frequencies = eis.Frequencies;
            Complex[] impedances = eis.Impedances[0].GetComplexImpedanceTrack();
            Console.WriteLine($"spectrum: {frequencies.Length} points");
            for (int i = 0; i < frequencies.Length; i += Math.Max(1, frequencies.Length / 4))
            {
                Console.WriteLine($"   {Format(frequencies[i]),10} Hz  |Z| = {Format(impedances[i].Magnitude),10} Ohm"
                                  + $"  phase = {Format(impedances[i].Phase * 180 / Math.PI),8} deg");
            }

            // --- The waveforms behind it -------------------------------------------------
            // WaveCount is the number of distinct waves of a path, not the number of rows: rows
            // that were measured with one excitation share a wave. This is a single sine sweep, so
            // here every row has its own wave and the two numbers happen to agree.
            PathEntry? voltage = FindPath(eis, TrackNames.Voltage);
            PathEntry? current = FindPath(eis, TrackNames.Current);
            if (voltage is null || current is null)
            {
                Console.WriteLine("this workstation returned no waveform paths");
            }
            else
            {
                Console.WriteLine($"waveform paths: '{voltage.Key}' and '{current.Key}', "
                                  + $"{voltage.WaveCount} distinct waves for {frequencies.Length} points");

                // GetWaveForRow takes a row of the spectrum, looks its wave id up and returns the
                // matching wave. Rows sharing a wave get the same samples back -- which is what
                // makes this correct for a multisine result as well, where indexing the wave table
                // with the row number would read the wrong wave.
                int row = frequencies.Length / 2;           // a point in the middle of the sweep
                double[] voltageWave = voltage.GetWaveForRow(row);
                double[] currentWave = current.GetWaveForRow(row);

                // The number of support points is not fixed, it changes with the measured
                // frequency, so the time axis is derived from the length of this wave. For a single
                // sine point all measured periods were averaged into the one period stored here.
                double samplePeriod = 1.0 / (frequencies[row] * voltageWave.Length);

                Console.WriteLine($"row {row} at {Format(frequencies[row])} Hz uses wave id "
                                  + $"{eis.GetWaveIdForRow(row)}: {voltageWave.Length} samples, "
                                  + $"{Format(voltageWave.Length * samplePeriod)} s long");
                Console.WriteLine($"   U {Format(Min(voltageWave))} .. {Format(Max(voltageWave))} V");
                Console.WriteLine($"   I {Format(Min(currentWave))} .. {Format(Max(currentWave))} A");

                // The wave id of every row, straight out of the meta columns. It comes back empty
                // on a workstation without multisine support; GetWaveIdForRow then returns the row
                // index itself, which is the correct mapping there, so nothing above changes.
                double[] waveIds = eis.WaveIds;
                if (waveIds.Length == 0)
                {
                    Console.WriteLine("   no wave id column, this workstation stores one wave per row");
                }
                else
                {
                    Console.WriteLine($"   {waveIds.Length} rows referencing wave ids "
                                      + $"{Format(waveIds[0])} .. {Format(waveIds[waveIds.Length - 1])}");
                }
            }

            using var measurement = new Measurement();
            measurement.AppendDataset(data);
            using var exporter = new ZXmlExporter { CompactXml = false };
            exporter.SaveAsFileStandalone(measurement, "eis_waves.zmx");
            Console.WriteLine("saved eis_waves.zmx");
        }

        TrySwitchOff(link);
        link.Disconnect();
        Console.WriteLine("done");
        return 0;
    }

    private static PathEntry? FindPath(EisDataset eis, string name)
    {
        foreach (PathEntry path in eis.Paths)
        {
            if (path.Key == name)
            {
                return path;
            }
        }

        return null;
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
