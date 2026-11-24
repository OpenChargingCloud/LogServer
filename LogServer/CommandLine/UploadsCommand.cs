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

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.LogServer.CommandLine
{

    /// <summary>
    /// The newest uploads - of every charging station, or of one - and an
    /// upload taken away.
    /// </summary>
    /// <param name="CLI">The command line of the log server to ask.</param>
    public class UploadsCommand(LogServerCLI CLI) : ACLICommand<LogServerCLI>(CLI),
                                                    ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public static readonly String CommandName = "uploads";

        /// <summary>
        /// How many uploads are listed.
        /// </summary>
        public const Int32 Listed = 20;

        private static readonly String[] Remove = [ "remove", "delete" ];

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            // The charging stations there are, and "remove".
            if (Arguments.Length == 2)
                return cli.LogServer.Uploads.ChargingStations.
                           Select(one => one.Name).
                           Append("remove").
                           Where (name => name.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                           Select(name => SuggestionResponse.ParameterCompleted($"{CommandName} {name}"));

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            if (Arguments.Length >= 2 && Remove.Any(word => word.Equals(Arguments[1], StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(Removed(Arguments));

            if (Arguments.Length > 2)
                return Task.FromResult<String[]>([ Help() ]);

            if (!cli.MayDo(Permission.Read(LogServerAccess.Uploads), CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            var station  = Arguments.Length == 2 ? Arguments[1] : null;
            var uploads  = cli.LogServer.Uploads.Query(new UploadQuery(ChargingStation: station, Take: Listed), out var total);

            if (total == 0)
                return Task.FromResult<String[]>([ station is not null
                                                       ? $"Nothing was uploaded of '{station}'."
                                                       : "Nothing was uploaded yet." ]);

            return Task.FromResult<String[]>([
                       total > uploads.Count
                           ? $"The newest {uploads.Count} of {total} upload(s){(station is not null ? $" of '{station}'" : "")}:"
                           : $"{total} upload(s){(station is not null ? $" of '{station}'" : "")}, the newest first:",
                       .. uploads.Select(Line)
                   ]);

        }

        #endregion

        #region (private) Removed(Arguments)

        private String[] Removed(String[] Arguments)
        {

            if (Arguments.Length != 3 || !UInt64.TryParse(Arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                return [ $"{CommandName} remove <number> - e.g. '{CommandName} remove 17'" ];

            if (!cli.MayDo(Permission.Edit(LogServerAccess.Uploads), CommandName, out var refused))
                return [ refused ];

            if (!cli.LogServer.Uploads.Remove(id, out var removed, out var error) || removed is null)
                return [ $"There is no upload {id}." ];

            if (error is not null)
                return [ $"Nothing was removed, since it could not be saved: {error}" ];

            cli.LogServer.Log.Notice($"'{cli.Caller}' removed upload {removed.Id}: '{removed.FileName}' of '{removed.ChargingStation}'.", "logserver", "uploads");

            return [ $"Upload {removed.Id} - '{removed.FileName}' of '{removed.ChargingStation}' - was removed, and its file with it." ];

        }

        #endregion


        #region (static) Line(Upload)

        /// <summary>
        /// One upload as the command line lists it.
        /// </summary>
        public static String Line(UploadEvent Upload)

            => $"    {Upload.Id,6}  {Upload.Timestamp.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z  {Upload.Protocol,-4}  " +
               $"{Upload.ChargingStation,-20} {Upload.FileName,-32} {LogServerNode.SizeOf(Upload.Size),10}  by {Upload.Uploader} from {Upload.RemoteAddress}";

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [<charging station> | remove <number>] - the newest {Listed} uploads, of every charging station or of one; " +
                "or one taken away";

        #endregion

    }

}
