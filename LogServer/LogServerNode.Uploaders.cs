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

using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;

#endregion

namespace cloud.charging.open.LogServer
{

    /// <summary>
    /// What a change of the uploaders came to.
    /// </summary>
    public enum UploaderChangeOutcome
    {

        /// <summary>Made, given a new password, or no longer let upload.</summary>
        Changed,

        /// <summary>What was asked for cannot be: a name that is none, one that is taken.</summary>
        Refused,

        /// <summary>There is no uploader of that name.</summary>
        NotFound,

        /// <summary>The accounts refused to write it.</summary>
        NotSaved

    }


    /// <summary>
    /// What a change of the uploaders came to, what it said, and the password
    /// of an account that was made or given a new one - shown once, and
    /// never written down.
    /// </summary>
    public sealed record UploaderChange(UploaderChangeOutcome  Outcome,
                                        String                 Said,
                                        String?                Uploader  = null,
                                        String?                Password  = null);


    /// <summary>
    /// The accounts files are uploaded under: accounts of the node, in the
    /// "uploader" role.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One a charging station, as a rule, named as the station is - so that
    /// what it uploads over SFTP, where no path names a station, is its own;
    /// or one a back end that uploads in the name of several, by HTTP, with
    /// the station in the path.
    /// </para>
    /// <para>
    /// The password of an uploader is made up here and shown once, as the
    /// node's first account's is: what the accounts keep is its hash. Over
    /// SFTP an uploader may sign in with it where the "ssh" section says
    /// "passwords": true, and with a public key of its own in every case -
    /// given to it as to any account, with 'sshKeys'.
    /// </para>
    /// <para>
    /// An uploader that is taken away keeps its account, and with it what
    /// was uploaded under it: it is only no longer in the role, and so may
    /// upload no more.
    /// </para>
    /// </remarks>
    public partial class LogServerNode
    {

        #region Data

        private const String  UploaderPasswordCharacters  = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz123456789";

        private const Int32   UploaderPasswordLength      = 24;

        #endregion


        #region UploaderAccounts

        /// <summary>
        /// Every account in the uploader role.
        /// </summary>
        public IReadOnlyList<IUser> UploaderAccounts

            => [.. ExtAPI.Users.Where  (IsUploader).
                                OrderBy(user => user.Id.ToString(), StringComparer.OrdinalIgnoreCase)];

        #endregion

        #region IsUploader(User)

        /// <summary>
        /// Whether the given account is in the uploader role.
        /// </summary>
        public Boolean IsUploader(IUser User)

            => RolesOf(User).Any(role => role.Name == LogServerAccess.Uploader.Name);

        #endregion

        #region UploadersJSON()

        /// <summary>
        /// Every uploader: its name, what it is called, how many uploads it
        /// made and when last.
        /// </summary>
        public JArray UploadersJSON()
        {

            var uploaded = Uploads.Uploaders.ToDictionary(one => one.Name, StringComparer.OrdinalIgnoreCase);

            return new JArray(UploaderAccounts.Select(user => {

                       var id = user.Id.ToString();

                       uploaded.TryGetValue(id, out var made);

                       return new JObject(
                                  new JProperty("id",          id),
                                  new JProperty("name",        user.Name.FirstText()),
                                  new JProperty("disabled",    user.IsDisabled),
                                  new JProperty("uploads",     made.Uploads),
                                  new JProperty("lastUpload",  made.Uploads > 0 ? made.Last.ToString("o") : null)
                              );

                   }));

        }

        #endregion


        #region AddUploader(Id, Name, By)

        /// <summary>
        /// An account more that may upload, with a password made up for it.
        /// </summary>
        /// <param name="Id">The name it signs in with - as a rule the charging station's.</param>
        /// <param name="Name">What it is called, or null for its name.</param>
        /// <param name="By">Who made it, for the log.</param>
        public async Task<UploaderChange> AddUploader(String   Id,
                                                      String?  Name,
                                                      String   By)
        {

            if (!User_Id.TryParse(Id.Trim(), out var userId) ||
                userId.ToString().Length is 0 or > 64 ||
                userId.ToString().Any(c => Char.IsWhiteSpace(c) || Char.IsControl(c) || c is '/' or '\\' or ':' or '@'))
            {
                return new UploaderChange(UploaderChangeOutcome.Refused, $"'{Id}' is no name an uploader can sign in with: letters, digits, '-', '_' and '.', without spaces.");
            }

            if (ExtAPI.TryGetUser(userId, out var existing) && existing is not null)
                return new UploaderChange(UploaderChangeOutcome.Refused,
                                          IsUploader(existing)
                                              ? $"'{userId}' is an uploader already. A new password is made with 'password'."
                                              : $"There is an account '{userId}' already, and it is no uploader. Give the uploader another name.");

            // The node's, which its first account was made in. Made only where
            // it is not there: "if not exists" answers with nothing - and not
            // with the one there is - where it is.
            var organizationId = Organization_Id.Parse(Kind.Organization);

            if (!ExtAPI.TryGetOrganization(organizationId, out var organization))
                organization = await ExtAPI.CreateOrganizationIfNotExists(
                                         organizationId,
                                         I18NString.Create(Languages.en, Kind.Organization)
                                     );

            if (organization is not Organization nodeOrganization)
                return new UploaderChange(UploaderChangeOutcome.NotSaved, $"The organization of this {Kind.Name} could not be had, and an account outside one cannot sign in.");

            var password = RandomNumberGenerator.GetString(UploaderPasswordCharacters, UploaderPasswordLength);

            // CreateUser, as the node makes its first account: the password is
            // set from inside it, where the account can already reach the
            // password store; the EULA is accepted at once, since nobody would
            // be there to accept it, and nothing is mailed to anybody.
            var made = await ExtAPI.CreateUser(
                                 userId,
                                 I18NString.Create(Languages.en, String.IsNullOrWhiteSpace(Name) ? userId.ToString() : Name.Trim()),
                                 SimpleEMailAddress.Parse($"{UploadStore.SafePart(userId.ToString())}@localhost"),
                                 User2OrganizationEdgeLabel.IsMember,
                                 nodeOrganization,
                                 Password:                  password,
                                 SkipDefaultNotifications:  true,
                                 SkipNewUserEMail:          true,
                                 SkipNewUserNotifications:  true,
                                 AcceptedEULA:              TimeProvider.GetUtcNow().AddSeconds(-1),
                                 IsAuthenticated:           true
                             );

            if (made is not User user)
                return new UploaderChange(UploaderChangeOutcome.NotSaved, $"The account '{userId}' could not be made.");

            if (!ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(LogServerAccess.Uploader.Name), out var found) ||
                found is not UserGroup group)
            {
                return new UploaderChange(UploaderChangeOutcome.NotSaved, $"There is no group of the {LogServerAccess.Uploader.Name} role to put '{userId}' in.");
            }

