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
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

module internal VecConst =
    
    [<Literal>] 
    let off = 5
    
    [<Literal>]
    let capacity = 32 // 1 << shift
    
    [<Literal>]
    let mask = 0x1f // capacity - 1
    
    let inline tailoff count = if count < capacity then 0 else ((count - 1) >>> off) <<< off
    
open VecConst

type internal VecNode<'a> =
    | Leaf   of array:'a[]
    | Branch of children: VecNode<'a>[] 
    static member internal Empty = Branch(Array.zeroCreate capacity)
    member this.Copy() =
        match this with
        | Leaf array -> Leaf (array.Clone() :?> 'a[])
        | Branch array -> Branch (array.Clone() :?> VecNode<'a>[])
    
[<IsByRefLike; Struct>]
type VecEnumerator<'a>(vector: Vec<'a>) =
    [<DefaultValue>]val mutable private array: 'a[]
    [<DefaultValue>]val mutable private index: int
    [<DefaultValue>]val mutable private offset: int
    member e.Current = e.array.[e.index &&& mask]
    member e.MoveNext() =
        let i = e.index
        if i < vector.Count then
            if isNull e.array then e.array <- vector.FindArrayForIndex(i)
            else
                if i - e.offset = capacity then
                    e.array <- vector.FindArrayForIndex(i)
                    e.offset <- e.offset + capacity
                e.index <- i + 1
            true
        else false
    interface IEnumerator<'a> with
        member this.Current: 'a = this.Current
        member this.Current: obj = upcast this.Current
        member this.Reset() =
            this.array <- null
            this.index <- 0
        member __.Dispose() = ()
        member this.MoveNext(): bool = this.MoveNext()
        
and VecRangedEnumerator<'a> =
    struct
        val private vector: Vec<'a>
        val mutable private array: 'a[]
        val mutable private index: int
        val mutable private offset: int
        val private stop: int
        val private start: int
        new (v: Vec<'a>, start: int, finish: int) =
            { vector = v;
              stop = finish;
              start = start;
              index = start;
              offset = (start - (start % capacity));
              array = null }
        member e.Current = e.array.[e.index &&& mask]
        member e.MoveNext() =
            let i = e.index
            if i < e.stop then
                if isNull e.array then e.array <- e.vector.FindArrayForIndex(i)
                else
                    if i - e.offset = capacity then
                        e.array <- e.vector.FindArrayForIndex(i)
                        e.offset <- e.offset + capacity
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
    
            
/// An immutable implementation of array-like data structure. It's based on
/// Bitmapped Vector Trie implementation and it offers a sane compromise
/// between append/insert and access to any element (which are all ~O(1)).
and [<Sealed>] Vec<'a> internal(count: int, shift: int, root: VecNode<'a>, tail: 'a[]) =
    
    let rec newPath level node =
        if level = 0 then node
        else
            let result = Array.zeroCreate capacity
            result.[0] <- newPath (level - off) node
            Branch result
    
    let rec appendTail (level: int) (Branch children) (tailNode: VecNode<'a>) =
        let subIdx = ((count - 1) >>> level) &&& mask            
        let result = Array.copy children
        let nodeToInsert =
            if level = off then tailNode
            else
                let child = children.[subIdx]
                if obj.ReferenceEquals(child, null) then appendTail (level-off) child tailNode
                else newPath (level-off) tailNode
        result.[subIdx] <- nodeToInsert
        Branch result
    
    static let empty = Vec<'a>(0, off, VecNode<'a>.Empty, [||])
    
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Empty(): Vec<'a> = empty 
    
    static member From(items: 'a[]) =
        let count = items.Length
        if count <= VecConst.capacity then
            let tail = Array.zeroCreate count
            items.CopyTo(tail, 0)
            Vec<'a>(count, off, VecNode<'a>.Empty, tail)
        else
            let mutable v = empty
            for item in items do
                v <- v.Append(item)
            v

    static member From(items: 'a seq) =
        let mutable vector = empty
        for item in items do
            vector <- vector.Append(item)
        vector
        
    member __.Count: int = count
    member this.GetEnumerator() = new VecEnumerator<'a>(this)
    
    member internal __.FindArrayForIndex(index: int): 'a[] =
        if index >= tailoff count then tail
        else
            let mutable node = root
            let mutable level = shift
            while level > 0 do
                match node with
                | Branch array ->
                    node <- array.[(index >>> level) &&& mask]
                | _ -> ()
                level <- level - shift
            let (Leaf array) = node
            array
    
    member __.Append(value) =
        if count - tailoff count < capacity
        then
            let tail' = Array.zeroCreate (tail.Length + 1)
            tail.CopyTo(tail', 0)
            tail'.[tail.Length] <- value
            Vec<'a>(count + 1, shift, root, tail')
        else
            // tail is full
            let tailNode = Leaf tail
            let mutable shift' = shift
            let root' =
                if (count >>> off) > (1 <<< shift)
                then
                    // overflow root
                    let arr = Array.zeroCreate capacity
                    arr.[0] <- root
                    arr.[1] <- newPath shift tailNode
                    shift' <- shift' + 5
                    Branch arr
                else
                    appendTail shift root tailNode
            Vec<'a>(count + 1, shift', root', [| value |])
            

    member this.ElementAt(index: int): 'a =
        if index >= 0 && index < count then
            let n = this.FindArrayForIndex index
            n.[index &&& mask]
        else raise (IndexOutOfRangeException(sprintf "Index [%i] out of range of vector (size: %i)" index count))
        
    member inline this.Item with get index = this.ElementAt index

    member this.IndexOf(value: 'a): int =
        let rec loop (enumerator: byref<VecEnumerator<'a>>) value i =
            if enumerator.MoveNext() && obj.Equals(enumerator.Current, value) then i
            else loop &enumerator value (i+1)
            
        let mutable enumerator = new VecEnumerator<'a>(this)
        loop &enumerator value 0

    member this.CopyTo(array: 'a[], arrayIndex: int) =
        let mutable offset = 0
        let mutable i = arrayIndex
        while offset < count do
            let src = this.FindArrayForIndex(offset)
            Array.Copy(src, 0, array, i, src.Length)
            offset <- offset + capacity
            i <- i + capacity

    interface IEquatable<Vec<'a>> with
        member this.Equals(other: Vec<'a>): bool =
            let rec loop (e1: VecEnumerator<'a>) (e2: VecEnumerator<'a>) = ()
            if obj.ReferenceEquals(other, null) then false
            elif obj.ReferenceEquals(this, other) then false
            elif count <> other.Count then false
            else true
                
    interface IEnumerable<'a> with
        member this.GetEnumerator(): IEnumerator<'a> = upcast this.GetEnumerator()
        member this.GetEnumerator(): System.Collections.IEnumerator = upcast this.GetEnumerator()
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

[<RequireQualifiedAccess>]
module Vec =
    
    /// Returns an empty vector.
    let inline empty<'a> = Vec<'a>.Empty()
    
    /// Constructs current vector out of the given array.
    [<CompiledName("OfArray")>]
    let inline ofArray (a: 'a[]): Vec<'a> = Vec<'a>.From(a)
    
    /// Constructs current vector out of the given sequence of elements.
    [<CompiledName("OfEnumerable")>]
    let inline ofSeq (a: 'a seq): Vec<'a> = Vec<'a>.From(a)
        
    /// Copies contents of current vector into new array and returns it.
    [<CompiledName("ToArray")>]
    let toArray (a: Vec<'a>): 'a[] = 
        let array = Array.zeroCreate a.Count
        a.CopyTo(array, 0)
        array
    
    /// Checks if current vector contains no elements. Complexity: O(1).
    [<CompiledName("IsEmpty")>]
    let inline isEmpty (v: Vec<'a>): bool = v.Count = 0
    
    /// Returns a length of the current vector.
    /// Complexity: O(1).
    [<CompiledName("Length")>]
    let inline length (v: Vec<'a>): int = v.Count
    
    /// Inserts an element at the end of vector, returning new vector in the result.
    /// Complexity: O(log32(n)) - most of the time it's close to O(1).
    [<CompiledName("Append")>]
    let inline append (item: 'a) (v: Vec<'a>) : Vec<'a> = v.Append(item)
    
    /// Inserts an element at the beginning of the vector, returning new vector in the result.
    /// Complexity: O(n) - requires copying entire vector.
    [<CompiledName("Prepend")>]
    let prepend (item: 'a) (v: Vec<'a>) : Vec<'a> = failwith "not implemented"
    
    /// Inserts an element at the given position of the vector, returning new vector in the result.
    /// Complexity: O(m + log32(n-m)) where i is the number of elements after given index.
    /// The closer to the end of the vector, the less expensive it is.
    [<CompiledName("Insert")>]
    let insert (index: int) (item: 'a) (v: Vec<'a>) : Vec<'a> = failwith "not implemented"

    [<CompiledName("InsertRange")>]
    let insertRange (index: int) (items: #seq<'a>) (v: Vec<'a>) : Vec<'a> = failwith "not implemented"
    
    /// Replaces element an given `index` with provided `item`. Returns an updated vector and replaced element.
    [<CompiledName("Update")>]
    let replace (index: int) (item: 'a) (v: Vec<'a>) : (Vec<'a> * 'a) = failwith "not implemented"
    
    /// Returns first element of a vector or None if vector is empty.
    /// Complexity: O(log32(n)).
    [<CompiledName("First")>]
    let head (v: Vec<'a>) : 'a voption = if isEmpty v then ValueNone else ValueSome(v.ElementAt 0)
    
    /// Returns the last element of a vector or None if vector is empty.
    /// Complexity: O(log32(n)).
    [<CompiledName("Last")>]
    let last (v: Vec<'a>): 'a voption = if isEmpty v then ValueNone else ValueSome(v.ElementAt (v.Count - 1))
    
    /// Returns vector element at provided index, or None if index is out of bound of a vector.
    [<CompiledName("ElementAt")>]
    let nth (index: int) (v: Vec<'a>): 'a voption = 
        if index >= 0 && index < v.Count then
            ValueSome(v.ElementAt index)
        else ValueNone
    
    /// Concatenates two vectors, returning new vector with elements of vector `a` first, then elements of vector `b` second.
    /// Complexity: O(b.length) - requires copying elements of b.
    [<CompiledName("Concat")>]
    let concat (a: Vec<'a>) (b: Vec<'a>): Vec<'a> = failwith "not implemented"
    
    [<CompiledName("IndexOf")>]
    let inline indexOf (item: 'a) (v: Vec<'a>): int = v.IndexOf item
    
    [<CompiledName("Contains")>]
    let inline contains (item: 'a) (v: Vec<'a>): bool = v.IndexOf item <> -1
    
    [<CompiledName("ForEach")>]
    let inline iter (f: 'a -> unit) (v: Vec<'a>): unit =
        let mutable e = new VecEnumerator<'a>(v)
        while e.MoveNext() do
            f e.Current
            
    [<CompiledName("ForEach")>]
    let inline iteri (f: int -> 'a -> unit) (v: Vec<'a>): unit =
        let mutable i = 0
        let mutable e = new VecEnumerator<'a>(v)
        while e.MoveNext() do
            f i e.Current
            i <- i + 1
    
    [<CompiledName("FirstOrDefault")>]
    let find (fn: 'a -> bool) (v: Vec<'a>): 'a voption =
        let rec loop (e: VecEnumerator<'a> byref) fn =
            if e.MoveNext() then
                if fn e.Current then ValueSome e.Current
                else loop &e fn
            else ValueNone
        let mutable e = new VecEnumerator<'a>(v)
        loop (&e) fn
    
    [<CompiledName("Map")>]
    let map (f: 'a -> 'b) (v: Vec<'a>): Vec<'b> = failwith "not implemented"
    
    [<CompiledName("MapWithIndex")>]
    let mapi (f: int -> 'a -> 'b) (v: Vec<'a>): Vec<'b> = failwith "not implemented"
    
    [<CompiledName("Filter")>]
    let filter (f: 'a -> bool) (v: Vec<'a>): Vec<'a> = failwith "not implemented"
    
    [<CompiledName("Exists")>]
    let exists (fn: 'a -> bool) (v: Vec<'a>): bool = 
        let rec loop (e: VecEnumerator<'a> byref) fn =
            if e.MoveNext() then
                if fn e.Current then true
                else loop &e fn
            else false
        let mutable e = new VecEnumerator<'a>(v)
        loop (&e) fn
    
    [<CompiledName("Choose")>]
    let choose (f: 'a -> 'b option) (v: Vec<'a>): Vec<'b> = failwith "not implemented"
    
    [<CompiledName("Slice")>]
    let slice (low: int) (high: int) (v: Vec<'a>): VecRangedEnumerator<'a> = new VecRangedEnumerator<'a>(v, low, high)
    
    [<CompiledName("Fold")>]
    let fold (f: 'b -> 'a -> 'b) (init: 'b) (v: Vec<'a>): 'b =
        let mutable e = new VecEnumerator<'a>(v) 
        let mutable state = init
        while e.MoveNext() do
            state <- f state e.Current
        state

    [<CompiledName("Reduce")>]
    let reduce (f: 'a -> 'a -> 'a) (v: Vec<'a>): 'a voption =
        let mutable e = new VecEnumerator<'a>(v)
        if not (e.MoveNext()) then ValueNone
        else
            let mutable state = e.Current
            while e.MoveNext() do
                state <- f state e.Current
            ValueSome state 