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
open System.Buffers
open System.Buffers
open System.Collections.Generic
open System.Numerics
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

//TODO: try change to struct with tagged union VenNode<'a>(tag:byte, (children:VecNode<'a>[] | values:'a[]))
[<NoEquality;NoComparison>]
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
type internal VecBuilder<'a> =
    val mutable count: int
    val mutable shift: int
    val mutable root: VecNode<'a>
    val mutable tail: 'a[]
    new (count: int, shift: int, root: VecNode<'a>, tail: 'a[]) =
        { count = count; shift = shift; root = root; tail = tail }
    member this.ToImmutable(): Vec<'a> =
        let trimmedTail = Array.zeroCreate (this.count - VecConst.tailoff this.count)
        Array.blit this.tail 0 trimmedTail 0 trimmedTail.Length
        Vec<_>(this.count, this.shift, this.root, trimmedTail)
    member this.Add(value) =
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
    member this.AddRange(values: 'a[]) =
        let mutable remaining = values.Length
        let mutable valuesOffset = 0
        if this.count - VecConst.tailoff this.count >= 32 then
            let mutable shift' = this.shift
            let tailNode = Leaf this.tail
            this.tail <- Array.zeroCreate VecConst.capacity
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
        while remaining <> 0 do
            let start = this.count&&&VecConst.mask
            let size = Math.Min(remaining, VecConst.capacity - start)
            Array.blit values valuesOffset this.tail start size
            this.count <- this.count + size
            valuesOffset <- valuesOffset + size
            remaining <- remaining - size
            if remaining <> 0 then
                let mutable shift' = this.shift
                let tailNode = Leaf this.tail
                this.tail <- Array.zeroCreate VecConst.capacity
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

/// Vec is an immutable vector implementation, optimized for fast append/pop operations,
/// as well as fast traverse access and random access. Internal structure uses an immutable
/// tree of chunks, so that when an item is pushed/popped, we don't need to copy an entire
/// vector. 
and [<Sealed>] Vec<'a> internal(count: int, shift: int, root: VecNode<'a>, tail: 'a[]) =
            
    static let emptyNode: VecNode<'a> = Branch (Array.zeroCreate VecConst.capacity)
    static let empty = Vec<_>(0, VecConst.off, emptyNode, [||])
    
    let rec popTail level (node) =
        let subidx = ((count-2) >>> level) &&& VecConst.mask
        if level > 5 then
            let (Branch children) = node
            let child' = popTail (level-5) children.[subidx]
            if obj.ReferenceEquals(child', Unchecked.defaultof<_>) && subidx = 0
            then Unchecked.defaultof<_>
            else
                let result = Array.copy children
                result.[subidx] <- child'
                Branch result
        elif subidx = 0 then
            Unchecked.defaultof<_>
        else
            match node with
            | Leaf children ->
                let result = Array.copy children
                result.[subidx] <- Unchecked.defaultof<_>
                Leaf result
            | Branch nodes ->
                let result = Array.copy nodes
                result.[subidx] <- Unchecked.defaultof<_>
                Branch result
            
    
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
    
    /// Returns an empty vector.
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Empty(): Vec<'a> = empty
    
    /// Creates a new vector from array of items.
    static member From(items: 'a[]): Vec<'a> = 
        let count = Array.length items
        if count = 0 then empty
        elif count > VecConst.capacity then
            let mutable v = empty.AsBuilder()
            v.AddRange(items)
            v.ToImmutable()
        else
            Vec<_>(count, VecConst.off, emptyNode, Array.copy items)
            
    /// Maps contents of a vector, creating new vector in a result.
    member this.Map(fn: 'a -> 'b): Vec<'b> =
        //TODO: special case for `id` function?
        if count = 0 then Vec<'b>.Empty()
        else
            let root' = root.Map fn
            let tail' = tail |> Array.map fn
            Vec<'b>(count, shift, root', tail')
                        
    /// Returns a number of items stored inside of current vector.
    member __.Count: int = count
    
    /// Gets an enumerable ref-like structure, that allows to iterate over current vector.
    /// It's optimized to be used together with for loops.
    member this.GetEnumerator(): VecEnumerator<_> = new VecEnumerator<_>(this)
    
    member internal __.FindArrayForIndex(index: int): 'a[] =
        if index >= VecConst.tailoff count then tail
        else
            let mutable node = root
            let mutable level = shift
            while level > 0 do
                match node with
                | Branch array ->
                    // TODO: Unsafe.Add to avoid bound checking
                    node <- array.[(index >>> level) &&& VecConst.mask]
                | _ -> ()
                level <- level - VecConst.off
            match node with
            | Leaf array -> array
            | _ -> failwithf "Expected leaf for index %i, but got %A (count: %i, shift: %i)" index node count shift
    
    /// Adds new `value` at the end of current vector, returning new vector in the result.
    member __.Add(value) =
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
           
    /// Replaces a value at given `index`, producing new vector with updated value in the result.
    /// Value stored previously under that index is returned as outref parameter. 
    member this.Replace(index: int, value: 'a, old: 'a outref): Vec<'a> =
        if index < 0 || index >= count then raise (IndexOutOfRangeException (sprintf "Tried to insert value at index %i in vector of size %i" index count))
        elif index >= VecConst.tailoff count then
            let tail' = Array.zeroCreate tail.Length
            Array.blit tail 0 tail' 0 tail.Length
            old <- tail'.[index &&& VecConst.mask]
            tail'.[index &&& VecConst.mask] <- value
            Vec<_>(count, shift, root, tail')
        else
            let root' = doAssoc shift root index value &old
            Vec<_>(count, shift, root', tail)
          
    /// Returns a value stored under given `index`  
    member this.ElementAt(index: int): 'a =
        if index >= 0 && index < count then
            let n = this.FindArrayForIndex index
            n.[index &&& VecConst.mask]
        else raise (IndexOutOfRangeException(sprintf "Index [%i] out of range of vector (size: %i)" index count))
        
    member inline this.Item with get index = this.ElementAt index

    /// Returns index of first occurence of given `value` inside of current vector.
    /// Value is searched for in `O(n)` complexity, starting from the beginning of
    /// a vector. Comparison is made using equality method defined on element type.
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

    /// Copies contents of current vector into given `array`. Values inserted into an
    /// array starts at given array's index. Array must be large enough to contain all
    /// of the vectors elements, otherwise an exception will be thrown.
    member this.CopyTo(array: 'a[], arrayIndex: int) =
        let mutable offset = 0
        let mutable i = arrayIndex
        while offset < count do
            let src = this.FindArrayForIndex(offset)
            Array.Copy(src, 0, array, i, src.Length)
            offset <- offset + VecConst.capacity
            i <- i + VecConst.capacity
            
    /// Removes the last value from a current vector, returning an updated vector with
    /// that value removed. Value is assinged to given out ref parameter. If current
    /// vector is empty, this method will raise an `InvalidOperationException`.
    member this.Pop(removed: 'a outref): Vec<'a> =
        match count with
        | 0 -> raise (InvalidOperationException "Cannot pop an empty vector")
        | 1 ->
            removed <- tail.[0]
            empty
        | i when i - VecConst.tailoff i > 1 ->
            removed <- tail.[tail.Length-1]
            let tail' = Array.zeroCreate (tail.Length-1)
            Array.blit tail 0 tail' 0 tail'.Length
            Vec<_>(i-1, shift, root, tail')
        | _ ->
            removed <- tail.[tail.Length-1]
            let tail' = this.FindArrayForIndex(count-2)
            let r = popTail shift root
            let children' =
                match r with
                | Branch c -> c
                | _ -> Array.zeroCreate VecConst.capacity
            let mutable shift' = shift
            let root' =
                if shift > 5 && obj.ReferenceEquals(children'.[1], Unchecked.defaultof<_>) then
                    shift' <- shift' - 5
                    children'.[0]
                else Branch children'
            Vec<_>(count-1, shift', root', tail')
            
    member internal this.AsBuilder(): VecBuilder<'a> =
        let (Branch children) = root
        let tail' = Array.zeroCreate VecConst.capacity
        Array.blit tail 0 tail' 0 tail.Length
        VecBuilder<_>(count, shift, Branch (Array.copy children), tail')
        
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
        
    override this.Equals(other: obj): bool =
        match other with
        | :? Vec<'a> as v -> this.Equals(v)
        | _ -> false
        
    override this.GetHashCode(): int =
        if count = 0 then 0
        else
            let eq = EqualityComparer<'a>.Default
            let mutable hash = 0
            let mutable e1 = new VecEnumerator<_>(this)
            while e1.MoveNext() do
                hash <- (hash * 397) + eq.GetHashCode(e1.Current)
            hash
            
    override this.ToString() =
        if count = 0 then "vec[]"
        else
            let sb = System.Text.StringBuilder("vec[")
            let mutable e1 = new VecEnumerator<_>(this)
            if e1.MoveNext() then
                sb.Append(e1.Current) |> ignore
            while e1.MoveNext() do
                sb.Append("; ").Append(e1.Current) |> ignore
            sb.Append(']').ToString()
        
    member this.CompareTo(other: Vec<_>): int =
        let eq = Comparer<'a>.Default
        let mutable e1 = new VecEnumerator<_>(this)
        let mutable e2 = new VecEnumerator<_>(other)
        let mutable result = 0
        let mutable ok = true
        while ok && result = 0 do
            if e1.MoveNext() then
                if e2.MoveNext() then
                    result <- eq.Compare(e1.Current, e2.Current)
                else
                    ok <- false
            elif e2.MoveNext() then
                ok <- false
                result <- eq.Compare(Unchecked.defaultof<_>, e2.Current)
            else
                ok <- false
                result <- eq.Compare(e1.Current, Unchecked.defaultof<_>)
        result
            
    interface IEquatable<Vec<'a>> with member this.Equals(other: Vec<_>): bool = this.Equals(other)                
    interface IComparable<Vec<'a>> with member this.CompareTo(other: Vec<_>): int = this.CompareTo(other)
                
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
            vector <- vector.Add(item)
        vector
        
    /// Constructs current vector out of the given enumerator.
    [<CompiledName("OfEnumerator")>]
    let ofEnumerator (e: #IEnumerator<'a> byref): Vec<'a> =
        let mutable vector = Vec<'a>.Empty()
        while e.MoveNext() do
            vector <- vector.Add(e.Current)
        vector
        
    /// Creates a new vector of given `size` filled with elements generated from provided function `fn`,
    /// which takes an index (0-based) from which an element will be created.
    [<CompiledName("Init")>]
    let init (size: int) (fn: int -> 'a): Vec<'a> =
        let t = empty.AsBuilder()
        for i = 0 to size - 1 do
            t.Add (fn i)
        t.ToImmutable()
        
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
    [<CompiledName("Add")>]
    let inline add (item: 'a) (v: Vec<_>) : Vec<_> = v.Add(item)
    
    let private appendArray (v: Vec<'a>) (items: 'a[]) =
        if items.Length = 0 then v
        else
            let mutable t = v.AsBuilder()
            t.AddRange(items)
            t.ToImmutable()
        
    let private appendSeq (v: Vec<'a>) (items: seq<'a>) =
        use e = items.GetEnumerator()
        if not (e.MoveNext()) then v
        else
            let mutable t = v.AsBuilder()
            t.Add e.Current
            while e.MoveNext() do t.Add e.Current
            t.ToImmutable()
    
    /// Inserts a collection of elements at the end of vector, returning new vector in the result.
    /// Complexity: O(m * log32(n)) - most of the time it's close to O(m), where m is the number of items to insert.
    [<CompiledName("Append")>]
    let append (v: Vec<'a>) (items: seq<'a>): Vec<'a> =
        if items.GetType().IsArray then appendArray v (downcast items)
        else appendSeq v items
        
    /// Replaces element an given `index` with provided `item`. Returns an updated vector and replaced element.
    [<CompiledName("Update")>]
    let inline replace (index: int) (item: 'a) (v: Vec<_>) : (Vec<_> * 'a) = v.Replace(index, item)
    
    /// Removes the last element of the vector `v` and returns an updated vector together with removed element.
    [<CompiledName("Pop")>]
    let inline pop (v: Vec<_>): (Vec<_> * 'a) = v.Pop()
    
    /// Returns a new vector with all elements of the provided vector `v` except the last one.
    [<CompiledName("Initial")>]
    let inline initial (v: Vec<_>): Vec<_> = v.Pop() |> fst
    
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
        let mutable t = empty.AsBuilder()
        for item in v do
            if f item then
                t.Add item
        t.ToImmutable()
            
    
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
        let t = empty.AsBuilder()
        for item in v do
            match f item with
            | None -> ()
            | Some m -> t.Add m
        t.ToImmutable()
            
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