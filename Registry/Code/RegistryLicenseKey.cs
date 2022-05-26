using IntegrationLib;
using System;
using System.Linq;

namespace BaseLmPlugin
{
    /// <summary>
    /// Registry license key.
    /// </summary>
    [Serializable()]
    public class RegistryLicenseKey : ApplicationLicenseKeyBase
    {
        #region OVERRIDES
        
        public override bool IsValid
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Value);
            }
        }

        public override string KeyString
        {
            get
            {
                return string.IsNullOrWhiteSpace(Value) ? null : Value.Split(Environment.NewLine.ToCharArray()).FirstOrDefault();
            }
        } 

        #endregion
    }
}
