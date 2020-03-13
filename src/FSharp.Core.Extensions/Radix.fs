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

open FSharp.Core
open System
open System.Collections
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

module internal Utils =
    
    /// Determine index, where two span's starts to differ from each other.
    let splitPoint (a: ReadOnlySpan<char>) (b: ReadOnlySpan<char>): int =
        let length = Math.Min(a.Length, b.Length)
        let mutable i: int = 0
        let mutable next = true
        while next && i < length do
            if a.[i] = b.[i] then i <- i + 1
            else next <- false
        i
        
    /// Modify the source array, by pushing all elements on the right of index one position on the right.
    let splitAt index (src: 'a[]) =
        let toWrite = src.AsSpan()
        let after = ReadOnlySpan(src, index, 1)
        after.CopyTo(toWrite)
        
    /// Returns an index at which a given `value` should be inserted
    /// within sorted `array`, it can be equal to array's length.
    let search value (array: 'a[]) arrayLen =
        let mutable lo = 0
        let mutable hi = arrayLen
        while lo < hi do
            let h = (lo+hi) >>> 1
            if array.[h] < value then lo <- h + 1
            else hi <- h
        lo

[<AbstractClass;AllowNullLiteral>]
type internal RadixNode<'a>() =
    abstract IsEmpty: bool
    abstract Count: int
    abstract Min: RadixLeaf<'a>
    abstract Max: RadixLeaf<'a>
    abstract Copy: unit -> RadixNode<'a>

and [<Sealed;AllowNullLiteral>] internal RadixLeaf<'a> (key: string, value: 'a) =
    inherit RadixNode<'a>()
    let mutable value = value
    member inline _.Key = key
    member _.Value with get () = value and set (value') = value <- value'
    override _.IsEmpty = false
    override _.Count = 1
    override this.Min = this
    override this.Max = this
    override _.Copy() = upcast RadixLeaf<'a>(key, value)
    member this.Equals(other: RadixLeaf<'a>) =
        if isNull other then false
        else key = other.Key && EqualityComparer.Default.Equals(value, other.Value)
    override this.Equals(o: obj) =
        match o with
        | :? RadixLeaf<'a> as other -> this.Equals(other)
        | _ -> false
    override this.GetHashCode() = key.GetHashCode() ^^^ EqualityComparer.Default.GetHashCode(value)
    override this.ToString() =
        if obj.ReferenceEquals(value, null) then
            String.Concat("(", key, ", <null>)")
        else
            String.Concat("(", key, ", ", value.ToString(), ")")
    /// Inserts a given `other` leaf into current one, splitting it in two at precomputed offset
    /// (both leafs share key prefixes up to that offset). Returns a new node, that contains
    /// both leafs as its children.
    member this.Insert(other: RadixLeaf<'a>, offset: int, count: int): RadixNode<'a> =
        if other.Key = this.Key then
            // if both keys are equal we don't split, just do straight value replace
            upcast other
        else
            let total = offset + count
            let prefix = this.Key.Substring(offset, count)
            let res = RadixBranch<'a>.Create(2, prefix, Array.zeroCreate 2, Array.zeroCreate 2)
            let a = if this.Key.Length > total then this.Key.[total] else char 0
            let b = if other.Key.Length > total then other.Key.[total] else char 0
            // direct equality between `a` and `b` should never happen (as it should be counted into `prefix`)
            if a > b then
               res.keys.[0] <- b
               res.keys.[1] <- a
               res.children.[0] <- upcast other
               res.children.[1] <- upcast this
            else 
               res.keys.[1] <- b
               res.keys.[0] <- a
               res.children.[1] <- upcast other
               res.children.[0] <- upcast this                
            upcast res
    interface IEquatable<RadixLeaf<'a>> with
        member this.Equals(other) = this.Equals(other)
    
and [<Sealed;AllowNullLiteral>] internal RadixBranch<'a>() =
    inherit RadixNode<'a>()
    /// While keys/children fields operate on capacity-based arrays,
    /// the actual length (number of elements inside them) is determined
    /// by a `childrenCount` field.
    [<DefaultValue>] val mutable childrenCount: int
    /// If branch is shared it means, it's already part of `Radix<'a>` tree.
    /// Otherwise it's being currently mutated as part of `RadixBuilder<'a>`
    /// type, which is safe as it's done via copy and owned privately by
    /// the builder itself.
    [<DefaultValue>] val mutable shared: bool
    /// The common prefix shared between each of the children. Prefix is
    /// segmented over many branches if necessary - this field only contains
    /// part unique for that branch. 
    [<DefaultValue>] val mutable prefix: string
    /// Binary sorted array determining first character different between
    /// each of the `children`.
    [<DefaultValue>] val mutable keys: char[]
    /// Binary sorted list of current branch children.
    [<DefaultValue>] val mutable children: RadixNode<'a>[]
                    
    static member Create(childrenCount: int, prefix: string, keys: char[], children: RadixNode<'a>[]): RadixBranch<'a> =
        let n = RadixBranch<'a>()
        n.childrenCount <- childrenCount
        n.prefix <- prefix
        n.keys <- keys
        n.children <- children
        n
    static member inline Create(capacity: int) = RadixBranch<'a>.Create(0, "", Array.zeroCreate capacity, Array.zeroCreate capacity)
    override this.IsEmpty = this.childrenCount <> 0
    override this.Count =
        let mutable sum = 0
        for i=0 to this.childrenCount-1 do
            sum <- sum + this.children.[i].Count
        sum
    override this.Min = if this.childrenCount = 0 then null else this.children.[0].Min
    override this.Max = if this.childrenCount = 0 then null else this.children.[this.childrenCount-1].Max
    override this.Copy() =
        if this.shared then
            upcast RadixBranch<'a>.Create(this.childrenCount, this.prefix, Array.copy this.keys, Array.copy this.children)
        else upcast this
    member this.Equals(other: RadixBranch<'a>): bool =
        if obj.ReferenceEquals(this, other) then true
        elif obj.ReferenceEquals(other, null) then false
        elif this.childrenCount <> other.childrenCount then false
        elif this.prefix <> other.prefix then false
        else
            let s1 = ReadOnlySpan(this.keys)
            let s2 = ReadOnlySpan(other.keys)
            if s1.SequenceEqual(s2) then
                let mutable i = 0
                let mutable res = true
                while res && i < this.childrenCount do
                   res <- this.children.[i] = other.children.[i]
                   i <- i + 1
                res
            else false
    override this.Equals(o: obj) =
        match o with
        | :? RadixBranch<'a> as other -> this.Equals(other)
        | _ -> false
    override this.GetHashCode() =
        if this.childrenCount = 0 then 0
        else 
            let h = HashCode()
            h.Add(this.prefix)
            let mutable i = 0
            while i < this.childrenCount do
                h.Add(this.children.[i])
                i <- i + 1
            h.ToHashCode()
            
    /// Returns index of the child, which contains a provided prefix. `consumed` informs
    /// how much of a prefix is shared with that child. If none of the children were valid
    /// (`consumed` is 0), it returns an index, at which new child should be placed.
    member this.FindChild(prefix: ReadOnlySpan<char>, consumed: int outref): int =
        let keySpan = this.prefix.AsSpan()
        consumed <- Utils.splitPoint keySpan prefix
        if consumed = prefix.Length then 0 // if we fit entire thing, we need to place current leaf at position 0
        else
            consumed <- Math.Max(consumed, 0)
            let c = prefix.[consumed]
            Utils.search c this.keys this.childrenCount
                
    /// Returns a first descendant, which will contain all nodes starting with a given prefix.        
    member this.FindDescendant(prefix: ReadOnlySpan<char>, exactMatch: bool) : RadixNode<'a> =
        let mutable node = this :> RadixNode<'a>
        let mutable depth = 0
        let mutable prefix = prefix
        let mutable cont = prefix.Length <> 0
        while cont do
            match node with
            | :? RadixBranch<'a> as branch ->
                let (index, consumed) = branch.FindChild(prefix)
                if consumed = 0 || index = branch.childrenCount then
                    node <- null
                    cont <- false // we didn't consume prefix or none of the children match, we should stop
                else
                    prefix <- prefix.Slice(consumed)
                    if prefix.Length = 0 then
                        // we consumed entire prefix on this node, all children will match
                        cont <- false
                    else
                        node <- (branch.children.[index])
                        depth <- depth + consumed
            | :? RadixLeaf<'a> as leaf ->
                let leafSegment = leaf.Key.AsSpan().Slice(depth)
                cont <- false
                if exactMatch then
                    if not(leafSegment.SequenceEqual(prefix)) then
                        node <- null
                else if not(leafSegment.StartsWith(prefix)) then
                    node <- null
        node
        
    member this.InsertAt(index: int, leaf: RadixLeaf<'a>, key: char): unit =
        if this.childrenCount = this.keys.Length then
            // children table is full, resize to 150% of previous size (enlarge by half)
            let newSize = this.childrenCount * 3 / 2
            let keys' = Array.zeroCreate newSize
            Array.blit this.keys 0 keys' 0 this.keys.Length
            this.keys <- keys'
            let children' = Array.zeroCreate newSize
            Array.blit this.children 0 children' 0 this.children.Length
            this.children <- children'
            
        if index < this.childrenCount then
            // index is in the middle of existing elements, we need to make a space for the new element
            Utils.splitAt index this.keys
            Utils.splitAt index this.children            
        
        this.keys.[index] <- key
        this.children.[index] <- upcast leaf
        this.childrenCount <- this.childrenCount + 1
    member this.SplitAt(offset: int): RadixBranch<'a> =
        let prefix = this.prefix.Substring(0, offset)        
        let sufix = this.prefix.Substring(offset)
        let child = RadixBranch<'a>.Create(this.childrenCount, sufix, Array.copy this.keys, Array.copy this.children)
        this.prefix <- prefix
        this.childrenCount <- 1
        Array.Fill(this.keys, Unchecked.defaultof<_>)
        Array.Fill(this.children, Unchecked.defaultof<_>)
        this.keys.[0] <- sufix.[0]
        this.children.[0] <- upcast child
        this
        
    member this.Freeze() =
        this.shared <- true
        let mutable i = 0
        while i < this.childrenCount do
            match this.children.[i] with
            | :? RadixBranch<'a> as child when not child.shared -> child.Freeze()
            | _ -> ()
            i <- i + 1
    member private this.ToString(sb: Text.StringBuilder, intend: int) =
        let tabs = Span.stackalloc(intend)
        tabs.Fill '\t'
        let pad: ReadOnlySpan<char> = Span.op_Implicit tabs
        sb.Append(pad).Append("Branch('").Append(this.prefix).AppendLine("')") |> ignore
        let mutable i = 0
        while i < this.childrenCount do
            match this.children.[i] with
            | :? RadixBranch<'a> as branch -> branch.ToString(sb, intend+1)
            | :? RadixLeaf<'a> as leaf ->
                let tabs = Span.stackalloc(intend+1)
                tabs.Fill '\t'
                let pad: ReadOnlySpan<char> = Span.op_Implicit tabs
                sb.Append(pad).Append("Leaf(").Append(leaf.Key).Append(": ").Append(leaf.Value.ToString()).AppendLine(")") |> ignore
            | null -> () 
            i <- i + 1
    override this.ToString() =
        let sb = Text.StringBuilder()
        this.ToString(sb, 0)
        sb.ToString()
    interface IEquatable<RadixBranch<'a>> with
        member this.Equals(other) = this.Equals(other)

[<Struct;CustomEquality;NoComparison>]
type Radix<'a> internal (root: RadixBranch<'a>) =
    static member Empty: Radix<'a> = Radix(null)
    static member OfSeq(s: #seq<KeyValuePair<string, 'a>>, sizeHint: int) =
        let root = RadixBranch<'a>.Create(sizeHint)
        let builder = RadixBuilder<'a>(root)
        for e in s do
            builder.Add(e.Key, e.Value)
        printfn "%O" root
        builder.ToImmutable()
    member inline internal this.Root = root
    member _.IsEmpty = isNull root || root.IsEmpty
    member _.Count = if isNull root then 0 else root.Count
    member _.Min: KeyValuePair<string, 'a> voption =
        let leaf = if isNull root then null else root.Min
        if isNull leaf then ValueNone else ValueSome (KeyValuePair<string,'a>(leaf.Key, leaf.Value))
    member _.Max: KeyValuePair<string, 'a> voption =
        let leaf = if isNull root then null else root.Max
        if isNull leaf then ValueNone else ValueSome (KeyValuePair<string,'a>(leaf.Key, leaf.Value))
    member private this.ToBuilder() =
        let root' =
            if isNull root
            then RadixBranch<'a>.Create(4)
            else downcast root.Copy()
        RadixBuilder<'a> root'
    member this.Add(key: string, value: 'a) =
        let builder = this.ToBuilder()
        builder.Add(key, value)
        builder.ToImmutable()        
    member this.AddRange(entries: (string * 'a) seq) =
        let builder = this.ToBuilder()
        for (key,value) in entries do
            builder.Add(key, value)
        builder.ToImmutable()
    member internal this.Remove(key: ReadOnlySpan<char>, node: RadixLeaf<'a> outref): Radix<'a> =
        let builder = this.ToBuilder()
        if builder.Remove(key, &node) then builder.ToImmutable() else this
    member internal this.FindLeaf(key: ReadOnlySpan<char>) : RadixLeaf<'a> =
        match root.FindDescendant(key, true) with
        | :? RadixLeaf<'a> as leaf -> leaf
        | _ -> null
    member this.Prefixed(key: string) =
        let start =
            if key = "" then root :> RadixNode<'a>
            else
                let idx = Utils.search (key.[0]) root.keys root.childrenCount
                if idx = key.Length then null
                else
                    match root.children.[idx] with
                    | :? RadixBranch<'a> as branch -> branch.FindDescendant(key.AsSpan(), false)
                    | other -> other
        match start with
        | null -> Seq.empty
        | :? RadixLeaf<'a> as leaf -> Seq.singleton <| KeyValuePair<string,'a>(leaf.Key, leaf.Value)
        | :? RadixBranch<'a> as branch -> seq {
            let stack = Stack<RadixBranch<'a>>()
            stack.Push branch
            let mutable current = branch
            while stack.Count <> 0 do
                let mutable i = 0
                while i < current.childrenCount do
                    let child = current.children.[i]
                    match child with
                    | :? RadixLeaf<'a> as leaf ->
                        if leaf.Key.StartsWith(key) then //TODO: we don't need to compare entire key
                            yield KeyValuePair<string,'a>(leaf.Key, leaf.Value)
                    | :? RadixBranch<'a> as child ->
                        stack.Push current
                        current <- child
                        i <- 0
                    i <- i + 1
                current <- stack.Pop()
        }
    member this.Equals(other: Radix<'a>): bool =
        let r1 = this.Root
        let r2 = other.Root
        if obj.ReferenceEquals(r1, r2) then true
        elif isNull r1 then false
        elif isNull r2 then false
        else r1.Equals(r2)
    override this.Equals(other: obj) =
        match other with
        | :? Radix<'a> as other -> this.Equals(other)
        | _ -> false
    override this.GetHashCode() =
        match root with
        | null -> 0
        | node -> node.GetHashCode()
    member this.GetEnumerator() = this.Prefixed("").GetEnumerator()
    override this.ToString() =
        let sb = System.Text.StringBuilder()
        sb.Append("{ ") |> ignore
        let mutable e = this.GetEnumerator()
        if e.MoveNext() then
            let current = e.Current
            sb.Append('"').Append(current.Key).Append("\": ").Append(current.Value) |> ignore
        while e.MoveNext() do
            let current = e.Current
            sb.Append(", \"").Append(current.Key).Append("\": ").Append(current.Value) |> ignore
        sb.Append("}").ToString()
    interface IEnumerable<KeyValuePair<string, 'a>> with
        member this.GetEnumerator(): IEnumerator<KeyValuePair<string,'a>> = this.GetEnumerator()
        member this.GetEnumerator(): IEnumerator = upcast this.GetEnumerator()
    interface IEquatable<Radix<'a>> with
        member this.Equals(other) = this.Equals(other)
    
    
and [<Sealed>] internal RadixBuilder<'a>(root: RadixBranch<'a>) =
    let mutable root = root
    member _.Add(key: string, value: 'a): unit =
        let root = root
        let ins = RadixLeaf<'a>(key, value)
        let mutable offset = 0
        let mutable keySpan = key.AsSpan()
        let mutable branch =            
            // we're at the root
            let c = if key = "" then char 0 else key.[0]
            let index = Utils.search c root.keys root.childrenCount
            if index < root.children.Length then
                match root.children.[index] with
                | :? RadixBranch<'a> as child ->
                    let len = child.prefix.Length
                    offset <- len 
                    child.Copy() :?> RadixBranch<'a>
                | :? RadixLeaf<'a> as leaf ->
                    // we got a leaf: we need to split it into branch
                    let consumed = Utils.splitPoint (leaf.Key.AsSpan()) keySpan
                    let subNode = leaf.Insert(ins, offset, consumed)
                    root.keys.[index] <- c
                    root.children.[index] <- subNode
                    null
                | null ->
                    // there's no other value, just do straight insert
                    root.InsertAt(index, ins, c)
                    null
            else
                root.InsertAt(index, ins, c)
                null
        while not (isNull branch) do
            let (index, consumed) = branch.FindChild(keySpan)            
            if consumed = 0 then
                // none of the children has anything in common with keySpan
                // we need to insert new node at index, possibly moving existing ones
                let c = keySpan.[0]
                branch.InsertAt(index, ins, c)
                branch <- null
            elif consumed = keySpan.Length || consumed = branch.prefix.Length then
                // we've managed to match entire remaining part of a key withing the prefix
                if branch.childrenCount > index then
                    match branch.children.[index] with
                    | :? RadixBranch<'a> as child ->
                        // we found a matching node, we need to go deeper
                        keySpan <- keySpan.Slice(consumed)
                        offset <- offset + consumed
                        let copy = child.Copy() //operate on copy as it will eventually change
                        branch.children.[index] <- copy
                        branch <- downcast copy
                    | :? RadixLeaf<'a> as leaf ->
                        // we got a leaf: we need to split it into branch
                        let consumed = Utils.splitPoint (leaf.Key.AsSpan().Slice(offset)) (keySpan.Slice(offset))
                        let subNode = leaf.Insert(ins, offset, consumed)
                        offset <- offset + consumed
                        let c = leaf.Key.[offset]
                        branch.keys.[index] <- c
                        branch.children.[index] <- subNode
                        branch <- null
                else
                    let c = if keySpan.Length > consumed then keySpan.[consumed] else char 0
                    branch.InsertAt(index, ins, c)
                    branch <- null
            else
                // key has some common part with current branch prefix, we need to split it
                branch <- branch.SplitAt(consumed)
                keySpan <- keySpan.Slice(consumed)
                offset <- offset + consumed
                let c = keySpan.[0]
                let idx = Utils.search c branch.keys branch.childrenCount
                branch.InsertAt(idx, ins, c)
                branch <- null
        
    member _.Remove(key: ReadOnlySpan<char>, node: RadixLeaf<'a> outref): bool = failwithf "not implemented"
    member _.ToImmutable(): Radix<'a> =
        root.Freeze()
        Radix<'a> (root)
           
[<RequireQualifiedAccess>]
module Radix =
    
    /// Returns a new empty, immutable Radix tree instance. 
    let empty<'a> = Radix<'a>.Empty
    
    /// Creates a Radix tree instance out of provided sequence of key-value pairs.
    /// Elements with duplicated string key will be conflated with the last provided
    /// value being stored in result Radix tree.  
    let ofSeq (sequence: seq<string * 'a>): Radix<'a> =
        let s =
            sequence
            |> Seq.map (fun (key, value) -> KeyValuePair<_,_>(key, value))
        Radix<'a>.OfSeq(s, 4)
    
    /// Creates a Radix tree instance out of provided map.
    let inline ofMap (map: Map<string, 'a>): Radix<'a> = Radix<'a>.OfSeq(map, Map.count map)
    
    /// Converts current Radix tree into lazy sequence of tuples having key-value items.
    let toSeq (map: Radix<'a>): seq<string * 'a> =
        map |> Seq.map (fun e -> (e.Key, e.Value))
    
    /// Returns value option containing entry with a minimal key stored in given Radix tree.
    /// If tree is empty, a ValueNone will be returned.
    let inline min (map: Radix<'a>): KeyValuePair<string, 'a> voption = map.Min
    
    /// Returns value option containing entry with a max key stored in given Radix tree.
    /// If tree is empty, a ValueNone will be returned.
    let inline max (map: Radix<'a>): KeyValuePair<string, 'a> voption = map.Max
    
    /// Checks if current Radix tree contains any elements.
    let inline isEmpty (map: Radix<'a>): bool = map.IsEmpty
    
    /// Returns number of elements stored inside of Radix tree.
    let inline count (map: Radix<'a>): int = map.Count
    
    /// Returns a new array with updated Radix tree having provided `key`-`value` pair inside.
    let add (key: string) (value: 'a) (map: Radix<'a>): Radix<'a> = map.Add(key, value)
    
    /// Returns a new array with updated Radix tree containing all `entries` in the sequence.
    /// In case when keys inside of a sequence are duplicated, the last of such entry's
    /// value will be stored.
    let addAll (entries: (string * 'a) seq) (map: Radix<'a>): Radix<'a> = map.AddRange(entries)
    
    /// Tries to remove an entry with provided `key` from given Radix tree. Returns an updated
    /// tree and option, which contains a removed value stored together with corresponding `key`
    /// (if there was any).
    let removeBySpanWithValue (key: ReadOnlySpan<char>) (map: Radix<'a>): Radix<'a> * 'a voption =
        match map.Remove(key) with
        | _, null -> (map, ValueNone)
        | map', node -> (map', ValueSome node.Value)
    
    /// Tries to remove an entry with provided `key` from given Radix tree. Returns an updated
    /// tree and option, which contains a removed value stored together with corresponding `key`
    /// (if there was any).
    let inline removeWithValue (key: string) (map: Radix<'a>): Radix<'a> * 'a voption =
        removeBySpanWithValue (key.AsSpan()) map
    
    /// Tries to remove an entry with provided `key` from given Radix tree. Returns an updated
    /// tree without given entry. Use `Radix.removeBySpanWithValue` in case when removed value
    /// is necessary.
    let inline removeBySpan (key: ReadOnlySpan<char>) (map: Radix<'a>): Radix<'a> =
        removeBySpanWithValue key map |> fst
    
    /// Tries to remove an entry with provided `key` from given Radix tree. Returns an updated
    /// tree without given entry. Use `Radix.removeWithValue` in case when removed value
    /// is necessary.
    let inline remove (key: string) (map: Radix<'a>): Radix<'a> =
        removeBySpanWithValue (key.AsSpan()) map |> fst
    
    /// Tries to locate an entry with corresponding `key` identifier. Returns an entry's value
    /// if there was any, or `ValueNone` if no entry for a given `key` exists within a given
    /// `map`. 
    let findBySpan (key: ReadOnlySpan<char>) (map: Radix<'a>): 'a voption =
        match map.FindLeaf key with
        | null -> ValueNone
        | leaf -> ValueSome(leaf.Value)
    
    /// Tries to locate an entry with corresponding `key` identifier. Returns an entry's value
    /// if there was any, or `ValueNone` if no entry for a given `key` exists within a given
    /// `map`.
    let inline find (key: string) (map: Radix<'a>): 'a voption = findBySpan (key.AsSpan()) map
    
    /// Returns a sequence of all containing all entries having a given `prefix` in their keys.
    let prefixed (prefix: string) (map: Radix<'a>): KeyValuePair<string, 'a> seq = map.Prefixed(prefix)
    
    let between (min: string) (max: string) (tree: Radix<'a>): KeyValuePair<string, 'a> seq =
        let cmp = min.CompareTo(max)
        if cmp < 0 then
            failwith "not implemented"
        elif cmp = 0 then
            match tree.FindLeaf(min.AsSpan()) with
            | null -> Seq.empty
            | node -> Seq.singleton (KeyValuePair<_,_>(node.Key, node.Value))
        else
            raise (ArgumentException("Minimum string key cannot be greater than maximum key."))
    
    let iter (fn: string -> 'a -> unit) (map: Radix<'a>): unit =
        let mutable e = map.GetEnumerator()
        while e.MoveNext() do
            let current = e.Current
            fn current.Key current.Value
    
    let map (fn: string -> 'a -> 'b) (tree: Radix<'a>): Radix<'b> =
        let builder = RadixBuilder<'b>(RadixBranch<'b>.Create(4))
        let mutable e = tree.GetEnumerator()
        while e.MoveNext() do
            let current = e.Current
            builder.Add(current.Key, fn current.Key current.Value)
        builder.ToImmutable()
    
    let fold (fn: 's -> string -> 'a -> 's) (init: 's) (tree: Radix<'a>): 's =
        let mutable state = init
        let mutable e = tree.GetEnumerator()
        while e.MoveNext() do
            let current = e.Current
            state <- fn state current.Key current.Value
        state
    
    let filter (fn: string -> 'a -> bool) (map: Radix<'a>): Radix<'a> =
        let builder = RadixBuilder<'a>(RadixBranch<'a>.Create(4))
        let mutable e = map.GetEnumerator()
        while e.MoveNext() do
            let current = e.Current
            if fn current.Key current.Value then builder.Add(current.Key, current.Value)
        builder.ToImmutable()
    
    let intersect (fn: string -> 'a -> 'b -> 'c) (a: Radix<'a>) (b: Radix<'b>): Radix<'c> =
        if isEmpty a || isEmpty b then Radix<'c>.Empty
        else
            let builder = RadixBuilder<'c>(RadixBranch<'c>.Create(4))
            let mutable e1 = a.GetEnumerator()
            let mutable e2 = b.GetEnumerator()
            let mutable next = e1.MoveNext() && e2.MoveNext()
            while next do
                let c1 = e1.Current
                let c2 = e2.Current
                let cmp = c1.Key.CompareTo(c2.Key)
                if cmp = 0 then
                    builder.Add(c2.Key, fn c2.Key c1.Value c2.Value)
                    next <- e1.MoveNext() && e2.MoveNext()
                elif cmp < 0 then
                    next <- e1.MoveNext()
                else
                    next <- e2.MoveNext()
            builder.ToImmutable()
    
    let union (fn: string -> 'a -> 'a -> 'a) (a: Radix<'a>) (b: Radix<'a>): Radix<'a> =
        if isEmpty a then b
        elif isEmpty b then a
        else
            let builder = RadixBuilder<'a>(RadixBranch<'a>.Create(4))
            let mutable e1 = a.GetEnumerator()
            let mutable e2 = b.GetEnumerator()
            let mutable next = e1.MoveNext() && e2.MoveNext()
            while next do
                let c1 = e1.Current
                let c2 = e2.Current
                let cmp = c1.Key.CompareTo(c2.Key)
                if cmp = 0 then
                    builder.Add(c2.Key, fn c2.Key c1.Value c2.Value)
                    next <- e1.MoveNext() && e2.MoveNext()
                elif cmp < 0 then
                    builder.Add(c1.Key, c1.Value)
                    next <- e1.MoveNext()
                else
                    builder.Add(c2.Key, c2.Value)
                    next <- e2.MoveNext()
            builder.ToImmutable()