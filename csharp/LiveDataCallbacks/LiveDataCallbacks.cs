// Live data while a measurement runs.
//
// Follows the Python LiveDataCallbacks notebook: subscribe to the events a running job produces,
// then run a DC and an impedance measurement and watch the data arrive. This is how a live plot or
// a progress display is fed -- the result data at the end is the same, only later. Finally a job
// runs without any stored result, delivering its data through the events alone.
//
//     dotnet run -- <host> [port] [user] [password]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Zahner.Link;
using Zahner.Link.Jobs;

internal static class Program
{
    private const string Potentiostat = "MAIN:1:POT";
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static int Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "169.254.73.242";
        string port = args.Length > 1 ? args[1] : "1994";
        string user = args.Length > 2 ? args[2] : "";
        string password = args.Length > 3 ? args[3] : "";

        using var link = new ZahnerLink(host, port, ConnectionFlags.None, user, password);

        // --- Subscribing to the events -------------------------------------------------
        // Ordinary .NET events. They are raised on the event pump's thread, so anything that
        // touches a UI has to marshal back to its own thread.
        link.ConnectionStatusChanged += (_, e) => Log($"connection: {e.Status}");
        link.JobStatusUpdate += (_, e) => Log($"job status: {e.Status}");
        link.JobDone += (_, e) =>
            Log($"job done: {e.JobInfo.JobId} - status: {e.JobInfo.Status}, "
                + $"result data discarded: {e.JobInfo.JobOptions.DiscardResultData}");

        link.LiveDataHeader += (_, e) =>
        {
            string columns = string.Join(", ", DimensionsOf(e.Header));
            Log($"DC header: {columns}");
        };

        int dcRows = 0;
        link.LiveDataRows += (_, e) =>
        {
            dcRows++;
            if (dcRows <= 3)
            {
                Log($"DC rows arrived ({dcRows})");
            }
        };

        link.LiveDataHeaderEis += (_, e) =>
        {
            // Every impedance, path and potentiostat header lists the tracks its group of an EIS
            // row carries; the order of the path headers is the order of every per-path wave list.
            var impedanceColumns = new List<string>();
            foreach (EisImpedanceHeader impedance in e.Header.Impedances)
            {
                foreach (ColumnHeader column in impedance.Columns)
                {
                    impedanceColumns.Add(column.Dimension);
                }
            }

            var metaColumns = new List<string>();
            foreach (EisMetaColumn column in e.Header.MetaColumns)
            {
                metaColumns.Add(column.Dimension);
            }

            var pathNames = new List<string>();
            foreach (EisPathHeader path in e.Header.Paths)
            {
                pathNames.Add(path.Name);
            }

            Log($"EIS header: {e.Header.Impedances.Count} impedance header(s), "
                + $"impedance columns: {string.Join(", ", impedanceColumns)}, "
                + $"meta columns: {string.Join(", ", metaColumns)}, paths: {string.Join(", ", pathNames)}");
        };

        int eisPoints = 0;
        link.LiveDataRowEis += (_, e) =>
        {
            eisPoints++;
            if (eisPoints <= 3)
            {
                Log($"EIS point {eisPoints}");
            }
        };

        int waveBlocks = 0;
        link.LiveDataWaves += (_, __) =>
        {
            waveBlocks++;
            if (waveBlocks <= 2)
            {
                Log("EIS waveform block");
            }
        };
        link.LiveDataFinished += (_, e) => Log($"live data finished for job {e.JobInfo.JobId}");

        try
        {
            link.Connect();
        }
        catch (ZahnerLinkException e)
        {
            Console.WriteLine($"failed to connect: {e.Message}");
            return 1;
        }

        // Without the pump nothing is delivered: it is what polls the connection and raises the
        // events above. StartEventPump is the safe default -- callbacks then run on their own
        // thread rather than on the library's service thread.
        link.StartEventPump();
        TrySwitchOff(link);

        // --- A DC measurement -------------------------------------------------------------
        Log("starting OCV");
        using (var parameters = new OcvParameters { Duration = 5.0, OutputDataRate = 5.0 })
        using (var job = new OcvJob(parameters))
        {
            link.DoJob(job);
            using DataSet data = link.GetJobResultData(job);
            Log($"OCV finished: {data.RowCount} rows, {dcRows} live batches seen");
        }

        // --- An impedance measurement -------------------------------------------------------
        SwitchOn(link, MeasureRestPotential(link));
        Log("starting EIS");
        using (var parameters = new EisGenerateParameters
               {
                   Bias = 0.0,
                   MinFrequency = 10,
                   MaxFrequency = 10e3,
                   StartFrequency = 1000,
                   PointsPerDecadeUpper = 10,
                   PointsPerDecadeLower = 10,
                   PreDuration = 0.0,
                   PreWaves = 1,
                   MeasDuration = 0.1,
                   MeasWaves = 3,
                   Amplitude = 10e-3,
               })
        using (var job = new EisGenerateJob(parameters))
        {
            link.DoJob(job);
            using DataSet data = link.GetJobResultData(job);
            Log($"EIS finished: {((EisDataset)data).Frequencies.Length} frequencies, "
                + $"{eisPoints} live points and {waveBlocks} waveform blocks seen");
        }

        TrySwitchOff(link);

        // --- Live data without stored results -------------------------------------------------
        // When the events already deliver everything that is needed, the IM7 does not have to
        // store the result at all -- useful for long or continuously repeated measurements that
        // would otherwise fill up the device. The options are read when the job starts, so they
        // are set before DoJob. The events fire exactly as before; only the stored result is gone.
        Log("starting OCV without stored result data");
        using (var parameters = new OcvParameters { Duration = 3.0, OutputDataRate = 5.0 })
        using (var job = new OcvJob(parameters))
        {
            job.JobOptions = new JobOptions(discardResultData: true);
            link.DoJob(job);
            try
            {
                using DataSet data = link.GetJobResultData(job);
            }
            catch (ZahnerLinkException e) when (e.ErrorCode == ErrorCode.ResultDataDiscarded)
            {
                Log($"as expected, no result data: {e.ErrorCode}");
            }
        }

        link.StopEventPump();
        link.Disconnect();
        Console.WriteLine("done");
        return 0;
    }

    private static string[] DimensionsOf(LiveDataHeader header)
    {
        var names = new string[header.Columns.Count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = header.Columns[i].Dimension;
        }

        return names;
    }

    private static void Log(string text) =>
        Console.WriteLine($"[{Clock.Elapsed.TotalSeconds,8:F3}s] {text}");

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
}
