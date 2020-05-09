namespace Benchmarks

open BenchmarkDotNet.Attributes
open FSharp.Control.Tasks.Builders
open FSharp.Core

[<MemoryDiagnoser>]
type ActorBenchmarks() =
    
    let [<Literal>] Ops = 1_000_000
    
    [<GlobalSetup>]
    member _.Setup() = ()
    
    [<GlobalCleanup>]
    member _.Cleanup() = ()
    
    [<Benchmark(Baseline=true)>]
    member _.FSharpAsyncActor() = task {
        let promise = Promise<unit>()
        use actor = MailboxProcessor.Start(fun ctx ->
            let rec loop count = async {
                let! msg = ctx.Receive()
                let count' = count + msg
                if count' = Ops then
                    promise.SetResult ()
                    return ()
                else return! loop count' }
            loop 0)
        for i in 0..Ops do
            actor.Post 1
        do! promise.Task }
        
    [<Benchmark>]
    member _.FSharpActorUnbounded() = task {
        let promise = Promise<unit>()
        use actor = Actor.stateful 0 (fun ctx msg -> vtask {
            let count' = ctx.State + msg
            if count' = Ops then
                promise.SetResult ()
                ctx.Complete ()
            return count' })
        for i in 0..Ops do
            actor.Send 1 |> ignore
        do! promise.Task }
        
    [<Benchmark>]
    member _.FSharpActorBounded() = task {
        let promise = Promise<unit>()
        use actor = Actor.statefulWith { MailboxSize = 1000 } 0 (fun ctx msg -> vtask {
            let count' = ctx.State + msg
            if count' = Ops then
                promise.SetResult ()
                ctx.Complete ()
            return count' })
        for i in 0..Ops do
            do! actor.Send 1
        do! promise.Task
    }