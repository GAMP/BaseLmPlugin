
using IntegrationLib;
using System;

namespace BaseLmPlugin
{
    [Serializable()]
    public class UserNamePasswordLicenseKeyBase : ApplicationLicenseKeyBase
    {
        private string
            username,
            password;

        /// <summary>
        /// Gets or sets licenses username.
        /// </summary>
        public string Username
        {
            get { return username; }
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
        /// Gets if license is valid.
        /// </summary>
        public override bool IsValid
        {
            get
            {
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
                return Username ?? "Invalid key";
            }
        }

        public override string ToString()
        {
            return KeyString;
        }
    }
}
