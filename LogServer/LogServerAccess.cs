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

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.LogServer
{

    /// <summary>
    /// What a log server adds to the resources every node has, and the roles
    /// of the people - and charging stations - around it.
    /// </summary>
    /// <remarks>
    /// The node brings the viewer, who may look at everything, and the
    /// administrators, who may do everything. The configuration file may add
    /// roles and say differently what one of them may do - see the node's
    /// "roles" section.
    /// </remarks>
    public static class LogServerAccess
    {

        #region Resources

        /// <summary>
        /// The uploads: read, the list of them, their metadata and their files;
        /// edited, an upload removed.
        /// </summary>
        public const String  Uploads    = "uploads";

        /// <summary>
        /// The accounts files are uploaded under: read, who they are; edited,
        /// one made, given a new password or no longer let upload.
        /// </summary>
        public const String  Uploaders  = "uploaders";

        /// <summary>
        /// Uploading itself: run, a file put by HTTP or over SFTP.
        /// </summary>
        public const String  Upload     = "upload";

        /// <summary>
        /// The resources of a log server's own.
        /// </summary>
        public static readonly IReadOnlyList<String>  Resources = [ Uploads, Uploaders, Upload ];

        #endregion

        #region Roles

        /// <summary>
        /// A charging station, or a back end uploading in the name of several:
        /// may put files, and nothing else.
        /// </summary>
        public static readonly Role  Uploader  = new ("uploader",
                                                      [ Permission.Run(Upload) ],
                                                      "uploads log files - a charging station, or a back end in the name of several: puts files, and sees nothing");

        /// <summary>
        /// Whoever reads what the charging stations sent: may look at
        /// everything, download the files, remove uploads, and ask whether the
        /// network works - but may not make or change uploaders.
        /// </summary>
        public static readonly Role  Operator  = new ("operator",
                                                      [ Permission.Read(Permission.AnyResource),
                                                        Permission.Edit(Uploads),
                                                        Permission.Run (NodeResources.DNS),
                                                        Permission.Run (NodeResources.NTS) ],
                                                      "reads the uploads of the log server: looks at everything, downloads and removes uploads, and asks whether the network works");

        /// <summary>
        /// All of them, in the order a sentence naming them reads best.
        /// </summary>
        public static readonly IReadOnlyList<Role>  Roles = [ Operator, Uploader ];

        #endregion

    }

}
