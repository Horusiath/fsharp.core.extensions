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
open System.Buffers

#nowarn "9"

[<RequireQualifiedAccess>]
module Span =

    open FSharp.NativeInterop
    
    /// Returns an empty span.
    let empty<'a> : Span<'a> = Span<'a>.Empty
    
    /// Returns a Span build from memory allocated on stack.
    let inline stackalloc<'a when 'a: unmanaged> (length: int): Span<'a> =
        let p = NativePtr.stackalloc<'a> length |> NativePtr.toVoidPtr
        Span<'a>(p, length)
        
    /// Returns a Span build from a given `ArraySegment` and its defined boundaries.
    let inline ofSegment (s: ArraySegment<'a>) = Span(s.Array, s.Offset, s.Count)
        
    /// Wraps provided array into a Span.
    let inline ofArray (a: 'a[]): Span<'a> = Span(a)
    
    /// Creates a Span out of (*void) pointer with a given byte length.
    let inline ofPtr length (ptr: voidptr): Span<'a> = Span(ptr, length)

    /// Changes Span into ReadOnlySpan.
    let inline readOnly (span: Span<_>): ReadOnlySpan<_> = Span.op_Implicit span
    
    /// Slices current span, returning a narrowed window.
    let inline slice offset length (span: Span<_>) = span.Slice(offset, length)
    
    /// Returns an `n`-th element of a span, 0-based.
    let inline nth n (span: Span<_>) = span.[n];
    
    /// Copies contents of current span into an array, which is then returned.
    let inline toArray (span: Span<_>) = span.ToArray()
    
    /// Returns a length of a current `span`.
    let inline length (span: Span<_>) = span.Length
    
    /// Checks if current span is `empty`.
    let inline isEmpty (span: Span<_>) = span.IsEmpty
    
    /// Copies content of `source` span into `destination`. If copy is not possible (eg. because
    /// `destination` size is too small), an exception will be thrown.
    let inline copyTo (destination: Span<_>) (source: Span<_>) = source.CopyTo(destination)
    
    /// Copies content of `source` span into `destination`. If copy is not possible (eg. because
    /// `destination` size is too small), an operation will abort and `false` will be returned.
    let inline tryCopyTo (destination: Span<_>) (source: Span<_>) = source.TryCopyTo(destination)
    
[<RequireQualifiedAccess>]
module ReadOnlySpan =
    
    /// Returns an empty read-only span.
    let empty<'a> : ReadOnlySpan<'a> = ReadOnlySpan<'a>.Empty
    
    /// Returns a ReadOnlySpan build from a given `ArraySegment` and its defined boundaries.
    let inline ofSegment (s: ArraySegment<'a>) = ReadOnlySpan(s.Array, s.Offset, s.Count)

    /// Wraps provided array into a ReadOnlySpan.
    let inline ofArray (a: 'a[]): ReadOnlySpan<'a> = ReadOnlySpan(a)
    
    /// Creates a Span out of (*void) pointer with a given byte length.
    let inline ofPtr length (ptr: voidptr): ReadOnlySpan<'a> = ReadOnlySpan(ptr, length)
    
    /// Converts current string into a read-only span of UTF-16 characters.
    let inline ofString (str: string): ReadOnlySpan<char> = str.AsSpan()
    
    /// Slices current span, returning a narrowed window.
    let inline slice offset length (span: ReadOnlySpan<_>) = span.Slice(offset, length)
    
    /// Returns an `n`-th element of a span, 0-based.
    let inline nth n (span: ReadOnlySpan<_>) = span.[n];
    
    /// Copies contents of current span into an array, which is then returned.
    let inline toArray (span: ReadOnlySpan<_>) = span.ToArray()
    
    /// Returns a length of a current `span`.
    let inline length (span: ReadOnlySpan<_>) = span.Length
    
    /// Checks if current span is `empty`.
    let inline isEmpty (span: ReadOnlySpan<_>) = span.IsEmpty
    
    /// Copies content of `source` span into `destination`. If copy is not possible (eg. because
    /// `destination` size is too small), an exception will be thrown.
    let inline copyTo (dst: Span<_>) (span: ReadOnlySpan<_>) = span.CopyTo(dst)
    
    /// Copies content of `source` span into `destination`. If copy is not possible (eg. because
    /// `destination` size is too small), an operation will abort and `false` will be returned.
    let inline tryCopyTo (dst: Span<_>) (span: ReadOnlySpan<_>) = span.TryCopyTo(dst)
    
    /// Checks, if contents of both readonly spans are the same.
    let inline eq (a: ReadOnlySpan<_>) (b: ReadOnlySpan<_>) = MemoryExtensions.SequenceEqual(a, b)
    
    /// Compares contents of first readonly span with a second one.
    let inline cmp (a: ReadOnlySpan<_>) (b: ReadOnlySpan<_>) = MemoryExtensions.SequenceCompareTo(a, b)

[<RequireQualifiedAccess>]
module Memory =
    
    /// Returns an empty span.
    let empty<'a> : Memory<'a> = Memory<'a>.Empty
    
    /// Rents a Memory segment from a shared memory pool. Memory pool will respect
    /// lower bound, therefore always returning memory having at least `minCapacity`.
    ///
    /// Memory buffers returned are grouped in buckets, at the moment of .NET v3.1 the lowest
    /// one being 4KiB, up to 2MiB. This means that for any value under 4KiB, a 4KiB buffer
    /// will be returned. For `minCapacity` over 2MiB, a pooling is implicitly ignored,
    /// so any rent/disposal cycle will allocate/return memory straight by using GC. 
    ///
    /// Returned object is a disposable resource. When disposed it will return memory buffer
    /// back to the pool. 
    let inline rent(minCapacity: int): IMemoryOwner<_> = MemoryPool.Shared.Rent(minCapacity)
    
    /// Returns a Memory build from a given `ArraySegment` and its defined boundaries.
    let inline ofSegment (s: ArraySegment<'a>) = Memory(s.Array, s.Offset, s.Count)
    
    /// Wraps provided array into a memory.
    let inline ofArray (a: 'a[]): Memory<'a> = Memory(a)
    
    /// Creates a memory out of (*void) pointer with a given byte length.
    let inline ofArrayBounded offset length (a: 'a[]): Memory<'a> = Memory(a, offset, length)

    /// Changes memory into ReadOnlymemory.
    let inline readOnly (memory: Memory<_>): ReadOnlyMemory<_> = Memory.op_Implicit memory
    
    /// Slices current memory, returning a narrowed window.
    let inline slice offset length (memory: Memory<_>) = memory.Slice(offset, length)
    
    /// Returns span of a current memory.
    let inline span (memory: Memory<_>) = memory.Span
    
    /// Pins current memory segment, returning a handler to it.
    let inline pin (memory: Memory<_>) = memory.Pin()
    
    /// Copies contents of current memory into an array, which is then returned.
    let inline toArray (memory: Memory<_>) = memory.ToArray()
    
    /// Returns a length of a current `memory`.
    let inline length (memory: Memory<_>) = memory.Length
    
    /// Checks if current memory is `empty`.
    let inline isEmpty (memory: Memory<_>) = memory.IsEmpty
    
    let inline copyTo (dst: Memory<_>) (span: Memory<_>) = span.CopyTo(dst)
    
    let inline tryCopyTo (dst: Memory<_>) (span: Memory<_>) = span.TryCopyTo(dst)
    
[<RequireQualifiedAccess>]
module ReadOnlyMemory =
        
    /// Returns an empty span.
    let empty<'a> : ReadOnlyMemory<'a> = ReadOnlyMemory<'a>.Empty
    
    let inline ofSegment (s: ArraySegment<'a>) = ReadOnlyMemory(s.Array, s.Offset, s.Count)

    /// Wraps provided array into a memory.
    let inline ofArray (a: 'a[]): ReadOnlyMemory<'a> = ReadOnlyMemory(a)
    
    /// Creates a memory out of (*void) pointer with a given byte length.
    let inline ofArrayBounded offset length (a: 'a[]): ReadOnlyMemory<'a> = ReadOnlyMemory(a, offset, length)

    /// Slices current memory, returning a narrowed window.
    let inline slice offset length (memory: ReadOnlyMemory<_>) = memory.Slice(offset, length)
    
    /// Returns span of a current `memory`.
    let inline span (memory: ReadOnlyMemory<_>) = memory.Span
    
    /// Pins current memory segment, returning a handler to it.
    let inline pin (memory: ReadOnlyMemory<_>) = memory.Pin()
    
    /// Copies contents of current memory into an array, which is then returned.
    let inline toArray (memory: ReadOnlyMemory<_>) = memory.ToArray()
    
    /// Returns a length of a current `memory`.
    let inline length (memory: ReadOnlyMemory<_>) = memory.Length
    
    /// Checks if current memory is `empty`.
    let inline isEmpty (memory: ReadOnlyMemory<_>) = memory.IsEmpty
    
    let inline copyTo (dst: Memory<_>) (memory: ReadOnlyMemory<_>) = memory.CopyTo(dst)
    
    let inline tryCopyTo (dst: Memory<_>) (memory: ReadOnlyMemory<_>) = memory.TryCopyTo(dst)
    
[<RequireQualifiedAccess>]
module ReadOnlySequence =
    
    /// Returns an empty readonly sequence.
    let empty<'a> : ReadOnlySequence<'a> = ReadOnlySequence<'a>.Empty
    
    /// Returns length of a readonly `sequence`.
    let inline length (sequence: ReadOnlySequence<'a>) = sequence.Length
    
    /// Returns true is current readonly `sequence` is empty. False otherwise.
    let inline isEmpty (sequence: ReadOnlySequence<'a>) = sequence.IsEmpty
    
    /// Returns true if current readonly `sequence` consists only of a single continuous block of memory.
    let inline isSingleSegment (sequence: ReadOnlySequence<'a>) = sequence.IsSingleSegment
    
    let inline start (sequence: ReadOnlySequence<'a>) = sequence.Start
    
    let inline finish (sequence: ReadOnlySequence<'a>) = sequence.End
    
    let inline slice (offset: int64) (length: int64) (sequence: ReadOnlySequence<'a>) = sequence.Slice(offset, length)
    
    let inline between (start: SequencePosition) (finish: SequencePosition) (sequence: ReadOnlySequence<'a>) =
        sequence.Slice(start, finish)
        
    /// Returns first memory segment of a current readonly `sequence`.
    let inline head (sequence: ReadOnlySequence<'a>) = sequence.First
    
    /// Returns first memory segment of a current readonly `sequence` or None if sequence is empty.
    let inline tryHead (sequence: ReadOnlySequence<'a>): ReadOnlyMemory<'a> voption =
        if isEmpty sequence then ValueNone else ValueSome sequence.First
        
    /// Copies contents of a current `sequence` into a provided `span`.
    let inline copyTo (span: Span<_>) (sequence: ReadOnlySequence<'a>) = sequence.CopyTo(span)
    
    /// Copies contents of a current readonly `sequence` to array and returns it.
    let inline toArray (sequence: ReadOnlySequence<'a>) = sequence.ToArray()
    
    /// Returns a sequence position of a provided `offset` within current `sequence`. 
    let inline positionOf (offset: int64) (sequence: ReadOnlySequence<'a>) = sequence.GetPosition(offset)
    
    /// Returns a sequence position of a provided `offset` within current `sequence`. 
    let inline positionFrom (offset: int64) (start: SequencePosition) (sequence: ReadOnlySequence<'a>) =
        sequence.GetPosition(offset, start)
    
    let inline tryAdvance (position) (memory) (sequence: ReadOnlySequence<'a>) =
        sequence.TryGet(position, memory, true)
        
    let inline tryGet (position) (memory) (sequence: ReadOnlySequence<'a>) =
        sequence.TryGet(position, memory, false)