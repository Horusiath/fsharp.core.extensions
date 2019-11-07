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
open System.Numerics
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Runtime.Intrinsics

module internal ArtConst =
    let [<Literal>] S = 4
    let [<Literal>] M = 16
    let [<Literal>] L = 48
    let [<Literal>] XL = 256
    let [<Literal>] MaxPrefixLength = 10
    
    let trailingZeros (x: int): int =
        if x = 0 then 32
        else
            let mutable i = x
            let mutable n = 31
            let mutable y = 0
            y <- i <<< 16; if y <> 0 then n <- n - 16; i <- y
            y <- i <<< 8; if y <> 0 then n <- n - 8; i <- y
            y <- i <<< 4; if y <> 0 then n <- n - 4; i <- y
            y <- i <<< 2; if y <> 0 then n <- n - 2; i <- y
            n - ((i <<< 1) >>> 31)
    
[<Flags>]
type NodeFlags =
    | None = 0uy
    | N4   = 1uy
    | N16  = 2uy
    | N48  = 3uy
    | N256 = 4uy
    | Leaf = 8uy
    
[<AbstractClass;AllowNullLiteral>]
type internal ArtNode<'a>(flags: NodeFlags) =
    member __.Flags = flags
    member __.IsLeaf = flags.HasFlag(NodeFlags.Leaf)

and [<Sealed>] internal ArtLeaf<'a>(key: byte[], value: 'a) =
    inherit ArtNode<'a>(NodeFlags.Leaf)
    member inline __.Key = key
    member inline __.Value = value
    member inline __.IsMatch(s: ReadOnlySpan<byte>) = s.SequenceEqual(ReadOnlySpan<_> key)
    
and [<Sealed>] internal ArtBranch<'a>(flags: NodeFlags, childrenCount: byte, partialLength:int, partial:byte[], keys:byte[], children:ArtNode<'a>[]) =
    inherit ArtNode<'a>(flags)
    
    member inline __.ChildrenCount = childrenCount
    member inline __.PartialLength = partialLength
    member inline __.Partial = partial
    member inline __.Keys = keys
    member inline __.Children = children
    
    member this.FindChild(c: byte): ArtNode<'a> =
        match flags with
        | NodeFlags.N4 ->
            let rec loop i =
                if i >= int childrenCount then null
                elif keys.[i] = c then children.[i]
                else loop (i+1)
            loop 0
        | NodeFlags.N16 ->
            let bitfield =
                if Vector.IsHardwareAccelerated then
                    // compare all 16 keys at once using SIMD registers
                    let v1 = Vector128.Create(c)
                    let v2 = Vector128.Create(keys.[0], keys.[1], keys.[2], keys.[3], keys.[4], keys.[5], keys.[6], keys.[7], keys.[8],
                                              keys.[9], keys.[10], keys.[11], keys.[12], keys.[13], keys.[14], keys.[15])
                    let cmp = System.Runtime.Intrinsics.X86.Aes.CompareEqual(v1, v2)
                    let mask = (1 <<< int childrenCount) - 1
                    System.Runtime.Intrinsics.X86.Aes.MoveMask(cmp) &&& mask
                else
                    // fallback to native comparison
                    let mutable bitfield = 0
                    for i = 0 to 15 do
                        if keys.[i] = c then  bitfield <- bitfield ||| (1 <<< i)
                    let mask = (1 <<< int childrenCount) - 1
                    bitfield &&& mask
            if bitfield <> 0 then children.[ArtConst.trailingZeros bitfield] else null
        | NodeFlags.N48 ->
            let i = int keys.[int c]
            if i <> 0 then children.[i-1] else null
        | NodeFlags.N256 ->
            children.[int c]
        | _ -> raise (NotSupportedException "Flag type not supported for ArtNode")
        
    /// Returns a number of prefix bytes shared between given `key` and this node.
    member this.PrefixLength(key: ReadOnlySpan<byte>, depth: int): int =
        let len = min (min partialLength ArtConst.MaxPrefixLength) (key.Length - depth)
        let mutable i = 0
        let mutable cont = true
        while cont && i < len do
            if partial.[i] <> key.[depth + i] then
                cont <- false
            i <- i + 1
        i
        
