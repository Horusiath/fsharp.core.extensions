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
open System
open System.Numerics
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Runtime.Intrinsics
open System.Runtime.Intrinsics.X86
open System.Text

#nowarn "9"
(*
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
    let inline memmove (dst: Span<'a>) (src: ReadOnlySpan<'a>) =
        src.CopyTo(dst)
        
[<AbstractClass;AllowNullLiteral>]
type internal ArtNode<'a>() =
    member this.IsLeaf: bool = this :? ArtLeaf<'a>

and [<Sealed>] internal ArtLeaf<'a>(key: ArraySegment<byte>, value: 'a) as this =
    inherit ArtNode<'a>()
    [<DefaultValue(false)>] val mutable key: ArraySegment<byte>
    [<DefaultValue(false)>] val mutable value: 'a
    do
        this.key <- key
        this.value <- value
    member inline this.IsMatch(s: ReadOnlySpan<byte>) = s.SequenceEqual(ReadOnlySpan.ofSegment this.key)
    member this.LongestCommonPrefix(other: ArtLeaf<'a>, depth: int) =
        let rec loop (a: byte[]) (b: byte[]) maxCmp depth i=
            if i < maxCmp then
                if a.[i] <> b.[i + depth] then i
                else loop a b maxCmp depth (i+1)
            else i
        let maxCmp = Math.Max(this.key.Count, other.key.Count) - depth
        loop this.key other.key maxCmp depth 0
    
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
        
    /// Checks if byte key for provided leaf starts with given prefix.
    let inline leafPrefixMatches (n: ArtLeaf<'a>) (prefix: ArraySegment<byte> inref) =
        if n.byteKey.Length < prefix.Count then false
        else ReadOnlySpan(n.byteKey, 0, prefix.Count).SequenceEqual(ReadOnlySpan(prefix.Array, prefix.Offset, prefix.Count))
        
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
                    let size = n.ChildrenCount - i
                    let keys = Span<_>(n.Keys, i+1, size)
                    let children = Span<_>(n.Children, i+1, size)
                    ArtConst.memmove keys (ReadOnlySpan<_>(n.Keys, i, size))
                    ArtConst.memmove children (ReadOnlySpan<_>(n.Children, i, size))
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
            let size = n.ChildrenCount-idx
            ArtConst.memmove (Span(n.Keys, idx+1, size)) (ReadOnlySpan(n.Keys, idx, size))
            ArtConst.memmove (Span(n.Children, idx+1, size)) (ReadOnlySpan(n.Children, idx, size))
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
                ArtConst.memmove (Span(branch'.Partial.Array, prefixDiff + 1, branch'.PartialLength)) (ReadOnlySpan(branch'.Partial.Array, prefixDiff, branch'.PartialLength))
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
                    
    let removeChild256 (c: byte) (n: ArtBranch<'a> byref) =
        n.Children.[int c] <- null 
        n.childCount <- n.childCount - 1
        // Resize to a node48 on underflow, not immediately to prevent
        // trashing if we sit on the 48/49 boundary
        if n.childCount = 47 then
            let n' = ArtBranch<'a>(ArtConst.Node48, n.childCount, n.Partial, Array.zeroCreate 256, Array.zeroCreate 48)
            let mutable pos = 0
            for i=0 to 255 do
                let child = n.Children.[i]
                if not (isNull child) then
                    n'.Children.[pos] <- child
                    n'.Keys.[i] <- byte (pos + i)
                    pos <- pos + 1
            n <- n'
            
    let removeChild48 (c: byte) (n: ArtBranch<'a> byref) =
        let mutable pos = n.Keys.[int c]
        n.Keys.[int c] <- Unchecked.defaultof<_>
        n.childCount <- n.childCount - 1
        
        if n.childCount = 12 then
            let n' = ArtBranch<'a>(ArtConst.Node48, n.childCount, n.Partial, Array.zeroCreate 16, Array.zeroCreate 16)
            let mutable child = 0
            for i=0 to 255 do
                pos <- n.Keys.[i]
                if pos <> 0uy then
                    n'.Keys.[child] <- byte i
                    n'.Children.[child] <- n.Children.[int pos-1]
                    child <- child + 1
            n <- n'
                    
    let removeChild16 (c: byte) (n: ArtBranch<'a> byref) pos =
        let size = n.ChildrenCount - 1 - pos
        ArtConst.memmove (Span(n.Keys, pos, size)) (ReadOnlySpan(n.Keys, pos+1, size))
        ArtConst.memmove (Span(n.Children, pos, size)) (ReadOnlySpan(n.Children, pos+1, size))
        n.childCount <- n.childCount - 1
        if n.childCount = 3 then
           let n' = node4 n.Partial
           //TODO: SIMD vectorize?
           n'.Keys.[0] <- n.Keys.[0]
           n'.Keys.[1] <- n.Keys.[1]
           n'.Keys.[2] <- n.Keys.[2]
           n'.Keys.[3] <- n.Keys.[3]
           n'.Children.[0] <- n.Children.[0]
           n'.Children.[1] <- n.Children.[1]
           n'.Children.[2] <- n.Children.[2]
           n'.Children.[3] <- n.Children.[3]
           n <- n'           
        
    let removeChild4 (node: ArtNode<'a> byref) pos =
        let n = node :?> ArtBranch<_>
        let size = n.ChildrenCount - 1 - pos
        ArtConst.memmove (Span(n.Keys, pos, size)) (ReadOnlySpan(n.Keys, pos+1, size))
        ArtConst.memmove (Span(n.Children, pos, size)) (ReadOnlySpan(n.Children, pos+1, size))
        n.childCount <- n.childCount - 1        
        // Remove nodes with only a single child
        if n.childCount = 1 then
            let child = n.Children.[0]
            if child.IsLeaf then
                node <- child
            else
                // Concatenate the prefixes
                let b = child :?> ArtBranch<_>
                let arr = Array.zeroCreate (n.PartialLength + b.PartialLength)
                Array.blit n.Partial.Array n.Partial.Offset arr 0 n.PartialLength
                Array.blit b.Partial.Array b.Partial.Offset arr n.PartialLength b.PartialLength
                node <- b.Copy(ArraySegment(arr, 0, arr.Length))
                
    let removeChild (n: ArtBranch<'a> byref) c pos =
        match n.Type with
        | ArtConst.Node4   -> removeChild4 &n pos
        | ArtConst.Node16  -> removeChild16 c &n pos
        | ArtConst.Node48  -> removeChild48 c &n
        | ArtConst.Node256 -> removeChild256 c &n
                
    let rec remove (n: ArtNode<'a> byref) (key: byte[]) (depth: int): ArtNode<'a> =
        if isNull n then null
        elif n.IsLeaf then // Handle hitting a leaf node
            let l = n :?> ArtLeaf<'a>
            if l.IsMatch key then
                n <- null
                l
            else null
        else
            let b = n :?> ArtBranch<'a>
            let prefix = b.PrefixLength(key, depth)
            if prefix <> b.PartialLength then null
            else
                let depth' = depth + b.PartialLength
                let child = b.FindChild(key.[depth'])
                if isNull child then null
                elif child.IsLeaf then // If the child is leaf, delete from this node
                    let l = child :?> ArtLeaf<'a>
                    if l.IsMatch key then
                        removeChild n child depth 
                    else null
                else remove child key depth'
            
            
                                
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
        
    let iteratePrefix (prefix: ArraySegment<byte>) (node: ArtNode<'a>) = seq {
        let mutable prefixLength = 0
        let mutable depth = 0
        let mutable n = node
        while not (isNull n) do
            if n.IsLeaf then
                let l = n :?> ArtLeaf<'a>
                if ArtNode.leafPrefixMatches l &prefix then
                    yield KeyValuePair<_,_>(l.key, l.value)
                n <- null
            else
                let b = n :?> ArtBranch<'a>
                if depth = prefix.Count then
                    // If the depth matches the prefix, we need to handle this node
                    let l = ArtNode.min n
                    if ArtNode.leafPrefixMatches l &prefix then
                        yield! iterate l
                    n <- null
                elif b.PartialLength <> 0 then
                    // Bail if the prefix does not match
                    prefixLength <- Math.Min(b.PartialLength, ArtNode.prefixMismatch prefix depth b)
                    if prefixLength = 0 then
                        n <- null
                    elif depth + prefixLength = prefix.Count then
                        yield! iterate b
                        n <- null
                    else depth <- depth + b.PartialLength // if there is a full match, go deeper
                if not (isNull n) then
                    let c = prefix.Array.[prefix.Offset + depth]
                    n <- b.FindChild c
                    depth <- depth + 1
    }
    
    static member Empty: Art<'a> = Art<'a>(0, ArtNode.leaf "" [||] Unchecked.defaultof<_>)
    member this.Count = count
    member this.Search(byteKey: ReadOnlySpan<byte>) =
        let mutable n = root
        let mutable depth = 0
        let mutable result = ValueNone
        while not (isNull n) do
            if n.IsLeaf then
                let leaf = n :?> ArtLeaf<'a>
                // Check if the expanded path matches
                if leaf.IsMatch byteKey then
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
        
    member __.Add(key: ArraySegment<byte>, value: 'a): Art<'a> =
        let byteKey = ArtConst.utf8 key
        let root' = ArtNode.insert root key byteKey.Array value 0 
        Art<'a>(count+1, root')
        
    member this.Remove(key: ReadOnlySpan<byte>): Art<'a> =
        let byteKey = ArtConst.utf8 key
        let root' = ArtNode.remove root byteKey.Array 0
        if isNull root' then this else Art<'a>(count-1, root')
        
    member this.GetEnumerator() = (iterate root).GetEnumerator()
    member this.Prefixed(prefix: ReadOnlySpan<byte>) =
        let byteKey = ArtConst.utf8 prefix
        iteratePrefix byteKey root
        
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
    let add (key: ArraySegment<byte>) (value: 'a) (map: Art<'a>): Art<'a> = map.Add(key, value)
    
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
    let inline remove (key: ReadOnlySpan<byte>) (map: Art<'a>): Art<'a> = map.Remove(key)
    
    /// Tries to find a a value under provided `key`.
    let inline tryFind (key: ReadOnlySpan<byte>) (map: Art<'a>): 'a voption = map.Search key
    
    /// Returns a sequence of key-value elements, which starts with a given `prefix`.
    let inline prefixed (prefix: ReadOnlySpan<byte>) (map: Art<'a>): KeyValuePair<string, 'a> seq = map.Prefixed(prefix)
    
    /// Maps all key-value entries of a given Adaptive Radix Tree into new values using function `fn`,
    /// returning a new ART map with modified values.
    let map (fn: ArraySegment<byte> -> 'a -> 'b) (map: Art<'a>): Art<'b> =
        let mutable b = empty
        if isEmpty map then b
        else
            for kv in map do
                b <- add kv.Key (fn kv.Key kv.Value) b
            b
    
    /// Filters all key-value entries of a given Adaptive Radix Tree into new values using predicate `fn`,
    /// returning a new ART map with only these entries, which satisfied given predicate.
    let filter (fn: ArraySegment<byte> -> 'a -> bool) (map: Art<'a>): Art<'a> =
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
    let intersect (fn: ArraySegment<byte> -> 'a -> 'b -> 'c) (a: Art<'a>) (b: Art<'b>): Art<'c> =
        let compare (x: ArraySegment<byte>) (y: ArraySegment<byte>) =
            let mutable i = 0
            let mutable cont = i < x.Count && i < y.Count
            let mutable cmp = 0
            while cont do
                cmp <- x.Array.[x.Offset+i].CompareTo(y.Array.[y.Offset+i])
                if cmp <> 0 then cont <- false
                else
                    i    <- i+1
                    cont <- (i < x.Count && i < y.Count)
            if cmp = 0 then
                if i >= x.Count then 1 else -1
            else cmp
              
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
    let union (fn: ArraySegment<byte> -> 'a -> 'a -> 'a) (a: Art<'a>) (b: Art<'a>): Art<'a> =
        let compare (x: ArraySegment<byte>) (y: ArraySegment<byte>) =
            let mutable i = 0
            let mutable cont = i < x.Count && i < y.Count
            let mutable cmp = 0
            while cont do
                cmp <- x.Array.[x.Offset+i].CompareTo(y.Array.[y.Offset+i])
                if cmp <> 0 then cont <- false
                else
                    i    <- i+1
                    cont <- (i < x.Count && i < y.Count)
            if cmp = 0 then
                if i >= x.Count then 1 else -1
            else cmp
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
*)