            var joined = await ExtAPI.AddUserToUserGroup(user, User2UserGroupEdgeLabel.IsMember, group);

            if (!joined.IsSuccess)
                return new UploaderChange(UploaderChangeOutcome.NotSaved,
                                          $"The account '{userId}' was made, but could not be put in the {LogServerAccess.Uploader.Name} role: {joined.ErrorDescription?.FirstText()}");

            Log.Notice($"'{By}' made the uploader '{userId}'.", "logserver", "uploaders", "auth");

            return new UploaderChange(UploaderChangeOutcome.Changed,
                                      $"'{userId}' may upload from now on.",
                                      userId.ToString(),
                                      password);

        }

        #endregion

        #region NewUploaderPassword(Id, By)

        /// <summary>
        /// A new password for an uploader, made up here: the one before opens
        /// nothing from then on.
        /// </summary>
        public async Task<UploaderChange> NewUploaderPassword(String  Id,
                                                              String  By)
        {

            if (!TryGetUploader(Id, out var user, out var notFound))
                return notFound;

            var password  = RandomNumberGenerator.GetString(UploaderPasswordCharacters, UploaderPasswordLength);

            // The overload of several accounts, which sets a password anew: the
            // one of one account changes it only against the one it has, and
            // whoever gives an uploader a new one has, as a rule, lost it.
            var changed   = await ExtAPI.ChangePassword(new[] { user }, password, SuppressNotifications: true);

            if (changed.Result != CommandResult.Success)
                return new UploaderChange(UploaderChangeOutcome.NotSaved, $"The password of '{user.Id}' could not be changed: {changed.Description.FirstText()}");

            Log.Notice($"'{By}' gave the uploader '{user.Id}' a new password.", "logserver", "uploaders", "auth");

            return new UploaderChange(UploaderChangeOutcome.Changed,
                                      $"'{user.Id}' signs in with a new password from now on; the one before opens nothing.",
                                      user.Id.ToString(),
                                      password);

        }

        #endregion

        #region RemoveUploader(Id, By)

        /// <summary>
        /// An uploader no longer let upload: its account taken out of the
        /// uploader role. What it uploaded is kept.
        /// </summary>
        public async Task<UploaderChange> RemoveUploader(String  Id,
                                                         String  By)
        {

            if (!TryGetUploader(Id, out var user, out var notFound))
                return notFound;

            if (user is not User account ||
                !ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(LogServerAccess.Uploader.Name), out var found) ||
                found is not UserGroup group)
            {
                return new UploaderChange(UploaderChangeOutcome.NotSaved, $"'{user.Id}' could not be taken out of the {LogServerAccess.Uploader.Name} role.");
            }

            var removed = await ExtAPI.RemoveUserFromUserGroup(account, User2UserGroupEdgeLabel.IsMember, group);

            if (!removed.IsSuccess)
                return new UploaderChange(UploaderChangeOutcome.NotSaved,
                                          $"'{user.Id}' could not be taken out of the {LogServerAccess.Uploader.Name} role: {removed.ErrorDescription?.FirstText()}");

            Log.Notice($"'{By}' took the uploader '{user.Id}' away: it may upload no more. What it uploaded is kept.", "logserver", "uploaders", "auth");

            return new UploaderChange(UploaderChangeOutcome.Changed,
                                      $"'{user.Id}' may upload no more. What it uploaded is kept, and its account too, without the {LogServerAccess.Uploader.Name} role.",
                                      user.Id.ToString());

        }

        #endregion

        #region (private) TryGetUploader(Id, out User, out NotFound)

        private Boolean TryGetUploader(String              Id,
                                       out IUser           User,
                                       out UploaderChange  NotFound)
        {

            User      = null!;
            NotFound  = new UploaderChange(UploaderChangeOutcome.NotFound, $"There is no uploader '{Id}'.");

            if (!User_Id.TryParse(Id.Trim(), out var userId) ||
                !ExtAPI.TryGetUser(userId, out var found) ||
                found is null ||
                !IsUploader(found))
            {
                return false;
            }

            User = found;
            return true;

        }

        #endregion

    }

}
