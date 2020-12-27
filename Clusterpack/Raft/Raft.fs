module Clusterpack.Raft

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Core
open FSharp.Core.Operators
open FSharp.Control.Tasks

type Protocol =
    /// Request to other nodes to vote for a `candidate` at a given `term`.
    | VoteReq of VoteReq * Promise<VoteRep>
    /// Reply from node for a `VoteReq` at the given `term`.
    | VoteRep of VoteRep
    /// Request used to persist multiple `entries` in a totally ordered persistent event l.og
    | AppendEntriesReq of AppendEntriesReq * Promise<AppendEntriesRep>
    /// Response to `AppendEntriesReq`. If `conflict` value is None, it means that the request completed successfully. 
    | AppendEntriesRep of AppendEntriesRep
    | Join of NodeId * Endpoint
    | Leave of NodeId
    | ElectionTimeout
    | ForceFollower of Term
    
[<RequireQualifiedAccess>]
module Replicator =
    
    let create (sm: SM) (target: NodeId) : ChannelWriter<_> = failwith ""
    
    type State =
        { /// Id of current Raft node.
          Id: NodeId
          /// Id of target Raft node, this replicator talks to. 
          Target: NodeId
          /// Current term - it will never change while this actor is alive. If it will be changed,
          /// this actor will be terminated and leader will create new replicator in its place. 
          Term: Term
          /// Index of log entry most recently appended by the leader.
          LastIndex: Index
          /// Highest entry's index known to be committed in the cluster.
          LastCommit: Index
          /// Index of the next log to be send. Can go back in as of §5.3.
          NextIndex: Index
          /// Last known successfully replicated entry's index.
          MatchIndex: Index
          /// Last known successfully replicated entry's term.
          MatchTerm: Term }
    
    [<RequireQualifiedAccess>]
    module State =
                
        let from (sm: SM) (target: NodeId) =
            { Id = sm.Id
              Target = target
              Term = sm.CurrentTerm
              LastIndex = sm.LastLogIndex
              LastCommit = sm.CommitIndex
              NextIndex = sm.LastLogIndex + 1UL
              MatchIndex = sm.LastLogIndex
              MatchTerm = sm.LastLogTerm }
            
        let isUpToDate (state: State) = state.LastCommit = state.MatchIndex
        
            
    let rec lineRate (state: State) (deliveryTick: IDisposable option) (ctx: Actor<_>) = actor {
        let! msg = ctx.Receive()
        match msg with
        | AppendEntriesReq _ ->
            let delivery =
                match deliveryTick with
                | Some _ ->
                    ctx.Stash()
                    deliveryTick
                | None   ->
                    let tick = state.Config.RedeliveryInterval
                    let target = receiver ctx state.Target
                    Some (ctx.System.Scheduler.ScheduleTellRepeatedlyCancelable(TimeSpan.Zero, tick, target, msg, untyped ctx.Self))
            return! lineRate state delivery ctx
            
        | AppendEntriesRep(term, conflict)  ->
            confirmDelivery deliveryTick ctx
            if term > state.Term then 
                // newer term has been returned - node should revert to follower
                ctx.Parent() <! ForceFollower term
                return Stop // stop this replicator
            else match conflict with
                 | None -> // success
                     let state = { state with NextIndex = state.NextIndex + 1UL; MatchIndex = state.NextIndex; MatchTerm = term }
                     return! lineRate state None ctx
                 | Some(conflictingTerm, conflictingIndex) when  conflictingIndex < state.LastIndex ->
                     // target is lagging behind, need to replicate stale old entries
                     let state = { state with NextIndex = conflictingIndex + 1UL; MatchIndex = conflictingIndex; MatchTerm = conflictingTerm }
                     return! lagging state ctx
                 | _ -> return! lineRate state None ctx
            
        | _ -> return Unhandled }
    
    and lagging (state: State) = failwith "not impl" 
    
    let props (state: State) = failwith "not impl" 
    
    
