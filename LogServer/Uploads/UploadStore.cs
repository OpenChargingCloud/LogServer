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
using System.Globalization;
using System.Security.Cryptography;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#endregion

namespace cloud.charging.open.LogServer
{

    /// <summary>
    /// Which uploads a query asks for, and in which order.
    /// </summary>
    /// <param name="Uploader">Only those of this account, or of every one where null.</param>
    /// <param name="ChargingStation">Only those of this charging station, or of every one where null.</param>
    /// <param name="Since">Only those kept at or after this time.</param>
    /// <param name="Until">Only those kept before this time.</param>
    /// <param name="Newest">The newest first, or the oldest.</param>
    /// <param name="Skip">How many of those to leave out, from the first in that order.</param>
    /// <param name="Take">How many to bring back at most.</param>
    public sealed record UploadQuery(String?          Uploader         = null,
                                     String?          ChargingStation  = null,
                                     DateTimeOffset?  Since            = null,
                                     DateTimeOffset?  Until            = null,
                                     Boolean          Newest           = true,
                                     Int32            Skip             = 0,
                                     Int32            Take             = UploadStore.DefaultPageSize);


    /// <summary>
    /// The uploads of a log server: each file in a directory of its charging
    /// station below the uploads directory, and one line about it in the index
    /// beside them - appended as it arrives, read back at every start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file is kept under a name of its own making - when it came, its
    /// number, and the name it came as, with every character a file system
    /// might mind written as '_' - so that two charging stations, or one
    /// station twice, never overwrite each other, whatever they called their
    /// file. What it was called is in the index.
    /// </para>
    /// <para>
    /// The index is JSON, one upload a line. A line that cannot be read is
    /// said and passed over, rather than refusing to start: the files it
    /// speaks of are still on the disk.
    /// </para>
    /// </remarks>
    public sealed class UploadStore
    {

        #region Data

        /// <summary>
        /// The name of the index beside the uploads.
        /// </summary>
        public const String  IndexFileName    = "uploads.jsonl";

        /// <summary>
        /// How many uploads a query brings back unless it says.
        /// </summary>
        public const Int32   DefaultPageSize  = 500;

        /// <summary>
        /// The most uploads one query may bring back.
        /// </summary>
        public const Int32   MaxPageSize      = 5_000;

        /// <summary>
        /// How long a part of a name may be on the disk.
        /// </summary>
        private const Int32  MaxPartLength    = 120;

        private readonly Lock               storeLock  = new ();
        private readonly List<UploadEvent>  uploads    = [];
        private readonly TimeProvider       timeProvider;
        private readonly Action<String>     warn;

        /// <summary>
        /// UTF-8 without a byte order mark: the index is read a line at a time,
        /// and a mark in front of the first would make it no JSON.
        /// </summary>
        private static readonly Encoding UTF8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private UInt64 lastId;

        #endregion

        #region Properties

        /// <summary>
        /// The directory the uploads are kept in, given in full.
        /// </summary>
        public String   Directory    { get; }

        /// <summary>
        /// The index file.
        /// </summary>
        public String   IndexFile
            => Path.Combine(Directory, IndexFileName);

        /// <summary>
        /// How many uploads are kept.
        /// </summary>
        public Int32    Count
        {
            get
            {
                lock (storeLock)
                    return uploads.Count;
            }
        }

        /// <summary>
        /// How many bytes they are together.
        /// </summary>
        public Int64    TotalSize
        {
            get
            {
                lock (storeLock)
                    return uploads.Sum(upload => upload.Size);
            }
        }

        /// <summary>
        /// The newest upload, or null.
        /// </summary>
        public UploadEvent? Latest
        {
            get
            {
                lock (storeLock)
                    return uploads.Count > 0 ? uploads[^1] : null;
            }
        }

