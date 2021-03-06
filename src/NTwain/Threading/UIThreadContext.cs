﻿using NTwain.Internals;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NTwain.Threading
{
    /// <summary>
    /// A <see cref="IThreadContext"/> using <see cref="SynchronizationContext"/>
    /// for running actions on an UI thread.
    /// </summary>
    class UIThreadContext : IThreadContext
    {
        private readonly SynchronizationContext context;

        /// <summary>
        /// Creates a new <see cref="UIThreadContext"/> using
        /// the specified <see cref="SynchronizationContext"/>.
        /// </summary>
        /// <param name="context"></param>
        public UIThreadContext(SynchronizationContext context)
        {
            this.context = context;
        }

        /// <summary>
        /// Runs the action asynchronously on the <see cref="SynchronizationContext"/> thread.
        /// </summary>
        /// <param name="action"></param>
        public void BeginInvoke(Action action)
        {
            if (action == null) return;

            context.Post(o =>
            {
                try
                {
                    action();
                }
                catch
                {
                    // TODO: do something
                }
            }, null);
        }

        /// <summary>
        /// Runs the action synchronously on the <see cref="SynchronizationContext"/> thread.
        /// </summary>
        /// <param name="action"></param>
        public void Invoke(Action action)
        {
            if (action == null) return;

            Exception error = null;
            context.Send(o =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }, null);

            error.TryRethrow();
        }

        void IThreadContext.Start()
        {
        }

        void IThreadContext.Stop()
        {
        }
    }
}
