/*
 * Copyright (c) 2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of LogServer <https://github.com/OpenChargingCloud/LogServer>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Net.Sockets;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;

using cloud.charging.open.LogServer.CommandLine;

#endregion

namespace cloud.charging.open.LogServer
{

    /// <summary>
    /// One log server for charging stations: a WWCP node that takes the log
    /// files they upload - by HTTP PUT on a port of its own, and over SFTP on
    /// its SSH server - keeps each of them with what is known about how it
    /// came, and shows them in its web interface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is this kind's own is the uploads: the store that keeps them (see
    /// <see cref="UploadStore"/>), the port that takes them by HTTP, the area
    /// of the SFTP tree that takes them over SSH, and the accounts they are
    /// uploaded under - charging stations, or a back end in their name - in
    /// the "uploader" role.
    /// </para>
    /// <para>
    /// What every program of this family has - the sign-in, the name servers
    /// it asks itself, the time servers, the certificate store, the SSH server
    /// with its command line, and the log - is the node below.
    /// </para>
    /// </remarks>
    public partial class LogServerNode : WWCPNode
    {

        #region Data

        /// <summary>
        /// The TCP port the web interface listens on, unless another is given.
        /// </summary>
        /// <remarks>
        /// Beside the PKI's 2360, the NTS server's 2361 and the DNS server's
        /// 2362, so that it can run on the same bench as all of them. Its SSH
        /// server - and with it SFTP - is then on 22363.
        /// </remarks>
        public static new readonly  IPPort    DefaultHTTPPort   = IPPort.Parse(2363);

        /// <summary>
        /// What kind of node a log server is.
        /// </summary>
        /// <remarks>
        /// "LogServer" as the organization its accounts are in - which a first
        /// start writes into the accounts file and every start after it reads
        /// back, so it is the one that must never change.
        /// </remarks>
        public static readonly      NodeKind  LogServerKind     = new (
                                                                      Name:           "log server",
                                                                      Tag:            "logserver",
                                                                      Product:        "LogServer",
                                                                      Organization:   "LogServer",
                                                                      LogFilePrefix:  "logserver"
                                                                  );

        /// <summary>
        /// The kinds of certificate the node's store keeps: the four of TLS -
        /// the identity its web interface and its upload port may show, and
        /// the roots of the name and time servers it asks itself.
        /// </summary>
        public static readonly      IReadOnlyList<CertificateKind>  CertificateKinds  = [ .. CertificateKindExtensions.TLS ];

        /// <summary>
        /// The port HTTP uploads are taken on, as a start that cannot have it says.
        /// </summary>
        public static readonly      NodePort  HTTPUploadPort    = new ("HTTP uploads");

        /// <summary>
        /// Where the uploads arrive in the SFTP tree: "/upload".
        /// </summary>
        public const                String    SftpUploadArea    = "upload";

        /// <summary>
        /// The directory below the uploads an SFTP upload lies in until it is
        /// taken into the store: hidden, and no name a charging station's
        /// directory can have.
        /// </summary>
        public const                String    SftpIncoming      = ".sftp-incoming";

        /// <summary>
        /// The directory below the uploads an HTTP upload is written to as it
        /// arrives, until its last byte is there and it is taken into the
        /// store: hidden, and no name a charging station's directory can have.
        /// </summary>
        public const                String    HTTPIncoming      = ".http-incoming";

        /// <summary>
        /// The listeners of the node's certificate store a TLS server identity
        /// may be told it is shown on: the web interface, and the upload port.
        /// </summary>
        public static readonly      IReadOnlyList<String>  CertificateListeners  = [ WebServerCertificate.Listener, LogServerSettings.UploadListener ];

        /// <summary>
        /// The manifest resource prefix of the embedded web interface (see the
        /// EmbedFrontend target of LogServer.csproj).
        /// </summary>
        public const                String    HTTPRoot          = "cloud.charging.open.LogServer.HTTPRoot.";

        private Int64 httpUploadsRefused;

        #endregion

        #region Properties

        /// <summary>
        /// What the "logServer" section and the switches of this start said.
        /// </summary>
        public LogServerSettings  Settings          { get; }

        /// <summary>
        /// The uploads kept.
        /// </summary>
        public UploadStore        Uploads           { get; }

        /// <summary>
        /// The JSON API.
        /// </summary>
        public LogServerHTTPAPI   API               { get; }

        /// <summary>
        /// The HTTP server that takes uploads by PUT, or null where the
        /// section says "http": false.
        /// </summary>
        public HTTPServer?        UploadServer      { get; }

        /// <summary>
        /// The area of the SFTP tree uploads arrive in, or null where the
        /// section says "sftp": false.
        /// </summary>
        public SftpArea?          SftpArea          { get; }

        /// <summary>
        /// The certificate the upload port shows - its own, where the section
        /// says "tls" - or null for plain HTTP.
        /// </summary>
        public UploadPortCertificate?  UploadCertificate  { get; }

        /// <summary>
        /// Whether the upload port speaks TLS.
        /// </summary>
        public Boolean            UploadsOverHTTPS
            => UploadCertificate is not null;

        /// <summary>
        /// The root of the upload port as charging stations are told it: what
        /// "publicURL" says, or - failing that - guessed from where the port
        /// listens: the address it is given, or the name of this machine where
        /// it listens on every address.
        /// </summary>
        public Uri                UploadURL
            => Settings.PublicURL ?? new Uri($"{(UploadsOverHTTPS ? "https" : "http")}://{GuessedHost()}:{Settings.HTTPPort}/");

        /// <summary>
        /// Where the upload port listens, as a URL: "http://any:8363/".
        /// </summary>
        public String             UploadListenURL
            => $"{(UploadsOverHTTPS ? "https" : "http")}://{LogServerSettings.TextOf(Settings.HTTPListen)}:{Settings.HTTPPort}/";

        /// <summary>
        /// How many HTTP uploads were turned away since the start.
        /// </summary>
        public Int64              HTTPUploadsRefused
            => Interlocked.Read(ref httpUploadsRefused);

        #endregion

        #region Constructor(s)

        /// <summary>
        /// One log server, with its web interface and its command line over SSH.
        /// </summary>
        /// <param name="HTTPHostname">The address the web interface listens on; 127.0.0.1 by default.</param>
        /// <param name="HTTPPort">The TCP port it listens on; <see cref="DefaultHTTPPort"/> by default.</param>
        /// <param name="HTTPServer">An HTTP server to register within, or null to make one.</param>
        /// <param name="BasePath">What everything of this node sits below; the root by default.</param>
        /// <param name="HTTPRootPath">Where the JSON API sits; "/api" below <paramref name="BasePath"/> by default.</param>
        /// <param name="ExtAPI">An HTTPExt API to sign in against, or null for one of this node's own.</param>
        /// <param name="AccountsPath">The directory the accounts live in between starts.</param>
        /// <param name="ConfigFile">Where the configuration lives between starts.</param>
        /// <param name="DNSClient">How this node resolves names itself, or null to make a client.</param>
        /// <param name="NTSClient">Where the node reads the time for its own check, or null to make a client.</param>
        /// <param name="Frontend">Where the web interface comes from, or null for the embedded bundle.</param>
        /// <param name="CertificatesPath">The directory the node's certificate store lives in between starts.</param>
        /// <param name="Log">Where everything that happens is written, or null to make a log.</param>
        /// <param name="LogToConsole">Whether the log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">How much of it reaches the console.</param>
        /// <param name="LogPath">The directory the log files are written to, or null to write none.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX is picked up.</param>
        /// <param name="SSH">What the program says about the SSH server; SFTP is served by default on a log server.</param>
        /// <param name="Web">What the program says of HTTPS and of the reverse proxies in front.</param>
        /// <param name="CommandLine">What the switches of this start say over the "logServer" section, or null for nothing.</param>
        public LogServerNode(IPAddress?                                    HTTPHostname       = null,
                             IPPort?                                        HTTPPort           = null,
                             HTTPServer?                                    HTTPServer         = null,
                             HTTPPath?                                      BasePath           = null,
                             HTTPPath?                                      HTTPRootPath       = null,
                             HTTPExtAPI?                                    ExtAPI             = null,
                             String?                                        AccountsPath       = null,
                             WWCPConfigFile?                                ConfigFile         = null,
                             DNSClient?                                     DNSClient          = null,
                             NTSClient?                                     NTSClient          = null,
                             IStaticContentSource?                          Frontend           = null,
                             String?                                        CertificatesPath   = null,
                             EventLog?                                      Log                = null,
                             Boolean                                        LogToConsole       = true,
                             LogLevel                                       ConsoleLogLevel    = LogLevel.Info,
                             String?                                        LogPath            = null,
                             Boolean                                        BridgeDebugLog     = true,
                             SSHSettings?                                   SSH                = null,
                             WebConfiguration?                              Web                = null,
                             Func<LogServerSettings, LogServerSettings>?    CommandLine        = null)

            : base(Kind:                  LogServerKind,
                   Version:               typeof(LogServerNode).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                   HTTPPort:              HTTPPort ?? DefaultHTTPPort,
                   HTTPHostname:          HTTPHostname,
                   HTTPServer:            HTTPServer,
                   BasePath:              BasePath,
                   HTTPRootPath:          HTTPRootPath,
                   ExtAPI:                ExtAPI,
                   AccountsPath:          AccountsPath,
                   Resources:             LogServerAccess.Resources,
                   RoleDefinitions:       LogServerAccess.Roles,
                   ConfigFile:            ConfigFile,
                   DNSClient:             DNSClient,
                   NTSClient:             NTSClient,
                   Frontend:              Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(LogServerNode).Assembly),
                   CertificatesPath:      CertificatesPath,
                   CertificateKinds:      CertificateKinds,
                   CertificateListeners:  CertificateListeners,
                   Log:                   Log,
                   LogToConsole:          LogToConsole,
                   ConsoleLogLevel:       ConsoleLogLevel,
                   LogPath:               LogPath,
                   BridgeDebugLog:        BridgeDebugLog,
                   // Files over SFTP are what a log server is there for: on,
                   // unless the "ssh" section says "sftp": false.
                   SSH:                   SSH is not null
                                              ? SSH with { SftpByDefault = true }
                                              : null,
                   Web:                   Web)

        {

            #region The "logServer" section, and what the switches say over it

            if (!LogServerSettings.TryParse(ConfigurationDocument[LogServerSettings.SectionName], out var settings, out var settingsError))
                throw new InvalidOperationException($"'{this.ConfigFile.Path}': {settingsError} Repair or remove '{this.ConfigFile.Path}' and start again.");

            this.Settings = CommandLine is not null
                                ? CommandLine(settings)
                                : settings;

            #endregion

            #region The uploads

            this.Uploads = new UploadStore(BesideTheConfiguration(Settings.Directory),
                                           TimeProvider,
                                           problem => this.Log.Warning(problem, "logserver", "uploads"));

            var kept = this.Uploads.Load();

            this.Log.Info($"{kept} upload(s) are kept in '{Uploads.Directory}'.", "logserver", "uploads");

            #endregion

            #region The port HTTP uploads are taken on

            if (Settings.HTTP)
            {

                // A certificate of its own, and not the web interface's: the
                // upload port is reached by charging stations under a name of
                // their network, the web interface by people under another -
                // and either may speak TLS without the other.
                if (Settings.TLS is WebTLSConfiguration tls)
                    this.UploadCertificate  = new UploadPortCertificate(
                                                  (tls.Certificate ?? WebCertificateSource.Store).Beside(this.ConfigFile.Path),
                                                  ListenerCertificates.For(LogServerSettings.UploadListener, "the upload port"),
                                                  this.Log
                                              );

                var certificate             = this.UploadCertificate;

                // An HTTP server of its own, on a port of its own. The body of a
                // PUT is never held in memory: Hermod hands it over as a stream,
                // which is written to the disk as it arrives - see PutUpload -
                // so the largest file costs no more memory than the smallest.
                // How long it may take is the section's, not Hermod's half
                // minute, which a large file over a slow line does not make.
                this.UploadServer           = new HTTPServer(
                                                  IPAddress:                  Settings.HTTPListen,
                                                  TCPPort:                    Settings.HTTPPort,
                                                  HTTPServerName:             $"OpenChargingCloud {Kind.Product} v{this.Version}",
                                                  ServerCertificateSelector:  certificate is not null
                                                                                  ? (server, client) => certificate.ChainFor(server, client).Certificate
                                                                                  : null,
                                                  MaxHTTPBodySize:            (UInt64) Settings.MaxFileSize,
                                                  BodyReadTimeout:            Settings.HTTPUploadTimeout
                                              );

                if (certificate is not null)
                    this.UploadServer.ServerCertificateChainSelector = certificate.ChainFor;

                RegisterHTTPUploads(this.UploadServer);

                // Nobody uploads before the node is started: whatever lies in
                // the directory an HTTP upload is written to is one that broke off.
                ForgetUnfinishedHTTPUploads();

            }

            #endregion

            #region The area of the SFTP tree uploads arrive in

            if (Settings.SFTP)
            {

                var incoming   = Path.Combine(Uploads.Directory, SftpIncoming);

                this.SftpArea  = new SftpArea(
                                     SftpUploadArea,
                                     incoming,
                                     SftpArea.IsFileName,
                                     Read:   Permission.Run(LogServerAccess.Upload),
                                     Write:  Permission.Run(LogServerAccess.Upload)
                                 ) {
                                     MaxFileSize  = Settings.MaxFileSize,
                                     Tags         = [ "logserver", "uploads" ]
                                 };

                this.SftpArea.Changed += TakeInSftpUpload;

                AddSftpArea(this.SftpArea);

                // Nobody uploads before the node is started: whatever is left
                // under "<name>.part" is an upload that was never finished.
                ForgetUnfinishedSftpUploads(OlderThan: TimeSpan.Zero);

            }

            #endregion

            #region The JSON API

            this.API = new LogServerHTTPAPI(
                           HTTPServer:  this.HTTPServer,
                           Node:        this,
                           ExtAPI:      this.ExtAPI,
                           Log:         this.Log,
                           APIPath:     this.HTTPRootPath,
                           Version:     Version
                       );

            #endregion

            #region What somebody signed in over SSH gets

            // The log server's command line, with its commands beside the
            // node's - whichever program runs this log server, and not only
            // LogServerCLI. Before the node starts, as the node asks.
            CommandLines = (terminal, caller) => new LogServerCLI(this, terminal, caller);

            #endregion

        }

        #endregion


        #region (private) BesideTheConfiguration(Path)

        /// <summary>
        /// A path as the section gives it: as it is where it is given in full,
        /// and beside the configuration file otherwise.
        /// </summary>
        private String BesideTheConfiguration(String Path)

            => System.IO.Path.IsPathRooted(Path)
                   ? System.IO.Path.GetFullPath(Path)
                   : System.IO.Path.GetFullPath(System.IO.Path.Combine(
                         System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(ConfigFile.Path)) ?? ".",
                         Path
                     ));

        #endregion

        #region (private) TakeInSftpUpload(Changed)

        /// <summary>
        /// A file put in place over SFTP, taken out of the area and into the
        /// store at once: the area is where it arrives, the store where it is
        /// kept - so that the next charging station can upload a file of the
        /// same name without taking its place.
        /// </summary>
        private void TakeInSftpUpload(SftpAreaChanged Changed)
        {

            // What uploads that were never finished left, as anything moves.
            ForgetUnfinishedSftpUploads(OlderThan: UnfinishedSftpUploadsKeptFor);

            if (Changed.Change != SftpAreaChange.PutInPlace)
                return;

            // An upload under the name it goes by until its last byte is there -
            // "<name>.part", as the white paper "SFTP for Logfile Uploads and
            // Firmware Downloads" has it - is not done yet: its uploader renames
            // it next, and that rename is what is taken in. Taken in now, the
            // rename would find nothing, and the upload would fail.
            if (Changed.Name.EndsWith(SFTPLocation.PartSuffix, StringComparison.OrdinalIgnoreCase))
                return;

            var uploader = Changed.Account.Id.ToString();

            try
            {

                var upload = Uploads.KeepFile(Changed.Area.FileOf(Changed.Name),
                                              UploadProtocol.SFTP,
                                              uploader,
                                              uploader,
                                              Changed.Name,
                                              Unmapped(Changed.From));

                Log.Notice($"'{uploader}' uploaded '{upload.FileName}' over SFTP from {Changed.From}: {SizeOf(upload.Size)}, kept as upload {upload.Id}.",
                           "logserver", "uploads", "sftp");

            }
            catch (Exception problem)
            {
                Log.Error($"What '{uploader}' uploaded over SFTP as '{Changed.Name}' could not be kept: {problem.Message}",
                          "logserver", "uploads", "sftp");
            }

        }

        #endregion


        #region UnfinishedSftpUploadsKeptFor / (private) ForgetUnfinishedSftpUploads(OlderThan)

        /// <summary>
        /// How long a file under "&lt;name&gt;.part" that was never renamed is
        /// kept while the log server runs: a day - an upload on a slow line
        /// takes long, and one left for a day is one its uploader gave up.
        /// </summary>
        public TimeSpan UnfinishedSftpUploadsKeptFor { get; set; } = TimeSpan.FromDays(1);

        /// <summary>
        /// Take away what uploads over SFTP left under "&lt;name&gt;.part" and
        /// never renamed, where it was last written longer ago than given: it
        /// is never taken in, and would lie there for ever.
        /// </summary>
        private void ForgetUnfinishedSftpUploads(TimeSpan OlderThan)
        {

            if (SftpArea is null || !Directory.Exists(SftpArea.Directory))
                return;

            var before = TimeProvider.GetUtcNow() - OlderThan;

            foreach (var part in Directory.EnumerateFiles(SftpArea.Directory, "*" + SFTPLocation.PartSuffix))
            {
                try
                {

                    if (File.GetLastWriteTimeUtc(part) > before.UtcDateTime && OlderThan > TimeSpan.Zero)
                        continue;

                    File.Delete(part);

                    Log.Notice($"'{Path.GetFileName(part)}' was taken away: an upload over SFTP that was never finished.", "logserver", "uploads", "sftp");

                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
                {
                    Log.Warning($"What an unfinished upload left in '{part}' could not be taken away: {problem.Message}", "logserver", "uploads", "sftp");
                }
            }

        }

        #endregion


        #region (private) GuessedHost()

        /// <summary>
        /// The host a charging station would be told where nobody said: the
        /// address the upload port listens on, or the name of this machine
        /// where it listens on every address.
        /// </summary>
        private String GuessedHost()
        {

            if (!Settings.HTTPListen.TryGetValue(out IPvXAddress _))
            {
                var address = Settings.HTTPListen.ToString();
                return address.Contains(':') && !address.StartsWith('[')
                           ? $"[{address}]"
                           : address;
            }

            try
            {
                var name = System.Net.Dns.GetHostName();
                if (!String.IsNullOrWhiteSpace(name))
                    return name.ToLowerInvariant();
            }
            catch (SocketException)
            { }

            return "localhost";

        }

        #endregion

        #region (private) HTTPIncomingDirectory / ForgetUnfinishedHTTPUploads()

        /// <summary>
        /// Where an HTTP upload is written to as it arrives.
        /// </summary>
        private String HTTPIncomingDirectory
            => Path.Combine(Uploads.Directory, HTTPIncoming);

        /// <summary>
        /// Take away whatever HTTP uploads that broke off left: at the start,
        /// when no upload can be under way.
        /// </summary>
        private void ForgetUnfinishedHTTPUploads()
        {

            if (!Directory.Exists(HTTPIncomingDirectory))
                return;

            foreach (var part in Directory.EnumerateFiles(HTTPIncomingDirectory))
            {
                try
                {
                    File.Delete(part);
                    Log.Notice($"'{Path.GetFileName(part)}' was taken away: an upload by HTTP that broke off.", "logserver", "uploads", "http");
                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
                {
                    Log.Warning($"What an HTTP upload that broke off left in '{part}' could not be taken away: {problem.Message}", "logserver", "uploads", "http");
                }
            }

        }

        #endregion


        #region (protected override) OnListening()

        /// <summary>
        /// The upload port opened, before the node calls itself started.
        /// </summary>
        protected override async Task OnListening()
        {

            if (UploadServer is null)
                return;

            try
            {
                await UploadServer.Start();
            }
            catch (SocketException problem)
            {
                throw new PortUnavailableException(Settings.HTTPPort, problem, HTTPUploadPort);
            }

        }

        #endregion

        #region (protected override) OnStarted()

        protected override Task OnStarted()
        {

            Log.Info($"The JSON API of the log server is at {APIURL}v1/logServer", "web", "http");

            Log.Notice($"Uploads are taken {ListenersText()}; {Uploads.Count} are kept in '{Uploads.Directory}'.", "logserver");

            // Not where the URL charging stations are told is "https": then a
            // proxy in front speaks TLS, and plain HTTP behind it is its business.
            if (UploadServer is not null && !UploadsOverHTTPS && UploadURL.Scheme != Uri.UriSchemeHttps)
                Log.Warning($"HTTP uploads are taken over plain HTTP: what a charging station sends, and the password it signs in with, cross the network in the clear. " +
                            $"Give the upload port a certificate (\"{LogServerSettings.SectionName}.tls\", or --upload-https), or put a proxy that speaks TLS in front and tell \"{LogServerSettings.SectionName}.publicURL\" its \"https\" URL.",
                            "logserver", "http", "security");

            if (UploadServer is not null && Settings.PublicURL is null && Settings.HTTPListen.TryGetValue(out IPvXAddress _))
                Log.Info($"Charging stations are told to upload to {UploadURL}, a guess from the name of this machine: where they know it by another name, say it in \"{LogServerSettings.SectionName}.publicURL\" (or --upload-url).",
                         "logserver", "http");

            if (Settings.SFTP && !SFTPEnabled)
                Log.Warning($"SFTP uploads are switched on in the \"{LogServerSettings.SectionName}\" section, but the node serves no SFTP: " +
                             "the SSH server is off, or the \"ssh\" section says \"sftp\": false.",
                            "logserver", "sftp");

            return Task.CompletedTask;

        }

        #endregion

        #region (protected override) OnStopping()

        protected override async Task OnStopping()
        {

            if (UploadServer is not null)
                await UploadServer.Stop();

            await base.OnStopping();

        }

        #endregion


        #region ListenersText()

        /// <summary>
        /// Where uploads are taken, as the banner and the command line say it.
        /// </summary>
        public String ListenersText()
        {

            var listeners = new List<String>();

            if (UploadServer is not null)
                listeners.Add($"by HTTP PUT on {UploadURL}<charging station>/<file name>" +
                              (UploadURL.ToString() != UploadListenURL ? $" (listening on {UploadListenURL})" : ""));

            if (SftpArea is not null)
                listeners.Add(SFTPEnabled
                                  ? $"over SFTP on port {SSHPort} in /{SftpUploadArea}"
                                  : $"over SFTP - which the node does not serve now");

            return listeners.Count > 0
                       ? String.Join(" and ", listeners)
                       : "nowhere - \"http\" and \"sftp\" are both false";

        }

        #endregion

        #region StatusJSON()

        /// <summary>
        /// How the log server stands.
        /// </summary>
        public JObject StatusJSON()
        {

            var latest = Uploads.Latest;

            return new (
                       new JProperty("directory",           Uploads.Directory),
                       new JProperty("http",                UploadServer is not null
                                                                ? new JObject(
                                                                      new JProperty("listening",    UploadServer.IsRunning),
                                                                      new JProperty("url",          UploadURL.ToString()),
                                                                      new JProperty("publicURL",    Settings.PublicURL?.ToString()),
                                                                      new JProperty("listen",       UploadListenURL),
                                                                      new JProperty("tls",          UploadsOverHTTPS),
                                                                      new JProperty("certificate",  UploadCertificate?.ToJSON()),
                                                                      new JProperty("timeout",      (Int64) Settings.HTTPUploadTimeout.TotalSeconds),
                                                                      new JProperty("anonymous",  Settings.AnonymousHTTP),
                                                                      new JProperty("refused",    HTTPUploadsRefused)
                                                                  )
                                                                : null),
                       new JProperty("sftp",                SftpArea is not null
                                                                ? new JObject(
                                                                      new JProperty("serving",    SFTPEnabled),
                                                                      new JProperty("port",       SSHPort?.ToUInt16()),
                                                                      new JProperty("path",       $"/{SftpUploadArea}"),
                                                                      new JProperty("passwords",  SSHPasswords)
                                                                  )
                                                                : null),
                       new JProperty("maxFileSize",         Settings.MaxFileSize),
                       new JProperty("uploads",             Uploads.Count),
                       new JProperty("totalSize",           Uploads.TotalSize),
                       new JProperty("uploaders",           Uploads.Uploaders.Count),
                       new JProperty("chargingStations",    Uploads.ChargingStations.Count),
                       new JProperty("latest",              latest?.ToJSON())
                   );

        }

        #endregion

        #region ConfigurationJSON()

        /// <summary>
        /// What this log server is, as the Configuration page reads it: what the
        /// node below says of itself, and on top the log server.
        /// </summary>
        public override JObject ConfigurationJSON()
        {

            var json = base.ConfigurationJSON();

            json.AddFirst(new JProperty("logServer", new JObject(
                              new JProperty("version",   Version),
                              new JProperty("createdAt", CreatedAt.ToString("o")),
                              new JProperty("machine",   Environment.MachineName),
                              new JProperty("runtime",   Environment.Version.ToString()),
                              new JProperty("os",        Environment.OSVersion.ToString()),
                              new JProperty("settings",  Settings.ToJSON()),
                              new JProperty("status",    StatusJSON())
                          )));

            return json;

        }

        #endregion

        #region (static) SizeOf(Bytes)

        /// <summary>
        /// A number of bytes as somebody reads it.
        /// </summary>
        public static String SizeOf(Int64 Bytes)

            => Bytes switch {
                   < 1024               => $"{Bytes} B",
                   < 1024 * 1024        => $"{Bytes / 1024.0:0.#} KiB",
                   < 1024L * 1024 * 1024 => $"{Bytes / (1024.0 * 1024):0.#} MiB",
                   _                    => $"{Bytes / (1024.0 * 1024 * 1024):0.##} GiB"
               };

        #endregion

    }

}