[<RequireQualifiedAccess>]
module internal ArtNode =
    
    let inline leaf key value = ArtLeaf<_>(key, value)
    
    let inline node4 count partialLen = ArtBranch<_>(NodeFlags.N4, count, partialLen, Array.zeroCreate ArtConst.MaxPrefixLength, Array.zeroCreate ArtConst.S, Array.zeroCreate ArtConst.S)
    
    let inline node16 count partialLen = ArtBranch<_>(NodeFlags.N16, count, partialLen, Array.zeroCreate ArtConst.MaxPrefixLength, Array.zeroCreate ArtConst.S, Array.zeroCreate ArtConst.S)
    
    let inline node48 count partialLen = ArtBranch<_>(NodeFlags.N48, count, partialLen, Array.zeroCreate ArtConst.MaxPrefixLength, Array.zeroCreate ArtConst.S, Array.zeroCreate ArtConst.S)
    
    let inline node256 count partialLen = ArtBranch<_>(NodeFlags.N256, count, partialLen, Array.zeroCreate ArtConst.MaxPrefixLength, Array.zeroCreate ArtConst.S, Array.zeroCreate ArtConst.S)
    
    let rec min (n: ArtNode<'a>) =
        match n.Flags with
        | NodeFlags.Leaf -> n :?> ArtLeaf<'a>
        | NodeFlags.N4 | NodeFlags.N16 -> min (n :?> ArtBranch<'a>).Children.[0]
        | NodeFlags.N48 ->
            let branch = n :?> ArtBranch<'a>
            let mutable i = 0
            while branch.Keys.[i] <> 0uy do i <- i + 1
            i <- int (branch.Keys.[i] - 1uy)
            min branch.Children.[i]
        | NodeFlags.N256 ->
            let branch = n :?> ArtBranch<'a>
            let mutable i = 0
            while branch.Keys.[i] <> 0uy do i <- i + 1
            min branch.Children.[i]
        | _ -> failwith "not supported"
    
    let rec max (n: ArtNode<'a>) =
        match n.Flags with
        | NodeFlags.Leaf -> n :?> ArtLeaf<'a>
        | NodeFlags.N4 | NodeFlags.N16 ->
            let branch = (n :?> ArtBranch<'a>)
            min branch.Children.[int branch.ChildrenCount - 1]
        | NodeFlags.N48 ->
            let branch = n :?> ArtBranch<'a>
            let mutable i = 255
            while branch.Keys.[i] <> 0uy do i <- i - 1
            i <- int (branch.Keys.[i] - 1uy)
            max branch.Children.[i]
        | NodeFlags.N256 ->
            let branch = n :?> ArtBranch<'a>
            let mutable i = 255
            while branch.Keys.[i] <> 0uy do i <- i - 1
            max branch.Children.[i]
        | _ -> failwith "not supported"

/// Immutable Adaptative Radix Tree (ART), which can be treated like `Map<byte[], 'a>`.
/// Major advantage is prefix-based lookup capability.
type Art<'a> internal(count: int, root: ArtNode<'a>) =
    static member Empty: Art<'a> = Art<'a>(0, ArtNode.leaf [||] Unchecked.defaultof<_>)
    member this.Count = count
    member this.Search(key: ReadOnlySpan<byte>) =
        let mutable n = root
        let mutable depth = 0
        let mutable result = ValueNone
        while not (isNull n) do
            if n.IsLeaf then
                let leaf = n :?> ArtLeaf<'a>
                // Check if the expanded path matches
                if leaf.IsMatch key then
                    result <- ValueSome leaf.Value
                n <- null
            else
                let branch = n :?> ArtBranch<'a>
                // Bail if the prefix does not match
                if branch.PartialLength > 0 then
                    let prefixLen = branch.PrefixLength(key, depth)
                    if prefixLen <> Math.Min(ArtConst.MaxPrefixLength, branch.PartialLength) then
                        n <- null
                    depth <- depth + branch.PartialLength
                if depth >= key.Length then
                    n <- null
                // Recursively search
                n <- branch.FindChild(key.[depth])
                depth <- depth + 1
        result
        
    member __.Minimum =
        if count = 0 then raise (InvalidOperationException "Cannot return minimum key-value of empty prefix map")
        let leaf = (ArtNode.min root)
        (leaf.Key, leaf.Value)
        
    member __.Maximum = 
        if count = 0 then raise (InvalidOperationException "Cannot return maximum key-value of empty prefix map")
        let leaf = (ArtNode.max root)
        (leaf.Key, leaf.Value)
             
[<RequireQualifiedAccess>]
module Art =
    
    let empty<'a> = Art<'a>.Empty
    
    let inline min (map: Art<'a>) = map.Minimum
    
    let inline max (map: Art<'a>) = map.Maximum

    let ofSeq (seq: seq<string * 'a>): Art<'a> = failwith "not implemented"
    
    let ofMap (map: #seq<KeyValuePair<string, 'a>>): Art<'a> = failwith "not implemented"
    
    let add (key: string) (value: 'a) (map: Art<'a>): Art<'a> = failwith "not implemented"
    
    let tryFind (key: string) (map: Art<'a>): 'a voption = failwith "not implemented"
    
    let prefixed (prefix: string) (map: Art<'a>) = failwith "not implemented"
    
    let map (fn: string -> 'a -> 'b) (map: Art<'a>) = failwith "not implemented"
    
    let filter (fn: string -> 'a -> bool) (map: Art<'a>) = failwith "not implemented"
    