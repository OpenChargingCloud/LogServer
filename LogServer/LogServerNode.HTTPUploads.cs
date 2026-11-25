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

using System.Text;
using System.Buffers;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;

#endregion

namespace cloud.charging.open.LogServer
{

    /// <summary>
    /// The port that takes uploads by HTTP PUT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PUT /&lt;charging station&gt;/&lt;file name&gt;</c> - or
    /// <c>PUT /&lt;file name&gt;</c>, where the charging station is the account
    /// it is uploaded under. The body is the file, as it is; whatever lies
    /// between the first and the last part of a longer path is not read. This
    /// is what a charging station is given as the location of OCPP's GetLog or
    /// GetDiagnostics, with the credentials of its account in it:
    /// <c>http://CS001:password@logs.example.org:8363/CS001/</c>.
    /// </para>
    /// <para>
    /// The body is written to the disk as it arrives, and never held in
    /// memory: an upload costs one buffer, however large the file.
    /// </para>
    /// <para>
    /// Signed in with HTTP Basic Auth - or an API key, or a session - as an
    /// account that may upload: the "uploader" role. Without credentials only
    /// where the section says "anonymousHTTP": true. Nothing is served on this
    /// port: no page, no API, no file - it takes files and says what it made
    /// of them.
    /// </para>
    /// </remarks>
    public partial class LogServerNode
    {

        #region Data

        /// <summary>
        /// The uploader an upload without credentials is written down as.
        /// </summary>
        public const String  AnonymousUploader  = "anonymous";

        /// <summary>
        /// The name of a file uploaded to "/", which names none.
        /// </summary>
        public const String  UnnamedUpload      = "upload.bin";

        /// <summary>
        /// The realm an HTTP client is asked to sign in to.
        /// </summary>
        public const String  UploadRealm        = "OpenChargingCloud LogServer";

        /// <summary>
        /// How much of an upload is read from the network, and written to the
        /// disk, at once: what one upload costs in memory, however large it is.
        /// </summary>
        private const Int32  StreamBufferSize   = 80 * 1024;

        #endregion


        #region (private) RegisterHTTPUploads(Server)

        private void RegisterHTTPUploads(HTTPServer Server)
        {

            var api = Server.AddHTTPAPI(HTTPPath.Root);

            api.AddHandler(HTTPPath.Root,               PutUpload,  HTTPMethod.PUT);
            api.AddHandler(HTTPPath.Root + "{path..}",  PutUpload,  HTTPMethod.PUT);

            // What else is asked of this port is told what it is for.
            foreach (var method in new[] { HTTPMethod.GET, HTTPMethod.HEAD, HTTPMethod.POST, HTTPMethod.PATCH, HTTPMethod.DELETE })
            {
                api.AddHandler(HTTPPath.Root,               OnlyPut,  method);
                api.AddHandler(HTTPPath.Root + "{path..}",  OnlyPut,  method);
            }

        }

        #endregion

        #region (private) PutUpload(Request)

        private Task<HTTPResponse> PutUpload(HTTPRequest Request)
        {

            var from = RemoteOf(Request);

            #region Who uploads

            String uploader;

            if (ExtAPI.TryGetHTTPUser(Request, out var user) && user is not null)
            {

                if (!IsAllowed(user, [ Permission.Run(LogServerAccess.Upload) ]))
                {

                    Interlocked.Increment(ref httpUploadsRefused);

                    Log.Warning($"'{user.Id}' was refused an HTTP upload to {Request.Path} from {from}: the account is not in the {LogServerAccess.Uploader.Name} role.",
                                "logserver", "uploads", "http", "auth");

                    return Task.FromResult(Answer(Request, HTTPStatusCode.Forbidden, $"'{user.Id}' may not upload: the account is not in the {LogServerAccess.Uploader.Name} role."));

                }

                uploader = user.Id.ToString();

            }

            else if (Request.Authorization is null && Settings.AnonymousHTTP)
                uploader = AnonymousUploader;

            else
            {

                Interlocked.Increment(ref httpUploadsRefused);

                if (Request.Authorization is not null)
                    Log.Warning($"An HTTP upload to {Request.Path} from {from} was refused: its credentials open no account.",
                                "logserver", "uploads", "http", "auth");

                return Task.FromResult(
                           new HTTPResponse.Builder(Request) {
                               HTTPStatusCode   = HTTPStatusCode.Unauthorized,
                               WWWAuthenticate  = WWWAuthenticate.Basic(UploadRealm),
                               ContentType      = HTTPContentType.Application.JSON_UTF8,
                               Content          = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", "Sign in with the account of an uploader.")).ToString(Formatting.None)),
                               CacheControl     = "no-store",
                               Connection       = ConnectionType.Close
                           }.AsImmutable
                       );

            }

            #endregion

            #region Which charging station, and which file

            var parts = Request.Path.ToString().
                                Split('/', StringSplitOptions.RemoveEmptyEntries).
                                Select(part => Uri.UnescapeDataString(part).Trim()).
                                Where (part => part.Length > 0).
                                ToArray();

            var chargingStation  = parts.Length >= 2 ? parts[0]  : uploader;
            var fileName         = parts.Length >= 1 ? parts[^1] : UnnamedUpload;

            if (!SftpArea.IsFileName(fileName))
            {
                Interlocked.Increment(ref httpUploadsRefused);
                return Task.FromResult(Answer(Request, HTTPStatusCode.BadRequest, $"'{fileName}' is no name a file can have."));
            }

            if (chargingStation.Length > 200 || chargingStation.Any(Char.IsControl))
            {
                Interlocked.Increment(ref httpUploadsRefused);
                return Task.FromResult(Answer(Request, HTTPStatusCode.BadRequest, "That is no name of a charging station."));
            }

            #endregion

            return Receive(Request, uploader, chargingStation, fileName, from);

        }

