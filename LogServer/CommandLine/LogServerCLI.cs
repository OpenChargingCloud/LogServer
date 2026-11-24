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

using System.Reflection;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;

#endregion

namespace cloud.charging.open.LogServer.CommandLine
{

    /// <summary>
    /// The command line of a running log server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Commands are not listed anywhere. The commands every node has - queryDNS
    /// among them, which asks the name servers this node uses itself - are the
    /// node's, and NodeCLI, which this derives from, finds them in WWCP_Node.
    /// The constructor registers this type as well, so that this assembly is
    /// walked for anything that implements ICLICommand and can be built from a
    /// LogServerCLI: a command of the log server's own is a new file and
    /// nothing else - 'logServer', 'uploads' and 'uploaders' among them.
    /// </para>
    /// <para>
    /// Here, beside the uploads, and not in the program that runs it: what
    /// a log server can be told is the log server's, and is told over SSH as
    /// well as at the console. The node hands it to whoever signs in over SSH
    /// by itself (see LogServerNode.cs), so that a log server run by another
    /// program - the tests, a test environment - has the same commands as one
    /// run by the program LogServerCLI.
    /// </para>
    /// </remarks>
    public class LogServerCLI : NodeCLI
    {

        #region Properties

        /// <summary>
        /// The log server these commands are about.
        /// </summary>
        public LogServerNode  LogServer  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the command line of the given log server, at the console.
        /// </summary>
        public LogServerCLI(LogServerNode      LogServer,
                            params Assembly[]  AssembliesWithCLICommands)

            : base(LogServer, AssembliesWithCLICommands)

        {

            this.LogServer = LogServer;

            RegisterCLIType(typeof(LogServerCLI));

        }

        /// <summary>
        /// Create the command line of the given log server on the given terminal,
        /// for the given caller - a session over SSH.
        /// </summary>
        public LogServerCLI(LogServerNode      LogServer,
                            ICLITerminal       Terminal,
                            CLICaller          Caller,
                            params Assembly[]  AssembliesWithCLICommands)

            : base(LogServer, Terminal, Caller, AssembliesWithCLICommands)

        {

            this.LogServer = LogServer;

            RegisterCLIType(typeof(LogServerCLI));

        }

        #endregion


        #region (protected override) GetPrompt()

        /// <summary>
        /// Which port this log server answers its web interface on, because a
        /// machine that is one of several on a bench should say which one it is
        /// before it asks for a command.
        /// </summary>
        protected override String GetPrompt()

            => $"logs:{LogServer.HTTPPort}> ";

        #endregion

    }

}
