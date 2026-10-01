// Encrypted connections.
//
// Follows the Python SecureCommunication notebook. The workstation serves plain and TLS traffic on
// the same port, so switching to TLS is a connection flag rather than a different address. What
// takes thought is trust: the device certificate is issued by a Zahner CA that the operating
// system does not know, so a connection either has to be told to accept it or given the CA.
//
//     dotnet run -- <host> [port] [root-ca.pem]
//
// The root certificate is the one thing that cannot be discovered: a workstation presents its
// own certificate and the intermediate above it, never the root. Obtain it from Zahner once
// and keep it with the application.

using System;
using System.IO;
using Zahner.Link;

internal static class Program
{
    private static int Main(string[] args)
    {
        string host = args.Length > 0 ? args[0] : "169.254.73.242";
        string port = args.Length > 1 ? args[1] : "1994";
        string? rootCa = args.Length > 2 ? args[2] : null;

        // --- Encrypted, without validating the certificate ------------------------------
        // The quickest way to get an encrypted connection. It protects against passive
        // eavesdropping and against nothing else: any server presenting any certificate is
        // accepted. Fine on a closed lab network, not fine anywhere a stranger could answer.
        // Because nothing is verified, the verification result stays empty -- there is no chain to
        // report when no chain was examined.
        Console.WriteLine("--- TLS, certificate not validated ---");
        Describe(host, port, ConnectionFlags.UseSsl | ConnectionFlags.SkipSslCertValidation);

        // --- Encrypted, with full validation ---------------------------------------------
        // Fails unless the machine already trusts the CA that signed the device certificate.
        // The failure is the interesting part: the verification result names the certificate and
        // says why it was refused.
        Console.WriteLine();
        Console.WriteLine("--- TLS, full validation ---");
        Describe(host, port, ConnectionFlags.UseSsl);

        // --- Encrypted, ignoring only the host name ----------------------------------------
        // Useful when a device is reached by IP but its certificate carries a name. It relaxes
        // the name check alone; the chain still has to be trusted.
        Console.WriteLine();
        Console.WriteLine("--- TLS, host name not checked ---");
        Describe(host, port, ConnectionFlags.UseSsl | ConnectionFlags.SkipSslHostnameMatching);

        // The two scenarios above only observe. The two below establish trust deliberately, which
        // is what a deployment should do instead of switching validation off. Both need something
        // to trust, so the device is asked for its certificate chain first.
        CertificateInfo[] chain = FetchChain(host, port);
        if (chain.Length == 0)
        {
            Console.WriteLine();
            Console.WriteLine("no certificate chain available; skipping the trust scenarios");
            return 0;
        }

        // --- Trusting the issuing authority ------------------------------------------------
        // The normal way for a fleet: hand over the root certificate, and every device signed under
        // it is trusted -- one file instead of one entry per device. Whenever the host name check
        // is relaxed, trust only this authority and not the ones the operating system knows.
        if (rootCa is not null)
        {
            Console.WriteLine();
            Console.WriteLine("--- TLS, trusting the root CA ---");
            Describe(host, port,
                     ConnectionFlags.UseSsl | ConnectionFlags.SkipSslHostnameMatching | ConnectionFlags.SslDoNotUseSystemCas,
                     link => link.SetTrustedCaFiles(new[] { rootCa }));
        }

        // Passing the intermediate instead of the root shows the difference between the two ways a
        // chain can be refused: error 2 ("unable to get issuer certificate") means the chain was
        // followed and its root is missing, where error 20 above meant the chain was unknown from
        // the start.
        Console.WriteLine();
        Console.WriteLine("--- TLS, trusting only the intermediate ---");
        Describe(host, port, ConnectionFlags.UseSsl | ConnectionFlags.SkipSslHostnameMatching, link =>
        {
            // In memory rather than as a file: certificate material has no business lying around
            // in a temporary directory, and nothing here needs it to.
            using var configuration = new TlsConfiguration();
            configuration.AddTrustedCaCertificate(chain[chain.Length - 1].Pem);
            link.SetTlsConfiguration(configuration);
        });

        // --- Pinning one certificate ---------------------------------------------------------
        // Trust exactly this device and nothing else, by the fingerprint of its own certificate.
        // Tighter than a CA, and it has to be updated when the device certificate is renewed.
        Console.WriteLine();
        Console.WriteLine("--- TLS, pinned to the device certificate ---");
        Describe(host, port, ConnectionFlags.UseSsl | ConnectionFlags.SkipSslHostnameMatching,
                 link => link.SetTrustedCertFingerprints(new[] { chain[0].Sha256Fingerprint }));

        // Both scenarios below need the root certificate.
        if (rootCa is null)
        {
            return 0;
        }

        // The certificate is issued to the device's mDNS name, e.g. "im7-72000044.local". Here it
        // is taken from the chain the device presented; a deployment knows it from the serial
        // number.
        string certificateHostname = chain[0].CommonName;
        const ConnectionFlags customHostname =
            ConnectionFlags.UseSsl | ConnectionFlags.SslVerifyCustomHostname | ConnectionFlags.SslDoNotUseSystemCas;

        // --- Verifying the name the certificate carries ------------------------------------
        // Skipping the host name check, as the scenarios above do, no longer verifies which device
        // answered. SslVerifyCustomHostname keeps the check and moves it to a name of your choice:
        // DNS resolution, the TCP connection and the HTTP Host header keep using the address
        // dialled, while SNI and the host name check use the name set in the configuration. That
        // is full validation while still connecting by IP.
        Console.WriteLine();
        Console.WriteLine($"--- TLS, verified against {certificateHostname} ---");
        Describe(host, port, customHostname, link =>
        {
            using var configuration = new TlsConfiguration();
            configuration.AddTrustedCaFile(rootCa);
            configuration.SetCertificateHostname(certificateHostname);
            link.SetTlsConfiguration(configuration);
        });

        // --- A certificate authority held in memory -----------------------------------------
        // Same trust, without a file on disk: the PEM is passed as text, which suits a certificate
        // that arrives from configuration management or is embedded in the application. One entry
        // may hold several concatenated PEM certificates.
        Console.WriteLine();
        Console.WriteLine("--- TLS, root CA supplied in memory ---");
        string pem = File.ReadAllText(rootCa);
        Describe(host, port, customHostname, link =>
        {
            using var configuration = new TlsConfiguration();
            configuration.AddTrustedCaCertificate(pem);
            configuration.SetCertificateHostname(certificateHostname);
            link.SetTlsConfiguration(configuration);
        });

        return 0;
    }

