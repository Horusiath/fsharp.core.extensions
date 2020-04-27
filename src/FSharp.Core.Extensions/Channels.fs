(*

Copyright 2019 Bartosz Sypytkowski

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

*)

namespace FSharp.Core

open System
open System.Threading
open FSharp.Control.Tasks.Builders
open System.Threading.Channels
open System.Threading.Tasks

module Channel =
    
    /// Returns decomposed writer/reader pair components of n-capacity bounded MPSC
    /// (multi-producer/single-consumer) channel.
    let inline boundedMpsc<'a> (n: int) : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateBounded(BoundedChannelOptions(n, SingleWriter=false, SingleReader=true))
        (ch.Writer, ch.Reader)
        
    /// Returns decomposed writer/reader pair components of unbounded MPSC
    /// (multi-producer/single-consumer) channel.
    let inline unboundedMpsc<'a> () : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateUnbounded(UnboundedChannelOptions(SingleWriter=false, SingleReader=true))
        (ch.Writer, ch.Reader)
       
    
    /// Returns decomposed writer/reader pair components of n-capacity bounded MPMC
    /// (multi-producer/multi-consumer) channel.
    let inline boundedMpmc<'a> (n: int) : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateBounded(BoundedChannelOptions(n, SingleWriter=false, SingleReader=false))
        (ch.Writer, ch.Reader)
        
    /// Returns decomposed writer/reader pair components of unbounded MPMC
    /// (multi-producer/multi-consumer) channel.
    let inline unboundedMpmc<'a> () : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateUnbounded(UnboundedChannelOptions(SingleWriter=false, SingleReader=false))
        (ch.Writer, ch.Reader)
        
    /// Returns decomposed writer/reader pair components of n-capacity bounded SPSC
    /// (single-producer/single-consumer) channel.
    let inline boundedSpsc<'a> (n: int) : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateBounded(BoundedChannelOptions(n, SingleWriter=true, SingleReader=true))
        (ch.Writer, ch.Reader)
        
    /// Returns decomposed writer/reader pair components of unbounded SPSC
    /// (multi-producer/multi-consumer) channel.
    let inline unboundedSpsc<'a> () : (ChannelWriter<'a> * ChannelReader<'a>) =
        let ch = Channel.CreateUnbounded(UnboundedChannelOptions(SingleWriter=true, SingleReader=true))
        (ch.Writer, ch.Reader)
        
    /// Tries to read as much elements as possible from a given reader to fill provided span
    /// without blocking.
    let readTo (span: Span<'a>) (reader: ChannelReader<'a>) : int =
        let mutable i = 0
        let mutable item = Unchecked.defaultof<_>
        while i < span.Length && reader.TryRead(&item) do
            span.[i] <- item
            i <- i+1
        i
         
    let rec private awaitForValue (readers: ChannelReader<'a>[]) = vtask {
        let pending = readers |> Array.map (fun reader -> task {
            let! ok = reader.WaitToReadAsync()
            if not ok then
                return raise (ChannelClosedException("Channel has been closed"))
            else return reader
        })
        let! t = Task.WhenAny(pending)
        let ok, value = t.Result.TryRead()
        
        // there's a risk that another thread has read value in between WaitToReadAsync and TryRead,
        // that's why we need to use recursion
        if ok then return value
        else return! awaitForValue readers
    }
    
    /// Listens or multiple channels, returning value from the one which completed first.
    /// Order in which channels where provided will be assumed priority order in case
    /// when multiple channels have produced their values at the same time. If any of the
    /// channels will be closed while waiting or any exception will be produced, entire
    /// select will fail.
    let select (readers: ChannelReader<'a>[]) : ValueTask<'a> =
        // 1st pass, check in any reader has it's value already prepared
        let mutable found = false
        let mutable i = 0
        let mutable result = Unchecked.defaultof<'a>
        while not found && i < readers.Length do
            let r = readers.[i]
            found <- r.TryRead(&result)
            i <- i + 1
            
        if found then ValueTask<'a>(result)
        else awaitForValue readers