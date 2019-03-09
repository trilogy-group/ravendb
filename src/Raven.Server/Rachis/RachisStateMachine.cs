﻿using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Voron;
using Voron.Data;
// ReSharper disable InconsistentNaming

namespace Raven.Server.Rachis
{
    public abstract class RachisStateMachine : IDisposable
    {
        protected TransactionContextPool ContextPoolForReadOnlyOperations;
        protected RachisConsensus _parent;
        public RachisVersionValidation Validator;
        
        public virtual void Initialize(RachisConsensus parent, TransactionOperationContext context)
        {
            _parent = parent;            
            ContextPoolForReadOnlyOperations = _parent.ContextPool;
            Validator = InitializeValidator();
        }

        public long Apply(TransactionOperationContext context, long uptoInclusive, Leader leader, ServerStore serverStore, Stopwatch duration)
        {
            Debug.Assert(context.Transaction != null);

            var lastAppliedIndex = _parent.GetLastCommitIndex(context);
            var maxTimeAllowedToWaitForApply = _parent.Timeout.TimeoutPeriod / 4;
            for (var index = lastAppliedIndex + 1; index <= uptoInclusive; index++)
            {
                var cmd = _parent.GetEntry(context, index, out RachisEntryFlags flags);
                if (cmd == null || flags == RachisEntryFlags.Invalid)
                    throw new InvalidOperationException("Expected to apply entry " + index + " but it isn't stored");

                lastAppliedIndex = index;

                if (flags != RachisEntryFlags.StateMachineCommand)
                    continue;

                Apply(context, cmd, index, leader, serverStore);
                
                if (duration.ElapsedMilliseconds >= maxTimeAllowedToWaitForApply)
                    // we don't want to spend so much time applying commands that we will time out the leader
                    // so we time this from the follower perspective and abort after applying a single command
                    // or 25% of the time has already passed
                    break; 
            }
            var term = _parent.GetTermForKnownExisting(context, lastAppliedIndex);

            _parent.SetLastCommitIndex(context, lastAppliedIndex, term);

            return lastAppliedIndex;
        }

        protected abstract void Apply(TransactionOperationContext context, BlittableJsonReaderObject cmd, long index, Leader leader, ServerStore serverStore);

        public virtual void EnsureNodeRemovalOnDeletion(TransactionOperationContext context, long term, string nodeTag)
        {
            
        }

        public void Dispose()
        {
            
        }

        protected abstract RachisVersionValidation InitializeValidator();

        public abstract bool ShouldSnapshot(Slice slice, RootObjectType type);

        public abstract Task<RachisConnection> ConnectToPeer(string url, string tag, X509Certificate2 certificate);

        public virtual Task OnSnapshotInstalledAsync(long lastIncludedIndex, ServerStore serverStore, CancellationToken token)
        {
            return Task.CompletedTask;
        }
    }
}
