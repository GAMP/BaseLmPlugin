using IntegrationLib;
using System;

namespace BaseLmPlugin
{
    [Serializable()]
    public class SteamLicenseKey : ApplicationLicenseKeyBase
    {
        #region FIELDS
        private string
            username,
            password,
            accountId;
        #endregion

        #region PROPERTIES

        /// <summary>
        /// Gets or sets licenses username.
        /// </summary>
        public string Username
        {
            get { return this.username; }
            set
            {
                username = value;
                RaisePropertyChanged("Username");
            }
        }

        /// <summary>
        /// Gets or sets license password.
        /// </summary>
        public string Password
        {
            get { return password; }
            set
            {
                password = value;
                RaisePropertyChanged("Password");
            }
        }

        /// <summary>
        /// Gets or sets account id.
        /// </summary>
        public string AccountId
        {
            get { return accountId; }
            set
            {
                accountId = value;
                RaisePropertyChanged("AccountId");
            }
        }

        /// <summary>
        /// Gets if license is valid.
        /// </summary>
        public override bool IsValid
        {
            get
            {
                //both username and password must not be null or empty in order to be considered valid
                return !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
            }
        }

        /// <summary>
        /// Gets license literal string representation.
        /// </summary>
        public override string KeyString
        {
            get
            {
                //by default only username will be shown as key representation.
                return Username ?? "Invalid key";
            }
        }

        #endregion

        #region OVERRIDES
        public override string ToString()
        {
            return this.KeyString;
        }
        #endregion
    }
}
