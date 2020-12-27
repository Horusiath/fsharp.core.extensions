/// Copyright 2020 Bartosz Sypytkowski <b.sypytkowski@gmail.com>
///
/// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
/// documentation files (the "Software"), to deal in the Software without restriction, including without limitation
/// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software,
/// and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
///
/// The above copyright notice and this permission notice shall be included in all copies or substantial portions of
/// the Software.
///
/// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
/// THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
/// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
/// TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
/// THE SOFTWARE.

(**
 *  RAFT distributed replication store based on: https://raft.github.io/raft.pdf
 **)
 
namespace Clusterpack.Raft

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Clusterpack
open FSharp.Core
open FSharp.Control.Tasks

type Term = uint64
type Binary = byte[]
type Index = uint64
type Path = string

[<Interface>]
type Logger =
    abstract Trace: string -> unit
    abstract Error: string -> unit
    
[<Interface>]
type HasLogger =
    abstract Get: unit -> Logger
    
[<RequireQualifiedAccess>]
module Log =
    
    let from (trace: TextWriter) (error: TextWriter) =
        { new Logger with
            member _.Trace line = trace.WriteLine line
            member _.Error line = error.WriteLine line }
        
    let console = from Console.Out Console.Error
    
    let trace (env: #HasLogger) fmt = Printf.kprintf (env.Get().Trace) fmt
    let error (env: #HasLogger) fmt = Printf.kprintf (env.Get().Error) fmt
    
[<Interface>]
type HasRandom =
    abstract Get: unit -> Random
    
[<RequireQualifiedAccess>]
module Random =
    
    let timespan (env: #HasRandom) (min: TimeSpan) (max: TimeSpan) =
        let rand = env.Get()
        let a = int min.TotalMilliseconds
        let b = int max.TotalMilliseconds
        TimeSpan.FromMilliseconds (float (rand.Next(a, b)))
        
    let pick (env: #HasRandom) (c: #IReadOnlyList<'t>): 't =
        let len = c.Count
        let rand = env.Get()
        let idx = rand.Next(len)
        c.[idx]
        
[<Interface>]
type NodeDiscovery =
    abstract SeedNodes: unit -> ValueTask<struct(NodeId * Endpoint) list>
    
[<Interface>]
type HasNodeDiscovery =
    abstract Get: unit -> NodeDiscovery
    
[<RequireQualifiedAccess>]
module NodeDiscovery =
    let fix (nodes: struct(NodeId * Endpoint) list): NodeDiscovery =
        { new NodeDiscovery with member _.SeedNodes () = ValueTask<_> nodes }
        
    let seeds (env: #HasNodeDiscovery) = env.Get().SeedNodes()
    
type Config =
    { /// Minimum and maximum range of election process timeout. Actual election timeout is chosen at random by every
      /// candidate.
      ElectionTimeoutRange: struct (TimeSpan * TimeSpan)
      /// Every `Entry` is delivered (with retries) until acknowledgement has been confirmed. In case if it's not
      /// confirmed right away, delivery will be retried after given interval, until eventual success. 
      RedeliveryInterval: TimeSpan
      /// Raft nodes expect timely heartbeat messages. If no message will be received before a timeout happens,
      /// a corresponding node will be considered dead.
      HeartbeatTimeout: TimeSpan }
    static member Default =
        { ElectionTimeoutRange = struct(TimeSpan.FromMilliseconds 150., TimeSpan.FromMilliseconds 300.)
          RedeliveryInterval = TimeSpan.FromMilliseconds 500.
          HeartbeatTimeout = TimeSpan.FromSeconds 5. }
        
[<Interface>]
type HasConfig =
    abstract Get: unit -> Config
    
[<RequireQualifiedAccess>]
module Config =
    
    let electionTimeout (env: #HasConfig) = env.Get().ElectionTimeoutRange
    let redeliveryInterval (env: #HasConfig) = env.Get().RedeliveryInterval
    let heartbeatTimeout (env: #HasConfig) = env.Get().HeartbeatTimeout
    
type Membership =
    { Members: Set<NodeId>
      NonVoters: Set<NodeId>
      ToRemove: Set<NodeId> }
   
module Membership =
    
    let contains (node: NodeId) (m: Membership) = Set.contains node m.Members || Set.contains node m.NonVoters
    
    let isJointConsensus (m: Membership) = Set.isEmpty m.NonVoters && Set.isEmpty m.ToRemove
    
    let allMembers (m: Membership) = m.Members + m.NonVoters
    
    let joint (m: Membership) =
        { Members = m.Members + m.NonVoters - m.ToRemove
          NonVoters = Set.empty
          ToRemove = Set.empty }
    
    let singleton (n: NodeId) =
        { Members = Set.singleton n
          NonVoters = Set.empty
          ToRemove = Set.empty }
        
    let ofSeq (nodes: NodeId seq) =
        { Members = Set.ofSeq nodes
          NonVoters = Set.empty
          ToRemove = Set.empty }

type Payload =
    | Data of Binary
    | MembershipChange of Membership

[<Struct>]
type Entry =
    { Term: Term
      Index: uint64
      Payload: Payload }
    
[<Struct>]
type VoteReq =
    { Term: Term
      Candidate: NodeId
      LastLogIndex: Index
      LastLogTerm: Term }
    
[<Struct>]
type VoteRep =
    { Term:Term
      Conflict: struct(Term*Index) option
      Granted: bool }

type AppendEntriesReq =
    { Term: Term
      LeaderCommitIndex: Index
      Leader: NodeId
      PrevLogIndex: Index
      PrevLogTerm: Term
      Entries: Entry[] }
    member inline this.IsHeartbeat = Array.isEmpty this.Entries
   
[<Struct>] 
type AppendEntriesRep =
    { Term:Term
      Conflict: struct(Term*Index) option
      Granted: bool }

/// State of the Raft node persisted throughout node lifetime.
type DurableState =
    { LastLogIndex: Index
      LastLogTerm: Term
      LastAppliedIndex: Index
      VotedFor: NodeId option
      Membership: Membership }
    
[<RequireQualifiedAccess>]
module DurableState =
    
    let init (m: Membership) =
        { LastLogTerm = 0UL
          LastLogIndex = 0UL
          LastAppliedIndex = 0UL
          VotedFor = None
          Membership = m }

[<Interface>]
type Storage =
    abstract LoadDurableState: unit -> ValueTask<DurableState option>
    abstract SaveDurableState: DurableState  -> ValueTask
    abstract GetEntries: start:Index * finish:Index -> AsyncSeq<Entry>
    abstract DeleteEntries: start:Index * finish:Index -> ValueTask
    abstract InsertEntries: entries:Entry[] -> ValueTask
    
[<Interface>]
type HasStorage =
    abstract Get: unit -> Storage
    
[<RequireQualifiedAccess>]
module Storage =
    let loadDurableState (env: #HasStorage) = env.Get().LoadDurableState()
    let saveDurableState (env: #HasStorage) state = env.Get().SaveDurableState(state)
    let getEntries (env: #HasStorage) start finish = env.Get().GetEntries(start, finish)
    let insertEntries (env: #HasStorage) entries = env.Get().InsertEntries(entries)
    let deleteEntries (env: #HasStorage) start finish = env.Get().DeleteEntries(start, finish)
    
[<Struct>]
type ConsensusState =
    | Uniform
    | Joint of incoming:Set<NodeId> * committed:bool
    
/// State machine of this Raft replica.
type SM =
    { Id: NodeId
      Membership: Membership
      LeaderId: NodeId option
      VotedFor: NodeId option
      CurrentTerm: Term
      CommitIndex: Index
      AppliedIndex: Index
      LastLogIndex: Index
      LastLogTerm: Term
      ElectionTimeout: IDisposable option }
    
[<RequireQualifiedAccess>]
module SM =
    
    let load env (id: NodeId) = vtask {
        let! state = Storage.loadDurableState env
        let state = state |> Option.defaultWith (fun () -> DurableState.init (Membership.singleton id))
        return { Id = id
                 Membership = state.Membership
                 LeaderId = None
                 VotedFor = state.VotedFor
                 CurrentTerm = state.LastLogTerm
                 CommitIndex = state.LastAppliedIndex
                 AppliedIndex = state.LastAppliedIndex
                 LastLogIndex = state.LastLogIndex
                 LastLogTerm = state.LastLogTerm
                 ElectionTimeout = None }
    }

    let isUpToDate (lastLogTerm: Term) (lastLogIndex: Index) (sm: SM) =
        lastLogTerm >= sm.LastLogTerm && lastLogIndex >= sm.LastLogIndex
    
    let saveHardState env (sm: SM) = unitVtask {
        let state =
            { LastLogTerm = sm.LastLogTerm
              LastLogIndex = sm.LastLogIndex
              LastAppliedIndex = sm.AppliedIndex
              VotedFor = sm.VotedFor
              Membership = sm.Membership }
        do! Storage.saveDurableState env state
    }

type private LeaderState =
    { Nodes: Map<NodeId, ChannelWriter<AppendEntriesReq>>
      AwaitingCommitted: Map<Index, Endpoint>
      Consensus: ConsensusState  }

type private FollowerState =
    | Idle
    
type CandidateState =
    { Countdown: int
      ElectionTimeout: CancellationTokenSource }

type Protocol =
    | VoteReq of VoteReq
    | VoteRep of VoteRep
    | AppendEntriesReq of AppendEntriesReq
    | AppendEntriesRep of AppendEntriesRep
    
[<Interface>]
type Client =
    abstract Vote: target:NodeId * VoteReq * CancellationToken -> Task<VoteRep>
    abstract AppendEntries: target:NodeId * AppendEntriesReq * CancellationToken -> Task<AppendEntriesRep>
    
[<Interface>]
type HasClient =
    abstract Get: unit -> Client
    
[<RequireQualifiedAccess>]
module Client =
    let vote (env: #HasClient) cancel req target = env.Get().Vote(target, req,  cancel)
    let append (env: #HasClient) cancel req target = env.Get().AppendEntries(target, req,  cancel)
    