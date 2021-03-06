// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security;
using System.Threading;
using Windows.Foundation;
using Windows.UI.Core;

using System.Diagnostics.Tracing;

namespace System.Threading
{
#if FEATURE_APPX
    #region class WinRTSynchronizationContextFactory

    [FriendAccessAllowed]
    internal sealed class WinRTSynchronizationContextFactory : WinRTSynchronizationContextFactoryBase
    {
        //
        // It's important that we always return the same SynchronizationContext object for any particular ICoreDispatcher
        // object, as long as any existing instance is still reachable.  This allows reference equality checks against the
        // SynchronizationContext to determine if two instances represent the same dispatcher.  Async frameworks rely on this.
        // To accomplish this, we use a ConditionalWeakTable to track which instances of WinRTSynchronizationContext are bound
        // to each ICoreDispatcher instance.
        //
        private static readonly ConditionalWeakTable<CoreDispatcher, WinRTSynchronizationContext> s_contextCache =
            new ConditionalWeakTable<CoreDispatcher, WinRTSynchronizationContext>();

        public override SynchronizationContext Create(object dispatcherObj)
        {
            Debug.Assert(dispatcherObj != null);

            //
            // Get the RCW for the dispatcher
            //
            CoreDispatcher dispatcher = (CoreDispatcher)dispatcherObj;

            //
            // The dispatcher is supposed to belong to this thread
            //
            Debug.Assert(dispatcher == CoreWindow.GetForCurrentThread().Dispatcher);
            Debug.Assert(dispatcher.HasThreadAccess);

            //
            // Get the WinRTSynchronizationContext instance that represents this CoreDispatcher.
            //
            return s_contextCache.GetValue(dispatcher, _dispatcher => new WinRTSynchronizationContext(_dispatcher));
        }
    }

    #endregion class WinRTSynchronizationContextFactory


    #region class WinRTSynchronizationContext

    internal sealed class WinRTSynchronizationContext : SynchronizationContext
    {
        private readonly CoreDispatcher _dispatcher;

        internal WinRTSynchronizationContext(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        #region class WinRTSynchronizationContext.Invoker

        private class Invoker
        {
            private readonly ExecutionContext _executionContext;
            private readonly SendOrPostCallback _callback;
            private readonly object _state;

            private static readonly ContextCallback s_contextCallback = new ContextCallback(InvokeInContext);

            private delegate void DelEtwFireThreadTransferSendObj(object id, int kind, string info, bool multiDequeues);
            private delegate void DelEtwFireThreadTransferObj(object id, int kind, string info);
            private static DelEtwFireThreadTransferSendObj s_EtwFireThreadTransferSendObj;
            private static DelEtwFireThreadTransferObj s_EtwFireThreadTransferReceiveObj;
            private static DelEtwFireThreadTransferObj s_EtwFireThreadTransferReceiveHandledObj;
            private static volatile bool s_TriedGetEtwDelegates;

            public Invoker(SendOrPostCallback callback, object state)
            {
                _executionContext = ExecutionContext.FastCapture();
                _callback = callback;
                _state = state;

                if (FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.ThreadTransfer))
                    EtwFireThreadTransferSendObj(this);
            }

            public void Invoke()
            {
                if (FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.ThreadTransfer))
                    EtwFireThreadTransferReceiveObj(this);

                if (_executionContext == null)
                    InvokeCore();
                else
                    ExecutionContext.Run(_executionContext, s_contextCallback, this, preserveSyncCtx: true);

                // If there was an ETW event that fired at the top of the winrt event handling loop, ETW listeners could
                // use it as a marker of completion of the previous request. Since such an event does not exist we need to
                // fire the "done handling off-thread request" event in order to enable correct work item assignment.
                if (FrameworkEventSource.Log.IsEnabled(EventLevel.Informational, FrameworkEventSource.Keywords.ThreadTransfer))
                    EtwFireThreadTransferReceiveHandledObj(this);
            }

            private static void InvokeInContext(object thisObj)
            {
                ((Invoker)thisObj).InvokeCore();
            }

