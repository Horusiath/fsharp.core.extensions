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
open System.Numerics
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

//TODO: try change to struct with tagged union VenNode<'a>(tag:byte, (children:VecNode<'a>[] | values:'a[])) 
type internal VecNode<'a> =
    | Leaf   of array:'a[]
    | Branch of children: VecNode<'a>[] 
    member this.Map(fn: 'a -> 'b) =
        let rec map fn node =
            match node with
            | Leaf array -> Leaf (Array.map fn array)
            | Branch children -> Branch (Array.map (map fn) children)
        map fn this
        
module internal VecConst =
    
    [<Literal>] 
    let off = 5
    
    [<Literal>]
    let capacity = 32 // 1 << off
    
    [<Literal>]
    let mask = 0x1f // capacity - 1
    
    let inline tailoff count = if count < capacity then 0 else ((count - 1) >>> off) <<< off
    
    let rec pathFor level node =
        if level = 0 then node
        else
            let result = Array.zeroCreate capacity
            result.[0] <- pathFor (level - off) node
            Branch result
    
    let rec appendTail count (level: int) (Branch children) (tailNode: VecNode<'a>) =
        let subIdx = ((count - 1) >>> level) &&& mask            
        let result = Array.copy children
        let nodeToInsert =
            if level = off then tailNode
            else
                try
                    let child = children.[subIdx]
                    if obj.ReferenceEquals(child, null) then pathFor (level-off) tailNode
                    else appendTail count (level-off) child tailNode
                with e ->
                    reraise()
        result.[subIdx] <- nodeToInsert
        Branch result
        
[<IsByRefLike;Struct>]
type internal TransientVec<'a> =
    val mutable count: int
    val mutable shift: int
    val mutable root: VecNode<'a>
    val mutable tail: 'a[]
    new (count: int, shift: int, root: VecNode<'a>, tail: 'a[]) =
        { count = count; shift = shift; root = root; tail = tail }
    member this.AsPersistent(): Vec<'a> =
        let trimmedTail = Array.zeroCreate (this.count - VecConst.tailoff this.count)
        Array.blit this.tail 0 trimmedTail 0 trimmedTail.Length
        Vec<_>(this.count, this.shift, this.root, trimmedTail)
    member this.Append(value) =
        let i = this.count
        if i - VecConst.tailoff this.count < 32 then
            this.tail.[i &&& VecConst.mask] <- value
            this.count <- this.count + 1
        else
            let mutable shift' = this.shift
            let tailNode = Leaf this.tail
            this.tail <- Array.zeroCreate VecConst.capacity
            this.tail.[0] <- value
            let root' =
                if ((this.count >>> VecConst.off) > (1 <<< this.shift)) then
                    let n = Array.zeroCreate VecConst.capacity
                    n.[0] <- this.root
                    n.[1] <- VecConst.pathFor this.shift tailNode
                    shift' <- shift' + 5
                    Branch n
                else
                    VecConst.appendTail this.count this.shift this.root tailNode
            this.root <- root'
            this.shift <- shift'
            this.count <- this.count + 1
    

and [<IsByRefLike;Struct>] VecEnumerator<'a>(vector: Vec<'a>) =
    [<DefaultValue(false)>]val mutable private array: 'a[]
    [<DefaultValue(false)>]val mutable private index: int
    [<DefaultValue(false)>]val mutable private offset: int
    [<DefaultValue(false)>]val mutable private current: 'a
    member e.Current = e.current
    member e.MoveNext() =
        let i = e.index
        if i < vector.Count then
            if isNull e.array then e.array <- vector.FindArrayForIndex(i)
            elif i - e.offset = VecConst.capacity then
                e.array <- vector.FindArrayForIndex(i)
                e.offset <- e.offset + VecConst.capacity
            e.current <- e.array.[e.index &&& VecConst.mask]
            e.index <- i + 1
            true
        else false
    interface IEnumerator<'a> with
        member this.Current: 'a = this.current
        member this.Current: obj = upcast this.current
        member this.Reset() =
            this.array <- null
            this.current <- Unchecked.defaultof<_>
            this.index <- 0
        member __.Dispose() = ()
        member this.MoveNext(): bool = this.MoveNext()
        
