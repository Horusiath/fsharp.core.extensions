namespace Benchmarks

open BenchmarkDotNet.Attributes
open FSharp.Control.Tasks.Affine.Unsafe
open FSharp.Core

[<Sealed>]
type TestUnboundedActor(ops) =
    inherit UnboundedActor<int>()
    let mutable state = 0
    override this.Receive msg = uunitVtask {
        state <- state + msg
        if state = ops then
            this.Complete ()
    }

[<Sealed>]
type TestBoundedActor(ops) =
    inherit BoundedActor<int>(1024)
    let mutable state = 0
    override this.Receive msg = uunitVtask {
        state <- state + msg
        if state = ops then
            this.Complete ()
    }
    
[<MemoryDiagnoser>]
type ActorBenchmarks() =
    
    let [<Literal>] Ops = 1_000_000
    
    [<GlobalSetup>]
    member _.Setup() = ()
    
    [<GlobalCleanup>]
    member _.Cleanup() = ()
    
    [<Benchmark(Baseline=true)>]
    member _.MailboxProcessorPost() = uunitTask {
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
        for i in 0..Ops-1 do
            actor.Post 1
        do! promise.Task }
        
    [<Benchmark>]
    member _.UnboundedActorSend() = uunitTask {
        use actor = new TestUnboundedActor(Ops)
        for i in 1..Ops do
            do! actor.Send 1
        do! actor.Terminated }
    
    [<Benchmark>]
    member _.UnboundedActorSendAsync() = uunitTask {
        use actor = new TestUnboundedActor(Ops)
        for i in 1..Ops do
            do! actor.SendAsync 1
        do! actor.Terminated }
        
    [<Benchmark>]
    member _.FSharpActorBounded() = uunitTask {
        use actor = new TestBoundedActor(Ops)
        for i in 1..Ops do
            do! actor.SendAsync 1
        do! actor.Terminated }