            private void InvokeCore()
            {
                try
                {
                    _callback(_state);
                }
                catch (Exception ex)
                {
                    //
                    // If we let exceptions propagate to CoreDispatcher, it will swallow them with the idea that someone will
                    // observe them later using the IAsyncInfo returned by CoreDispatcher.RunAsync.  However, we ignore
                    // that IAsyncInfo, because there's nothing Post can do with it (since Post returns void).
                    // So, to avoid these exceptions being lost forever, we post them to the ThreadPool.
                    //
                    if (!(ex is ThreadAbortException) && !(ex.GetType().Name.EndsWith("AppDomainUnloadedException")))
                    {
                        if (!WindowsRuntimeMarshal.ReportUnhandledError(ex))
                        {
                            var edi = ExceptionDispatchInfo.Capture(ex);
                            ThreadPool.QueueUserWorkItem(o => ((ExceptionDispatchInfo)o).Throw(), edi);
                        }
                    }
                }
            }

            #region ETW Activity-tracing support
            private static void InitEtwMethods()
            {
                Type fest = typeof(FrameworkEventSource);
                var mi1 = fest.GetMethod("ThreadTransferSendObj", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var mi2 = fest.GetMethod("ThreadTransferReceiveObj", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var mi3 = fest.GetMethod("ThreadTransferReceiveHandledObj", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (mi1 != null && mi2 != null && mi3 != null)
                {
                    s_EtwFireThreadTransferSendObj = (DelEtwFireThreadTransferSendObj)mi1.CreateDelegate(typeof(DelEtwFireThreadTransferSendObj),
                                                                        FrameworkEventSource.Log);
                    s_EtwFireThreadTransferReceiveObj = (DelEtwFireThreadTransferObj)mi2.CreateDelegate(typeof(DelEtwFireThreadTransferObj),
                                                                           FrameworkEventSource.Log);
                    s_EtwFireThreadTransferReceiveHandledObj = (DelEtwFireThreadTransferObj)mi3.CreateDelegate(typeof(DelEtwFireThreadTransferObj),
                                                                           FrameworkEventSource.Log);
                }
                s_TriedGetEtwDelegates = true;
            }

            private static void EtwFireThreadTransferSendObj(object id)
            {
                if (!s_TriedGetEtwDelegates)
                    InitEtwMethods();
                if (s_EtwFireThreadTransferSendObj != null)
                    s_EtwFireThreadTransferSendObj(id, 3, string.Empty, false);
            }

            private static void EtwFireThreadTransferReceiveObj(object id)
            {
                if (!s_TriedGetEtwDelegates)
                    InitEtwMethods();
                if (s_EtwFireThreadTransferReceiveObj != null)
                    s_EtwFireThreadTransferReceiveObj(id, 3, string.Empty);
            }

            private static void EtwFireThreadTransferReceiveHandledObj(object id)
            {
                if (!s_TriedGetEtwDelegates)
                    InitEtwMethods();
                if (s_EtwFireThreadTransferReceiveHandledObj != null)
                    s_EtwFireThreadTransferReceiveHandledObj(id, 3, string.Empty);
            }
            #endregion ETW Activity-tracing support
        }

        #endregion class WinRTSynchronizationContext.Invoker

        [SecuritySafeCritical]
        public override void Post(SendOrPostCallback d, object state)
        {
            if (d == null)
                throw new ArgumentNullException("d");
            Contract.EndContractBlock();

            var ignored = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, new Invoker(d, state).Invoke);
        }

        [SecuritySafeCritical]
        public override void Send(SendOrPostCallback d, object state)
        {
            throw new NotSupportedException(SR.InvalidOperation_SendNotSupportedOnWindowsRTSynchronizationContext);
        }

        public override SynchronizationContext CreateCopy()
        {
            return new WinRTSynchronizationContext(_dispatcher);
        }
    }
    #endregion class WinRTSynchronizationContext
#endif //FEATURE_APPX
}  // namespace
