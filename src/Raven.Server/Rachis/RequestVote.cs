﻿namespace Raven.Server.Rachis
{
    public class RequestVote
    {
        public long Term { get; set; }
        public long LastLogIndex { get; set; }
        public long LastLogTerm { get; set; }
        public bool IsTrialElection { get; set; }
        public bool IsForcedElection { get; set; }
        public string Source { get; set; }
    }

    public class RequestVoteResponse
    {
        public long Term { get; set; }
        public bool VoteGranted { get; set; }
        public string Message { get; set; }
    }
}