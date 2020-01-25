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
open System
open System
open System.Numerics
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Runtime.Intrinsics
open System.Runtime.Intrinsics.X86
open System.Text

#nowarn "9"

module internal ArtConst =
    let [<Literal>] Node4Size  = 4    // 0b00000000_00000100
    let [<Literal>] Node16Size  = 16   // 0b00000000_00010000
    let [<Literal>] Node48Size  = 48   // 0b00000000_00110000
    let [<Literal>] Node256Size = 256  // 0b00000001_00000000
    let [<Literal>] Node256 = 0b10000000_00000000
    let [<Literal>] Node48  = 0b01000000_00000000
    let [<Literal>] Node16  = 0b00100000_00000000
    let [<Literal>] Node4   = 0b00010000_00000000
    let [<Literal>] NodeMask   = 0b11111110_00000000
    
    /// Equivalent of C's __builtin_ctz
    let ctz (x: int): int =
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
    
    /// Equivalent of C's memmove 
    let memmove (array: 'a[]) (index: int) (count: int) =
        let mutable i = index + count
        let mutable j = index
        while i > index do
            array.[i] <- array.[j]
            i <- i - 1
            j <- j - 1
            
    let utf8 (s: string): ArraySegment<byte> =
        let array = Encoding.UTF8.GetBytes(s)
        ArraySegment<_>(array, 0, array.Length)
        
[<AbstractClass;AllowNullLiteral>]
type internal ArtNode<'a>() =
    member this.IsLeaf: bool = this :? ArtLeaf<'a>

and [<Sealed>] internal ArtLeaf<'a>(byteKey: byte[], key: string, value: 'a) as this =
    inherit ArtNode<'a>()
    [<DefaultValue(false)>] val mutable byteKey: byte[]
    [<DefaultValue(false)>] val mutable key: string
    [<DefaultValue(false)>] val mutable value: 'a
    do
        this.byteKey <- byteKey
        this.key <- key
        this.value <- value
    member inline this.IsMatch(s: string) = this.key = s
    member this.LongestCommonPrefix(other: ArtLeaf<'a>, depth: int) =
        let rec loop (a: byte[]) (b: byte[]) maxCmp depth i=
            if i < maxCmp then
                if a.[i] <> b.[i + depth] then i
                else loop a b maxCmp depth (i+1)
            else i
        let maxCmp = Math.Max(this.byteKey.Length, other.key.Length) - depth
        loop this.byteKey other.byteKey maxCmp depth 0
    
and [<Sealed;AllowNullLiteral>] internal ArtBranch<'a>(flags: int, count: int, partial: ArraySegment<byte>, keys:byte[], children:ArtNode<'a>[]) as this =
    inherit ArtNode<'a>()
    [<DefaultValue(false)>] val mutable childCount: int
    do
        this.childCount <- count
    member inline __.ChildrenCount
        with get() = this.childCount
        and set(value) = this.childCount <- value
    member inline __.Type = flags
    member inline __.PartialLength = partial.Count
    member inline __.Partial = partial
    member inline __.Keys = keys
    member inline __.Children = children    
    member inline internal this.Copy(newPartial) =
        ArtBranch<'a>(flags, count, newPartial, Array.copy keys, Array.copy children)        
    member inline internal this.Copy() =
        ArtBranch<'a>(flags, count, partial, Array.copy keys, Array.copy children)
    member this.FindChild(c: byte): ArtNode<'a> =
        let childrenCount = this.ChildrenCount
        match this.Type with
        | ArtConst.Node4 ->
            let rec loop i =
                if i >= int childrenCount then null
                elif keys.[i] = c then children.[i]
                else loop (i+1)
            loop 0
        | ArtConst.Node16 ->
            let bitfield =
                if Vector.IsHardwareAccelerated then
                    // compare all 16 keys at once using SIMD registers
                    let v1 = Vector128.Create(c)
                    use k = fixed keys
                    let v2 = Aes.LoadVector128 k
                    let cmp = Aes.CompareEqual(v1, v2)
                    let mask = (1 <<< int childrenCount) - 1
                    System.Runtime.Intrinsics.X86.Aes.MoveMask(cmp) &&& mask
                else
                    // fallback to native comparison
                    let mutable bitfield = 0
                    for i = 0 to 15 do
                        if keys.[i] = c then  bitfield <- bitfield ||| (1 <<< i)
                    let mask = (1 <<< int childrenCount) - 1
                    bitfield &&& mask
            if bitfield <> 0 then children.[ArtConst.ctz bitfield] else null
        | ArtConst.Node48 ->
            let i = int keys.[int c]
            if i <> 0 then children.[i-1] else null
        | ArtConst.Node256 ->
            children.[int c]
        | _ -> raise (NotSupportedException "Flag type not supported for ArtNode")
        
    /// Returns a number of prefix bytes shared between given `key` and this node.
    member this.PrefixLength(key: byte[], depth: int): int =
        let len = Math.Min(this.PartialLength, (key.Length - depth))
        let mutable i = 0
        let mutable cont = true
        while cont && i < len do
            if partial.[i] <> key.[depth + i] then
                cont <- false
            i <- i + 1
        i
        
    member this.AddChildUnsafe() = ()

        
