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

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.LogServer.Tests
{

    /// <summary>
    /// What every node has to answer, asked of a log server - the conformance
    /// suite of WWCP_Node_TestKit - and the log server's own: uploads by HTTP
    /// PUT, the JSON API the web interface is drawn from, and who may do what.
    /// </summary>
    /// <remarks>
    /// HTTP uploads are taken on the loopback address only, on a port of their
    /// own the tests chose, so that the suite's nodes do not fight over 8363.
    /// </remarks>
    public class LogServerConformance : NodeConformanceTests
    {

        protected override WWCPNode NewNode(String   Directory,
                                            JObject  Configuration)
            => Make(Directory, Configuration, null);

        protected override WWCPNode? NewNodeWritingLogFiles(String   Directory,
                                                            JObject  Configuration,
                                                            String   LogPath)
            => Make(Directory, Configuration, LogPath);

        private static LogServerNode Make(String   Directory,
                                          JObject  Configuration,
                                          String?  LogPath)
        {

            if (Configuration[LogServerSettings.SectionName] is not JObject)
                Configuration[LogServerSettings.SectionName] = new JObject(
                                                                   new JProperty("httpListen", "127.0.0.1")
                                                               );

            var file = Path.Combine(Directory, WWCPConfigFile.DefaultFileName);
            File.WriteAllText(file, Configuration.ToString());

            var uploadPort = TestPorts.Free();

            return new LogServerNode(
                       HTTPPort:          TestPorts.Free(),
                       AccountsPath:      Path.Combine(Directory, "accounts"),
                       ConfigFile:        new WWCPConfigFile(file),
                       CertificatesPath:  Path.Combine(Directory, "certificates"),
                       LogToConsole:      false,
                       LogPath:           LogPath,
                       BridgeDebugLog:    false,
                       CommandLine:       settings => settings with { HTTPPort = uploadPort }
                   );

        }


        #region (private) LogServer / UploadClient(Login, Password)

        private LogServerNode LogServer
            => (LogServerNode) Node;

        /// <summary>
        /// A client of the upload port, signed in with HTTP Basic Auth where a
        /// login is given - as a charging station uploads.
        /// </summary>
        private HttpClient UploadClient(String?  Login     = null,
                                        String?  Password  = null)
        {

            var http = new HttpClient {
                           BaseAddress  = new Uri($"http://127.0.0.1:{LogServer.Settings.HTTPPort}/"),
                           Timeout      = TimeSpan.FromSeconds(20)
                       };

            if (Login is not null && Password is not null)
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                                                                                         Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Login}:{Password}")));

            return http;

        }

        #endregion


        #region AnUploaderUploadsByHTTPPut_AndTheUploadIsListedAndDownloaded()

        /// <summary>
        /// An uploader made over the JSON API puts a file for a charging
        /// station by HTTP: it is kept, listed with what is known of it,
        /// filtered by station and uploader, and downloaded as it was sent.
        /// </summary>
        [Test]
        public async Task AnUploaderUploadsByHTTPPut_AndTheUploadIsListedAndDownloaded()
        {

            using var http            = await SignedIn();

            var (made, uploader)      = await Send(http, HttpMethod.Post, "api/v1/logServer/uploaders",
                                                   new JObject(new JProperty("id",    "CS001"),
                                                               new JProperty("name",  "The charger in the yard")));

            Assert.That(made,                                              Is.EqualTo(HttpStatusCode.OK), uploader.ToString());

            var password              = uploader["password"]!.Value<String>()!;

            Assert.That(password,                                          Has.Length.GreaterThanOrEqualTo(20));
            Assert.That(uploader["uploaders"]![0]!["id"]!.Value<String>(), Is.EqualTo("CS001"));

            using var station         = UploadClient("CS001", password);

            var content               = Encoding.UTF8.GetBytes("2026-11-24T10:00:00Z boot\n2026-11-24T10:00:01Z ready\n");

            var first                 = await station.PutAsync("diagnostics.log", new ByteArrayContent(content));
            var second                = await station.PutAsync("CS002/security.log", new ByteArrayContent(content));

            Assert.That(first.StatusCode,                                  Is.EqualTo(HttpStatusCode.Created), await first. Content.ReadAsStringAsync());
            Assert.That(second.StatusCode,                                 Is.EqualTo(HttpStatusCode.Created), await second.Content.ReadAsStringAsync());

            var kept                  = JObject.Parse(await first.Content.ReadAsStringAsync());

            Assert.That(kept["uploader"]!.Value<String>(),                 Is.EqualTo("CS001"));
            Assert.That(kept["chargingStation"]!.Value<String>(),          Is.EqualTo("CS001"));
            Assert.That(kept["size"]!.Value<Int64>(),                      Is.EqualTo(content.Length));
            Assert.That(kept["sha256"]!.Value<String>(),                   Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(content))));

            var all                   = await GetJSON(http, "api/v1/logServer/uploads");

            Assert.That(all["total"]!.Value<Int32>(),                      Is.EqualTo(2));
            Assert.That(all["uploads"]![0]!["fileName"]!.Value<String>(),  Is.EqualTo("security.log"), "the newest first");
            Assert.That(all["chargingStations"]!.Values<String>(),         Is.EquivalentTo(new[] { "CS001", "CS002" }));

            var ofCS002               = await GetJSON(http, "api/v1/logServer/uploads?chargingStation=CS002");

            Assert.That(ofCS002["total"]!.Value<Int32>(),                  Is.EqualTo(1));
            Assert.That(ofCS002["uploads"]![0]!["uploader"]!.Value<String>(), Is.EqualTo("CS001"));

            var oldest                = await GetJSON(http, "api/v1/logServer/uploads?order=oldest&uploader=CS001");

            Assert.That(oldest["uploads"]![0]!["fileName"]!.Value<String>(), Is.EqualTo("diagnostics.log"));

            var file                  = await http.GetByteArrayAsync($"api/v1/logServer/uploads/{kept["id"]}/file");

            Assert.That(file,                                              Is.EqualTo(content));

        }

        #endregion

        #region AnUploadIsRefused_WithoutCredentials_AndToWhoIsNoUploader()

        [Test]
        public async Task AnUploadIsRefused_WithoutCredentials_AndToWhoIsNoUploader()
        {

            using var nobody          = UploadClient();

            var anonymous             = await nobody.PutAsync("CS001/diagnostics.log", new ByteArrayContent([ 1, 2, 3 ]));

            Assert.That(anonymous.StatusCode,                              Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(anonymous.Headers.WwwAuthenticate.ToString(),      Does.StartWith("Basic"));

            var password              = "An-Operator-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

            await AccountIn(User_Id.Parse("operator1"), LogServerAccess.Operator.Name, password);

            using var operatorClient  = UploadClient("operator1", password);

            var refused               = await operatorClient.PutAsync("CS001/diagnostics.log", new ByteArrayContent([ 1, 2, 3 ]));

            Assert.That(refused.StatusCode,                                Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(LogServer.Uploads.Count,                           Is.Zero);

        }

        #endregion

        #region AnOperatorMayReadTheUploadsButNotMakeUploaders()

        [Test]
        public async Task AnOperatorMayReadTheUploadsButNotMakeUploaders()
        {

            var password              = "An-Operator-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

            await AccountIn(User_Id.Parse("operator1"), LogServerAccess.Operator.Name, password);

            using var http            = await SignedInAs("operator1", password);

            var uploads               = await GetJSON(http, "api/v1/logServer/uploads");

            Assert.That(uploads["total"]!.Value<Int32>(),                  Is.Zero);

            var (status, answer)      = await Send(http, HttpMethod.Post, "api/v1/logServer/uploaders",
                                                   new JObject(new JProperty("id", "CS001")));

            Assert.That(status,                                            Is.EqualTo(HttpStatusCode.Forbidden), answer.ToString());
            Assert.That(LogServer.UploaderAccounts,                        Is.Empty);

        }

        #endregion

        #region AnUploaderGivenANewPasswordOrTakenAway_MayNoLongerUploadWithTheOldOne()

        [Test]
        public async Task AnUploaderGivenANewPasswordOrTakenAway_MayNoLongerUploadWithTheOldOne()
        {

            using var http            = await SignedIn();

            var (_, made)             = await Send(http, HttpMethod.Post, "api/v1/logServer/uploaders", new JObject(new JProperty("id", "CS001")));
            var first                 = made["password"]!.Value<String>()!;

            var (changed, renewed)    = await Send(http, HttpMethod.Post, "api/v1/logServer/uploaders/password", new JObject(new JProperty("id", "CS001")));

            Assert.That(changed,                                           Is.EqualTo(HttpStatusCode.OK), renewed.ToString());

            var second                = renewed["password"]!.Value<String>()!;

            using (var old = UploadClient("CS001", first))
                Assert.That((await old.PutAsync("a.log", new ByteArrayContent([ 1 ]))).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            using (var now = UploadClient("CS001", second))
                Assert.That((await now.PutAsync("b.log", new ByteArrayContent([ 1 ]))).StatusCode, Is.EqualTo(HttpStatusCode.Created));

            var (removed, after)      = await Send(http, HttpMethod.Post, "api/v1/logServer/uploaders/remove", new JObject(new JProperty("id", "CS001")));

            Assert.That(removed,                                           Is.EqualTo(HttpStatusCode.OK), after.ToString());
            Assert.That(after["uploaders"]!.Count(),                       Is.Zero);

            using (var gone = UploadClient("CS001", second))
                Assert.That((await gone.PutAsync("c.log", new ByteArrayContent([ 1 ]))).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            Assert.That(LogServer.Uploads.Count,                           Is.EqualTo(1), "what it uploaded is kept");

        }

        #endregion

    }

}
