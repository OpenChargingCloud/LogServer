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

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.LogServer.Tests
{

    /// <summary>
    /// What an upload over SFTP leaves under "&lt;name&gt;.part" and never
    /// renames is never taken in: the log server takes it away rather than
    /// keep it for ever.
    /// </summary>
    public class UnfinishedSftpUploadTests
    {

        private String          directory  = "";
        private LogServerNode?  node;

        [SetUp]
        public void Setup()
        {
            directory = Path.Combine(Path.GetTempPath(), "logserver-parts-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public async Task TearDown()
        {

            if (node is not null)
                await node.DisposeAsync();

            node = null;

            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            { }

        }

        /// <summary>
        /// Nobody uploads before the log server is started: at its start every
        /// file under "&lt;name&gt;.part" is taken away, and nothing else.
        /// </summary>
        [Test]
        public void WhatAnUnfinishedUploadLeft_IsTakenAwayAtTheStart()
        {

            var incoming = Path.Combine(directory, LogServerSettings.DefaultDirectory, LogServerNode.SftpIncoming);

            Directory.CreateDirectory(incoming);
            File.WriteAllBytes(Path.Combine(incoming, "CS001-diagnostics.log.part"), [ 1, 2, 3 ]);
            File.WriteAllBytes(Path.Combine(incoming, "kept.txt"),                   [ 4 ]);

            File.WriteAllText(Path.Combine(directory, WWCPConfigFile.DefaultFileName),
                              """{ "nts": { "groups": { "legal": { "enabled": false } } }, "ssh": { "enabled": false }, "logServer": { "httpListen": "127.0.0.1" } }""");

            node = new LogServerNode(HTTPPort:          TestPorts.Free(),
                                     AccountsPath:      Path.Combine(directory, "accounts"),
                                     ConfigFile:        new WWCPConfigFile(Path.Combine(directory, WWCPConfigFile.DefaultFileName)),
                                     CertificatesPath:  Path.Combine(directory, "certificates"),
                                     LogToConsole:      false,
                                     BridgeDebugLog:    false,
                                     CommandLine:       settings => settings with { HTTPPort = TestPorts.Free() });

            Assert.Multiple(() => {
                Assert.That(Directory.GetFiles(incoming).Select(Path.GetFileName), Is.EqualTo(new[] { "kept.txt" }));
                Assert.That(node.Log.Recent(100).Select(entry => entry.Message),    Has.Some.Contains("CS001-diagnostics.log.part' was taken away"));
            });

        }

    }

}