        #endregion

        #region (private) Receive(Request, Uploader, ChargingStation, FileName, From)

        /// <summary>
        /// The body of the PUT written to the disk as it arrives - never held in
        /// memory, however large - and, once its last byte is there, moved into
        /// the store. A body that breaks off, is too large or takes too long
        /// leaves nothing behind.
        /// </summary>
        private async Task<HTTPResponse> Receive(HTTPRequest  Request,
                                                 String       Uploader,
                                                 String       ChargingStation,
                                                 String       FileName,
                                                 String       From)
        {

            // No body stream: no Content-Length but 0, and no chunks.
            if (Request.HTTPBodyStream is not Stream body)
            {
                Interlocked.Increment(ref httpUploadsRefused);
                return Answer(Request, HTTPStatusCode.BadRequest, "The upload is empty: the body of the PUT is the file.");
            }

            Directory.CreateDirectory(HTTPIncomingDirectory);

            // Below the uploads, and so on the volume of the store: taking it
            // in is a rename, however large it is.
            var part    = Path.Combine(HTTPIncomingDirectory, $"{Guid.NewGuid():N}.part");
            var size    = 0L;
            var buffer  = ArrayPool<Byte>.Shared.Rent(StreamBufferSize);

            try
            {

                #region Write it to the disk as it arrives

                try
                {

                    await using var file = new FileStream(part,
                                                          FileMode.CreateNew,
                                                          FileAccess.Write,
                                                          FileShare.None,
                                                          StreamBufferSize,
                                                          FileOptions.Asynchronous);

                    Int32 read;

                    while ((read = await body.ReadAsync(buffer.AsMemory(0, StreamBufferSize), Request.CancellationToken)) > 0)
                    {

                        size += read;

                        // Hermod counts as well - a Content-Length too large is
                        // refused before the first byte, chunks as they come -
                        // but what is kept is this log server's to limit.
                        if (size > Settings.MaxFileSize)
                            throw new HTTPBodyTooLargeException((UInt64) size, (UInt64) Settings.MaxFileSize);

                        await file.WriteAsync(buffer.AsMemory(0, read), Request.CancellationToken);

                    }

                }
                catch (HTTPBodyTooLargeException)
                {

                    Log.Warning($"An HTTP upload of '{FileName}' by '{Uploader}' from {From} was refused: it is larger than the {SizeOf(Settings.MaxFileSize)} this log server takes (\"maxFileSize\").",
                                "logserver", "uploads", "http");

                    return Refused(HTTPStatusCode.RequestEntityTooLarge, $"The file is larger than the {SizeOf(Settings.MaxFileSize)} this log server takes.");

                }
                catch (HTTPReadTimeoutException)
                {

                    Log.Warning($"An HTTP upload of '{FileName}' by '{Uploader}' from {From} was refused: it took longer than the {Settings.HTTPUploadTimeout} an upload may take (\"httpUploadTimeout\"); {SizeOf(size)} had arrived.",
                                "logserver", "uploads", "http");

                    return Refused(HTTPStatusCode.RequestTimeout, $"The upload took longer than the {Settings.HTTPUploadTimeout} an upload may take; {SizeOf(size)} had arrived.");

                }
                catch (Exception problem) when (problem is HTTPIncompleteBodyException or HTTPInvalidChunkException or HTTPChunkMetadataTooLargeException or IOException or OperationCanceledException)
                {

                    Log.Warning($"An HTTP upload of '{FileName}' by '{Uploader}' from {From} broke off after {SizeOf(size)}: {problem.Message}",
                                "logserver", "uploads", "http");

                    return Refused(HTTPStatusCode.BadRequest, $"The upload broke off after {SizeOf(size)}: {problem.Message}");

                }

                if (size == 0)
                    return Refused(HTTPStatusCode.BadRequest, "The upload is empty: the body of the PUT is the file.");

                #endregion

                #region ...and into the store

                try
                {

                    var upload = Uploads.KeepFile(part,
                                                  UploadProtocol.HTTP,
                                                  Uploader,
                                                  ChargingStation,
                                                  FileName,
                                                  From,
                                                  Request.ContentType?.ToString(),
                                                  Request.UserAgent);

                    Log.Notice($"'{Uploader}' uploaded '{upload.FileName}' of '{upload.ChargingStation}' by HTTP from {From}: {SizeOf(upload.Size)}, kept as upload {upload.Id}.",
                               "logserver", "uploads", "http");

                    return new HTTPResponse.Builder(Request) {
                               HTTPStatusCode  = HTTPStatusCode.Created,
                               ContentType     = HTTPContentType.Application.JSON_UTF8,
                               Content         = Encoding.UTF8.GetBytes(upload.ToJSON().ToString(Formatting.None)),
                               CacheControl    = "no-store"
                           }.AsImmutable;

                }
                catch (Exception problem)
                {

                    Log.Error($"What '{Uploader}' uploaded by HTTP as '{FileName}' could not be kept: {problem.Message}",
                              "logserver", "uploads", "http");

                    return Answer(Request, HTTPStatusCode.InternalServerError, "The file could not be kept. The log of the log server says why.");

                }

                #endregion

            }
            finally
            {

                ArrayPool<Byte>.Shared.Return(buffer);

                // Taken into the store, it is no longer there; otherwise it is
                // what an upload that was refused or broke off left.
                try
                {
                    if (File.Exists(part))
                        File.Delete(part);
                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
                {
                    Log.Warning($"What an HTTP upload left in '{part}' could not be taken away: {problem.Message}", "logserver", "uploads", "http");
                }

            }


            HTTPResponse Refused(HTTPStatusCode Status, String Error)
            {
                Interlocked.Increment(ref httpUploadsRefused);
                return Answer(Request, Status, Error);
            }

        }

        #endregion

        #region (private) RemoteOf(Request)

        /// <summary>
        /// Who sent the request, as the node says it - behind a trusted proxy,
        /// the client the proxy names - with an IPv4 client that reached a
        /// socket of both families written as the IPv4 address it is, and not
        /// as "[0000:...:ffff:7f00:0001]".
        /// </summary>
        private String RemoteOf(HTTPRequest Request)

            => Unmapped(ClientOf(Request));

        #endregion

        #region (static) Unmapped(Socket)

        /// <summary>
        /// A socket as Hermod writes it - "[0000:0000:0000:0000:0000:ffff:c000:020a]:50832" -
        /// with an IPv4 address mapped into IPv6 written as the IPv4 address it
        /// is: "192.0.2.10:50832". Everything else as it is.
        /// </summary>
        public static String Unmapped(String Socket)
        {

            var match = System.Text.RegularExpressions.Regex.Match(Socket, @"^\[([0-9A-Fa-f:.]+)\]:(\d+)(.*)$");

            return match.Success &&
                   System.Net.IPAddress.TryParse(match.Groups[1].Value, out var address) &&
                   address.IsIPv4MappedToIPv6
                       ? $"{address.MapToIPv4()}:{match.Groups[2].Value}{match.Groups[3].Value}"
                       : Socket;

        }

        #endregion

        #region (private) OnlyPut(Request)

        private Task<HTTPResponse> OnlyPut(HTTPRequest Request)

            => Task.FromResult(
                   new HTTPResponse.Builder(Request) {
                       HTTPStatusCode  = HTTPStatusCode.MethodNotAllowed,
                       Allow           = [ HTTPMethod.PUT ],
                       ContentType     = HTTPContentType.Application.JSON_UTF8,
                       Content         = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", "This port takes log files by PUT /<charging station>/<file name>, and nothing else.")).ToString(Formatting.None)),
                       CacheControl    = "no-store"
                   }.AsImmutable
               );

        #endregion

        #region (private static) Answer(Request, Status, Error)

        private static HTTPResponse Answer(HTTPRequest     Request,
                                           HTTPStatusCode  Status,
                                           String          Error)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = Status,
                   ContentType     = HTTPContentType.Application.JSON_UTF8,
                   Content         = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", Error)).ToString(Formatting.None)),
                   CacheControl    = "no-store"
               }.AsImmutable;

        #endregion

    }

}