        /// <summary>
        /// Every upload, the oldest first.
        /// </summary>
        public IReadOnlyList<UploadEvent> All
        {
            get
            {
                lock (storeLock)
                    return [.. uploads];
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Raised after an upload was kept.
        /// </summary>
        public event Action<UploadEvent>? OnUploaded;

        /// <summary>
        /// Raised after an upload was removed.
        /// </summary>
        public event Action<UploadEvent>? OnRemoved;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The uploads kept in the given directory.
        /// </summary>
        /// <param name="Directory">The directory, given in full; made where it is not there.</param>
        /// <param name="TimeProvider">What the time is.</param>
        /// <param name="Warn">Where what is wrong with the index is said.</param>
        public UploadStore(String           Directory,
                           TimeProvider?    TimeProvider  = null,
                           Action<String>?  Warn          = null)
        {

            if (String.IsNullOrWhiteSpace(Directory) || !Path.IsPathFullyQualified(Directory))
                throw new ArgumentException($"The uploads need a directory given in full, not '{Directory}'.", nameof(Directory));

            this.Directory     = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Directory));
            this.timeProvider  = TimeProvider ?? System.TimeProvider.System;
            this.warn          = Warn         ?? (_ => { });

        }

        #endregion


        #region Load()

        /// <summary>
        /// Read the index; how many uploads it holds.
        /// </summary>
        public Int32 Load()
        {

            System.IO.Directory.CreateDirectory(Directory);

            lock (storeLock)
            {

                uploads.Clear();
                lastId = 0;

                if (!File.Exists(IndexFile))
                    return 0;

                var lineNumber = 0;

                foreach (var line in File.ReadLines(IndexFile, UTF8))
                {

                    lineNumber++;

                    if (String.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {

                        if (ParseLine(line.TrimStart((Char) 0xFEFF)) is JObject json &&
                            UploadEvent.TryParse(json, out var upload))
                        {
                            uploads.Add(upload);
                            lastId = Math.Max(lastId, upload.Id);
                            continue;
                        }

                    }
                    catch (JsonException)
                    { }

                    warn($"Line {lineNumber} of '{IndexFile}' is no upload, and was passed over.");

                }

                uploads.Sort((a, b) => a.Timestamp != b.Timestamp
                                           ? a.Timestamp.CompareTo(b.Timestamp)
                                           : a.Id.CompareTo(b.Id));

                return uploads.Count;

            }

        }

        #endregion


        #region KeepBytes(...)

        /// <summary>
        /// Keep what an HTTP upload brought.
        /// </summary>
        public UploadEvent KeepBytes(Byte[]          Content,
                                     UploadProtocol  Protocol,
                                     String          Uploader,
                                     String          ChargingStation,
                                     String          FileName,
                                     String          RemoteAddress,
                                     String?         ContentType  = null,
                                     String?         UserAgent    = null)

            => Keep(target => File.WriteAllBytes(target, Content),
                    Protocol, Uploader, ChargingStation, FileName, RemoteAddress, ContentType, UserAgent);

        #endregion

        #region KeepFile(...)

        /// <summary>
        /// Keep a file that is on the disk already - one SFTP put in place - by
        /// moving it into the store.
        /// </summary>
        public UploadEvent KeepFile(String          File,
                                    UploadProtocol  Protocol,
                                    String          Uploader,
                                    String          ChargingStation,
                                    String          FileName,
                                    String          RemoteAddress)

            => Keep(target => System.IO.File.Move(File, target),
                    Protocol, Uploader, ChargingStation, FileName, RemoteAddress, null, null);

        #endregion

        #region (private) Keep(...)

        private UploadEvent Keep(Action<String>  Write,
                                 UploadProtocol  Protocol,
                                 String          Uploader,
                                 String          ChargingStation,
                                 String          FileName,
                                 String          RemoteAddress,
                                 String?         ContentType,
                                 String?         UserAgent)
        {

            UInt64          id;
            DateTimeOffset  timestamp;
            String          storedAs;
            String          target;

            lock (storeLock)
            {

                id         = ++lastId;
                timestamp  = timeProvider.GetUtcNow();

                var stationDirectory  = SafePart(ChargingStation);
                var name              = $"{timestamp.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}-{id}-{SafePart(FileName)}";

                storedAs   = $"{stationDirectory}/{name}";
                target     = Path.Combine(Directory, stationDirectory, name);

                System.IO.Directory.CreateDirectory(Path.Combine(Directory, stationDirectory));

            }

            Write(target);

            Int64   size;
            String  sha256;

            using (var stream = File.OpenRead(target))
            {
                size    = stream.Length;
                sha256  = Convert.ToHexStringLower(SHA256.HashData(stream));
            }

            var upload = new UploadEvent(id,
                                         timestamp,
                                         Protocol,
                                         Uploader,
                                         ChargingStation,
                                         FileName,
                                         storedAs,
                                         size,
                                         sha256,
                                         RemoteAddress,
                                         ContentType,
                                         UserAgent);

            lock (storeLock)
            {

                File.AppendAllText(IndexFile, upload.ToJSON().ToString(Formatting.None) + "\n", UTF8);

                // In the order of their time, which a file that took long to
                // arrive and one that did not may not have had.
                var at = uploads.FindLastIndex(other => other.Timestamp <= upload.Timestamp) + 1;
                uploads.Insert(at, upload);

            }

            Raise(OnUploaded, upload);

            return upload;

        }

        #endregion

        #region TryGet(Id, out Upload)

        /// <summary>
        /// The upload of the given number.
        /// </summary>
        public Boolean TryGet(UInt64 Id, out UploadEvent? Upload)
        {
            lock (storeLock)
            {
                Upload = uploads.FirstOrDefault(upload => upload.Id == Id);
                return Upload is not null;
            }
        }

        #endregion

        #region FileOf(Upload)

        /// <summary>
        /// Where the file of the given upload lies - in the uploads directory
        /// and nowhere else, whatever the index says.
        /// </summary>
        /// <exception cref="InvalidOperationException">Where the index would point outside the directory.</exception>
        public String FileOf(UploadEvent Upload)
        {

            var file        = Path.GetFullPath(Path.Combine(Directory, Upload.StoredAs.Replace('/', Path.DirectorySeparatorChar)));
            var comparison  = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            if (!file.StartsWith(Directory + Path.DirectorySeparatorChar, comparison))
                throw new InvalidOperationException($"Upload {Upload.Id} would lie outside '{Directory}'.");

            return file;

        }

        #endregion

        #region Remove(Id, out Removed, out Error)

        /// <summary>
        /// Remove an upload: its file and its line in the index.
        /// </summary>
        /// <returns>Whether there was one of that number.</returns>
        public Boolean Remove(UInt64 Id, out UploadEvent? Removed, out String? Error)
        {

            Error = null;

            lock (storeLock)
            {

                Removed = uploads.FirstOrDefault(upload => upload.Id == Id);

                if (Removed is null)
                    return false;

                var remaining  = uploads.Where(upload => upload.Id != Id).ToList();
                var temporary  = IndexFile + ".new";

                try
                {

                    File.WriteAllText(temporary,
                                      String.Concat(remaining.Select(upload => upload.ToJSON().ToString(Formatting.None) + "\n")),
                                      UTF8);

                    File.Move(temporary, IndexFile, overwrite: true);

                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
                {
                    Error = $"The index '{IndexFile}' could not be written: {problem.Message}";
                    return true;
                }

                uploads.Remove(Removed);

                try
                {

                    var file = FileOf(Removed);

                    if (File.Exists(file))
                        File.Delete(file);

                    // The directory of a charging station goes with its last file.
                    if (Path.GetDirectoryName(file) is String directory &&
                        !String.Equals(directory, Directory, StringComparison.OrdinalIgnoreCase) &&
                        System.IO.Directory.Exists(directory) &&
                        !System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        System.IO.Directory.Delete(directory);
                    }

                }
                catch (Exception problem)
                {
                    warn($"The file of upload {Id} could not be removed: {problem.Message}");
                }

            }

            Raise(OnRemoved, Removed);

            return true;

        }

        #endregion

        #region Query(Query, out Total)

        /// <summary>
        /// The uploads a query asks for, and how many there are of them in all.
        /// </summary>
        public IReadOnlyList<UploadEvent> Query(UploadQuery  Query,
                                                out Int32    Total)
        {

            IEnumerable<UploadEvent> matching = All;

            if (Query.Uploader is { Length: > 0 } uploader)
                matching = matching.Where(upload => String.Equals(upload.Uploader, uploader, StringComparison.OrdinalIgnoreCase));

            if (Query.ChargingStation is { Length: > 0 } station)
                matching = matching.Where(upload => String.Equals(upload.ChargingStation, station, StringComparison.OrdinalIgnoreCase));

            if (Query.Since is DateTimeOffset since)
                matching = matching.Where(upload => upload.Timestamp >= since);

            if (Query.Until is DateTimeOffset until)
                matching = matching.Where(upload => upload.Timestamp <  until);

            var list = matching.ToList();

            Total = list.Count;

            if (Query.Newest)
                list.Reverse();

            return [.. list.Skip(Math.Max(0, Query.Skip)).
                            Take(Math.Clamp(Query.Take, 0, MaxPageSize))];

        }

        #endregion

        #region Uploaders / ChargingStations

        /// <summary>
        /// Every account that uploaded, with how many and when last.
        /// </summary>
        public IReadOnlyList<(String Name, Int32 Uploads, DateTimeOffset Last)> Uploaders

            => [.. All.GroupBy (upload => upload.Uploader, StringComparer.OrdinalIgnoreCase).
                       Select  (group  => (group.Key, group.Count(), group.Max(upload => upload.Timestamp))).
                       OrderBy (one    => one.Key, StringComparer.OrdinalIgnoreCase)];

        /// <summary>
        /// Every charging station something was uploaded of, with how many and
        /// when last.
        /// </summary>
        public IReadOnlyList<(String Name, Int32 Uploads, DateTimeOffset Last)> ChargingStations

            => [.. All.GroupBy (upload => upload.ChargingStation, StringComparer.OrdinalIgnoreCase).
                       Select  (group  => (group.Key, group.Count(), group.Max(upload => upload.Timestamp))).
                       OrderBy (one    => one.Key, StringComparer.OrdinalIgnoreCase)];

        #endregion


        #region (static) SafePart(Text)

        /// <summary>
        /// A part of a name on the disk: letters, digits, '-', '_' and '.' as
        /// they are, every other character as '_', no dot in front, never "."
        /// or "..", and not longer than a file system likes.
        /// </summary>
        public static String SafePart(String? Text)
        {

            var safe = new String((Text ?? "").Select(c => Char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray()).
                           TrimStart('.').
                           TrimEnd  ('.');

            if (safe.Length > MaxPartLength)
                safe = safe[..MaxPartLength];

            return safe.Length == 0 ? "_" : safe;

        }

        #endregion

        #region (private static) ParseLine(Line)

        /// <summary>
        /// One line of the index as JSON - its times as the text they were
        /// written as: Newtonsoft would otherwise read them as dates, and give
        /// them back as text without their milliseconds.
        /// </summary>
        private static JObject? ParseLine(String Line)
        {

            using var reader = new JsonTextReader(new StringReader(Line)) {
                                   DateParseHandling = DateParseHandling.None
                               };

            return JToken.ReadFrom(reader) as JObject;

        }

        #endregion

        #region (private) Raise(Event, Upload)

        private void Raise(Action<UploadEvent>? Event, UploadEvent Upload)
        {
            foreach (var listener in Event?.GetInvocationList().Cast<Action<UploadEvent>>() ?? [])
            {
                try
                {
                    listener(Upload);
                }
                catch (Exception problem)
                {
                    warn($"Whoever listens to the uploads failed over upload {Upload.Id}: {problem.Message}");
                }
            }
        }

        #endregion

    }

}
