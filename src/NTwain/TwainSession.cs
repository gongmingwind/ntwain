﻿using NTwain.Data;
using NTwain.Resources;
using NTwain.Threading;
using NTwain.Triplets;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NTwain
{
    /// <summary>
    /// Manages a TWAIN session.
    /// </summary>
    public partial class TwainSession
    {
        internal readonly TwainConfig Config;

        // cache generated twain sources so if you get same source from same session it'll return the same object
        readonly Dictionary<string, DataSource> _ownedSources = new Dictionary<string, DataSource>();
        // need to keep delegate around to prevent GC
        readonly Callback32 _callback32Delegate;
        // for windows only
        readonly IThreadContext _internalContext;

        private IntPtr _hWnd;
        private IThreadContext _externalContext;


        /// <summary>
        /// Constructs a new <see cref="TwainSession"/>.
        /// </summary>
        /// <param name="config"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public TwainSession(TwainConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            SetSynchronizationContext(SynchronizationContext.Current);
            switch (config.Platform)
            {
                case PlatformID.MacOSX:
                case PlatformID.Unix:
                    _internalContext = new DispatcherLoop(this);
                    break;
                default:
                    _internalContext = new WinMsgLoop(this);
                    _callback32Delegate = new Callback32(Handle32BitCallback);
                    break;
            }
            //CapReader = new CapReader(this);
            CapWriter = new CapWriter(config);
        }

        /// <summary>
        /// Sets the optional synchronization context.
        /// Because most TWAIN-related things are happening on a different thread,
        /// this allows events to be raised on the thread associated with this context and
        /// may be useful if you want to handle them in the UI thread.
        /// </summary>
        /// <param name="context">Usually you want to use <see cref="SynchronizationContext.Current"/> while on the UI thread.</param>
        public void SetSynchronizationContext(SynchronizationContext context)
        {
            if (context == null) _externalContext = null;
            else _externalContext = new UIThreadContext(context);
        }

        /// <summary>
        /// Synchronously invokes an action on the external user thread if possible.
        /// </summary>
        /// <param name="action"></param>
        void ExternalInvoke(Action action)
        {
            if (_externalContext != null) _externalContext.Invoke(action);
            action();
        }

        /// <summary>
        /// Asynchronously invokes an action on the external user thread if possible.
        /// </summary>
        /// <param name="action"></param>
        void ExternalBeginInvoke(Action action)
        {
            if (_externalContext != null) _externalContext.BeginInvoke(action);
            action();
        }

        /// <summary>
        /// Synchronously invokes an action on the internal TWAIN thread if possible.
        /// </summary>
        /// <param name="action"></param>
        internal void InternalInvoke(Action action)
        {
            if (_internalContext != null) _internalContext.Invoke(action);
            else action();
        }

        /// <summary>
        /// Asynchronously invokes an action on the internal TWAIN thread if possible.
        /// </summary>
        /// <param name="action"></param>
        void InternalBeginInvoke(Action action)
        {
            if (_internalContext != null) _internalContext.BeginInvoke(action);
            else action();
        }

        /// <summary>
        /// Opens the TWAIN session.
        /// </summary>
        /// <param name="hWnd"></param>
        /// <returns></returns>
        public ReturnCode Open(IntPtr hWnd)
        {
            _hWnd = hWnd;
            var rc = DGControl.Parent.OpenDSM(hWnd);
            if (rc == ReturnCode.Success)
            {
                _internalContext?.Start();
            }
            return rc;
        }

        /// <summary>
        /// Closes the TWAIN session.
        /// </summary>
        /// <returns></returns>
        public ReturnCode Close()
        {
            var rc = DGControl.Parent.CloseDSM(_hWnd);
            if (rc == ReturnCode.Success)
            {
                _internalContext?.Stop();
            }
            return rc;
        }

        /// <summary>
        /// Steps down to the target session state.
        /// </summary>
        /// <param name="targetState"></param>
        /// <returns></returns>
        public ReturnCode StepDown(TwainState targetState)
        {
            if (targetState == this.State) return ReturnCode.Success;

            var rc = ReturnCode.Failure;
            while (State > targetState)
            {
                switch (State)
                {
                    case TwainState.DsmLoaded:
                    case TwainState.DsmUnloaded:
                    case TwainState.Invalid:
                        // can do nothing in these states
                        return ReturnCode.Success;
                    case TwainState.DsmOpened:
                        rc = Close();
                        if (rc != ReturnCode.Success) return rc;
                        break;
                    case TwainState.SourceOpened:
                        rc = DGControl.Identity.CloseDS(CurrentSource.Identity32);
                        if (rc != ReturnCode.Success) return rc;
                        break;
                    case TwainState.SourceEnabled:
                        rc = DGControl.UserInterface.DisableDS(ref _lastEnableUI, false);
                        if (rc != ReturnCode.Success) return rc;
                        break;
                    case TwainState.TransferReady:
                    case TwainState.Transferring:
                        _disableDSNow = true;
                        break;
                }
            }
            return rc;
        }

        /// <summary>
        /// Gets the manager status. Useful after getting a non-success return code.
        /// </summary>
        /// <returns></returns>
        public TW_STATUS GetStatus()
        {
            TW_STATUS stat = default;
            var rc = DGControl.Status.Get(ref stat, null);
            return stat;
        }

        /// <summary>
        /// Gets the translated string for a <see cref="TW_STATUS"/>.
        /// </summary>
        /// <param name="status"></param>
        /// <param name="source"></param>
        /// <returns></returns>
        public string GetLocalizedStatus(ref TW_STATUS status, DataSource source = null)
        {
            var rc = DGControl.StatusUtf8.Get(ref status, source, out string message);
            return message;
        }

        internal void RegisterCallback()
        {
            // TODO: support other platforms
            var callbackPtr = Marshal.GetFunctionPointerForDelegate(_callback32Delegate);

            // try new callback first
            var cb2 = new TW_CALLBACK2 { CallBackProc = callbackPtr };
            var rc = DGControl.Callback2.RegisterCallback(ref cb2);
            if (rc == ReturnCode.Success) Debug.WriteLine("Registed Callback2 success.");
            else
            {
                var status = GetStatus();
                Debug.WriteLine($"Register Callback2 failed with condition code: {status.ConditionCode}.");
            }


            if (rc != ReturnCode.Success)
            {
                // always register old callback
                var cb = new TW_CALLBACK { CallBackProc = callbackPtr };

                rc = DGControl.Callback.RegisterCallback(ref cb);

                if (rc == ReturnCode.Success) Debug.WriteLine("Registed Callback success.");
                else
                {
                    var status = GetStatus();
                    Debug.WriteLine($"Register Callback failed with {status.ConditionCode}.");
                }
            }
        }


        /// <summary>
        /// Enumerate list of sources available on the machine.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<DataSource> GetSources()
        {
            var rc = DGControl.Identity.GetFirst(out TW_IDENTITY srcId);
            while (rc == ReturnCode.Success)
            {
                yield return GetSourceSingleton(srcId);
                rc = DGControl.Identity.GetNext(out srcId);
            }
        }

        /// <summary>
        /// Gets/sets the default data source. Setting to null is not supported.
        /// </summary>
        public DataSource DefaultSource
        {
            get
            {
                if (DGControl.Identity.GetDefault(out TW_IDENTITY src) == ReturnCode.Success)
                {
                    return GetSourceSingleton(src);
                }
                return null;
            }
            set
            {
                if (value != null)
                {
                    if (value.Session != this)
                    {
                        throw new InvalidOperationException(MsgText.SourceNotThisSession);
                    }
                    var rc = DGControl.Identity.Set(value);
                    RaisePropertyChanged(nameof(DefaultSource));
                }
            }
        }

        /// <summary>
        /// Tries to show the built-in source selector dialog and return the selected source.
        /// </summary>
        /// <returns></returns>
        public DataSource ShowSourceSelector()
        {
            if (DGControl.Identity.UserSelect(out TW_IDENTITY id) == ReturnCode.Success)
            {
                return GetSourceSingleton(id);
            }
            return null;
        }

        private DataSource _currentSource;

        /// <summary>
        /// Gets the currently open data source.
        /// </summary>
        public DataSource CurrentSource
        {
            get { return _currentSource; }
            internal set
            {
                var old = _currentSource;
                _currentSource = value;

                RaisePropertyChanged(nameof(CurrentSource));
                old?.RaisePropertyChanged(nameof(DataSource.IsOpen));
                value?.RaisePropertyChanged(nameof(DataSource.IsOpen));
            }
        }


        internal DataSource GetSourceSingleton(TW_IDENTITY sourceId)
        {
            DataSource source = null;
            var key = $"{sourceId.Id}|{sourceId.Manufacturer}|{sourceId.ProductFamily}|{sourceId.ProductName}";
            if (_ownedSources.ContainsKey(key))
            {
                source = _ownedSources[key];
            }
            else
            {
                _ownedSources[key] = source = new DataSource(this, sourceId);
            }
            return source;
        }

        /// <summary>
        /// Returns a <see cref="System.String"/> that represents this instance.
        /// </summary>
        /// <returns>
        /// A <see cref="System.String"/> that represents this instance.
        /// </returns>
        public override string ToString()
        {
            return State.ToString();
        }
    }
}
