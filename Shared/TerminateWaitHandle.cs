using System.Threading;

namespace BaseLmPlugin
{
    public sealed class TerminateWaitHandle : ManualResetEventSlim
    {
        #region CONSTRUCTOR
        public TerminateWaitHandle(bool initialState)
            : base(initialState)
        { }
        #endregion

        #region PROPERTIES
        /// <summary>
        /// Gets or sets if handle is waiting for termination.
        /// </summary>
        public bool WaitingTermination
        {
            get;
            internal set;
        }
        /// <summary>
        /// Gets process name.
        /// </summary>
        public string TerminatingProcessName
        {
            get;
            internal set;
        }
        #endregion
    }
}
