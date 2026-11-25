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
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.LogServer.Tests
{

    /// <summary>
    /// The upload port as a port of its own: a body written to the disk as it
    /// arrives and never held in memory, a certificate of its own and not the
    /// web interface's, and the URL charging stations are told.
    /// </summary>
    public class UploadPortTests
    {

        #region Data

        private String          directory  = "";
        private LogServerNode?  node;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void Setup()
        {
            directory = Path.Combine(Path.GetTempPath(), "logserver-port-" + Guid.NewGuid().ToString("N")[..12]);
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

        #endregion


        #region (private) Started(LogServerSection) / Uploader(Id)

        /// <summary>
        /// A log server started with the given "logServer" section - on the
        /// loopback address, its upload port one of the tests' - and no NTS and
        /// SSH, which these tests do not ask.
        /// </summary>
        private async Task<LogServerNode> Started(JObject? LogServerSection = null)
        {

            var section = LogServerSection ?? new JObject();
            section["httpListen"] = "127.0.0.1";

            var configuration = new JObject(
                                    new JProperty("nts",                          JObject.Parse("""{ "groups": { "legal": { "enabled": false } } }""")),
                                    new JProperty("ssh",                          JObject.Parse("""{ "enabled": false }""")),
                                    new JProperty(LogServerSettings.SectionName,  section)
                                );

            File.WriteAllText(Path.Combine(directory, WWCPConfigFile.DefaultFileName), configuration.ToString());

            var uploadPort = TestPorts.Free();

            node = new LogServerNode(HTTPPort:          TestPorts.Free(),
                                     AccountsPath:      Path.Combine(directory, "accounts"),
                                     ConfigFile:        new WWCPConfigFile(Path.Combine(directory, WWCPConfigFile.DefaultFileName)),
                                     CertificatesPath:  Path.Combine(directory, "certificates"),
                                     LogToConsole:      false,
                                     BridgeDebugLog:    false,
                                     CommandLine:       settings => settings with { HTTPPort = uploadPort });

            await node.Start();

            return node;

        }

        /// <summary>
        /// A client of the upload port signed in as a new uploader.
        /// </summary>
        private static async Task<HttpClient> Uploader(LogServerNode  Node,
                                                       String         Id,
                                                       String         Scheme       = "http",
                                                       Action<X509Certificate?>?  Shown  = null)
        {

            var made = await Node.AddUploader(Id, null, "test");

            Assert.That(made.Outcome, Is.EqualTo(UploaderChangeOutcome.Changed), made.Said);

            var handler = new SocketsHttpHandler();

            if (Scheme == "https")
                handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => {
                    Shown?.Invoke(certificate);
                    return true;
                };

            var http = new HttpClient(handler) {
                           BaseAddress  = new Uri($"{Scheme}://127.0.0.1:{Node.Settings.HTTPPort}/"),
                           Timeout      = TimeSpan.FromSeconds(60)
                       };

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                                                                                     Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Id}:{made.Password}")));

            return http;

        }

        #endregion


        #region ALargeUpload_IsWrittenToTheDiskAsItArrives_AndNeverHeldInMemory()

        /// <summary>
        /// 64 MiB put by HTTP - with a Content-Length, and in chunks without one -
        /// are kept whole, and the log server took far less memory for them than
        /// they are large: a body held in memory would have taken all of it.
        /// </summary>
        [Test]
        public async Task ALargeUpload_IsWrittenToTheDiskAsItArrives_AndNeverHeldInMemory()
        {

            const Int64 size = 64L * 1024 * 1024;

            var logServer  = await Started();
            using var http = await Uploader(logServer, "CS001");

            foreach (var chunked in new[] { false, true })
            {

                var body       = new PatternStream(size);
                var content    = new StreamContent(body, 64 * 1024);

                if (!chunked)
                    content.Headers.ContentLength = size;

                GC.Collect();

                var before     = GC.GetTotalAllocatedBytes(precise: true);
                var response   = await http.PutAsync($"CS001/large-{(chunked ? "chunked" : "length")}.bin", content);
                var allocated  = GC.GetTotalAllocatedBytes(precise: true) - before;

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created), await response.Content.ReadAsStringAsync());

                var kept = JObject.Parse(await response.Content.ReadAsStringAsync());

                Assert.Multiple(() => {
                    Assert.That(kept["size"]!.Value<Int64>(),     Is.EqualTo(size));
                    Assert.That(kept["sha256"]!.Value<String>(),  Is.EqualTo(body.SHA256));
                    Assert.That(allocated,                        Is.LessThan(size / 2),
                                $"{allocated / 1024 / 1024} MiB were allocated for an upload of {size / 1024 / 1024} MiB{(chunked ? " in chunks" : "")}.");
                });

            }

            Assert.That(Directory.GetFiles(Path.Combine(logServer.Uploads.Directory, LogServerNode.HTTPIncoming)), Is.Empty,
                        "nothing is left where uploads are written to as they arrive");

        }

        #endregion

        #region AnUploadInChunks_LargerThanItMayBe_IsRefused_AndLeavesNothing()

        [Test]
        public async Task AnUploadInChunks_LargerThanItMayBe_IsRefused_AndLeavesNothing()
        {

            var logServer  = await Started(new JObject(new JProperty("maxFileSize", 1024 * 1024)));
            using var http = await Uploader(logServer, "CS001");

            HttpStatusCode? status = null;

            try
            {
                status = (await http.PutAsync("CS001/too-large.bin", new StreamContent(new PatternStream(3 * 1024 * 1024)))).StatusCode;
            }
            catch (HttpRequestException)
            {
                // The server answers 413 and closes the connection while the
                // client is still sending chunks: the client may never read it.
            }

            await RefusedOnce(logServer);

            Assert.Multiple(() => {
                Assert.That(status is null or HttpStatusCode.RequestEntityTooLarge, Is.True, $"answered {status}");
                Assert.That(logServer.Uploads.Count,              Is.Zero);
                Assert.That(Directory.GetFiles(Path.Combine(logServer.Uploads.Directory, LogServerNode.HTTPIncoming)), Is.Empty);
            });

        }

        /// <summary>
        /// Wait until the log server counted the upload as refused - after the
        /// client gave up, its handler may still be taking away what it wrote.
        /// </summary>
        private static async Task RefusedOnce(LogServerNode Node)
        {

            for (var waited = 0; Node.HTTPUploadsRefused == 0 && waited < 100; waited++)
                await Task.Delay(50);

            Assert.That(Node.HTTPUploadsRefused, Is.EqualTo(1), "the log server refused the upload itself");

        }

        #endregion

        #region AnUploadThatTakesLongerThanItMay_IsRefused_AndLeavesNothing()

        /// <summary>
        /// The time an upload may take is the section's: one second here, and a
        /// body that stops for three is refused - and nothing of it is kept.
        /// </summary>
        [Test]
        public async Task AnUploadThatTakesLongerThanItMay_IsRefused_AndLeavesNothing()
        {

            var logServer  = await Started(new JObject(new JProperty("httpUploadTimeout", 1)));
            using var http = await Uploader(logServer, "CS001");

            var content    = new StreamContent(new PatternStream(256 * 1024, PauseAfter: 64 * 1024, Pause: TimeSpan.FromSeconds(3)));
            content.Headers.ContentLength = 256 * 1024;

            HttpStatusCode? status = null;

            try
            {
                status = (await http.PutAsync("CS001/slow.bin", content)).StatusCode;
            }
            catch (HttpRequestException)
            {
                // A server that has answered may close the connection before the
                // client has written all it wanted to: refused as well.
            }

            await RefusedOnce(logServer);

            Assert.Multiple(() => {
                Assert.That(status is null or HttpStatusCode.RequestTimeout, Is.True, $"answered {status}");
                Assert.That(logServer.Uploads.Count, Is.Zero);
                Assert.That(Directory.Exists(Path.Combine(logServer.Uploads.Directory, LogServerNode.HTTPIncoming))
                                ? Directory.GetFiles(Path.Combine(logServer.Uploads.Directory, LogServerNode.HTTPIncoming))
                                : [],
                            Is.Empty);
            });

        }

        #endregion

        #region TheUploadPortShowsACertificateOfItsOwn_WhileTheWebInterfaceSpeaksPlainHTTP()

        /// <summary>
        /// "tls" of the section, with a pair of PEM files: the upload port shows
        /// that certificate, an upload over HTTPS is kept, the URL charging
        /// stations are told says "https" - and the web interface, which was
        /// given no certificate, still speaks plain HTTP.
        /// </summary>
        [Test]
        public async Task TheUploadPortShowsACertificateOfItsOwn_WhileTheWebInterfaceSpeaksPlainHTTP()
        {

            using var key          = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request            = new CertificateRequest("CN=uploads.test", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));
            var subjectNames       = new SubjectAlternativeNameBuilder();
            subjectNames.AddDnsName("uploads.test");
            subjectNames.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(subjectNames.Build());

            using var certificate  = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

            File.WriteAllText(Path.Combine(directory, "upload.pem"),
                              certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n");

            var logServer          = await Started(new JObject(
                                                       new JProperty("tls", new JObject(
                                                           new JProperty("certificate", new JObject(
                                                               new JProperty("fullchain", "upload.pem")
                                                           ))
                                                       ))
                                                   ));

            X509Certificate? shown = null;

            using var http         = await Uploader(logServer, "CS001", "https", certificate => shown = certificate);

            var response           = await http.PutAsync("CS001/diagnostics.log", new ByteArrayContent(Encoding.UTF8.GetBytes("over TLS\n")));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created), await response.Content.ReadAsStringAsync());

            var status             = logServer.StatusJSON()["http"]!;

            Assert.Multiple(() => {
                Assert.That(shown?.GetCertHashString(),                    Is.EqualTo(certificate.GetCertHashString()));
                Assert.That(logServer.UploadsOverHTTPS,                    Is.True);
                Assert.That(logServer.UploadURL.Scheme,                    Is.EqualTo("https"));
                Assert.That(status["tls"]!.Value<Boolean>(),               Is.True);
                Assert.That(status["certificate"]!["shows"]!.Value<String>(), Is.EqualTo("CN=uploads.test"));
                Assert.That(logServer.WebCertificate,                      Is.Null, "the web interface was given no certificate");
            });

            using var web          = new HttpClient();
            var page               = await web.GetAsync($"http://127.0.0.1:{logServer.HTTPPort}/");

            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the web interface speaks plain HTTP still");

        }

        #endregion

        #region TheURLChargingStationsAreTold_IsTheSections_OrGuessed()

        [Test]
        public async Task TheURLChargingStationsAreTold_IsTheSections_OrGuessed()
        {

            var guessed = await Started();

            Assert.Multiple(() => {
                Assert.That(guessed.UploadURL.ToString(),                       Is.EqualTo($"http://127.0.0.1:{guessed.Settings.HTTPPort}/"));
                Assert.That(guessed.StatusJSON()["http"]!["publicURL"]!.Type,   Is.EqualTo(JTokenType.Null));
            });

            await guessed.DisposeAsync();
            node = null;

            var told = await Started(new JObject(new JProperty("publicURL", "https://logs.example.org/charging")));

            Assert.Multiple(() => {
                Assert.That(told.UploadURL.ToString(),                          Is.EqualTo("https://logs.example.org/charging/"));
                Assert.That(told.StatusJSON()["http"]!["url"]!.Value<String>(), Is.EqualTo("https://logs.example.org/charging/"));
                Assert.That(told.ListenersText(),                               Does.Contain("https://logs.example.org/charging/<charging station>/<file name>"));
                Assert.That(told.ListenersText(),                               Does.Contain($"listening on http://127.0.0.1:{told.Settings.HTTPPort}/"));
                Assert.That(told.Log.Recent(200).Select(entry => entry.Message), Has.None.Contains("plain HTTP"),
                            "a proxy that speaks TLS is in front where the URL says \"https\"");
            });

        }

        #endregion

        #region TheNewKeysOfTheSection_AreReadAndWhatIsWrongInThemIsSaid()

        [Test]
        public void TheNewKeysOfTheSection_AreReadAndWhatIsWrongInThemIsSaid()
        {

            Assert.That(LogServerSettings.TryParse(null, out var defaults, out _), Is.True);

            Assert.Multiple(() => {
                Assert.That(defaults!.TLS,                Is.Null);
                Assert.That(defaults.PublicURL,           Is.Null);
                Assert.That(defaults.HTTPUploadTimeout,   Is.EqualTo(TimeSpan.FromHours(1)));
            });

            Assert.That(LogServerSettings.TryParse(JObject.Parse("""{ "tls": { "certificate": "store" }, "publicURL": "http://logs.example.org:8363", "httpUploadTimeout": 600 }"""),
                                                   out var given, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(given!.TLS?.Certificate?.IsStore,    Is.True);
                Assert.That(given.PublicURL?.ToString(),         Is.EqualTo("http://logs.example.org:8363/"));
                Assert.That(given.HTTPUploadTimeout,             Is.EqualTo(TimeSpan.FromMinutes(10)));
                Assert.That(given.ToJSON()["tls"]!["certificate"]!.Value<String>(), Is.EqualTo("store"));
            });

            Assert.That(LogServerSettings.TryParse(JObject.Parse("""{ "tls": { "certificate": "store", "redirectPort": 80 } }"""), out _, out error), Is.False);
            Assert.That(error, Does.Contain("redirectPort"));

            Assert.That(LogServerSettings.TryParse(JObject.Parse("""{ "tls": { "certificate": "elsewhere" } }"""),                 out _, out error), Is.False);
            Assert.That(error, Does.Contain("logServer.tls.certificate"));

            Assert.That(LogServerSettings.TryParse(JObject.Parse("""{ "publicURL": "ftp://logs.example.org/" }"""),                 out _, out error), Is.False);
            Assert.That(error, Does.Contain("publicURL"));

            Assert.That(LogServerSettings.TryParse(JObject.Parse("""{ "publicURL": "https://CS001:secret@logs.example.org/" }"""),  out _, out error), Is.False);
            Assert.That(error, Does.Contain("credentials"));

            Assert.That(LogServerSettings.TryParse(JObject.Parse("""{ "httpUploadTimeout": 0 }"""),                                  out _, out error), Is.False);
            Assert.That(error, Does.Contain("httpUploadTimeout"));

        }

        #endregion


        #region (private class) PatternStream

        /// <summary>
        /// A body of the given length, made up as it is read and never held
        /// whole - so that what the upload costs in memory is the server's -
        /// with its SHA-256 at hand once it was read to its end. Where asked, it
        /// stops for a while after so many bytes.
        /// </summary>
        private sealed class PatternStream(Int64      TotalLength,
                                           Int64      PauseAfter  = -1,
                                           TimeSpan?  Pause       = null) : Stream
        {

            private readonly IncrementalHash  hash      = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            private          Int64            position;
            private          Boolean          paused;
            private          String?          sha256;

            public String SHA256
                => sha256 ??= Convert.ToHexStringLower(hash.GetHashAndReset());

            public override Boolean CanRead   => true;
            public override Boolean CanSeek   => false;
            public override Boolean CanWrite  => false;
            public override Int64   Length    => throw new NotSupportedException();
            public override Int64   Position  { get => position; set => throw new NotSupportedException(); }

            public override Int32 Read(Byte[] Buffer, Int32 Offset, Int32 Count)
                => Read(Buffer.AsSpan(Offset, Count));

            public override Int32 Read(Span<Byte> Buffer)
            {

                var count = (Int32) Math.Min(Buffer.Length, TotalLength - position);

                for (var i = 0; i < count; i++)
                    Buffer[i] = (Byte) ((position + i) * 31 % 251);

                hash.AppendData(Buffer[..count]);
                position += count;

                return count;

            }

            public override async ValueTask<Int32> ReadAsync(Memory<Byte> Buffer, CancellationToken CancellationToken = default)
            {

                if (PauseAfter >= 0 && !paused && position >= PauseAfter)
                {
                    paused = true;
                    await Task.Delay(Pause ?? TimeSpan.Zero, CancellationToken);
                }

                var limit = PauseAfter >= 0 && !paused
                                ? (Int32) Math.Min(Buffer.Length, PauseAfter - position)
                                : Buffer.Length;

                return Read(Buffer.Span[..Math.Max(limit, 0)]);

            }

            public override Task<Int32> ReadAsync(Byte[] Buffer, Int32 Offset, Int32 Count, CancellationToken CancellationToken)
                => ReadAsync(Buffer.AsMemory(Offset, Count), CancellationToken).AsTask();

            public override void Flush() { }
            public override Int64 Seek(Int64 Offset, SeekOrigin Origin) => throw new NotSupportedException();
            public override void SetLength(Int64 Value) => throw new NotSupportedException();
            public override void Write(Byte[] Buffer, Int32 Offset, Int32 Count) => throw new NotSupportedException();

            // The hash outlives the stream: the client may close the body before
            // the test asks what it was.

        }

        #endregion

    }

}