let private updateElectionTimeout (sm: SM) (ctx: Actor<_>) =
    sm.ElectionTimeout |> Option.iter (fun c -> c.Cancel())
    let timeout =
        let struct(min, max) = sm.Config.ElectionTimeoutRange
        let randomized = ThreadLocalRandom.Current.Next(int min.TotalMilliseconds, int max.TotalMilliseconds)
        TimeSpan.FromMilliseconds (float randomized)
    let cancel = ctx.Schedule timeout ctx.Self ElectionTimeout
    { sm with ElectionTimeout = Some cancel }

let private appendEntries (state: LeaderState) (sm: SM) (ctx: Actor<_>) data = async {
    let index = sm.LastLogIndex + 1UL
    let entries = [| { Payload = data; Term = sm.CurrentTerm; Index = index } |]
    do! sm.Store.Append entries
    let appendReq = AppendEntriesReq(sm.CurrentTerm, sm.CommitIndex, sm.Id, sm.LastLogIndex, sm.LastLogTerm, entries)
    state.Nodes |> Map.iter (fun id replicator -> replicator <! appendReq)
    logDebugf ctx "append entries (term: %i, commitIndex: %i, leader: %i, lastLogIndex: %i, lastLogTerm: %i, count: %i)" sm.CurrentTerm sm.CommitIndex sm.Id sm.LastLogIndex sm.LastLogTerm entries.Length
    return { sm with LastLogIndex = index }
}


let rec candidate env (sm: SM) (state: CandidateState) msg = vtask {
    match msg with
    | VoteReq(term, id, lastLogIndex, lastLogTerm) ->
        let granted =
            if term >= sm.CurrentTerm &&                         // candidate must have term >= current node term
               Membership.contains id sm.Membership &&           // must be part of current cluster membership config
               SM.isUpToDate lastLogTerm lastLogIndex sm &&        // its log must not be behind current node
               Option.defaultValue id sm.VotedFor = id then true // current node haven't voted already for another candidate
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
        
    | VoteRep(term, granted) ->
        if term > sm.CurrentTerm then
            logDebugf ctx "candidate - vote res (term: %i, granted: %b) - term higher than current" term granted
            let sm = { sm with CurrentTerm = term; VotedFor = None; LeaderId = None }
            SM.saveHardState sm
            ctx.UnstashAll()
            return! follower Idle sm ctx
        elif granted then
            let countdown = state.Countdown - 1
            logDebugf ctx "candidate - vote res (term: %i, granted: %b) - remaining votes to win: %i" term granted countdown
            if countdown = 0 then
                let state = becomeLeader sm ctx
                ctx.UnstashAll()
                return! leader state { sm with LeaderId = Some sm.Id } ctx
            else return candidate { state with Countdown = countdown } sm ctx
        else return! candidate state sm ctx
    
    | ElectionTimeout ->
        logDebugf ctx "candidate - election timeout. Starting new election."
        return! election sm ctx

    | _ ->
        ctx.Stash()
        return! candidate state sm ctx }

and private follower (state: FollowerState) (sm: SM) (ctx: Actor<_>) = actor {
    match! ctx.Receive() with
    | AppendEntriesReq(term, leaderCommitIndex, leader, prevLogIndex, prevLogTerm, entries) ->
        let sender = ctx.Sender()
        let sm = updateElectionTimeout sm ctx
        if term < sm.CurrentTerm then 
            sender <! AppendEntriesRep(term, Some (sm.CurrentTerm, sm.LastLogIndex))
            return! follower state sm ctx
        elif prevLogIndex <> sm.LastLogIndex || prevLogTerm <> sm.LastLogTerm then
            sender <! AppendEntriesRep(term, Some (sm.LastLogTerm, sm.LastLogIndex))
            return! follower state sm ctx
        else
            do! sm.Store.Append entries
            sender <! AppendEntriesRep(term, None)
            return! follower state sm ctx
            
    | msg -> return Unhandled }

let private emit env (sm: SM) (state: LeaderState) payload = vtask {
    failwith "not impl"
}