and [<Sealed>] Vec<'a> internal(count: int, shift: int, root: VecNode<'a>, tail: 'a[]) =
            
    static let emptyNode: VecNode<'a> = Branch (Array.zeroCreate VecConst.capacity)
    static let empty = Vec<_>(0, VecConst.off, emptyNode, [||])
    
    let rec doAssoc level node i value (old: 'a outref) =
        if level = 0 then
            let (Leaf values) = node
            let result = Array.copy values
            old <- result.[i &&& VecConst.mask]
            result.[i &&& VecConst.mask] <- value
            Leaf result
        else
            let (Branch children) = node
            let result = Array.copy children
            let subidx = (i >>> shift) &&& VecConst.mask
            result.[subidx] <- doAssoc (level-5) children.[subidx] i value &old
            Branch result
    
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Empty(): Vec<'a> = empty
    
    static member From(items: 'a[]): Vec<'a> = 
        let count = Array.length items
        if count = 0 then empty
        elif count > VecConst.capacity then
            let mutable v = empty.AsTransient()
            for item in items do
                v.Append(item)
            v.AsPersistent()
        else
            Vec<_>(count, VecConst.off, emptyNode, Array.copy items)
            
    member this.Map(fn: 'a -> 'b): Vec<'b> =
        if count = 0 then Vec<'b>.Empty()
        else
            let root' = root.Map fn
            let tail' = tail |> Array.map fn
            Vec<'b>(count, shift, root', tail')
            
    member __.Count: int = count
    member this.GetEnumerator(): VecEnumerator<_> = new VecEnumerator<_>(this)
    
    member internal __.FindArrayForIndex(index: int): 'a[] =
        if index >= VecConst.tailoff count then tail
        else
            let mutable node = root
            let mutable level = shift
            while level > 0 do
                match node with
                | Branch array ->
                    node <- array.[(index >>> level) &&& VecConst.mask]
                | _ -> ()
                level <- level - VecConst.off
            let (Leaf array) = node
            array
    
    member __.Append(value) =
        if count - VecConst.tailoff count < VecConst.capacity
        then
            let tail' = Array.zeroCreate (tail.Length + 1)
            tail.CopyTo(tail', 0)
            tail'.[tail.Length] <- value
            Vec<_>(count + 1, shift, root, tail')
        else
            // tail is full
            let tailNode = Leaf tail
            let mutable shift' = shift
            let root' =
                if (count >>> VecConst.off) > (1 <<< shift)
                then
                    // overflow root
                    let arr = Array.zeroCreate VecConst.capacity
                    arr.[0] <- root
                    arr.[1] <- VecConst.pathFor shift tailNode
                    shift' <- shift' + VecConst.off
                    Branch arr
                else
                    VecConst.appendTail count shift root tailNode
            Vec<_>(count + 1, shift', root', [| value |])
           
    member this.Replace(index: int, value: 'a, old: 'a outref): Vec<'a> =
        if index < 0 || index >= count then raise (IndexOutOfRangeException (sprintf "Tried to insert value at index %i in vector of size %i" index count))
        elif index >= VecConst.tailoff count then
            let tail' = Array.zeroCreate tail.Length
            Array.blit tail 0 tail' 0 tail.Length
            old <- tail'.[index &&& VecConst.mask]
            tail'.[index &&& VecConst.mask] <- old
            Vec<_>(count, shift, root, tail')
        else
            let root' = doAssoc shift root index value &old
            Vec<_>(count, shift, root', tail)
            
    member this.ElementAt(index: int): 'a =
        if index >= 0 && index < count then
            let n = this.FindArrayForIndex index
            n.[index &&& VecConst.mask]
        else raise (IndexOutOfRangeException(sprintf "Index [%i] out of range of vector (size: %i)" index count))
        
    member inline this.Item with get index = this.ElementAt index

    member this.IndexOf(value: 'a): int =
        let eq = EqualityComparer<'a>.Default
        let mutable e = new VecEnumerator<'a>(this)
        let mutable found = -1
        let mutable i = 0
        while found = -1 && e.MoveNext() do
            if eq.Equals(e.Current, value) then
                found <- i
            i <- i + 1
        found            

    member this.CopyTo(array: 'a[], arrayIndex: int) =
        let mutable offset = 0
        let mutable i = arrayIndex
        while offset < count do
            let src = this.FindArrayForIndex(offset)
            Array.Copy(src, 0, array, i, src.Length)
            offset <- offset + VecConst.capacity
            i <- i + VecConst.capacity
            
    member internal this.AsTransient(): TransientVec<'a> =
        let (Branch children) = root
        let tail' = Array.zeroCreate VecConst.capacity
        Array.blit tail 0 tail' 0 tail.Length
        TransientVec<_>(count, shift, Branch (Array.copy children), tail')

    interface IEquatable<Vec<'a>> with
        member this.Equals(other: Vec<_>): bool =
            if obj.ReferenceEquals(other, null) then false
            elif obj.ReferenceEquals(this, other) then false
            elif count <> other.Count then false
            else
                let eq = EqualityComparer<'a>.Default
                let mutable e1 = new VecEnumerator<_>(this)
                let mutable e2 = new VecEnumerator<_>(other)
                let mutable result = false
                while not result && e1.MoveNext() && e2.MoveNext() do
                    result <- eq.Equals(e1.Current, e2.Current)
                result
                
    interface IEnumerable<'a> with
        member this.GetEnumerator(): IEnumerator<'a> = 
            let mutable array: 'a[] = null
            let mutable index = 0
            let mutable offset = 0
            { new IEnumerator<'a> with
                member __.Dispose () = ()
                member __.Reset() =
                    array <- null
                    index <- 0
                member __.Current: 'a = array.[index &&& VecConst.mask]
                member __.Current: obj = upcast array.[index &&& VecConst.mask]
                member __.MoveNext() =
                    let i = index
                    if i < this.Count then
                        if isNull array then array <- this.FindArrayForIndex(i)
                        else
                            if i - offset = VecConst.capacity then
                                array <- this.FindArrayForIndex(i)
                                offset <- offset + VecConst.capacity
                            index <- i + 1
                        true
                    else false }
        member this.GetEnumerator(): System.Collections.IEnumerator =
            upcast (this :> IEnumerable<'a>).GetEnumerator()
    interface IList<'a> with
        member this.Count = this.Count
        member __.IsReadOnly = true
        member this.IndexOf(value) = this.IndexOf(value)
        member this.Contains(value) = this.IndexOf(value) <> -1
        member this.CopyTo(array, arrayIndex) = this.CopyTo(array, arrayIndex)
        member this.Item
            with get (index: int): 'a = this.ElementAt(index)
            and set index value = raise (NotSupportedException "Vec cannot insert elements in mutable fashion.") 
        member __.Add _ = raise (NotSupportedException "Vec cannot add elements in mutable fashion.")
        member __.Clear() = raise (NotSupportedException "Vec cannot be cleared in mutable fashion.")
        member __.Insert(_,_) = raise (NotSupportedException "Vec cannot insert elements in mutable fashion.")
        member __.RemoveAt(_) = raise (NotSupportedException "Vec cannot remove elements in mutable fashion.")
        member __.Remove(_) = raise (NotSupportedException "Vec cannot remove elements in mutable fashion.")
    interface IReadOnlyList<'a> with
        member this.Count = this.Count
        member this.Item with get index = this.ElementAt(index)

type vec<'a> = Vec<'a>

type VecRangedEnumerator<'a> =
    struct
        val private vector: Vec<'a>
        val mutable private array: 'a[]
        val mutable private index: int
        val mutable private offset: int
        val private stop: int
        val private start: int
        new (v: Vec<_>, start: int, finish: int) =
            { vector = v;
              stop = finish;
              start = start;
              index = start;
              offset = (start - (start % VecConst.capacity));
              array = null }
        member e.Current = e.array.[e.index &&& VecConst.mask]
        member e.MoveNext() =
            let i = e.index
            if i < e.stop then
                if isNull e.array then e.array <- e.vector.FindArrayForIndex(i)
                else
                    if i - e.offset = VecConst.capacity then
                        e.array <- e.vector.FindArrayForIndex(i)
                        e.offset <- e.offset + VecConst.capacity
                    e.index <- i + 1
                true
            else false
    end
    interface IEnumerator<'a> with
        member this.Current: 'a = this.Current
        member this.Current: obj = upcast this.Current
        member this.Reset() =
            this.array <- null
            this.index <- this.start
        member __.Dispose() = ()
        member this.MoveNext(): bool = this.MoveNext()

[<IsByRefLike;Struct>]
type VecReverseEnumerator<'a> =
    val mutable private vector: 'a vec
    val mutable private array: 'a[]
    val mutable private index: int
    val mutable private offset: int
    val mutable private current: 'a
    new (v: Vec<_>) =
        let start = v.Count - 1
        { vector = v;
          array = if start < 0 then null else v.FindArrayForIndex(start)
          index = start;
          offset = (start - (start % VecConst.capacity));
          current = Unchecked.defaultof<_>; }
    member this.Current = this.current
    member this.MoveNext() =
        if this.index < 0 then false
        else
            if this.index - this.offset = -1 then
                this.offset <- this.offset - VecConst.capacity
                this.array <- this.vector.FindArrayForIndex(this.index)
            this.current <- this.array.[this.index &&& VecConst.mask]
            this.index <- this.index - 1
            true
    interface IEnumerator<'a> with
        member this.Current: 'a = this.current
        member this.Current: obj = upcast this.current
        member this.Reset() =
            this.array <- null
            this.current <- Unchecked.defaultof<_>
        member __.Dispose() = ()
        member this.MoveNext(): bool = this.MoveNext()
       
[<RequireQualifiedAccess>]
module Vec =
    
    /// Returns an empty vector.
    let inline empty<'a> = Vec<'a>.Empty()
    
    /// Constructs current vector out of the given array.
    [<CompiledName("OfArray")>]
    let ofArray (items): Vec<_> = Vec<_>.From items
    
    /// Constructs current vector out of the given sequence of elements.
    [<CompiledName("OfEnumerable")>]
    let ofSeq (items: 'a seq): Vec<'a> =
        let mutable vector = Vec<'a>.Empty()
        for item in items do
            vector <- vector.Append(item)
        vector
        
    /// Constructs current vector out of the given enumerator.
    [<CompiledName("OfEnumerator")>]
    let ofEnumerator (e: #IEnumerator<'a> byref): Vec<'a> =
        let mutable vector = Vec<'a>.Empty()
        while e.MoveNext() do
            vector <- vector.Append(e.Current)
        vector           
        
    /// Copies contents of current vector into new array and returns it.
    [<CompiledName("ToArray")>]
    let toArray (a: Vec<_>): 'a[] = 
        let array = Array.zeroCreate a.Count
        a.CopyTo(array, 0)
        array
    
    /// Checks if current vector contains no elements. Complexity: O(1).
    [<CompiledName("IsEmpty")>]
    let inline isEmpty (v: Vec<_>): bool = v.Count = 0
    
    /// Returns a length of the current vector.
    /// Complexity: O(1).
    [<CompiledName("Length")>]
    let inline length (v: Vec<_>): int = v.Count
    
    /// Inserts an element at the end of vector, returning new vector in the result.
    /// Complexity: O(log32(n)) - most of the time it's close to O(1).
    [<CompiledName("Append")>]
    let inline append (item: 'a) (v: Vec<_>) : Vec<_> = v.Append(item)
    
    /// Inserts an element at the beginning of the vector, returning new vector in the result.
    /// Complexity: O(n) - requires copying entire vector.
    [<CompiledName("Prepend")>]
    let prepend (item: 'a) (v: Vec<_>) : Vec<_> = failwith "not implemented"
    
    /// Inserts an element at the given position of the vector, returning new vector in the result.
    /// Complexity: O(m + log32(n-m)) where i is the number of elements after given index.
    /// The closer to the end of the vector, the less expensive it is.
    [<CompiledName("Insert")>]
    let insert (index: int) (item: 'a) (v: Vec<_>) : Vec<_> = failwith "not implemented"

    [<CompiledName("InsertRange")>]
    let insertRange (index: int) (items: #seq<'a>) (v: Vec<_>) : Vec<_> = failwith "not implemented"
    
    /// Replaces element an given `index` with provided `item`. Returns an updated vector and replaced element.
    [<CompiledName("Update")>]
    let inline replace (index: int) (item: 'a) (v: Vec<_>) : (Vec<_> * 'a) = v.Replace(index, item)
    
    /// Returns first element of a vector or None if vector is empty.
    /// Complexity: O(log32(n)).
    [<CompiledName("First")>]
    let head (v: Vec<_>) : 'a voption = if isEmpty v then ValueNone else ValueSome(v.ElementAt 0)
    
    /// Returns the last element of a vector or None if vector is empty.
    /// Complexity: O(log32(n)).
    [<CompiledName("Last")>]
    let last (v: Vec<_>): 'a voption = if isEmpty v then ValueNone else ValueSome(v.ElementAt (v.Count - 1))
    
    /// Returns vector element at provided index, or None if index is out of bound of a vector.
    [<CompiledName("ElementAt")>]
    let item (index: int) (v: Vec<_>): 'a voption = 
        if index >= 0 && index < v.Count then
            ValueSome(v.ElementAt index)
        else ValueNone
    
    /// Concatenates two vectors, returning new vector with elements of vector `a` first, then elements of vector `b` second.
    /// Complexity: O(b.length) - requires copying elements of b.
    [<CompiledName("Concat")>]
    let concat (a: Vec<'a>) (b: #IList<'a>): Vec<_> =
        match b.Count with
        | 0 -> a
        | 1 -> append b.[0] a
        | _ -> failwith "not implemented"
    
    /// Traverses given vector `v` from its beginning and returns an index of first element equal to a given `item`.
    /// Returns -1 if no matching element was found or vector is empty. A default generic equality comparer is used
    /// for equality check. If custom equality comparer is necessary, use `Vec.exists` function instead.
    [<CompiledName("IndexOf")>]
    let inline indexOf (item: 'a) (v: Vec<_>): int = v.IndexOf item
    
    /// Traverses given vector `v` starting from its end and returns an index of first element equal to a given `item`.
    /// Returns -1 if no matching element was found or vector is empty. A default generic equality comparer is used
    /// for equality check. If custom equality comparer is necessary, use `Vec.exists` function instead.
    [<CompiledName("LastIndexOf")>]
    let inline lastIndexOf (item: 'a) (v: Vec<_>): int = 
        let eq = EqualityComparer<'a>.Default
        let mutable e = new VecReverseEnumerator<'a>(v)
        let mutable found = -1
        let mutable i = v.Count - 1
        while found = -1 && e.MoveNext() do
            if eq.Equals(e.Current, item) then
                found <- i
            i <- i - 1
        found 
    
    /// Verifies if provided `item` exists inside of a given vector `v`. A default generic equality comparer is used
    /// for equality check. If custom equality comparer is necessary, use `Vec.exists` function instead.
    [<CompiledName("Contains")>]
    let inline contains (item: 'a) (v: Vec<_>): bool = v.IndexOf item <> -1
    
    /// Iterates over the elements of a given vector `v` (starting from the begining of it),
    /// applying a function `f` over each one of them.
    [<CompiledName("ForEach")>]
    let inline iter (f: 'a -> unit) (v: Vec<_>): unit =
        let mutable e = new VecEnumerator<'a>(v)
        while e.MoveNext() do
            f e.Current
            
    /// Iterates over the elements of a given vector `v` (starting from the begining of it),
    /// applying a function `f` over each one of them together with an index of that element inside of a vector.
    [<CompiledName("ForEach")>]
    let inline iteri (f: int -> 'a -> unit) (v: Vec<_>): unit =
        let mutable i = 0
        let mutable e = new VecEnumerator<'a>(v)
        while e.MoveNext() do
            f i e.Current
            i <- i + 1
    
    /// Tries to find a first element inside of a vector, for which a given predicate `fn` is true, and returns it.
    /// If vector is empty or none of its elements satisfies given predicate, a ValueNone is returned.
    [<CompiledName("FirstOrDefault")>]
    let find (fn: 'a -> bool) (v: Vec<_>): 'a voption =
        let mutable e = new VecEnumerator<'a>(v)
        let mutable found = ValueNone
        while ValueOption.isNone found && e.MoveNext() do
            let c = e.Current
            if fn c then
                found <- ValueSome c
        found
                
    /// Iterates over elements of a vector `v` and applies a tranforming function `f` over each of them, returning a new
    /// vector of modified values in the result.
    [<CompiledName("Map")>]
    let inline map (f: 'a -> 'b) (v: Vec<_>): Vec<'b> = v.Map(f)
    
    [<CompiledName("MapWithIndex")>]
    let mapi (f: int -> 'a -> 'b) (v: Vec<_>): Vec<'b> =
        let mutable i = 0
        v.Map (fun item ->
            let result = f i item
            i <- i + 1
            result)
    
    [<CompiledName("Filter")>]
    let filter (f: 'a -> bool) (v: Vec<_>): Vec<_> =
        //TODO: optimize path, where all elements have satisfied the predicate
        let mutable t = empty.AsTransient()
        for item in v do
            if f item then
                t.Append item
        t.AsPersistent()
            
    
    /// Iterates over all elements of the vector (starting from its beginning), and checks if any of them satisfies
    /// given predicate `fn`. Returns *false* if vector `v` is empty or none of its elements satisfies a predicate.
    [<CompiledName("Exists")>]
    let exists (fn: 'a -> bool) (v: Vec<_>): bool =
        let mutable e = new VecEnumerator<'a>(v)
        let mutable found = false
        while not found && e.MoveNext() do
            found <- fn e.Current
        found
        
    /// Iterates over all elements of the vector (starting from its beginning), and checks if all of them satisfy
    /// given predicate `fn`. Returns *true* if vector `v` is empty or all of its elements satisfy a predicate.
    [<CompiledName("ForAll")>]
    let forall (fn: 'a -> bool) (v: Vec<_>): bool =
        let mutable e = new VecEnumerator<'a>(v)
        let mutable result = true
        while result && e.MoveNext() do
            result <- fn e.Current
        result
    
    [<CompiledName("Choose")>]
    let choose (f: 'a -> 'b option) (v: Vec<_>): Vec<'b> =
        let t = empty.AsTransient()
        for item in v do
            match f item with
            | None -> ()
            | Some m -> t.Append m
        t.AsPersistent()
            
    [<CompiledName("Slice")>]
    let slice (low: int) (high: int) (v: Vec<_>): VecRangedEnumerator<'a> = new VecRangedEnumerator<'a>(v, low, high)
    
    /// Folds over all elements of a given vector `v` (starting from its beginning), continuously applying accumulating
    /// function `f` over them to build incremental state from previous accumulation (using `init` value at the start)
    /// and currently iterated element. Returns `init` if vector was empty.
    [<CompiledName("Fold")>]
    let fold (f: 'b -> 'a -> 'b) (init: 'b) (v: Vec<_>): 'b =
        let mutable e = new VecEnumerator<'a>(v) 
        let mutable state = init
        while e.MoveNext() do
            state <- f state e.Current
        state
        
    /// Folds over all elements of a given vector `v` (starting from its end), continuously applying accumulating
    /// function `f` over them to build incremental state from previous accumulation (using `init` value at the start)
    /// and currently iterated element. Returns `init` if vector was empty.
    [<CompiledName("FoldBack")>]
    let foldBack (f: 'b -> 'a -> 'b) (init: 'b) (v: Vec<_>): 'b =
        let mutable e = new VecReverseEnumerator<'a>(v) 
        let mutable state = init
        while e.MoveNext() do
            state <- f state e.Current
        state

    /// Folds over all elements of a given vector `v` (starting from its beginning), continuously applying accumulating
    /// function `f` over them to build incremental state from previous accumulation. Returns *ValueNone* if vector
    /// was empty. See also: `Vec.fold`.
    [<CompiledName("Reduce")>]
    let reduce (f: 'a -> 'a -> 'a) (v: Vec<_>): 'a voption =
        let mutable e = new VecEnumerator<'a>(v)
        if not (e.MoveNext()) then ValueNone
        else
            let mutable state = e.Current
            while e.MoveNext() do
                state <- f state e.Current
            ValueSome state
    
    [<Struct;IsReadOnly>]
    type Reverser<'a>(vector: Vec<'a>) =
        member __.GetEnumerator() = new VecReverseEnumerator<'a>(vector)
    
    /// Returns an enumerator-like struct, which allows to make iterate over the elements of a given vector `v`
    /// in reverse direction (starting from end).
    [<CompiledName("GetReverseEnumerator")>]       
    let inline rev (v: Vec<'a>) = Reverser(v)