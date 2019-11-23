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
    member __.LongestCommonPrefix(other: ArtLeaf<'a>, depth: int) =
        let rec loop (a: byte[]) (b: byte[]) maxCmp depth i=
            if i < maxCmp then
                if a.[i] <> b.[i + depth] then i
                else loop a b maxCmp depth (i+1)
            else i
        let maxCmp = Math.Max(key.Length, other.Key.Length) - depth
        loop key other.Key maxCmp depth 0
    
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
        let len = Math.Min(Math.Min(partialLength, ArtConst.MaxPrefixLength), (key.Length - depth))
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
        
    /// Calculates the index at which the prefixes mismatch   
    let prefixMismatch(key: ReadOnlySpan<byte>) (depth: int) (n: ArtBranch<'a>) =
        let mutable len = Math.Min(Math.Min(n.PartialLength, ArtConst.MaxPrefixLength), (key.Length - depth))
        let mutable idx = -1
        let mutable i = 0
        while idx = -1 && i < len do
            if n.Partial.[i] <> key.[depth + i] then
                idx <- i
            i <- i + 1
        
        if idx <> -1 then idx
        elif n.PartialLength > ArtConst.MaxPrefixLength then
            // If the prefix is short we can avoid finding a leaf
            let leaf = min n
            let maxCmp = Math.Min(leaf.Key.Length, key.Length) - depth
            while idx = -1 && i < maxCmp do
                if leaf.Key.[depth + i] <> key.[depth + i] then
                    idx <- i
                i <- i + 1
            idx
        else -1
        
    let rec insert (n: ArtNode<'a>) (ref: ArtNode<'a> outref) (key: byte[]) (value: 'a) (depth: int) (old: int outref) =
        // If we are at a NULL node, inject a leaf
        if isNull n then
            ref <- leaf key value
            null
        elif n.IsLeaf then
            let l = n :?> ArtLeaf<'a>
            // Check if we are updating an existing value
            if l.IsMatch(ReadOnlySpan<_>(key)) then
                // replace existing key
                null
            else
                // New value, we must split the leaf into a node4
                let leaf' = leaf key value
                let longestPrefix = l.LongestCommonPrefix(leaf', depth)
                null
                
            (*
            art_leaf *l = LEAF_RAW(n);

            // Check if we are updating an existing value
            if (!leaf_matches(l, key, key_len, depth)) {
                *old = 1;
                void *old_val = l->value;
                l->value = value;
                return old_val;
            }

            // New value, we must split the leaf into a node4
            art_node4 *new_node = (art_node4* )alloc_node(NODE4);

            // Create a new leaf
            art_leaf *l2 = make_leaf(key, key_len, value);

            // Determine longest prefix
            int longest_prefix = longest_common_prefix(l, l2, depth);
            new_node->n.partial_len = longest_prefix;
            memcpy(new_node->n.partial, key+depth, min(MAX_PREFIX_LEN, longest_prefix));
            // Add the leafs to the new node4
            *ref = (art_node* )new_node;
            add_child4(new_node, ref, l->key[depth+longest_prefix], SET_LEAF(l));
            add_child4(new_node, ref, l2->key[depth+longest_prefix], SET_LEAF(l2));
            return NULL;            
            *)
            null
        else
            let branch = n :?> ArtBranch<'a>
            // Check if given node has a prefix
            if branch.PartialLength <> 0 then
                let prefixDiff = prefixMismatch (ReadOnlySpan<_> key) depth branch
                (*
                // Determine if the prefixes differ, since we need to split
                int prefix_diff = prefix_mismatch(n, key, key_len, depth);
                if ((uint32_t)prefix_diff >= n->partial_len) {
                    depth += n->partial_len;
                    goto RECURSE_SEARCH;
                }

                // Create a new node
                art_node4 *new_node = (art_node4* )alloc_node(NODE4);
                *ref = (art_node* )new_node;
                new_node->n.partial_len = prefix_diff;
                memcpy(new_node->n.partial, n->partial, min(MAX_PREFIX_LEN, prefix_diff));

                // Adjust the prefix of the old node
                if (n->partial_len <= MAX_PREFIX_LEN) {
                    add_child4(new_node, ref, n->partial[prefix_diff], n);
                    n->partial_len -= (prefix_diff+1);
                    memmove(n->partial, n->partial+prefix_diff+1,
                            min(MAX_PREFIX_LEN, n->partial_len));
                } else {
                    n->partial_len -= (prefix_diff+1);
                    art_leaf *l = minimum(n);
                    add_child4(new_node, ref, l->key[depth+prefix_diff], n);
                    memcpy(n->partial, l->key+depth+prefix_diff+1,
                            min(MAX_PREFIX_LEN, n->partial_len));
                }

                // Insert the new leaf
                art_leaf *l = make_leaf(key, key_len, value);
                add_child4(new_node, ref, key[depth+prefix_diff], SET_LEAF(l));
                return NULL;
                
                *)
                null
            else
                null
//                match branch.FindChild key.[depth] with
//                | null ->
//                    // No child, node goes within us
//                    let leaf = leaf key value
//                    //branch.AddChild(ref, key.[depth], leaf)
//                    null
//                | child -> insert child &child key value (depth+1) old
                
[<Struct>]
type ArtEnumerator<'a> internal(root: ArtNode<'a>) =
    interface IEnumerator<KeyValuePair<byte[], 'a>> with
        member this.Current: KeyValuePair<byte[], 'a> = failwith "not implemented"
        member this.Current: obj = failwith "not implemented"
        member this.MoveNext() = failwith "not implemented"
        member this.Reset() = failwith "not implemented"
        member this.Dispose() = ()
                

/// Immutable Adaptive Radix Tree (ART), which can be treated like `Map<byte[], 'a>`.
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
        
    member __.GetEnumerator() = new ArtEnumerator<'a>(root)
    interface IEnumerable<KeyValuePair<byte[], 'a>> with
        member this.GetEnumerator(): IEnumerator<KeyValuePair<byte[], 'a>> = upcast this.GetEnumerator()
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
    let inline min (map: Art<'a>): (byte[] * 'a) = map.Minimum
    
    /// Returns a maximum element of provided Adaptive Radix Tree.
    /// Fails with exception if `map` is empty.
    let inline max (map: Art<'a>): (byte[] * 'a) = map.Maximum

    /// Creates a new Adaptive Radix Tree out of provided tuple sequence.
    let ofSeq (seq: seq<byte[] * 'a>): Art<'a> = failwith "not implemented"
    
    /// Creates a new Adaptive Radix Tree out of provided key-value sequence.
    let ofMap (map: #seq<KeyValuePair<byte[], 'a>>): Art<'a> = failwith "not implemented"
    
    /// Inserts a new `value` under provided `key`, returning a new Adaptive Radix Tree in the result.
    /// If there was already another value under the provided `key`, it will be overriden.
    let add (key: ReadOnlySpan<byte>) (value: 'a) (map: Art<'a>): Art<'a> = failwith "not implemented"
    
    /// Removes an entry for a provided `key` returning a new Adaptive Radix Tree without that element.
    let remove (key: ReadOnlySpan<byte>) (map: Art<'a>): Art<'a> = failwith "not implemented"
    
    /// Inserts a new `value` under provided `key`, returning a new Adaptive Radix Tree in the result.
    /// If there was already another value under the provided `key`, it will be overriden.
    /// Unlike `Art.add`, this one takes byte array as a parameter and will not copy the key - it's up to
    /// user to guarantee that `key` byte array will not be changed. 
    let addUnsafe (key: byte[]) (value: 'a) (map: Art<'a>): Art<'a> = failwith "not implemented"
    
    /// Tries to find a a value under provided `key`.
    let tryFind (key: ReadOnlySpan<byte>) (map: Art<'a>): 'a voption = failwith "not implemented"
    
    /// Returns a sequence of key-value elements, which starts with a given `prefix`.
    let prefixed (prefix: byte[]) (map: Art<'a>): KeyValuePair<byte[], 'a> seq = failwith "not implemented"
    
    /// Maps all key-value entries of a given Adaptive Radix Tree into new values using function `fn`,
    /// returning a new ART map with modified values.
    let map (fn: byte[] -> 'a -> 'b) (map: Art<'a>): Art<'a> = failwith "not implemented"
    
    /// Filters all key-value entries of a given Adaptive Radix Tree into new values using predicate `fn`,
    /// returning a new ART map with only these entries, which satisfied given predicate.
    let filter (fn: byte[] -> 'a -> bool) (map: Art<'a>): Art<'a> = failwith "not implemented"
    
    /// Zips two maps together, combining the corresponding entries using provided function `fn`.
    /// Any element that was not found in either of the maps, will be omitted from the output.
    let intersect (fn: byte[] -> 'a -> 'b -> 'c) (a: Art<'a>) (b: Art<'b>): Art<'c> = failwith "not implemented"
    
    /// Combines two maps together, taking a union of their corresponding entries. If both maps have
    /// a value for the corresponding entry, a function 'fn` will be used to resolve conflict.
    let union (fn: byte[] -> 'a -> 'a -> 'a) (a: Art<'a>) (b: Art<'a>): Art<'a> = failwith "not implemented"
    