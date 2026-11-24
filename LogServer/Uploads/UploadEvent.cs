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

#endregion

namespace cloud.charging.open.LogServer
{

    /// <summary>
    /// How a file arrived.
    /// </summary>
    public enum UploadProtocol
    {

        /// <summary>
        /// By HTTP PUT, on the upload port.
        /// </summary>
        HTTP,

        /// <summary>
        /// Over SFTP, on the node's SSH server.
        /// </summary>
        SFTP

    }


    /// <summary>
    /// One file a charging station - or whoever uploads in its name - put on
    /// this log server, and everything known about how it came.
    /// </summary>
    /// <param name="Id">The number of the upload: one above the one before, never reused.</param>
    /// <param name="Timestamp">When it was complete and kept.</param>
    /// <param name="Protocol">How it arrived.</param>
    /// <param name="Uploader">The account it was uploaded under, or "anonymous".</param>
    /// <param name="ChargingStation">The charging station it is from: as the path of an HTTP upload names it, and the uploader otherwise.</param>
    /// <param name="FileName">The name it was uploaded as.</param>
    /// <param name="StoredAs">Where it is kept, below the uploads directory, with '/' between the parts.</param>
    /// <param name="Size">Its size in bytes.</param>
    /// <param name="SHA256">Its SHA-256, in lower-case hex.</param>
    /// <param name="RemoteAddress">Where it came from: the address and port of the client.</param>
    /// <param name="ContentType">The Content-Type an HTTP upload said, or null.</param>
    /// <param name="UserAgent">The User-Agent an HTTP upload said, or null.</param>
    public sealed record UploadEvent(UInt64          Id,
                                     DateTimeOffset  Timestamp,
                                     UploadProtocol  Protocol,
                                     String          Uploader,
                                     String          ChargingStation,
                                     String          FileName,
                                     String          StoredAs,
                                     Int64           Size,
                                     String          SHA256,
                                     String          RemoteAddress,
                                     String?         ContentType  = null,
                                     String?         UserAgent    = null)
    {

        #region ToJSON()

        /// <summary>
        /// The upload as the index file and the JSON API write it.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject(
                           new JProperty("id",               Id),
                           new JProperty("timestamp",        Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
                           new JProperty("protocol",         Protocol.ToString()),
                           new JProperty("uploader",         Uploader),
                           new JProperty("chargingStation",  ChargingStation),
                           new JProperty("fileName",         FileName),
                           new JProperty("storedAs",         StoredAs),
                           new JProperty("size",             Size),
                           new JProperty("sha256",           SHA256),
                           new JProperty("remoteAddress",    RemoteAddress)
                       );

            if (ContentType is not null)
                json.Add(new JProperty("contentType", ContentType));

            if (UserAgent is not null)
                json.Add(new JProperty("userAgent",   UserAgent));

            return json;

        }

        #endregion

        #region (static) TryParse(JSON, out Upload)

        /// <summary>
        /// One line of the index file read back.
        /// </summary>
        public static Boolean TryParse(JObject                                JSON,
                                       [NotNullWhen(true)] out UploadEvent?  Upload)
        {

            Upload = null;

            try
            {

                if (JSON["id"]?.Type != JTokenType.Integer ||
                    !DateTimeOffset.TryParse(JSON["timestamp"]?.Value<String>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ||
                    !Enum.TryParse<UploadProtocol>(JSON["protocol"]?.Value<String>(), true, out var protocol) ||
                    JSON["uploader"]       ?.Value<String>() is not String uploader        ||
                    JSON["chargingStation"]?.Value<String>() is not String chargingStation ||
                    JSON["fileName"]       ?.Value<String>() is not String fileName        ||
                    JSON["storedAs"]       ?.Value<String>() is not String storedAs        ||
                    JSON["size"]?.Type != JTokenType.Integer                               ||
                    JSON["sha256"]         ?.Value<String>() is not String sha256)
                {
                    return false;
                }

                Upload = new UploadEvent(
                             JSON["id"]!.Value<UInt64>(),
                             timestamp,
                             protocol,
                             uploader,
                             chargingStation,
                             fileName,
                             storedAs,
                             JSON["size"]!.Value<Int64>(),
                             sha256,
                             JSON["remoteAddress"]?.Value<String>() ?? "",
                             JSON["contentType"]  ?.Value<String>(),
                             JSON["userAgent"]    ?.Value<String>()
                         );

                return true;

            }
            catch (Exception)
            {
                return false;
            }

        }

        #endregion

    }

}
