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

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.LogServer.CommandLine
{

    /// <summary>
    /// How the log server stands: where it takes uploads, how many it keeps,
    /// and the newest of them.
    /// </summary>
    /// <param name="CLI">The command line of the log server to ask.</param>
    public class LogServerCommand(LogServerCLI CLI) : ACLICommand<LogServerCLI>(CLI),
                                                      ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public static readonly String CommandName = "logServer";

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)

            => Arguments.Length == 1 && CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                   ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                   : [];

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            if (Arguments.Length > 1)
                return Task.FromResult<String[]>([ Help() ]);

            if (!cli.MayDo(Permission.Read(LogServerAccess.Uploads), CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            var node    = cli.LogServer;
            var latest  = node.Uploads.Latest;

            String[] lines = [
                $"uploads taken   {node.ListenersText()}",
                $"kept in         {node.Uploads.Directory}",
                $"largest file    {LogServerNode.SizeOf(node.Settings.MaxFileSize)}",
                $"uploads         {node.Uploads.Count} ({LogServerNode.SizeOf(node.Uploads.TotalSize)}) of " +
                                  $"{node.Uploads.ChargingStations.Count} charging station(s) by {node.Uploads.Uploaders.Count} uploader(s)" +
                                  (node.Uploads.Count > 0 ? $"; see '{UploadsCommand.CommandName}'" : ""),
                $"newest          {(latest is not null ? UploadsCommand.Line(latest).Trim() : "none yet")}",
                $"uploaders       {node.UploaderAccounts.Count}" +
                                  (node.UploaderAccounts.Count == 0 ? $" - '{UploadersCommand.CommandName} add <name>' makes one" : $"; see '{UploadersCommand.CommandName}'"),
                $"refused         {node.HTTPUploadsRefused} HTTP upload(s) since the start"
            ];

            return Task.FromResult(lines);

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} - how the log server stands: where it takes uploads, how many it keeps, the newest";

        #endregion

    }

}
