module Clusterpack.Raft.Election

open System.Threading
open FSharp.Control.Tasks
open FSharp.Core
open Clusterpack

let private voteRequest env (sm: SM) (req: VoteReq) = vtask {
     let granted =
         if req.Term >= sm.CurrentTerm &&                                 // candidate must have term >= current node term
            Membership.contains req.Candidate sm.Membership &&            // must be part of current cluster membership config
            SM.isUpToDate req.LastLogTerm req.LastLogIndex sm &&          // its log must not be behind current node
            Option.defaultValue req.Candidate sm.VotedFor = req.Candidate // current node haven't voted already for another candidate
         then true 
         else false
     ctx.Sender() <! VoteRep(term, granted)
     logDebugf ctx "candidate - vote req (term: %i, candidate: %i, lastIndex: %i, lastTerm: %i, granted: %b)" term id lastLogIndex lastLogTerm granted
     if granted then
         let sm = { sm with CurrentTerm = term; VotedFor = Some id }
         SM.saveHardState sm
         if id <> sm.Id then
             let sm = updateElectionTimeout sm ctx
             ctx.UnstashAll()
             logDebugf ctx "becoming follower."
             return! follower Idle sm ctx
         else return! candidate state sm ctx
     else return! candidate state sm ctx
}

let private becomeLeader env (sm: SM) = task {
    let replicators =
        Set.remove sm.Id (Membership.allMembers sm.Membership)
        |> Seq.map (fun target ->
            let replicator = Replicator.create sm target
            (target, replicator))
        |> Map.ofSeq
        
    let heartbeat : AppendEntriesReq =
        { Term = sm.CurrentTerm
          LeaderCommitIndex = sm.CommitIndex
          Leader = sm.Id
          PrevLogIndex = sm.LastLogIndex
          PrevLogTerm = sm.LastLogTerm
          Entries = [||] }
        
    do! replicators
        |> Seq.map (fun e -> e.Value.WriteAsync(heartbeat).AsTask())
        |> Task.WhenAll
        
    Log.trace env "%i is becoming new leader" sm.Id
    let consensus =
        if Membership.isJointConsensus sm.Membership then
            Joint (sm.Membership.NonVoters, false)
        else Uniform
    let state = { Consensus = consensus; Nodes = replicators; AwaitingCommitted = Map.empty }
    let payload = if sm.LastLogIndex = 0UL then MembershipChange sm.Membership else Data [||]
        
    // Write a new blank entry to the cluster to guard against stale-reads, per §8.
    failwith "Write a new blank entry to the cluster to guard against stale-reads, per §8."
     
    return state
}

let private countVotes env (sm: SM) (candidate: CandidateState) = task {
    let mutable remaining = candidate.Countdown
    let mutable failed = 0
    let mutable leader = None
    let mutable sm' = sm
    
    while remaining - failed > 0 && obj.ReferenceEquals(sm, sm') do
        match! candidate.NextVote() with
        | Error e ->
            Log.error env "Candidate %i failed to receive reply." sm'.Id
            failed <- failed + 1
        | Ok rep ->
            if rep.Term > sm'.CurrentTerm then
                Log.trace env "Candidate %i - vote reply (term: %i, granted: %b) - term higher than current" sm'.Id rep.Term rep.Granted
                sm' <- { sm' with CurrentTerm = rep.Term; VotedFor = None; LeaderId = None }
                do! SM.saveHardState env sm'
            elif rep.Granted then
                remaining <- remaining - 1
                Log.trace env "Candidate %i - vote res (term: %i, granted: %b) - remaining votes to win: %i" sm'.Id rep.Term rep.Granted remaining
                if remaining = 0 then
                    let! l = becomeLeader env sm'
                    leader <- Some l
            else
                failed <- failed + 1
              
    return (sm', leader)
}

let electionTimeout env =
    let struct(min, max) = Config.electionTimeout env
    let timeout = Random.timespan env min max
    new CancellationTokenSource(timeout)

let private askForVotes env voters state electionTimeout = 
    let req: VoteReq = { Term = state.CurrentTerm; Candidate = state.Id; LastLogIndex = state.LastLogIndex; LastLogTerm = state.LastLogTerm }
    let (inbox, outbox) = Channel.unboundedMpsc<Result<VoteRep, exn>> ()
    voters
    |> Seq.map (Client.vote env electionTimeout req)
    |> Seq.iter (Task.secure >> Task.map (inbox.TryWrite >> ignore) >> ignore)
    outbox

let startElection env (sm: SM) = task {
    let state = { sm with CurrentTerm = sm.CurrentTerm + 1UL }
    let voters = Set.remove state.Id state.Membership.Members
    let countdown = Set.count voters / 2
    
    Log.trace env "Candidate %i starts election in term %i (lastLogIndex: %i, lastLogTerm: %i): needs %i votes to win" state.Id state.CurrentTerm state.LastLogIndex state.LastLogTerm countdown
    
    // send vote requests and redirect them to outbox
    use timeout = electionTimeout env
    let mailbox = askForVotes env voters state timeout.Token
        
    // count votes
    try
        let candidate = { Countdown = countdown; ElectionTimeout = timeout.Token; Mailbox = mailbox }
        let! (state', leader) = countVotes env sm candidate
        return leader
    with e ->
        Log.trace env "Candidate %i - becoming follower." (sm).Id
        return None
}