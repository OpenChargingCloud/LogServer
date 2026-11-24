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

using Newtonsoft.Json.Linq;

#endregion

namespace cloud.charging.open.LogServer.Tests
{

    /// <summary>
    /// The store of the uploads on its own: what is kept where, the index read
    /// back as it was written, filtered and ordered, and taken away.
    /// </summary>
    [TestFixture]
    public class UploadStoreTests
    {

        #region Data

        private String directory = "";

        /// <summary>
        /// A clock that moves on by the given step at every reading.
        /// </summary>
        private sealed class SteppingClock(DateTimeOffset Start, TimeSpan Step) : TimeProvider
        {
            private DateTimeOffset now = Start;
            public override DateTimeOffset GetUtcNow()
            {
                var then = now;
                now += Step;
                return then;
            }
        }

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "LogServerTests-" + Guid.NewGuid().ToString("N")[..12]);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(directory, true); } catch { }
        }

        private UploadStore NewStore(TimeProvider? Clock = null)
        {
            var store = new UploadStore(directory, Clock ?? new SteppingClock(new DateTimeOffset(2026, 11, 24, 10, 0, 0, 123, TimeSpan.Zero), TimeSpan.FromSeconds(1)));
            store.Load();
            return store;
        }

        #endregion


        [Test]
        public void AFileIsKeptInTheDirectoryOfItsStation_UnderANameOfItsOwn()
        {

            var store   = NewStore();

            var one     = store.KeepBytes("a"u8.ToArray(), UploadProtocol.HTTP, "CS001", "CS001", "diag.log", "192.0.2.1:1");
            var two     = store.KeepBytes("b"u8.ToArray(), UploadProtocol.HTTP, "CS001", "CS001", "diag.log", "192.0.2.1:2");

            Assert.That(one.Id,                              Is.EqualTo(1));
            Assert.That(two.Id,                              Is.EqualTo(2));
            Assert.That(one.StoredAs,                        Is.Not.EqualTo(two.StoredAs), "the same name twice is two files");
            Assert.That(one.StoredAs,                        Does.StartWith("CS001/"));
            Assert.That(File.ReadAllText(store.FileOf(one)), Is.EqualTo("a"));
            Assert.That(File.ReadAllText(store.FileOf(two)), Is.EqualTo("b"));

        }

        [Test]
        public void WhatANameCannotBeOnTheDiskIsWrittenAsUnderscores()
        {

            var store   = NewStore();

            var upload  = store.KeepBytes([ 1 ], UploadProtocol.HTTP, "x", "../../etc", "..\\passwd:stream", "192.0.2.1:1");

            Assert.That(upload.StoredAs,                     Does.StartWith("_.._etc/"));
            Assert.That(upload.StoredAs,                     Does.EndWith("-_passwd_stream"));
            Assert.That(store.FileOf(upload),                Does.StartWith(store.Directory + Path.DirectorySeparatorChar));
            Assert.That(upload.FileName,                     Is.EqualTo("..\\passwd:stream"), "what it was called is kept as it was");

        }

        [Test]
        public void TheIndexIsReadBackAsItWasWritten_TimesToTheMillisecond()
        {

            var store   = NewStore();

            var kept    = store.KeepBytes([ 1, 2, 3 ], UploadProtocol.SFTP, "CS001", "CS001", "diag.log", "192.0.2.1:22");

            var again   = NewStore();

            Assert.That(again.Count,                         Is.EqualTo(1));
            Assert.That(again.All[0],                        Is.EqualTo(kept));
            Assert.That(again.All[0].Timestamp.Millisecond,  Is.EqualTo(123));

            var next    = again.KeepBytes([ 4 ], UploadProtocol.HTTP, "CS001", "CS001", "next.log", "192.0.2.1:1");

            Assert.That(next.Id,                             Is.EqualTo(2), "numbers go on where they left off");

        }

        [Test]
        public void AnIndexWithAByteOrderMarkAndALineThatIsNone_IsStillRead()
        {

            var store   = NewStore();

            store.KeepBytes([ 1 ], UploadProtocol.HTTP, "CS001", "CS001", "diag.log", "192.0.2.1:1");

            var lines   = File.ReadAllText(store.IndexFile);

            File.WriteAllText(store.IndexFile, lines + "this is no upload\n", new UTF8Encoding(true));

            var warned  = new List<String>();
            var again   = new UploadStore(directory, Warn: warned.Add);

            Assert.That(again.Load(),                        Is.EqualTo(1));
            Assert.That(warned,                              Has.Count.EqualTo(1));

        }

        [Test]
        public void UploadsAreFilteredByUploaderAndStation_AndOrderedByTime()
        {

            var store   = NewStore();

            store.KeepBytes([ 1 ], UploadProtocol.HTTP, "CS001",    "CS001", "1.log", "a");
            store.KeepBytes([ 2 ], UploadProtocol.HTTP, "backend1", "CS002", "2.log", "b");
            store.KeepBytes([ 3 ], UploadProtocol.SFTP, "CS001",    "CS001", "3.log", "c");
            store.KeepBytes([ 4 ], UploadProtocol.HTTP, "backend1", "CS001", "4.log", "d");

            Assert.That(store.Query(new UploadQuery(), out var total).Select(upload => upload.FileName),
                        Is.EqualTo(new[] { "4.log", "3.log", "2.log", "1.log" }));
            Assert.That(total, Is.EqualTo(4));

            Assert.That(store.Query(new UploadQuery(Newest: false), out _).Select(upload => upload.FileName),
                        Is.EqualTo(new[] { "1.log", "2.log", "3.log", "4.log" }));

            Assert.That(store.Query(new UploadQuery(ChargingStation: "cs001"), out total).Select(upload => upload.FileName),
                        Is.EqualTo(new[] { "4.log", "3.log", "1.log" }));
            Assert.That(total, Is.EqualTo(3));

            Assert.That(store.Query(new UploadQuery(Uploader: "backend1", ChargingStation: "CS001"), out _).Select(upload => upload.FileName),
                        Is.EqualTo(new[] { "4.log" }));

            Assert.That(store.Query(new UploadQuery(Skip: 1, Take: 2), out total).Select(upload => upload.FileName),
                        Is.EqualTo(new[] { "3.log", "2.log" }));
            Assert.That(total, Is.EqualTo(4));

            Assert.That(store.ChargingStations.Select(one => (one.Name, one.Uploads)),
                        Is.EqualTo(new[] { ("CS001", 3), ("CS002", 1) }));

        }

        [Test]
        public void AnUploadRemoved_IsGoneFromTheIndexAndTheDisk()
        {

            var store   = NewStore();

            var one     = store.KeepBytes([ 1 ], UploadProtocol.HTTP, "CS001", "CS001", "1.log", "a");
            var two     = store.KeepBytes([ 2 ], UploadProtocol.HTTP, "CS001", "CS002", "2.log", "b");

            Assert.That(store.Remove(two.Id, out var removed, out var error), Is.True);
            Assert.That(error,                               Is.Null);
            Assert.That(removed,                             Is.EqualTo(two));
            Assert.That(File.Exists(store.FileOf(two)),      Is.False);
            Assert.That(Directory.Exists(Path.Combine(store.Directory, "CS002")), Is.False, "the station's directory went with its last file");

            Assert.That(store.Remove(two.Id, out _, out _),  Is.False);

            var again   = NewStore();

            Assert.That(again.All,                           Is.EqualTo(new[] { one }));

        }

        [Test]
        public void TheSectionIsReadWithItsDefaults_AndWhatIsWrongInItIsSaid()
        {

            Assert.That(LogServerSettings.TryParse(null, out var defaults, out _), Is.True);
            Assert.That(defaults!.HTTP,                      Is.True);
            Assert.That(defaults .SFTP,                      Is.True);
            Assert.That(defaults .AnonymousHTTP,             Is.False);
            Assert.That(defaults .HTTPPort.ToUInt16(),       Is.EqualTo(8363));
            Assert.That(defaults .Directory,                 Is.EqualTo("uploads"));

            Assert.That(LogServerSettings.TryParse(JObject.Parse("""{ "httpPort": 9000, "httpListen": "127.0.0.1", "maxFileSize": 1024, "sftp": false }"""),
                                                   out var given, out _), Is.True);
            Assert.That(given!.HTTPPort.ToUInt16(),          Is.EqualTo(9000));
            Assert.That(LogServerSettings.TextOf(given.HTTPListen), Is.EqualTo("127.0.0.1"));
            Assert.That(given .MaxFileSize,                  Is.EqualTo(1024));
            Assert.That(given .SFTP,                         Is.False);

            Assert.That(LogServerSettings.TryParse(JObject.Parse("""{ "httpPort": "eighty" }"""),    out _, out var error), Is.False);
            Assert.That(error,                               Does.Contain("httpPort"));

            Assert.That(LogServerSettings.TryParse(JObject.Parse("""{ "maxFileSize": 0 }"""),        out _, out error), Is.False);
            Assert.That(error,                               Does.Contain("maxFileSize"));

            Assert.That(LogServerSettings.TryParse(JObject.Parse("""{ "httpListen": "nowhere" }"""), out _, out error), Is.False);
            Assert.That(error,                               Does.Contain("httpListen"));

        }

        [Test]
        public void ASocketOfAnIPv4ClientMappedIntoIPv6_IsWrittenAsIPv4()
        {

            Assert.That(LogServerNode.Unmapped("[0000:0000:0000:0000:0000:ffff:c000:020a]:50832"), Is.EqualTo("192.0.2.10:50832"));
            Assert.That(LogServerNode.Unmapped("[2001:db8::1]:22"),                                 Is.EqualTo("[2001:db8::1]:22"));
            Assert.That(LogServerNode.Unmapped("192.0.2.10:1"),                                     Is.EqualTo("192.0.2.10:1"));

        }

    }

}
