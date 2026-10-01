// Electrochemical impedance spectroscopy.
//
// Follows the Python Eis notebook: a spectrum with automatically generated frequency points, then
// the same measurement driven by an explicit frequency table, one frequency measured at several
// operating points and collected into one dataset, and the results written to Zahner measurement
// files. The notebook plots the spectra; this prints them.
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
        Console.WriteLine($"rest potential: {Format(rest)} V");

        // EIS excites around a DC operating point. The notebook uses 0 V because its cell rests
        // there; in general the operating point is the cell's own rest potential.
        SwitchOn(link, rest);

        try
        {
            // --- EIS with automatically generated frequency points ---------------------
            using (var parameters = new EisGenerateParameters
                   {
                       Bias = rest,
                       MinFrequency = 10,
                       MaxFrequency = 1e5,
                       StartFrequency = 10e3,
                       PointsPerDecadeUpper = 8,
                       PointsPerDecadeLower = 5,
                       PreDuration = 0,
                       PreWaves = 1,
                       MeasDuration = 0.1,
                       MeasWaves = 4,
                       Amplitude = 1e-2,
                       // The default. Spelled out here because it is the knob for the drift
                       // correction: DcOnly keeps only the correction that works from the recorded
                       // periods, Off subtracts nothing and only reports the drift.
                       DriftCorrection = DriftCorrection.Full,
                   })
            using (var job = new EisGenerateJob(parameters))
            {
                link.DoJob(job);
                using DataSet data = link.GetJobResultData(job);
                PrintSpectrum("generated", (EisDataset)data);
                Save(data, "eis_generate_job.zmx");
            }

            // --- The same spectrum, lowest decades measured with one multisine -----------
            // A single sine sweep pays at least one full period per point, so the lowest decade
            // dominates the measurement time. Here everything from 1 Hz up to 100 Hz is excited at
            // once and evaluated from a single recording, which is also the case the spectral drift
            // correction is meant for. Running a normal spectrum first, as above, helps: the IM7
            // then already knows the current range and does not have to find it on the multisine.
            using (var parameters = new EisMultisineGenerateParameters
                   {
                       Bias = rest,
                       MinFrequency = 0.1,
                       MaxFrequency = 1e5,
                       StartFrequency = 10e3,
                       PointsPerDecadeUpper = 8,
                       PointsPerDecadeLower = 5,
                       PreDuration = 0,
                       PreWaves = 1,
                       Amplitude = 1e-2,
                       MeasDuration = 0.1,
                       MeasWaves = 4,
                       MultisinePreWaves = 0,
                       MultisineMeasWaves = 1,
                       MultisineDecades = 3,
                       MultisineStartingHarmonic = 1,
                       DriftCorrection = DriftCorrection.Full,
                   })
            using (var job = new EisMultisineGenerateJob(parameters))
            {
                link.DoJob(job);
                using DataSet data = link.GetJobResultData(job);
                PrintSpectrum("multisine", (EisDataset)data);
                Save(data, "eis_multisine_generate_job.zmx");
            }

            // --- Custom spectrum from an explicit frequency table -----------------------
            // Each entry carries its own amplitude and averaging, which is how a real measurement
            // spends more time on the noisy decades and less on the quiet ones.
            using (var parameters = new EisFrequencyTableParameters { Bias = rest })
            {
                parameters.AddEntry(frequency: 10, amplitude: 10e-3, preDuration: 0.1, preWaves: 1, measDuration: 0.5, measWaves: 3);
                parameters.AddEntry(frequency: 1e3, amplitude: 20e-3, preDuration: 0.1, preWaves: 1, measDuration: 0.5, measWaves: 3);
                parameters.AddEntry(frequency: 100e3, amplitude: 30e-3, preDuration: 0.1, preWaves: 1, measDuration: 0.5, measWaves: 3);

                using var job = new EisFrequencyTableJob(parameters);
                link.DoJob(job);
                using DataSet data = link.GetJobResultData(job);
                PrintSpectrum("table", (EisDataset)data);
                Save(data, "eis_table_job.zmx");
            }

            // --- The same table built from two arrays -----------------------------------
            double[] frequencies = { 10, 1e2, 1e3, 1e4, 1e5 };
            double[] amplitudes = { 10e-3, 5e-3, 10e-3, 100e-3, 100e-3 };

            using (var parameters = new EisFrequencyTableParameters { Bias = rest })
            {
                for (int i = 0; i < frequencies.Length; i++)
                {
                    parameters.AddEntry(frequencies[i], amplitudes[i], 0.1, 1, 0.5, 3);
                }

                using var job = new EisFrequencyTableJob(parameters);
                link.DoJob(job);
                using DataSet data = link.GetJobResultData(job);
                PrintSpectrum("array-built table", (EisDataset)data);
            }

            // --- One frequency at several operating points -------------------------------
            // The impedance at 100 Hz, measured at a series of biases. Append collects the
            // single-point results into one dataset, so the DC tracks of the paths line up with the
            // impedance: one row per operating point. The notebook sweeps the current up to 2 A on
            // its test cell; here the potential is stepped by 25 mV around the rest potential,
            // which is harmless on any cell.
            using (var biasSweep = new EisDataset())
            {
                for (int step = -2; step <= 2; step++)
                {
                    using var parameters = new EisFrequencyTableParameters { Bias = rest + step * 0.025 };
                    parameters.AddEntry(frequency: 100, amplitude: 10e-3, preDuration: 0.2, preWaves: 1, measDuration: 0.5, measWaves: 3);

                    using var job = new EisFrequencyTableJob(parameters);
                    link.DoJob(job);
                    using DataSet data = link.GetJobResultData(job);
                    biasSweep.Append((EisDataset)data);
                }

                double[] voltages = FindPath(biasSweep, TrackNames.Voltage).GetDcTrack();
                double[] currents = FindPath(biasSweep, TrackNames.Current).GetDcTrack();
                Complex[] impedances = biasSweep.Impedances[0].GetComplexImpedanceTrack();
                Console.WriteLine("bias sweep at 100 Hz: voltage; current; |Z|");
                for (int row = 0; row < impedances.Length; row++)
                {
                    Console.WriteLine($"   {Format(voltages[row])} V; {Format(currents[row])} A; {Format(impedances[row].Magnitude)} Ohm");
                }

                Save(biasSweep, "eis_bias_sweep.zmx");
            }
        }
        finally
        {
            TrySwitchOff(link);
            link.Disconnect();
        }

        Console.WriteLine("done");
        return 0;
    }

    /// <summary>Prints magnitude and phase per frequency -- what the notebook plots.</summary>
    private static void PrintSpectrum(string label, EisDataset data)
    {
        double[] frequencies = data.Frequencies;
        Complex[] impedances = data.Impedances[0].GetComplexImpedanceTrack();

        Console.WriteLine($"{label}: {frequencies.Length} points");
        for (int i = 0; i < frequencies.Length; i += Math.Max(1, frequencies.Length / 5))
        {
            Complex z = impedances[i];
            Console.WriteLine($"   {Format(frequencies[i]),12} Hz   |Z| = {Format(z.Magnitude),12} Ohm"
                              + $"   phase = {Format(z.Phase * 180.0 / Math.PI),9} deg");
        }
    }

    private static PathEntry FindPath(EisDataset data, string dimension)
    {
        foreach (PathEntry path in data.Paths)
        {
            if (path.Key == dimension)
            {
                return path;
            }
        }

        throw new InvalidOperationException($"the dataset has no {dimension} path");
    }

    private static void Save(DataSet data, string filename)
    {
        using var measurement = new Measurement();
        measurement.AppendDataset(data);
        using var exporter = new ZXmlExporter { CompactXml = false };
        exporter.SaveAsFileStandalone(measurement, filename);
        Console.WriteLine($"saved {filename}");
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

    private static string Format(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}
