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

using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.LogServer
{

    /// <summary>
    /// What the "logServer" section of the configuration file says: where the
    /// uploaded files are kept, and how they may arrive - by HTTP PUT on a port
    /// of their own, and over SFTP on the node's SSH server.
    /// </summary>
    /// <remarks>
    /// <code>
    /// "logServer": {
    ///     "directory":          "uploads",
    ///     "http":               true,
    ///     "httpListen":         "any",
    ///     "httpPort":           8363,
    ///     "tls":                { "certificate": "store" },
    ///     "publicURL":          "https://logs.example.org:8363/",
    ///     "httpUploadTimeout":  3600,
    ///     "anonymousHTTP":      false,
    ///     "sftp":               true,
    ///     "maxFileSize":        104857600
    /// }
    /// </code>
    /// Every key may be left out, and then reads as its default - "tls" and
    /// "publicURL" as none. A directory, and the files of a certificate, that
    /// are not given in full lie beside the configuration file.
    /// </remarks>
    public sealed record LogServerSettings
    {

        #region Data

        /// <summary>
        /// The section of the configuration file.
        /// </summary>
        public const            String    SectionName           = "logServer";

        /// <summary>
        /// Where the uploads are kept, unless another directory is given.
        /// </summary>
        public const            String    DefaultDirectory      = "uploads";

        /// <summary>
        /// The port HTTP uploads are taken on, unless another is given: the
        /// web interface's 2363 with 8 in front - a port of its own, so that
        /// charging stations can reach it while the web interface stays on the
        /// loopback.
        /// </summary>
        public static readonly  IPPort    DefaultHTTPUploadPort = IPPort.Parse(8363);

        /// <summary>
        /// The largest file taken, unless another size is given: 100 MiB.
        /// </summary>
        public const            Int64     DefaultMaxFileSize    = 100L * 1024 * 1024;

        /// <summary>
        /// The largest file that can be taken at all: what one HTTP request body
        /// of Hermod's may be. It is written to the disk as it arrives, and never
        /// held in memory - the limit is Hermod's count of its bytes.
        /// </summary>
        public const            Int64     LargestMaxFileSize    = Int32.MaxValue;

        /// <summary>
        /// How long one HTTP upload may take from its first byte to its last,
        /// unless another time is given: an hour - a diagnostics file of a
        /// hundred MiB over a mobile line that gives 256 kbit/s takes most of it.
        /// </summary>
        public static readonly  TimeSpan  DefaultHTTPUploadTimeout  = TimeSpan.FromHours(1);

        /// <summary>
        /// The longest an HTTP upload may be given: a day.
        /// </summary>
        public static readonly  TimeSpan  LongestHTTPUploadTimeout  = TimeSpan.FromDays(1);

        /// <summary>
        /// The listener of the node's certificate store a TLS server identity
        /// may be told it is shown on to be shown on the upload port.
        /// </summary>
        public const            String    UploadListener        = "upload";

        #endregion

        #region Properties

        /// <summary>
        /// Where the uploads are kept: one directory a charging station, and
        /// the index of every upload beside them.
        /// </summary>
        public String      Directory       { get; init; } = DefaultDirectory;

        /// <summary>
        /// Whether files are taken by HTTP PUT.
        /// </summary>
        public Boolean     HTTP            { get; init; } = true;

        /// <summary>
        /// The address HTTP uploads are taken on: every address of both
        /// families by default.
        /// </summary>
        public IPAddress  HTTPListen      { get; init; } = IPvXAddress.Any;

        /// <summary>
        /// The TCP port HTTP uploads are taken on.
        /// </summary>
        public IPPort      HTTPPort        { get; init; } = DefaultHTTPUploadPort;

        /// <summary>
        /// Whether the upload port speaks TLS, and with which certificate - its
        /// own, and not the web interface's: out of the node's certificate store
        /// the TLS server identity for the listener "upload", or one told no
        /// listener; or a pair of PEM files somebody else keeps up to date. Null
        /// for plain HTTP.
        /// </summary>
        public WebTLSConfiguration?  TLS   { get; init; }

        /// <summary>
        /// The root of the upload port as charging stations reach it - a name
        /// rather than the address it listens on, a proxy's port in front, its
        /// "https" - which the banner, the command line and the web interface
        /// say. Null where it is to be guessed from where the port listens.
        /// </summary>
        public Uri?        PublicURL       { get; init; }

        /// <summary>
        /// How long one HTTP upload may take from its first byte to its last.
        /// </summary>
        public TimeSpan    HTTPUploadTimeout  { get; init; } = DefaultHTTPUploadTimeout;

        /// <summary>
        /// Whether an HTTP upload without credentials is taken as well -
        /// written down as "anonymous", and its charging station as the first
        /// part of its path says. Off by default: whoever can reach the port
        /// could otherwise fill the disk.
        /// </summary>
        public Boolean     AnonymousHTTP   { get; init; } = false;

        /// <summary>
        /// Whether files are taken over SFTP, on the node's SSH server.
        /// </summary>
        public Boolean     SFTP            { get; init; } = true;

        /// <summary>
        /// The largest file taken, in bytes.
        /// </summary>
        public Int64       MaxFileSize     { get; init; } = DefaultMaxFileSize;

        #endregion


        #region (static) TryParse(JSON, out Settings, out Error)

        /// <summary>
        /// Read the "logServer" section; a missing section is every default.
        /// </summary>
        public static Boolean TryParse(JToken?                                      JSON,
                                       [NotNullWhen(true)]  out LogServerSettings?  Settings,
                                       [NotNullWhen(false)] out String?             Error)
        {

            Settings  = null;
            Error     = null;

            var settings = new LogServerSettings();

            if (JSON is null || JSON.Type == JTokenType.Null)
            {
                Settings = settings;
                return true;
            }

            if (JSON is not JObject json)
            {
                Error = $"The \"{SectionName}\" section is no JSON object.";
                return false;
            }

            #region directory

            if (json["directory"] is JToken directoryToken && directoryToken.Type != JTokenType.Null)
            {

                if (directoryToken.Type != JTokenType.String || String.IsNullOrWhiteSpace(directoryToken.Value<String>()))
                {
                    Error = $"\"directory\" of the \"{SectionName}\" section is no directory: '{directoryToken}'.";
                    return false;
                }

                settings = settings with { Directory = directoryToken.Value<String>()!.Trim() };

            }

            #endregion

            #region http / anonymousHTTP / sftp

            foreach (var (key, set) in new (String, Func<LogServerSettings, Boolean, LogServerSettings>)[] {
                                           ("http",           (s, value) => s with { HTTP          = value }),
                                           ("anonymousHTTP",  (s, value) => s with { AnonymousHTTP = value }),
                                           ("sftp",           (s, value) => s with { SFTP          = value })
                                       })
            {

                if (json[key] is not JToken token || token.Type == JTokenType.Null)
                    continue;

                if (token.Type != JTokenType.Boolean)
                {
                    Error = $"\"{key}\" of the \"{SectionName}\" section is neither true nor false: '{token}'.";
                    return false;
                }

                settings = set(settings, token.Value<Boolean>());

            }

            #endregion

            #region httpListen

            if (json["httpListen"] is JToken listenToken && listenToken.Type != JTokenType.Null)
            {

                if (!TryParseListen(listenToken.Value<String>(), out var listen))
                {
                    Error = $"\"httpListen\" of the \"{SectionName}\" section is no address to listen on: '{listenToken}'. " +
                             "An IPv4 or IPv6 address, or \"any\" for both families.";
                    return false;
                }

                settings = settings with { HTTPListen = listen.Value };

            }

            #endregion

            #region httpPort

            if (json["httpPort"] is JToken portToken && portToken.Type != JTokenType.Null)
            {

                if (portToken.Type != JTokenType.Integer ||
                    portToken.Value<Int64>() is < 1 or > UInt16.MaxValue)
                {
                    Error = $"\"httpPort\" of the \"{SectionName}\" section is no port number: '{portToken}'.";
                    return false;
                }

                settings = settings with { HTTPPort = IPPort.Parse(portToken.Value<UInt16>()) };

            }

            #endregion

            #region tls

            // As the "web" section's "tls" is written - a certificate out of the
            // store, or a pair of PEM files - but without a port of plain HTTP
            // that redirects: a charging station is told one URL, and follows it.
            if (json["tls"] is JToken tlsToken && tlsToken.Type != JTokenType.Null)
            {

                var path = $"{SectionName}.tls";

                if (tlsToken is not JObject tlsJSON)
                {
                    Error = $"'{path}' is no JSON object: {{ \"certificate\": \"{WebTLSConfiguration.StoreText}\" }}, or {{ \"certificate\": {{ \"fullchain\": \"<file>\", \"privateKey\": \"<file>\" }} }}.";
                    return false;
                }

                if (tlsJSON.ContainsKey("redirectPort"))
                {
                    Error = $"'{path}' says 'redirectPort': the upload port redirects nowhere - a charging station is told its URL, and HTTPS in it where the port speaks TLS.";
                    return false;
                }

                if (!WebTLSConfiguration.TryParse(tlsJSON, path, out var tls, out var tlsError))
                {
                    Error = tlsError;
                    return false;
                }

                settings = settings with { TLS = tls };

            }

            #endregion

            #region publicURL

            if (json["publicURL"] is JToken urlToken && urlToken.Type != JTokenType.Null)
            {

                if (urlToken.Type != JTokenType.String)
                {
                    Error = $"'{SectionName}.publicURL' is no text: '{urlToken}'. A URL, as \"https://logs.example.org:8363/\".";
                    return false;
                }

                if (!TryParsePublicURL(urlToken.Value<String>(), out var publicURL, out var urlError))
                {
                    Error = $"'{SectionName}.publicURL' is no URL a charging station could upload to: {urlError}";
                    return false;
                }

                settings = settings with { PublicURL = publicURL };

            }

            #endregion

            #region httpUploadTimeout

            if (!ConfigurationReader.TryReadSeconds(json,
                                                    "httpUploadTimeout",
                                                    SectionName,
                                                    1,
                                                    LongestHTTPUploadTimeout.TotalSeconds,
                                                    out var uploadTimeout,
                                                    out var timeoutError))
            {
                Error = timeoutError;
                return false;
            }

            if (uploadTimeout is TimeSpan timeout)
                settings = settings with { HTTPUploadTimeout = timeout };

            #endregion

            #region maxFileSize

            if (json["maxFileSize"] is JToken sizeToken && sizeToken.Type != JTokenType.Null)
            {

                if (sizeToken.Type != JTokenType.Integer ||
                    sizeToken.Value<Int64>() is < 1 or > LargestMaxFileSize)
                {
                    Error = $"\"maxFileSize\" of the \"{SectionName}\" section is no number of bytes between 1 and {LargestMaxFileSize}: '{sizeToken}'.";
                    return false;
                }

                settings = settings with { MaxFileSize = sizeToken.Value<Int64>() };

            }

            #endregion

            Settings = settings;
            return true;

        }

        #endregion

        #region (static) TryParseListen(Text, out Address)

        /// <summary>
        /// An IPv4 or IPv6 address, or "any" for every address of both families.
        /// </summary>
        public static Boolean TryParseListen(String?                              Text,
                                             [NotNullWhen(true)] out IPAddress? Address)
        {

            Address = null;

            var text = Text?.Trim() ?? "";

            if (text.Equals("any", StringComparison.OrdinalIgnoreCase) || text == "::")
            {
                Address = IPvXAddress.Any;
                return true;
            }

            if (!IPAddress.TryParse(text, out var parsed))
                return false;

            Address = parsed;
            return true;

        }

        #endregion

        #region (static) TryParsePublicURL(Text, out URL, out Error)

        /// <summary>
        /// The root of the upload port as charging stations reach it: "http" or
        /// "https", a host, a port where it is not the scheme's, and a path
        /// below which "&lt;charging station&gt;/&lt;file name&gt;" follows - a
        /// proxy's prefix - but no credentials, query or fragment. Always with
        /// a "/" at its end.
        /// </summary>
        public static Boolean TryParsePublicURL(String?                           Text,
                                                [NotNullWhen(true)]  out Uri?     URL,
                                                [NotNullWhen(false)] out String?  Error)
        {

            URL    = null;
            Error  = null;

            var text = Text?.Trim() ?? "";

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                Error = $"'{text}' is no URL of \"http\" or \"https\", as \"https://logs.example.org:8363/\".";
                return false;
            }

            if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                Error = $"'{text}' says credentials, a query or a fragment: the URL is the root a charging station is given, and its credentials are its own.";
                return false;
            }

            URL = new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
            return true;

        }

        #endregion

        #region (static) TextOf(Address)

        /// <summary>
        /// The address as the section writes it: "any", or the address.
        /// </summary>
        public static String TextOf(IPAddress Address)

            => Address.TryGetValue(out IPvXAddress _)
                   ? "any"
                   : Address.ToString();

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("directory",          Directory),
                   new JProperty("http",               HTTP),
                   new JProperty("httpListen",         TextOf(HTTPListen)),
                   new JProperty("httpPort",           HTTPPort.ToUInt16()),
                   new JProperty("tls",                TLS?.ToJSON()),
                   new JProperty("publicURL",          PublicURL?.ToString()),
                   new JProperty("httpUploadTimeout",  (Int64) HTTPUploadTimeout.TotalSeconds),
                   new JProperty("anonymousHTTP",      AnonymousHTTP),
                   new JProperty("sftp",               SFTP),
                   new JProperty("maxFileSize",        MaxFileSize)
               );

        #endregion

    }

}
