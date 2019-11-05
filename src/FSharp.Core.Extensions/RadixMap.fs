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
open System
open System.Collections.Generic
open System.Runtime.CompilerServices

module internal RadixConst =
    let [<Literal>] S = 4
    let [<Literal>] M = 16
    let [<Literal>] L = 48
    let [<Literal>] XL = 256
    let [<Literal>] MaxPrefixLength = 10
    
[<Flags>]
type NodeFlags =
    | None   = 0uy
    | Small  = 1uy
    | Medium = 2uy
    | Large  = 3uy
    | XLarge = 4uy
    | Leaf   = 8uy

[<NoEquality;NoComparison>]           
type internal Node<'a> =
    { flags: NodeFlags; childCount: byte; partialLen: int; partial: byte[]; keys: byte[]; children: Node<'a>[] }
    static member Empty() = { flags = NodeFlags.Leaf; childCount = 0uy; partialLen = 0; partial = Array.zeroCreate RadixConst.MaxPrefixLength; keys = [||]; children = [||] }
    member this.IsLeaf = this.flags.HasFlag(NodeFlags.Leaf)
    
    /// Returns a number of prefix bytes shared between given `key` and this node.
    member this.PrefixLength(key: ReadOnlySpan<byte>, depth: int): int =
        let len = min (min this.partialLen RadixConst.MaxPrefixLength) (key.Length - depth)
        let mutable i = 0
        let mutable cont = true
        while cont && i < len do
            if this.partial.[i] <> key.[depth + i] then
                cont <- false
            i <- i + 1
        i
        
    member this.FindChild(c: byte) = ()
        
            
/// Immutable radix tree, which can be treated like `Map<string, 'a>`.
/// Major advantage is prefix-based lookup capability.
type RadixMap<'a> internal(count: int, root: Node<'a>) =
    static member Empty: RadixMap<'a> = RadixMap<'a>(0, Node<'a>.Empty())
    member this.Count = count
    

[<RequireQualifiedAccess>]
module RadixMap =
    
    let empty<'a> = RadixMap<'a>.Empty
    
    let ofSeq (seq: seq<string * 'a>): RadixMap<'a> = failwith "not implemented"
    
    let ofMap (map: #seq<KeyValuePair<string, 'a>>): RadixMap<'a> = failwith "not implemented"
    
    let add (key: string) (value: 'a) (map: RadixMap<'a>): RadixMap<'a> = failwith "not implemented"
    
    let tryFind (key: string) (map: RadixMap<'a>): 'a voption = failwith "not implemented"
    
    let prefixed (prefix: string) (map: RadixMap<'a>) = failwith "not implemented"
    
    let map (fn: string -> 'a -> 'b) (map: RadixMap<'a>) = failwith "not implemented"
    
    let filter (fn: string -> 'a -> bool) (map: RadixMap<'a>) = failwith "not implemented"
    