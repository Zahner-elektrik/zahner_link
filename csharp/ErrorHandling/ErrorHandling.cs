// Error handling.
//
// Follows the Python ErrorHandling notebook: what a rejected job looks like, what happens without
// a connection, how parameters are validated before a job runs, what the job list says afterwards,
// and how a running measurement is stopped and its partial result collected.
//
//     dotnet run -- <host> [port] [user] [password]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
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

        // --- Executing Jobs Without a Valid Connection --------------------------------
        // Every call on an unconnected link fails the same way, with a message that says so.
        using (var unconnected = new ZahnerLink(host, port, ConnectionFlags.None, user, password))
        using (var parameters = new OcvParameters { Duration = 1.0, OutputDataRate = 10.0 })
        using (var job = new OcvJob(parameters))
        {
            try
            {
                unconnected.DoJob(job);
                Console.WriteLine("unexpected: the job ran without a connection");
            }
            catch (ZahnerLinkException e)
            {
                Console.WriteLine($"no connection: {e.Message}");
            }
        }

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

        // --- Parameter Errors ----------------------------------------------------------
        // CheckJobParameters sends the parameters to the workstation's own validation without
        // running anything. It is the cheapest way to find out that a job cannot work.
        using (var parameters = new PogaParameters
               {
                   Bias = 0.0,
                   Duration = -1.0,          // deliberately impossible
                   OutputDataRate = 25,
                   Autorange = true,
                   CurrentRange = 0.1,
               })
        using (var job = new PogaJob(parameters))
        {
            try
            {
                link.CheckJobParameters(job);
                Console.WriteLine("unexpected: a negative duration was accepted");
            }
            catch (ZahnerLinkException e)
            {
                Console.WriteLine($"parameter check rejected the job: {e.Message}");
            }
        }

        // --- Rejected Before the Job Exists ---------------------------------------------
        // A job the workstation refuses outright never gets an id, so there is no JobInfo to ask.
        // LastStatus and LastErrorMessage are what to read in this case.
        using (var parameters = new SwitchOffParameters { Potentiostat = "NO:SUCH:POT" })
        using (var job = new SwitchOffJob(parameters))
        {
            try
            {
                link.DoJob(job);
            }
            catch (ZahnerLinkException e)
            {
                Console.WriteLine($"rejected: {e.Message}");
                Console.WriteLine($"   job status        : {job.LastStatus}");
                Console.WriteLine($"   job error message : {job.LastErrorMessage}");
            }
        }

        // --- Runtime Errors --------------------------------------------------------------
        // A job that is valid on paper but wrong for the device's current state. It reaches the
        // workstation, gets an id, and fails there -- so the full JobInfo is available.
        // Switching on at an absolute 0 V would be a large step for a cell that rests elsewhere,
        // and the potentiostat refuses it. Hold the cell where it already is.
        SwitchOn(link, MeasureRestPotential(link));
        using (var parameters = new OcvParameters { Duration = 1.0, OutputDataRate = 10.0 })
        using (var job = new OcvJob(parameters))
        {
            try
            {
                link.DoJob(job);       // open-circuit voltage needs the potentiostat switched off
                Console.WriteLine("unexpected: OCV ran with the potentiostat on");
            }
            catch (ZahnerLinkException e)
            {
                Console.WriteLine($"runtime error: {e.Message}");
                Console.WriteLine($"   job status        : {job.LastStatus}");
                Console.WriteLine($"   job error message : {job.LastErrorMessage}");

                // JobInfo carries what the exception does not: the device-side id the workstation
                // logged the job under, the status detail, and the timestamps. It is null only for
                // a job that never ran.
                JobInfo? info = job.LastJobInfo;
                if (info is not null)
                {
                    Console.WriteLine($"   job id            : {info.JobId}");
                    Console.WriteLine($"   status detail     : {info.StatusDetail}");
                }
            }
        }

        TrySwitchOff(link);

        // --- Inspecting the Job List ----------------------------------------------------
        // Every job the workstation still knows about, failed ones included. Pass true to see only
        // what is running or queued.
        IReadOnlyList<JobInfo> jobs = link.GetJobInfoList(excludeFinishedJobs: false);
        Console.WriteLine($"jobs on the workstation: {jobs.Count}");
        foreach (JobInfo entry in jobs)
        {
            Console.WriteLine($"   #{entry.JobId}: {entry.Status}");
        }

        // --- Connection Inspection --------------------------------------------------------
        WebsocketConnectionInfo own = link.GetOwnConnectionInfo();
        Console.WriteLine($"own connection: id {own.ConnectionId} from {own.RemoteAddress}");
        Console.WriteLine($"clients on this workstation: {link.GetEcwConnections().Count}");

        // --- Cancelling a Running Job and Retrieving Results Later -------------------------
        // A long measurement is started, stopped early, and its partial data collected. This is
        // the pattern behind every "abort" button.
        using (var parameters = new OcvParameters { Duration = 30.0, OutputDataRate = 10.0 })
        using (var job = new OcvJob(parameters))
        {
            var started = DateTime.UtcNow;
            var worker = new Thread(() =>
            {
                try
                {
                    link.DoJob(job);
                }
                catch (ZahnerLinkException e)
                {
                    Console.WriteLine($"the stopped job reported: {e.Message}");
                }
            });
            worker.Start();

            Thread.Sleep(2000);
            Console.WriteLine("stopping the running measurement");
            // Continue stops the running job and lets the queue carry on; Flush drops the queue too.
            link.SendStop(QueueStopMode.Continue);
            worker.Join();
            Console.WriteLine($"the job returned after {Format((DateTime.UtcNow - started).TotalSeconds)} s");

            try
            {
                using DataSet partial = link.GetJobResultData(job);
                double[] time = ((DcDataset)partial).GetTrack(TrackNames.Time);
                // What comes back is what the device managed to record before the stop -- the
                // measurement is not lost. (A simulator running on an accelerated time scale may
                // already have computed the whole thing, and then the dataset is complete.)
                Console.WriteLine($"partial result: {time.Length} points covering "
                                  + $"{Format(time.Length > 0 ? time[time.Length - 1] : 0)} s of the requested 30 s");
            }
            catch (ZahnerLinkException e)
            {
                Console.WriteLine($"no partial result available: {e.Message}");
            }
        }

        // --- Cancelling Pending Operations --------------------------------------------------
        link.CancelPendingOperations();
        Console.WriteLine("pending operations cancelled");

        TrySwitchOff(link);
        link.Disconnect();
        Console.WriteLine("done");
        return 0;
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

    private static double MeasureRestPotential(ZahnerLink link)
    {
        using var parameters = new OcvParameters { Duration = 1.0, OutputDataRate = 10.0 };
        using var job = new OcvJob(parameters);
        link.DoJob(job);
        using DataSet data = link.GetJobResultData(job);
        double[] voltage = ((DcDataset)data).GetTrack(TrackNames.Voltage);
        return voltage[voltage.Length - 1];
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