    /// <summary>Reads the chain the device presents, without trusting it.</summary>
    private static CertificateInfo[] FetchChain(string host, string port)
    {
        using var link = new ZahnerLink(host, port, ConnectionFlags.UseSsl);
        try
        {
            link.Connect();
            link.Disconnect();
        }
        catch (ZahnerLinkException)
        {
            // Expected: the chain is reported whether the handshake succeeded or not.
        }

        TlsVerificationResult? result = link.GetLastTlsVerificationResult();
        if (result is null)
        {
            return Array.Empty<CertificateInfo>();
        }

        var chain = new CertificateInfo[result.Certificates.Count];
        for (int i = 0; i < chain.Length; i++)
        {
            chain[i] = result.Certificates[i];
        }

        return chain;
    }

    /// <summary>Connects with the given flags and prints what TLS made of it.</summary>
    private static void Describe(string host, string port, ConnectionFlags flags,
                                 Action<ZahnerLink>? configureTrust = null)
    {
        using var link = new ZahnerLink(host, port, flags);
        configureTrust?.Invoke(link);
        try
        {
            link.Connect();
            Console.WriteLine("connected");
            link.Disconnect();
        }
        catch (ZahnerLinkException e)
        {
            Console.WriteLine($"refused: {e.Message}");
        }

        // Available whether the connection succeeded or not -- that is the point of it.
        TlsVerificationResult? result = link.GetLastTlsVerificationResult();
        if (result is null)
        {
            Console.WriteLine("no TLS verification result (was the connection encrypted at all?)");
            return;
        }

        Console.WriteLine($"   trusted by the system : {result.TrustedBySystem}");
        Console.WriteLine($"   OpenSSL error         : {result.OpenSslErrorCode} at depth {result.ErrorDepth}");
        if (result.ErrorMessage.Length != 0)
        {
            Console.WriteLine($"   message               : {result.ErrorMessage}");
        }

        Console.WriteLine($"   chain                 : {result.Certificates.Count} certificate(s)");
        for (int i = 0; i < result.Certificates.Count; i++)
        {
            CertificateInfo cert = result.Certificates[i];
            Console.WriteLine($"      [{i}] {(i == 0 ? "device" : "issuer")}: {cert.CommonName}");
            Console.WriteLine($"          issued by  : {cert.IssuerDistinguishedName}");
            Console.WriteLine($"          valid until: {cert.ValidUntil:yyyy-MM-dd} (expired: {cert.IsExpired})");
            Console.WriteLine($"          SHA-256    : {cert.Sha256Fingerprint}");
            if (cert.SubjectAlternativeNames.Count != 0)
            {
                Console.WriteLine($"          also valid for: {string.Join(", ", cert.SubjectAlternativeNames)}");
            }
        }
    }
}
