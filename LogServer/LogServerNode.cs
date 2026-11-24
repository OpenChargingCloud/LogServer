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
        /// Whether the upload port speaks TLS - with the web interface's
        /// certificate, where the node shows one there.
        /// </summary>
        public Boolean            UploadsOverHTTPS  { get; }

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

                // With the web interface's certificate, where it shows one: a
                // charging station's log is no less private than a browser's
                // page, and one certificate to renew is one thing to forget less.
                var certificate         = WebCertificate;

                this.UploadsOverHTTPS   = certificate is not null;

                this.UploadServer       = new HTTPServer(
                                              IPAddress:                  Settings.HTTPListen,
                                              TCPPort:                    Settings.HTTPPort,
                                              HTTPServerName:             $"OpenChargingCloud {Kind.Product} v{this.Version}",
                                              ServerCertificateSelector:  certificate is not null
                                                                              ? (server, client) => certificate.ChainFor(server, client).Certificate
                                                                              : null,
                                              MaxHTTPBodySize:            (UInt64) Settings.MaxFileSize
                                          );

                if (certificate is not null)
                    this.UploadServer.ServerCertificateChainSelector = certificate.ChainFor;

                RegisterHTTPUploads(this.UploadServer);

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

            if (UploadServer is not null && !UploadsOverHTTPS)
                Log.Warning($"HTTP uploads are taken over plain HTTP: what a charging station sends, and the password it signs in with, cross the network in the clear. " +
                            $"Give the web interface a certificate (\"web.tls\"), which the upload port then shows as well, or put a proxy that speaks TLS in front.",
                            "logserver", "http", "security");

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
                listeners.Add($"by HTTP PUT on {(UploadsOverHTTPS ? "https" : "http")}://{LogServerSettings.TextOf(Settings.HTTPListen)}:{Settings.HTTPPort}/<charging station>/<file name>");

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
                                                                      new JProperty("listening",  UploadServer.IsRunning),
                                                                      new JProperty("url",        $"{(UploadsOverHTTPS ? "https" : "http")}://{LogServerSettings.TextOf(Settings.HTTPListen)}:{Settings.HTTPPort}/"),
                                                                      new JProperty("tls",        UploadsOverHTTPS),
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