[<RequireQualifiedAccess>]
module internal ArtNode =
    
    let inline leaf key byteKey value =
        ArtLeaf<'a>(byteKey, key, value)
    
    let inline node4 partial = ArtBranch<_>(ArtConst.Node4, 0, partial, Array.zeroCreate ArtConst.Node4Size, Array.zeroCreate ArtConst.Node4Size)
    
    let inline node16 count partial keys children =
        let keys' = Array.zeroCreate ArtConst.Node16Size
        let children' = Array.zeroCreate ArtConst.Node16Size
        Array.blit keys 0 keys' 0 count
        Array.blit children 0 children' 0 count
        ArtBranch<_>(ArtConst.Node16, count, partial, keys', children')
    
    let inline node48 count partial keys children =
        let keys' = Array.zeroCreate 256 // yes, 256 keys
        let children' = Array.zeroCreate ArtConst.Node48Size
        Array.blit keys 0 keys' 0 count
        Array.blit children 0 children' 0 count
        ArtBranch<_>(ArtConst.Node48, count, partial, keys', children')
    
    let inline node256 count partial keys children =
        let children' = Array.zeroCreate ArtConst.Node256Size
        Array.blit children 0 children' 0 count
        ArtBranch<_>(ArtConst.Node256, count, partial, [||], children')
    
    let rec min (n: ArtNode<'a>) =
        match n with
        | :? ArtLeaf<'a> as leaf -> leaf
        | :? ArtBranch<'a> as branch ->
            match branch.Type with
            | ArtConst.Node4 | ArtConst.Node16 -> min branch.Children.[0]
            | ArtConst.Node48 ->
                let mutable i = 0
                while branch.Keys.[i] <> 0uy do i <- i + 1
                i <- int (branch.Keys.[i] - 1uy)
                min branch.Children.[i]
            | ArtConst.Node256 ->
                let mutable i = 0
                while branch.Keys.[i] <> 0uy do i <- i + 1
                min branch.Children.[i]
            | _ -> failwith "not supported"
        | _ -> failwith "not supported"
    
    let rec max (n: ArtNode<'a>) =
        match n with
        | :? ArtLeaf<'a> as leaf -> leaf
        | :? ArtBranch<'a> as branch ->
            match branch.Type with
            | ArtConst.Node4 | ArtConst.Node16 ->
                min branch.Children.[int branch.ChildrenCount - 1]
            | ArtConst.Node48 ->
                let mutable i = 47
                while branch.Keys.[i] = 0uy do i <- i - 1
                i <- int (branch.Keys.[i] - 1uy)
                max branch.Children.[i]
            | ArtConst.Node256 ->
                let mutable i = 255
                while branch.Keys.[i] = 0uy do i <- i - 1
                max branch.Children.[i]
            | _ -> failwith "not supported"
        | _ -> failwith "not supported"
        
    /// Calculates the index at which the prefixes mismatch   
    let prefixMismatch(key: ArraySegment<byte>) (depth: int) (n: ArtBranch<'a>) =
        let mutable len = Math.Min(n.PartialLength, (key.Count - depth))
        let mutable idx = -1
        let mutable i = 0
        while idx = -1 && i < len do
            if n.Partial.[i] <> key.[depth + i] then
                idx <- i
            i <- i + 1
        idx
        
    let inline private addUnsafe256 (n: ArtBranch<'a>) (c: byte) (child: ArtNode<'a>): ArtNode<'a> =
        n.ChildrenCount <- n.ChildrenCount + 1
        n.Children.[int c] <- child
        upcast n
        
    let private addUnsafe48 (n: ArtBranch<'a>) (c: byte) (child: ArtNode<'a>): ArtNode<'a> =
        if n.ChildrenCount < 48 then
            let mutable pos = 0
            while not (isNull n.Children.[pos]) do pos <- pos + 1
            n.Children.[pos] <- child
            n.Keys.[int c] <- byte (pos + 1)
            n.ChildrenCount <- n.ChildrenCount + 1
            upcast n
        else
            let node' = node256 n.ChildrenCount n.Partial n.Keys n.Children
            addUnsafe256 node' c child
        
    let private addUnsafe16 (n: ArtBranch<'a>) (c: byte) (child: ArtNode<'a>): ArtNode<'a> =
        if n.ChildrenCount < 16 then
            let mask = (1uy <<< n.ChildrenCount) - 1uy;
            let bitfield =
                if Vector.IsHardwareAccelerated then
                    // compare all 16 keys at once using SIMD registers
                    let v1 = Vector128.Create(c)
                    use k = fixed n.Keys
                    let v2 = Aes.LoadVector128 k
                    let cmp = Aes.CompareEqual(v1, v2)
                    let mask = (1 <<< n.ChildrenCount) - 1
                    Aes.MoveMask(cmp) &&& mask
                else
                    // fallback to native comparison
                    let mutable bitfield = 0
                    for i = 0 to 15 do
                        if n.Keys.[i] = c then  bitfield <- bitfield ||| (1 <<< i)
                    let mask = (1 <<< n.ChildrenCount) - 1
                    bitfield &&& mask
            let idx =
                if bitfield = 0 then n.ChildrenCount
                else
                    let i = ArtConst.ctz bitfield
                    ArtConst.memmove n.Keys (i+1) (n.ChildrenCount-i)
                    ArtConst.memmove n.Children (i+1) (n.ChildrenCount-i)
                    i
                    
            n.Keys.[idx] <- c;
            n.Children.[idx] <- child
            n.ChildrenCount <- n.ChildrenCount + 1
            upcast n
        else
            let node' = node48 n.ChildrenCount n.Partial n.Keys n.Children
            addUnsafe48 node' c child
        
    let private addUnsafe4 (n: ArtBranch<'a>) (c: byte) (child: ArtNode<'a>): ArtNode<'a> =
        if n.ChildrenCount < 4 then
            let idx =
                if c < n.Keys.[0] then 0
                elif c < n.Keys.[1] then 0
                else 3
            // Shift to make space -> this should be memmove
            ArtConst.memmove n.Keys (idx+1) (n.ChildrenCount-idx)
            ArtConst.memmove n.Children (idx+1) (n.ChildrenCount-idx)
            // Insert element
            n.Keys.[idx] <- c
            n.Children.[idx] <- child
            n.ChildrenCount <- n.ChildrenCount + 1
            upcast n
        else
            let node' = node16 n.ChildrenCount n.Partial n.Keys n.Children
            addUnsafe16 node' c child
            
    let private addUnsafe (n: ArtBranch<'a>) (c: byte) (child: ArtNode<'a>): ArtNode<'a> =
        match n.Type with
        | ArtConst.Node4   -> addUnsafe4 n c child
        | ArtConst.Node16  -> addUnsafe16 n c child
        | ArtConst.Node48  -> addUnsafe48 n c child
        | ArtConst.Node256 -> addUnsafe256 n c child
        | _ -> null
                    
    let rec internal insert (n: ArtNode<'a>) (key: string) (byteKey: byte[]) (value: 'a) (depth: int): ArtNode<'a> =
        // If we are at a NULL node, inject a leaf
        if isNull n then
            upcast leaf key byteKey value
        elif n.IsLeaf then
            let l = n :?> ArtLeaf<'a>
            // Check if we are updating an existing value
            if l.IsMatch(key) then
                // replace existing key
                upcast leaf key byteKey value
            else
                // New value, we must split the leaf into a node4
                let l2 = leaf key byteKey value
                let longestPrefix = l.LongestCommonPrefix(l2, depth)
                let segment = ArraySegment<byte>(l2.byteKey, depth, longestPrefix)
                let node = node4 segment
                addUnsafe4 node l.byteKey.[depth+longestPrefix] l |> ignore
                addUnsafe4 node l2.byteKey.[depth+longestPrefix] l2 |> ignore
                upcast node
        else
            let branch = n :?> ArtBranch<'a>
            // Check if given node has a prefix
            let prefixDiff = prefixMismatch (ArraySegment<_>(byteKey, 0, byteKey.Length)) depth branch
            // Determine if the prefixes differ, since we may need to split
            if prefixDiff < branch.PartialLength then
                // we need to split this node in two
                let newPrefix = ArraySegment<_>(branch.Partial.Array, branch.Partial.Offset, branch.Partial.Count - prefixDiff + 1)
                let branch' = branch.Copy newPrefix
                let node' = node4 (ArraySegment(byteKey, branch.Partial.Offset, prefixDiff))
                addUnsafe4 node' branch'.Partial.[prefixDiff] branch' |> ignore
                ArtConst.memmove branch'.Partial.Array branch'.Partial.Offset (prefixDiff + 1)
                // Insert the new leaf
                let leaf = leaf key byteKey value
                addUnsafe4 node' byteKey.[depth+prefixDiff] leaf |> ignore
                upcast branch'
            else
                let branch' = branch.Copy()
                // prefix mismatch must be somewhere down the children tree
                match branch'.FindChild byteKey.[depth+branch'.PartialLength] with
                | null ->
                    // No child, node goes within us
                    let leaf = leaf key byteKey value
                    addUnsafe branch' byteKey.[depth+branch.PartialLength] leaf
                | child ->
                    insert child key byteKey value (depth+branch'.PartialLength+1)

/// Immutable Adaptive Radix Tree (ART), which can be treated like `Map<byte[], 'a>`.
/// Major advantage is prefix-based lookup capability.
type Art<'a> internal(count: int, root: ArtNode<'a>) =
    
    let rec iterate (n: ArtNode<'a>) = seq {
        let mutable cont = true
        while cont do
            if isNull n then cont <- false
            elif n.IsLeaf then
                let l = n :?> ArtLeaf<'a>
                yield KeyValuePair<_,_>(l.key, l.value)
            else
                let b = n :?> ArtBranch<'a>
                match b.Type with
                | ArtConst.Node4 ->
                    for i=0 to b.ChildrenCount-1 do
                        yield! iterate b.Children.[i]
                | ArtConst.Node16 ->
                    for i=0 to b.ChildrenCount-1 do
                        yield! iterate b.Children.[i]
                | ArtConst.Node48 ->
                    for i=0 to 255 do
                        let idx = b.Keys.[i]
                        if idx <> 0uy then
                            yield! iterate b.Children.[int (idx-1uy)]
                | ArtConst.Node256 ->
                    for i=0 to 255 do
                        let n' = b.Children.[i]
                        if not (isNull n') then
                            yield! iterate n'
                | _ -> raise(NotSupportedException (sprintf "Unknown node type %i" b.Type))
    }
    
    static member Empty: Art<'a> = Art<'a>(0, ArtNode.leaf "" [||] Unchecked.defaultof<_>)
    member this.Count = count
    member this.Search(phrase: string) =
        let byteKey = Encoding.UTF8.GetBytes phrase
        let mutable n = root
        let mutable depth = 0
        let mutable result = ValueNone
        while not (isNull n) do
            if n.IsLeaf then
                let leaf = n :?> ArtLeaf<'a>
                // Check if the expanded path matches
                if leaf.IsMatch phrase then
                    result <- ValueSome leaf.value
                n <- null
            else
                let branch = n :?> ArtBranch<'a>
                // Bail if the prefix does not match
                if branch.PartialLength > 0 then
                    let prefixLen = branch.PrefixLength(byteKey, depth)
                    if prefixLen <> branch.PartialLength then
                        n <- null
                    depth <- depth + branch.PartialLength
                if depth >= byteKey.Length then
                    n <- null
                // Recursively search
                n <- branch.FindChild(byteKey.[depth])
                depth <- depth + 1
        result
        
    member __.Minimum =
        if count = 0 then ValueNone
        else
            let leaf = (ArtNode.min root)
            ValueSome (leaf.key, leaf.value)
        
    member __.Maximum = 
        if count = 0 then ValueNone
        else 
            let leaf = (ArtNode.max root)
            ValueSome (leaf.key, leaf.value)
        
    member __.Add(key: string, value: 'a): Art<'a> =
        let byteKey = ArtConst.utf8 key
        let root' = ArtNode.insert root key byteKey.Array value 0 
        Art<'a>(count+1, root')
        
    member this.GetEnumerator() = (iterate root).GetEnumerator()
        
    interface IEnumerable<KeyValuePair<string, 'a>> with
        member this.GetEnumerator(): IEnumerator<KeyValuePair<string, 'a>> = this.GetEnumerator()
        member this.GetEnumerator(): System.Collections.IEnumerator = upcast this.GetEnumerator()
        
                 
[<RequireQualifiedAccess>]
module Art =
    
    /// Returns a new empty Adaptive Radix Tree.
    let empty<'a> = Art<'a>.Empty
    
    /// Checks if provided `map` is empty.
    let inline isEmpty (map: Art<'a>): bool = map.Count = 0
    
    /// Returns a number of entries stored in a given `map`.
    let inline count (map: Art<'a>): int = map.Count
    
    /// Returns a minimum element of provided Adaptive Radix Tree.
    /// Fails with exception if `map` is empty.
    let inline min (map: Art<'a>): (string * 'a) voption = map.Minimum
    
    /// Returns a maximum element of provided Adaptive Radix Tree.
    /// Fails with exception if `map` is empty.
    let inline max (map: Art<'a>): (string * 'a) voption = map.Maximum

    /// Inserts a new `value` under provided `key`, returning a new Adaptive Radix Tree in the result.
    /// If there was already another value under the provided `key`, it will be overriden.
    let add (key: string) (value: 'a) (map: Art<'a>): Art<'a> = map.Add(key, value)
    
    /// Creates a new Adaptive Radix Tree out of provided tuple sequence.
    let ofSeq (seq: seq<string * 'a>): Art<'a> =
        let mutable a = empty
        for (key, value) in seq do
            a <- add key value a
        a
    
    /// Creates a new Adaptive Radix Tree out of provided key-value sequence.
    let ofMap (map: #seq<KeyValuePair<string, 'a>>): Art<'a> =
        let mutable a = empty
        for kv in map do
            a <- add kv.Key kv.Value a
        a

    /// Removes an entry for a provided `key` returning a new Adaptive Radix Tree without that element.
    let remove (key: string) (map: Art<'a>): Art<'a> = failwith "not implemented"
    
    /// Tries to find a a value under provided `key`.
    let tryFind (key: string) (map: Art<'a>): 'a voption = map.Search key
    
    /// Returns a sequence of key-value elements, which starts with a given `prefix`.
    let prefixed (prefix: string) (map: Art<'a>): KeyValuePair<string, 'a> seq = failwith "not implemented"
    
    /// Maps all key-value entries of a given Adaptive Radix Tree into new values using function `fn`,
    /// returning a new ART map with modified values.
    let map (fn: string -> 'a -> 'b) (map: Art<'a>): Art<'b> =
        let mutable b = empty
        if isEmpty map then b
        else
            for kv in map do
                b <- add kv.Key (fn kv.Key kv.Value) b
            b
    
    /// Filters all key-value entries of a given Adaptive Radix Tree into new values using predicate `fn`,
    /// returning a new ART map with only these entries, which satisfied given predicate.
    let filter (fn: string -> 'a -> bool) (map: Art<'a>): Art<'a> =
        if isEmpty map then map
        else
            //TODO: optimize case, when all map elements satisfy predicate - we can return old map then
            let mutable filtered = empty
            for kv in map do
                if fn kv.Key kv.Value then
                    filtered <- add kv.Key kv.Value filtered
            filtered
    
    /// Zips two maps together, combining the corresponding entries using provided function `fn`.
    /// Any element that was not found in either of the maps, will be omitted from the output.
    let intersect (fn: string -> 'a -> 'b -> 'c) (a: Art<'a>) (b: Art<'b>): Art<'c> =
        let inline compare (x: string) (y: string) = StringComparer.Ordinal.Compare(x, y)
        let mutable c = Art<'c>.Empty
        if isEmpty a || isEmpty b then c 
        else
            let mutable ea = a.GetEnumerator()
            let mutable eb = b.GetEnumerator()
            let mutable inProgress = ea.MoveNext() && eb.MoveNext()
            while inProgress do
                let kva = ea.Current
                let kvb = eb.Current
                match sign (compare kva.Key kvb.Key) with
                | 0 ->
                    let value' = fn kva.Key kva.Value kvb.Value
                    c <- add kva.Key value' c
                    inProgress <- ea.MoveNext() && eb.MoveNext()
                | 1 ->
                    inProgress <- eb.MoveNext()
                | -1 ->
                    inProgress <- ea.MoveNext()
            c
    
    /// Combines two maps together, taking a union of their corresponding entries. If both maps have
    /// a value for the corresponding entry, a function 'fn` will be used to resolve conflict.
    let union (fn: string -> 'a -> 'a -> 'a) (a: Art<'a>) (b: Art<'a>): Art<'a> = 
        let inline compare (x: string) (y: string) = StringComparer.Ordinal.Compare(x, y)
        let mutable c = Art<'a>.Empty
        if isEmpty a || isEmpty b then c 
        else
            let mutable ea = a.GetEnumerator()
            let mutable eb = b.GetEnumerator()
            let mutable inProgress = ea.MoveNext() && eb.MoveNext()
            while inProgress do
                let kva = ea.Current
                let kvb = eb.Current
                match sign (compare kva.Key kvb.Key) with
                | 0 ->
                    let value' = fn kva.Key kva.Value kvb.Value
                    c <- add kva.Key value' c
                    inProgress <- ea.MoveNext() && eb.MoveNext()
                | 1 ->
                    c <- add kvb.Key kvb.Value c
                    inProgress <- eb.MoveNext()
                | -1 ->
                    c <- add kva.Key kva.Value c
                    inProgress <- ea.MoveNext()
            c
    