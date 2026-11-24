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

using System.Globalization;
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.LogServer
{

    /// <summary>
    /// The JSON API of a log server: what every node's has, and the log server
    /// at "/api/v1/logServer".
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>GET  /api/v1/logServer                          - how the log server stands: where uploads are taken, how many are kept.</item>
    ///   <item>GET  /api/v1/logServer/settings                 - the "logServer" section as it is in effect, the switches of this start included.</item>
    ///   <item>GET  /api/v1/logServer/uploads                  - the uploads, filtered by ?uploader=, ?chargingStation=, ?since=, ?until=, ordered by ?order=newest|oldest, paged by ?skip= and ?take=.</item>
    ///   <item>GET  /api/v1/logServer/uploads/{id}             - one upload.</item>
    ///   <item>GET  /api/v1/logServer/uploads/{id}/file        - its file, to save - or, with ?inline=true, to read in the browser as text.</item>
    ///   <item>POST /api/v1/logServer/uploads/{id}/remove      - the upload and its file taken away.</item>
    ///   <item>GET  /api/v1/logServer/uploaders                - the accounts in the uploader role.</item>
    ///   <item>POST /api/v1/logServer/uploaders                - { "id", "name"? }: an uploader more, answered with its password, once.</item>
    ///   <item>POST /api/v1/logServer/uploaders/password       - { "id" }: a new password for an uploader, answered once.</item>
    ///   <item>POST /api/v1/logServer/uploaders/remove         - { "id" }: an uploader no longer let upload.</item>
    /// </list>
    /// </remarks>
    public class LogServerHTTPAPI : NodeHTTPAPI
    {

        #region Properties

        /// <summary>
        /// The log server this API speaks for.
        /// </summary>
        public LogServerNode  LogServer  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the JSON API within the given HTTP server.
        /// </summary>
        public LogServerHTTPAPI(HTTPServer     HTTPServer,
                                LogServerNode  Node,
                                HTTPExtAPI     ExtAPI,
                                EventLog       Log,
                                HTTPPath?      APIPath   = null,
                                String?        Version   = null)

            : base(HTTPServer,
                   Node,
                   ExtAPI,
                   Log,
                   APIPath,
                   Version ?? typeof(LogServerHTTPAPI).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")

        {

            this.LogServer = Node;

            var root = HTTPPath.Root + "v1/logServer";

            AddHandler(root,                             GetStatus,           HTTPMethod.GET);
            AddHandler(root + "/settings",               GetSettings,         HTTPMethod.GET);
            AddHandler(root + "/uploads",                GetUploads,          HTTPMethod.GET);
            AddHandler(root + "/uploads/{id}",           GetUpload,           HTTPMethod.GET);
            AddHandler(root + "/uploads/{id}/file",      GetUploadFile,       HTTPMethod.GET);
            AddHandler(root + "/uploads/{id}/remove",    PostRemoveUpload,    HTTPMethod.POST);
            AddHandler(root + "/uploaders",              GetUploaders,        HTTPMethod.GET);
            AddHandler(root + "/uploaders",              PostUploader,        HTTPMethod.POST);
            AddHandler(root + "/uploaders/password",     PostPassword,        HTTPMethod.POST);
            AddHandler(root + "/uploaders/remove",       PostRemoveUploader,  HTTPMethod.POST);

            // Every browser on the Uploads page hears of an upload at once.
            Node.Uploads.OnUploaded += upload => Publish("upload",        upload.ToJSON());
            Node.Uploads.OnRemoved  += upload => Publish("uploadRemoved", new JObject(new JProperty("id", upload.Id)));

        }

        #endregion


        #region (private) GetStatus(Request) / GetSettings(Request)

        private Task<HTTPResponse> GetStatus(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(LogServerAccess.Uploads), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, LogServer.StatusJSON()));

        }

        private Task<HTTPResponse> GetSettings(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(LogServerAccess.Uploads), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, LogServer.Settings.ToJSON()));

        }

        #endregion

        #region (private) GetUploads(Request)

        /// <summary>
        /// The uploads asked for, how many there are in all, and every uploader
        /// and charging station there is - for the filters to offer.
        /// </summary>
        private Task<HTTPResponse> GetUploads(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(LogServerAccess.Uploads), false, out _, out var refused))
                return Task.FromResult(refused);

            var query = Request.QueryString;

            DateTimeOffset? since = null;
            DateTimeOffset? until = null;

            if (query.GetString("since") is { Length: > 0 } sinceText)
            {
                if (!DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, $"'since' is no point in time: '{sinceText}'."));
                since = parsed;
            }

            if (query.GetString("until") is { Length: > 0 } untilText)
            {
                if (!DateTimeOffset.TryParse(untilText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, $"'until' is no point in time: '{untilText}'."));
                until = parsed;
            }

            var order = query.GetString("order") ?? "newest";

            if (order is not ("newest" or "oldest"))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, $"'order' is \"newest\" or \"oldest\", not '{order}'."));

            var uploads = LogServer.Uploads.Query(new UploadQuery(
                                                      Uploader:         query.GetString("uploader"),
                                                      ChargingStation:  query.GetString("chargingStation"),
                                                      Since:            since,
                                                      Until:            until,
                                                      Newest:           order == "newest",
                                                      Skip:             Math.Max(0, query.GetInt32("skip") ?? 0),
                                                      Take:             Math.Clamp(query.GetInt32("take") ?? UploadStore.DefaultPageSize, 1, UploadStore.MaxPageSize)
                                                  ),
                                                  out var total);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, new JObject(
                       new JProperty("total",             total),
                       new JProperty("uploads",           new JArray(uploads.Select(upload => upload.ToJSON()))),
                       new JProperty("uploaders",         new JArray(LogServer.Uploads.Uploaders.       Select(one => one.Name))),
                       new JProperty("chargingStations",  new JArray(LogServer.Uploads.ChargingStations.Select(one => one.Name)))
                   )));

        }

        #endregion

        #region (private) GetUpload(Request)

        private Task<HTTPResponse> GetUpload(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(LogServerAccess.Uploads), false, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryGetUpload(Request, out var upload, out var notFound))
                return Task.FromResult(notFound);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, upload.ToJSON()));

        }

        #endregion

        #region (private) GetUploadFile(Request)

        /// <summary>
        /// The file of an upload: to be saved under the name it came as - or,
        /// with ?inline=true, to be read in the browser as text, whatever it
        /// is, so that nothing in it is run as a page.
        /// </summary>
        private async Task<HTTPResponse> GetUploadFile(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(LogServerAccess.Uploads), false, out _, out var refused))
                return refused;

            if (!TryGetUpload(Request, out var upload, out var notFound))
                return notFound;

            String file;

            try
            {
                file = LogServer.Uploads.FileOf(upload);
            }
            catch (InvalidOperationException problem)
            {
                return ErrorJSON(Request, HTTPStatusCode.InternalServerError, problem.Message);
            }

            if (!File.Exists(file))
                return ErrorJSON(Request, HTTPStatusCode.Gone, $"The file of upload {upload.Id} is no longer on the disk.");

            var inline   = String.Equals(Request.QueryString.GetString("inline"), "true", StringComparison.OrdinalIgnoreCase);
            var content  = await File.ReadAllBytesAsync(file);
            var ascii    = new String(upload.FileName.Select(c => c is >= ' ' and < (Char) 127 and not '"' and not '\\' ? c : '_').ToArray());

            return new HTTPResponse.Builder(Request) {
                       HTTPStatusCode  = HTTPStatusCode.OK,
                       ContentType     = inline
                                             ? HTTPContentType.Text.PLAIN
                                             : HTTPContentType.Application.OCTETSTREAM,
                       Content         = content,
                       CacheControl    = "no-store"
                   }.
                   Set("Content-Disposition",     $"{(inline ? "inline" : "attachment")}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(upload.FileName)}").
                   Set("X-Content-Type-Options",  "nosniff").
                   Set("Content-Security-Policy", "default-src 'none'; sandbox").
                   AsImmutable;

        }

        #endregion

        #region (private) PostRemoveUpload(Request)

        private Task<HTTPResponse> PostRemoveUpload(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(LogServerAccess.Uploads), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryGetUpload(Request, out var upload, out var notFound))
                return Task.FromResult(notFound);

            if (!LogServer.Uploads.Remove(upload.Id, out var removed, out var error) || removed is null)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.NotFound, $"There is no upload {upload.Id}."));

            if (error is not null)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.InternalServerError, $"Nothing was removed, since it could not be saved: {error}"));

            Log.Notice($"'{user.Id}' removed upload {removed.Id}: '{removed.FileName}' of '{removed.ChargingStation}', uploaded by '{removed.Uploader}' at {removed.Timestamp:u}.",
                       "logserver", "uploads");

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, new JObject(
                       new JProperty("said", $"Upload {removed.Id} - '{removed.FileName}' of '{removed.ChargingStation}' - was removed, and its file with it.")
                   )));

        }

        #endregion


        #region (private) GetUploaders(Request)

        private Task<HTTPResponse> GetUploaders(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(LogServerAccess.Uploaders), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, LogServer.UploadersJSON()));

        }

        #endregion

        #region (private) PostUploader(Request)

        private async Task<HTTPResponse> PostUploader(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(LogServerAccess.Uploaders), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return badRequest;

            if (!TryText(json, "id",   true,  out var id,   out var problem) ||
                !TryText(json, "name", false, out var name, out problem))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, problem);

            return Answer(Request, await LogServer.AddUploader(id!, name, user.Id.ToString()));

        }

        #endregion

        #region (private) PostPassword(Request)

        private async Task<HTTPResponse> PostPassword(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(LogServerAccess.Uploaders), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return badRequest;

            if (!TryText(json, "id", true, out var id, out var problem))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, problem);

            return Answer(Request, await LogServer.NewUploaderPassword(id!, user.Id.ToString()));

        }

        #endregion

        #region (private) PostRemoveUploader(Request)

        private async Task<HTTPResponse> PostRemoveUploader(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(LogServerAccess.Uploaders), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return badRequest;

            if (!TryText(json, "id", true, out var id, out var problem))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, problem);

            return Answer(Request, await LogServer.RemoveUploader(id!, user.Id.ToString()));

        }

        #endregion


        #region (private) Answer(Request, Change)

        /// <summary>
        /// What a change of the uploaders came to: what it said, the password
        /// where one was made, and the uploaders as they are now - or why
        /// nothing changed, with the status that says whose fault.
        /// </summary>
        private HTTPResponse Answer(HTTPRequest     Request,
                                    UploaderChange  Change)

            => Change.Outcome switch {

                   UploaderChangeOutcome.Changed   => JSONResponse(Request, HTTPStatusCode.OK, new JObject(
                                                          new JProperty("said",       Change.Said),
                                                          new JProperty("uploader",   Change.Uploader),
                                                          new JProperty("password",   Change.Password),
                                                          new JProperty("uploaders",  LogServer.UploadersJSON())
                                                      )),

                   UploaderChangeOutcome.NotFound  => ErrorJSON(Request, HTTPStatusCode.NotFound,            Change.Said),
                   UploaderChangeOutcome.NotSaved  => ErrorJSON(Request, HTTPStatusCode.InternalServerError, Change.Said),
                   _                               => ErrorJSON(Request, HTTPStatusCode.BadRequest,          Change.Said)

               };

        #endregion

        #region (private) TryGetUpload(Request, out Upload, out NotFound)

        private Boolean TryGetUpload(HTTPRequest                             Request,
                                     [NotNullWhen(true)]  out UploadEvent?   Upload,
                                     [NotNullWhen(false)] out HTTPResponse?  NotFound)
        {

            Upload    = null;
            NotFound  = null;

            var text  = Request.ParsedURLParameters.Length > 0
                            ? Request.ParsedURLParameters[0].Trim()
                            : "";

            if (!UInt64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                !LogServer.Uploads.TryGet(id, out Upload) ||
                Upload is null)
            {
                NotFound = ErrorJSON(Request, HTTPStatusCode.NotFound, $"There is no upload '{text}'.");
                return false;
            }

            return true;

        }

        #endregion

        #region (private static) TryText(JSON, Key, Required, out Text, out Problem)

        private static Boolean TryText(JObject                           JSON,
                                       String                            Key,
                                       Boolean                           Required,
                                       out String?                       Text,
                                       [NotNullWhen(false)] out String?  Problem)
        {

            Text     = null;
            Problem  = null;

            if (JSON[Key] is not JToken token || token.Type == JTokenType.Null ||
                (token.Type == JTokenType.String && String.IsNullOrWhiteSpace(token.Value<String>()) && !Required))
            {

                if (!Required)
                    return true;

                Problem = $"\"{Key}\" is missing.";
                return false;

            }

            if (token.Type != JTokenType.String || String.IsNullOrWhiteSpace(token.Value<String>()))
            {
                Problem = $"\"{Key}\" is text, and not empty.";
                return false;
            }

            Text = token.Value<String>()!.Trim();
            return true;

        }

        #endregion

    }

}
