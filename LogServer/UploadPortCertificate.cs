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
using org.GraphDefined.Vanaheimr.Hermod.TCP;

using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.LogServer
{

    /// <summary>
    /// The certificate the upload port shows at every handshake - its own, and
    /// not the web interface's: out of the node's certificate store, or out of
    /// a pair of PEM files, asked anew at every handshake, so that a
    /// certificate renewed under a running log server is shown from the next
    /// connection on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Out of the store</b>: what the node's listeners choose for the
    /// listener "upload" - the TLS server identities told that listener, or
    /// told none, which are switched on and valid now, the one whose validity
    /// began last. An identity told "web" only is the web interface's, and
    /// never shown here: the upload port is reached by charging stations under
    /// a name of their network, the web interface by people under another.
    /// </para>
    /// <para>
    /// <b>Out of files</b>: Hermod's <see cref="ServerCertificateFile"/>, read
    /// again whenever their content changes, the certificate before kept where
    /// the new one does not read.
    /// </para>
    /// <para>
    /// <b>With nothing to show, a connection is refused</b>, and not served in
    /// the clear: a charging station told an "https" URL that gets plain HTTP
    /// sends its password unprotected.
    /// </para>
    /// </remarks>
    public sealed class UploadPortCertificate
    {

        #region Data

        private readonly ListenerCertificate      chooser;
        private readonly ServerCertificateFile?   files;
        private readonly EventLog                 log;
        private readonly Lock                     cacheLock  = new ();
        private          String?                  shown;
        private          Boolean                  saidNone;

        #endregion

        #region Properties

        /// <summary>
        /// Where the certificate comes from.
        /// </summary>
        public WebCertificateSource  Source    { get; }

        /// <summary>
        /// What is shown now, with its intermediates - read again first where
        /// the files changed - or null while there is nothing to show.
        /// </summary>
        public ServerCertificateChain? Current
        {
            get
            {

                var current = Source.IsStore
                                  ? chooser.Current
                                  : files!.Current;

                SayWhatChanged(current);

                return current;

            }
        }

        /// <summary>
        /// The entry of the store shown now, or null - for files, and while
        /// there is nothing to show.
        /// </summary>
        public CertificateEntry? StoreEntry
            => Source.IsStore
                   ? chooser.Entry
                   : null;

        /// <summary>
        /// Why the files as they are now were not taken, or null.
        /// </summary>
        public String? LastError
            => files?.LastError;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The certificate the upload port shows.
        /// </summary>
        /// <param name="Source">Where it comes from: the store, or files - whose paths are as they are read.</param>
        /// <param name="Chooser">What the listener "upload" shows out of the store, as the node's listeners choose.</param>
        /// <param name="Log">Where a certificate taken, or one that would not be, is said.</param>
        public UploadPortCertificate(WebCertificateSource  Source,
                                     ListenerCertificate   Chooser,
                                     EventLog              Log)
        {

            this.Source   = Source;
            this.chooser  = Chooser;
            this.log      = Log;

            if (!Source.IsStore)
            {

                // Read for the first time here. That they are not there yet is
                // no reason not to start: certbot may be about to write them.
                this.files = new ServerCertificateFile(Source.FullChain!, Source.PrivateKey);

                this.files.OnLoadFailed += error => log.Warning($"The certificate of the upload port was not taken from {Source}: {error} " +
                                                                (shown is not null
                                                                     ? "The one before is shown still."
                                                                     : "Every connection to the upload port is refused until it can be read."),
                                                                "logserver", "http", "tls", "security");

                if (this.files.LastError is String error)
                    log.Warning($"The certificate of the upload port could not be read from {Source}: {error}", "logserver", "http", "tls", "security");

            }

        }

        #endregion


        #region ChainFor(TCPServer, TCPClient)

        /// <summary>
        /// What the HTTP server asks at every handshake: the certificate and its
        /// intermediates - or an exception where there is nothing to show, which
        /// ends the connection rather than leaving it to go on without TLS.
        /// </summary>
        public ServerCertificateChain ChainFor(ITCPServer  TCPServer,
                                               TcpClient   TCPClient)

            => Current ?? throw new InvalidOperationException("This log server has no certificate for its upload port to show.");

        #endregion

        #region ToJSON()

        /// <summary>
        /// What the upload port shows: where it comes from, and the certificate.
        /// </summary>
        public JObject ToJSON()
        {

            var current  = Source.IsStore ? chooser.Current : files!.Current;
            var entry    = StoreEntry;

            return new JObject(
                       new JProperty("certificate",  Source.IsStore ? WebTLSConfiguration.StoreText : "files"),
                       new JProperty("fullchain",    Source.FullChain),
                       new JProperty("privateKey",   Source.PrivateKey),
                       new JProperty("label",        entry?.Label),
                       new JProperty("shows",        current?.Certificate.Subject),
                       new JProperty("thumbprint",   current is not null ? CertificateEntry.ThumbprintOf(current.Certificate) : null),
                       new JProperty("notAfter",     current?.Certificate.NotAfter.ToUniversalTime().ToString("o")),
                       new JProperty("problem",      current is null
                                                         ? LastError ?? (Source.IsStore
                                                                             ? $"The store holds no TLS server identity for '{LogServerSettings.UploadListener}', or for every listener, that is switched on and valid now."
                                                                             : "The files hold no certificate yet.")
                                                         : LastError)
                   );

        }

        #endregion

        #region (override) ToString()

        public override String ToString()
        {

            var current = Source.IsStore ? chooser.Current : files!.Current;

            return current is null
                       ? $"nothing yet from {Source}"
                       : $"{current.Certificate.Subject}, until {current.Certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}, from " +
                         (Source.IsStore && StoreEntry is CertificateEntry entry
                              ? $"the store's '{entry.Label}'"
                              : Source.ToString());

        }

        #endregion


        #region (private) SayWhatChanged(Current)

        /// <summary>
        /// Say so in the log when the upload port shows another certificate than
        /// at the handshake before, or nothing any more.
        /// </summary>
        private void SayWhatChanged(ServerCertificateChain? Current)
        {

            var now = Current is not null
                          ? CertificateEntry.ThumbprintOf(Current.Certificate)
                          : null;

            String?  before;
            Boolean  saidNoneBefore;

            lock (cacheLock)
            {

                before          = shown;
                saidNoneBefore  = saidNone;

                if (now is not null)
                {
                    shown     = now;
                    saidNone  = false;
                }
                else
                    saidNone  = true;

            }

            if (now is not null && !String.Equals(before, now, StringComparison.Ordinal))
                log.Notice($"The upload port shows {Current!.Certificate.Subject} ({now}), valid until {Current.Certificate.NotAfter.ToUniversalTime():yyyy-MM-dd HH:mm} UTC, from {Source}.",
                           "logserver", "http", "tls", "security");

            else if (now is null && !saidNoneBefore)
                log.Error($"The upload port has no certificate to show from {Source}: every connection to it is refused until there is one.",
                          "logserver", "http", "tls", "security");

        }

        #endregion

    }

}
