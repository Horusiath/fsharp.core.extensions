namespace FSharp.Core

open System
open FSharp.Control.Tasks.Builders
open System.Threading.Channels
open System.Threading.Tasks

module Channel =
    
    /// Returns decomposed writer/reader pair components of n-capacity bounded MPSC
    /// (multi-producer/single-consumer) channel.
    let inline boundedMpsc<'a> (n: int) : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateBounded(BoundedChannelOptions(n, SingleReader=true))
        (ch.Writer, ch.Reader)
        
    /// Returns decomposed writer/reader pair components of unbounded MPSC
    /// (multi-producer/single-consumer) channel.
    let inline unboundedMpsc<'a> () : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateUnbounded(UnboundedChannelOptions(SingleReader=true))
        (ch.Writer, ch.Reader)
        
    /// Tries to read as much elements as possible from a given reader to fill provided span
    /// without blocking.
    let tryReadTo (span: Span<'a>) (reader: ChannelReader<'a>) : int =
        let mutable i = 0
        let mutable item = Unchecked.defaultof<_>
        while i < span.Length && reader.TryRead(&item) do
            span.[i] <- item
            i <- i+1
        i
    
    /// Listens or multiple channels, returning value from the one which completed first.
    /// Order in which channels where provided will be assumed priority order in case
    /// when multiple channels have produced their values at the same time. If any of the
    /// channels will be closed while waiting or any exception will be produced, entire
    /// select will fail.
    let select (readers: ChannelReader<'a>[]) : ValueTask<'a> = vtask {
        // 1st pass, check in any reader has it's value already prepared
        let mutable found = false
        let mutable i = 0
        let mutable result = Unchecked.defaultof<'a>
        while not found && i < readers.Length do
            let r = readers.[i]
            found <- r.TryRead(&result)
            i <- i + 1
            
        if found then return result
        else
            // 2nd pass - call WaitToReadAsync and get first completing channel
            // there's a risk that another thread has read value in between WaitToReadAsync and TryRead,
            // that's why we need to use loop
            let mutable finished = false
            while not finished do
                let pending = readers |> Array.map (fun r -> task {
                    let! ok = r.WaitToReadAsync()
                    if not ok then
                        return raise (ChannelClosedException("Channel has been closed"))
                    return r
                })
                let! winner = Task.WhenAny(pending)
                finished <- winner.Result.TryRead(&result) 
            return result
    }