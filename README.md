# FSharp.Core.Extensions

This library contains a set of utilities to support building efficient, concurrent programs using functional paradigm in F#, like:

- [x] `atom` - an equivalent of F# `ref` cells, which major difference is that all operations (reads and updates) are **thread-safe**, yet executed without expensive OS-level locks, relying fully on lock-free data structures instead.
- [x] `Vec` - an immutable efficient array-like data structure, with fast random reads, element traversal and append to the tail operations. Operations like `Vec.add` and `Vec.item` are *O(log32(n))*, which for in-memory data structure goes close to *O(1)*. Usually a performance is better than that of F# list or System.Collection.Immutable data structures for supported operations. 
- [x] `Hlc` which is an implementation of [hybrid-logical time protocol](http://users.ece.utexas.edu/~garg/pdslab/david/hybrid-time-tech-report-01.pdf), that works as a middle-ground between wall-clock UTC time (with precision up to millisecond) and monotonicity guarantees (originally `DateTime.UtcNow` doesn't guarantee, that returned value will always be greater than the one previously obtained due to nature of NTP and phenomenas like leap seconds).
- [x] `Random` which provides a **thread-safe** module for generating random operations. It also contains few extra functions not supplied originally by `System.Random` class.
- [x] `LazyTask` which is going to work like `lazy (factory)` data structure, but the factory method can return a `Task`. In order to make this data structure non-blocking, the `lazyTask.Value` call itself returns a task.
  - [ ] *TODO: make a `lazyTask { ... }` computation expression.*
- [ ] *In progress: an implementation of [Persistent Adaptative Radix Tree](https://ankurdave.com/dl/part-tr.pdf).*
- [x] `Enum` module, which provides a `Seq`-like module functions (`map`, `filter` etc.), that are optimized to work directly on enumerators. The difference is that, Enum is aware of generic nature of enumerators, so it can inline and devirtualize certain calls.
- [x] Bunch of utility functions:
  - [x] Helper functions to work with `Task` and `Channel` (including multi-channel `select` operator).
  - [x] `Array` and `Array.Simd` which provide vectorized variants of some operations (like `Array.Simd.contains`) that can be much faster.
  - [x] Utility functions in form of `Span`, `ReadOnlySpan`, `Memory`, `ReadOnlyMemory` and `ReadOnlySequence` modules.
  - [x] Extra functions to `Map` type.
  - [x] Other functions I missed from time to time:
    - `~%` operator as equivalent of `op_Implicit` cast.
    - `=>` which creates a KeyValuePair out of its parameters.
    - `*` multiply operator for `TimeSpan`.  
- [x] `AsyncSeq` module which enables dozens of operators over `IAsyncEnumerable<'a>` interface, including element transformation, adding time dimensions, stream joining and splitting:

## API

### Atomic

`atom` is meant to be an almost drop-in replacement of F# `ref` cells. It offers a read/update operations that use efficient interlocked semantics to perform a thread-safe read/modify access on all supported operations.

```fsharp
open FSharp.Core
open FSharp.Core.Atomic.Operators
// Create an atomic cell. Currently it's limited to work only with:
//
// - Reference types (classes, non-struct records, discriminated unions, tuples or anonymous objects)
// - 32- and 64-bit integers (NOTE: interlocked 64-bit operations on 32-bit OSes are downgraded by .NET runtime)
// - booleans
// - 32- and 64-bit floating point numbers
// - booleans
let a = atom "hello"

// Receive stored value
!a  // returns: "hello"

// Replace stored value with a new one and return the previously stored one
a := "Hello" // returns: "hello"

// Those operations can be joined together to receive swap mechanics with no temporary variables necessary
let a = atom 1
let b = atom 2
a := b := !a |> ignore // swap contents of `a` and `b`. Now !a = 2 and !b = 1

// You can also increment/decrement atomic values, effectivelly turning it into thread-safe counter
let a = atom 1
Atomic.incr a // returns 2

// You can also perform a conditional updates a.k.a. Compare-And-Swap or Compare-Exchange.
// NOTE: for reference types it uses a reference equality (pointer check) instead of structural equality
let value = "hello"
let a = atom value
if a |> Atomic.cas value "new-value" then
    printfn "Successfully updated value of `a` to %s" !a
else
    printfn "Value of `a` has been updated in the meantime to %s" !a

// If you want to perform a thread safe update no matter if 
// atomic cell is concurrently modified by other threads you can still do that:
let users = atom Set.empty
// keep in mind, that update function should be pure and lightweight, as it may be called multiple times
users |> Atomic.update (fun s -> Set.add "Alice" s) //returns: a state of Set prior to update
```

#### Atomic update performance

Here's the performance check of given operations - the operation is about getting reading a stored value, modifying it, storing and returning updated result. All of these operations must happen in thread-safe manner. We're comparing:

- *InterlockedLoop* as a baseline which uses an `Interlocked.CompareExchange` done in a loop until value has been safelly updated.
- *AtomicUpdate* which is a variant above wrapped into a generic functionality of `Atomic.update` function.
- *ObjectLock* which uses `lock` method over object sync root.
- *Mutex* which uses `Mutex` as a tool of control.
- *SemaphoreSlim* which uses `SemaphoreSlim` - an optimized variant of mutex/semaphore.

|          Method |        Mean |     Error |    StdDev |  Ratio | RatioSD | Gen 0 | Gen 1 | Gen 2 | Allocated |
|---------------- |------------:|----------:|----------:|-------:|--------:|------:|------:|------:|----------:|
| InterlockedLoop |    16.03 ns |  0.078 ns |  0.069 ns |   1.00 |    0.00 |     - |     - |     - |         - |
|    AtomicUpdate |    16.11 ns |  0.106 ns |  0.099 ns |   1.00 |    0.01 |     - |     - |     - |         - |
|      ObjectLock |    36.18 ns |  0.204 ns |  0.190 ns |   2.26 |    0.01 |     - |     - |     - |         - |
|           Mutex | 3,225.95 ns | 29.755 ns | 24.847 ns | 201.37 |    1.95 |     - |     - |     - |         - |
|   SemaphoreSlim |   103.32 ns |  0.965 ns |  0.903 ns |   6.45 |    0.06 |     - |     - |     - |         - |


### Vec

`Vec<'a>` or `'a vec` is a persistent immutable data type, which provides a fairly fast add/remove operations (which append value to the tail of the collection as oposed to list), which are *O(log32(n))* (which for an in-memory data structure is effectivelly close to *O(1)*). It's also optimized for `for` loop traversals which are close in speed to native array.

```fsharp
// Create an empty vector.
let a = Vec.empty
// Initialize vector with function mapping indexes (0-based) to elements.
let a = Vec.init 100 (fun i -> i * i) 

// ... or initialize it from other collection
let a = Vec.ofArray [|1;2;3|]
let b = Vec.ofSeq [1;2;3]

// Check if it's empty
if Vec.isEmpty a then printfn "Vector is empty"
// ... or get number of stored elements
let len = Vec.length a

// Add new element to the tail
let a = Vec.ofArray [|1;2|]
let b = a |> Vec.add 3 // result: b = [|1;2;3|]
// ... or add entire collection of elements at once
let c = a |> Vec.append [3;4;5] // result: c = [|1;2;3;4;5|]

// You can also replace an element at given index (without changing original structure)
let a = Vec.ofSeq ["Hello"; "world"; "!"]
let d = c |> Vec.replace 1 "F#" // result d = [|"Hello"; "F#"; "!"|]

// You can treat Vec as immutable stack
let a = Vec.ofSeq [1;2;3]
let (b, last) = Vec.pop a //result: b = [|1;2|], last = 3
// ... or just drop the last value
let b = Vec.initial a

// Vectors offer fast random access by index
let a = Vec.init 100 (fun i -> i * i)
let b = a |> Vec.item 51 // returns: ValueSome 2500
// You can also try to find an index of a value (if it exists), starting from either beginning or end of a vector
let x = a |> Vec.indexOf -199 // in case when value is not found -1 is returned
let y = a |> Vec.lastIndexOf 100

// There are also helper methods for a first and last element
let (ValueSome first) = a |> Vec.head
let (ValueSome last) =  a |> Vec.last

// Just like array, vector also has `contains` function
if Vec.contains 100 a then printfn "Value 100 exists within a vector"

// ...or you can use more generic checkers
let exists = a |> Vec.exists (fun x -> x % 2 = 0)
let (ValueSome x) = a |> Vec.find (fun x -> x % 2 = 0)
```

... and other common collection operations like: `iter`, `iteri`, `map`, `mapi`, `filter`, `choose`, `forall`, `fold`, `foldBack`, `reduce` and `rev` (reverse). 

#### `Vec<'a>` benchmarks

Belows are some of the benchmarks for this implementation, all of them are running on the same machine configuration:

```ini
BenchmarkDotNet=v0.12.0, OS=Windows 10.0.18362
Intel Core i7-7660U CPU 2.50GHz (Kaby Lake), 1 CPU, 4 logical and 2 physical cores
.NET Core SDK=3.0.100
  [Host]     : .NET Core 3.0.0 (CoreCLR 4.700.19.46205, CoreFX 4.700.19.46214), X64 RyuJIT DEBUG
  DefaultJob : .NET Core 3.0.0 (CoreCLR 4.700.19.46205, CoreFX 4.700.19.46214), X64 RyuJIT
```

#### Appending

Here, we're appending elements one by one in a loop of 1, 100, 1000 and 10 000 items (starting with an empty collection). Compared implementations are:

- *MutableListAppend* used as a baseline - it represents `System.Collections.Generic.List<'a>.Add(x)`.
- *ImmutableListAppend* represents `System.Collections.Immutable.ImmutableList<'a>.Add(x)`.
- *ImmutableArrayAppend* represents `System.Collections.Immutable.ImmutableArray<'a>.Add(x)`.
- *VecAppend* which is a immutable vector as implemented by this library. 
- *FSharpxVectorAppend* represents a `PersistentVector<'a>` from [FSharpx.Collections](https://github.com/fsprojects/FSharpx.Collections), which is another implementation of the same data structure as the one implemented here. 

|               Method | to_append |             Mean |          Error |         StdDev |           Median |  Ratio | RatioSD |       Gen 0 |    Gen 1 |   Gen 2 |   Allocated |
|--------------------- |---------- |-----------------:|---------------:|---------------:|-----------------:|-------:|--------:|------------:|---------:|--------:|------------:|
|    MutableListAppend |         1 |         26.95 ns |       1.407 ns |       4.038 ns |         25.33 ns |   1.00 |    0.00 |      0.0421 |        - |       - |        88 B |
|  ImmutableListAppend |         1 |         45.65 ns |       0.992 ns |       1.018 ns |         45.61 ns |   1.96 |    0.13 |      0.0344 |        - |       - |        72 B |
| ImmutableArrayAppend |         1 |         14.25 ns |       0.319 ns |       0.425 ns |         14.11 ns |   0.58 |    0.08 |      0.0153 |        - |       - |        32 B |
|  FSharpxVectorAppend |         1 |        177.47 ns |       5.538 ns |      16.328 ns |        174.50 ns |   6.79 |    1.35 |      0.2561 |        - |       - |       536 B |
|            VecAppend |         1 |         38.02 ns |       0.848 ns |       1.712 ns |         37.23 ns |   1.54 |    0.17 |      0.0343 |        - |       - |        72 B |
|                      |           |                  |                |                |                  |        |         |             |          |         |             |
|    MutableListAppend |       100 |        689.72 ns |       4.335 ns |       4.055 ns |        690.22 ns |   1.00 |    0.00 |      1.0481 |        - |       - |      2192 B |
|  ImmutableListAppend |       100 |     17,839.33 ns |     115.082 ns |      96.099 ns |     17,837.45 ns |  25.86 |    0.16 |     16.5710 |        - |       - |     34704 B |
| ImmutableArrayAppend |       100 |      5,690.08 ns |     112.959 ns |     150.797 ns |      5,615.92 ns |   8.32 |    0.29 |     20.4620 |        - |       - |     42800 B |
|  FSharpxVectorAppend |       100 |      6,491.06 ns |     129.398 ns |     108.053 ns |      6,476.84 ns |   9.41 |    0.17 |     12.1002 |        - |       - |     25304 B |
|            VecAppend |       100 |      5,610.23 ns |      91.306 ns |      85.407 ns |      5,577.25 ns |   8.13 |    0.13 |      9.6283 |        - |       - |     20136 B |
|                      |           |                  |                |                |                  |        |         |             |          |         |             |
|    MutableListAppend |      1000 |      4,653.65 ns |      68.112 ns |      63.712 ns |      4,649.60 ns |   1.00 |    0.00 |      7.9346 |        - |       - |     16600 B |
|  ImmutableListAppend |      1000 |    341,368.64 ns |   6,736.781 ns |   8,759.719 ns |    340,537.92 ns |  74.03 |    1.67 |    240.2344 |        - |       - |    502896 B |
| ImmutableArrayAppend |      1000 |    380,422.74 ns |   8,284.896 ns |  23,637.275 ns |    368,581.81 ns |  83.57 |    5.28 |   1922.8516 |        - |       - |   4028000 B |
|  FSharpxVectorAppend |      1000 |     70,497.83 ns |     964.505 ns |     902.198 ns |     70,390.37 ns |  15.15 |    0.22 |    121.0938 |        - |       - |    253320 B |
|            VecAppend |      1000 |     61,596.14 ns |   1,686.168 ns |   2,523.776 ns |     60,990.33 ns |  13.28 |    0.63 |     98.1445 |        - |       - |    205400 B |
|                      |           |                  |                |                |                  |        |         |             |          |         |             |
|    MutableListAppend |     10000 |    144,939.65 ns |   3,632.198 ns |   3,219.849 ns |    143,854.16 ns |   1.00 |    0.00 |     82.5195 |  41.5039 | 41.5039 |    262456 B |
|  ImmutableListAppend |     10000 |  5,822,711.41 ns |  51,205.254 ns |  47,897.426 ns |  5,831,860.16 ns |  40.16 |    0.92 |   1070.3125 | 281.2500 | 39.0625 |   6653616 B |
| ImmutableArrayAppend |     10000 | 40,742,758.37 ns | 762,077.943 ns | 782,597.940 ns | 40,635,738.46 ns | 281.91 |    9.14 | 188538.4615 |        - |       - | 400280000 B |
|  FSharpxVectorAppend |     10000 |  1,124,129.70 ns |  15,587.402 ns |  14,580.466 ns |  1,119,198.63 ns |   7.75 |    0.19 |   1087.8906 | 220.7031 |  1.9531 |   2624120 B |
|            VecAppend |     10000 |    962,364.54 ns |  18,915.218 ns |  15,795.064 ns |    963,077.54 ns |   6.64 |    0.22 |    881.8359 | 164.0625 |  3.9063 |   2146433 B |

Note: since FSharpx implementation internally represents stored elements as `obj`, in case of value types there's an additional cost related to boxing.

#### Iterating

Here, we're iterating over 10, 1000 and 200 000 elements of the collections in a `for item in collection do` loop to check the data structure overhead over that operation:

- *MutableListEnumerate* used as a baseline - it represents `System.Collections.Generic.List<'a>.Add(x)`.
- *ImmutableListEnumerate* represents `System.Collections.Immutable.ImmutableList<'a>.Add(x)`.
- *ImmutableArrayEnumerate* represents `System.Collections.Immutable.ImmutableArray<'a>.Add(x)`.
- *VecEnumerate* which is a immutable vector as implemented by this library. 
- *FSharpxVectorEnumerate* represents a `PersistentVector<'a>` from [FSharpx.Collections](https://github.com/fsprojects/FSharpx.Collections), which is another implementation of the same data structure as the one implemented here.

|                  Method |  count |             Mean |          Error |         StdDev |           Median | Ratio | RatioSD |  Gen 0 | Gen 1 | Gen 2 | Allocated |
|------------------------ |------- |-----------------:|---------------:|---------------:|-----------------:|------:|--------:|-------:|------:|------:|----------:|
|    MutableListEnumerate |     10 |         52.33 ns |       0.536 ns |       0.501 ns |         52.34 ns |  1.00 |    0.00 |      - |     - |     - |         - |
|  ImmutableListEnumerate |     10 |        876.14 ns |      20.904 ns |      29.980 ns |        873.56 ns | 16.77 |    0.74 |      - |     - |     - |         - |
| ImmutableArrayEnumerate |     10 |         16.33 ns |       0.391 ns |       0.327 ns |         16.32 ns |  0.31 |    0.01 |      - |     - |     - |         - |
|  FSharpxVectorEnumerate |     10 |        315.96 ns |      19.858 ns |      58.552 ns |        331.66 ns |  4.82 |    0.31 | 0.1109 |     - |     - |     232 B |
|            VecEnumerate |     10 |         71.13 ns |       2.137 ns |       1.999 ns |         70.47 ns |  1.36 |    0.04 |      - |     - |     - |         - |
|                         |        |                  |                |                |                  |       |         |        |       |       |           |
|    MutableListEnumerate |   1000 |      3,651.23 ns |     128.698 ns |     375.417 ns |      3,488.58 ns |  1.00 |    0.00 |      - |     - |     - |         - |
|  ImmutableListEnumerate |   1000 |     67,096.30 ns |   1,327.362 ns |   3,076.364 ns |     66,223.86 ns | 17.70 |    1.48 |      - |     - |     - |         - |
| ImmutableArrayEnumerate |   1000 |      2,047.00 ns |      13.241 ns |      12.385 ns |      2,043.86 ns |  0.50 |    0.04 |      - |     - |     - |         - |
|  FSharpxVectorEnumerate |   1000 |     15,226.12 ns |     296.629 ns |     329.702 ns |     15,059.95 ns |  3.73 |    0.33 | 0.0916 |     - |     - |     232 B |
|            VecEnumerate |   1000 |      4,625.08 ns |      45.560 ns |      38.045 ns |      4,614.30 ns |  1.12 |    0.09 |      - |     - |     - |         - |
|                         |        |                  |                |                |                  |       |         |        |       |       |           |
|    MutableListEnumerate | 200000 |    689,594.71 ns |  16,050.917 ns |  15,014.038 ns |    685,333.79 ns |  1.00 |    0.00 |      - |     - |     - |         - |
|  ImmutableListEnumerate | 200000 | 18,099,508.62 ns | 359,991.898 ns | 774,920.537 ns | 18,345,503.91 ns | 25.27 |    1.66 |      - |     - |     - |         - |
| ImmutableArrayEnumerate | 200000 |    482,744.71 ns |  11,278.890 ns |  31,253.750 ns |    478,121.04 ns |  0.75 |    0.05 |      - |     - |     - |         - |
|  FSharpxVectorEnumerate | 200000 |  5,035,035.54 ns |  99,934.161 ns | 250,715.271 ns |  4,915,667.19 ns |  7.32 |    0.40 |      - |     - |     - |     232 B |
|            VecEnumerate | 200000 |  1,114,727.52 ns |  20,929.033 ns |  23,262.565 ns |  1,115,355.66 ns |  1.61 |    0.05 |      - |     - |     - |         - |

#### Building a collection out of array

Here, we are converting an array of 1000 elements into a final representation of a given collection:

- *ResizeArrayOfArray* represents construction of `new System.Collections.Generic.List<'a>(items)`.
- *ImmutableListOfArray* represents construction via `ImmutableList.Create<User>(items)`.
- *FSharpListOfArray* represents `List.ofArray items`
- *VecOfArray* represents `Vec.ofArray items` - an implementation of immutable vector made in this repository.
- *FSharpxVecOfArray* represents a `PersistentVector.ofSeq items` from [FSharpx.Collections](https://github.com/fsprojects/FSharpx.Collections), which is another implementation of the same data structure as the one implemented here.

|               Method |      Mean |     Error |    StdDev |   Gen 0 | Gen 1 | Gen 2 | Allocated |
|--------------------- |----------:|----------:|----------:|--------:|------:|------:|----------:|
|   ResizeArrayOfArray |  1.580 us | 0.0318 us | 0.0390 us |  3.8452 |     - |     - |   7.87 KB |
| ImmutableListOfArray | 62.571 us | 1.2125 us | 1.7389 us | 22.9492 |     - |     - |  46.92 KB |
|    FSharpListOfArray | 20.699 us | 0.4118 us | 0.4406 us | 15.2893 |     - |     - |  31.25 KB |
|    FSharpxVecOfArray | 85.473 us | 5.5849 us | 8.0098 us |  5.0049 |     - |     - |  10.26 KB |
|           VecOfArray | 11.720 us | 0.2322 us | 0.3020 us |  9.3536 |     - |     - |   19.1 KB |

Note: since FSharpx implementation internally represents stored elements as `obj`, in case of value types there's an additional cost related to boxing.

### Channel

Channel module allows you to operate over `System.Threading.Channel<T>` in more F#-idiomatic way.

```fsharp
// create a writer/reader pair for multi-producer/single-consumer bounded channel
let writer, reader = Channel.boundedMpsc 10
for item in [|1..9|]
    do! writer.WriteAsync(1)

// you can try to pull all items currently stored inside of the channel and write them into span
let buffer = Array.zeroCreate 6
let span = Span buffer
let itemsRead = Channel.readTo span reader

// select can be used to await on multiple channels output, returning first pulled value
// (in case when multiple channels have values read, the order of channels in select is used)
let otherWriter, otherReader = Channel.boundedMpsc 10
do! otherWriter.WriteAsync("hello")
let! value = Channel.select [|
    otherReader,
    // Channel.map can be used to modify `ChannelReader<'a>` into `ChannelReader<'b>`
    reader |> Channel.map (fun item -> string item)
|]
```

### AsyncSeq

`AsyncSeq<'a>` alone is an alias over `IAsyncEnumerable<'a>` interface available in modern .NET.

- `AsyncSeq.withCancellation: CancellationToken -> AsyncSeq<'a> -> AsyncSeq<'a>` which explicitly assigns a `CancellationToken` to async enumerators created from async sequence.
- `timer: TimeSpan -> AsyncSeq<TimeSpan>` will produce async sequence that will asynchronously "tick" in given intervals, returning time passed from one MoveNextAsync call to another.
- `into: bool -> ChannelWriter<'a> -> AsyncSeq<'a> -> ValueTask` will flush all contents of async sequence into given channel, returning value task that can be awaited to completion. Set flag to determine if channel should be closed once async sequence completes sending all element.
- `tryHead: AsyncSeq<'a> -> ValueTask<'a option>` will try to (asynchronously) return first element of the async sequence, if it's not empty.
- `tryLast: AsyncSeq<'a> -> ValueTask<'a option>` will try to (asynchronously) return last element of the async sequence, if it's not empty.
- `collect: AsyncSeq<'a> -> ValueTask<'a[]>` will try to materialize all elements of an async sequence into an array, returning value task that will complete one async sequence has no more elements to pull.
- `fold: ('s->'a->ValueTask<'s>) -> 's -> AsyncSeq<'a> -> ValueTask<'s>` will execute given function over elements of async sequence, incrementally building accumulating value and returning it eventually once sequence is completed.
- `reduce: ('a->'a->'a) -> AsyncSeq<'a> -> ValueTask<'a option>` works like fold, but instead of picking initial state it will use first async sequence element. If sequence was empty, a None value will be returned eventually.
- `scan: ('s->'a->'a) -> 's -> AsyncSeq<'a> -> AsyncSeq<'s>` works like `fold`, but with every incremental step it will emit new (possibly altered) accumulated value as subsequent async sequence.
- `iteri: (int64->'a->ValueTask) -> AsyncSeq<'a> -> ValueTask`, `iter: ('a->ValueTask) -> AsyncSeq<'a> -> ValueTask` and `ignore: AsyncSeq<'a> -> ValueTask` all let you iterate over elements of the async sequence returning value task that finishes once sequence has been emptied.
- `mapParallel: int -> ('a->ValueTask<'b>) -> AsyncSeq<'a> -> AsyncSeq<'b>` lets you (potentially asynchronously) map async sequence elements in parallel. Due to its parallel nature, produced result sequence may output mapped elements in different order than the input.
- `mapi: (int64->'a->'b) -> AsyncSeq<'a> -> AsyncSeq<'b>`, `map: ('a->'b) -> AsyncSeq<'a> -> AsyncSeq<'b>`, `mapAsynci: (int64->'a->ValueTask<'b>) -> AsyncSeq<'a> -> AsyncSeq<'b>` and `mapAsync: ('a->ValueTask<'b>) -> AsyncSeq<'a> -> AsyncSeq<'b>` are all variants of applying mapping function over values (and potentially indexes) produced by upstream into new async sequence of mapped elements pushed downstream.
- `filter: ('a->bool) -> AsyncSeq<'a> -> AsyncSeq<'b>` simply let's you pick (using given predicate function) which of whe upstream elements should be pushed downstream.
- `choose: ('a->ValueTask<'b option>) -> AsyncSeq<'a> -> AsyncSeq<'b>` works like combination of `mapAsync` and `filter`: it allows you to both map and filter out (by making mapping function return None) elements from the upstream.
- `recover: (exn->'ValueTask<'a option) -> AsyncSeq<'a> -> AsyncSeq<'a>` allows you to react on potential exceptions that may have happen while pulling upstream, producing new event out of exception or completing the async sequence (if error mapping function returned None).
- `onError: (exn->AsyncSeq<'a>) -> AsyncSeq<'a> -> AsyncSeq<'a>` allows you to react on potential exceptions that may have happen while pulling upstream, by providing an new stream that will replace failing one. This function works recursively.
- `merge: AsyncSeq<'a> seq -> AsyncSeq<'a>` let's you merge many async sequences into one. Sequences are pulled sequentially: one must be completed first before next one will start to be pulled. 
- `mergeParallel: AsyncSeq<'a> seq -> AsyncSeq<'a>` works like `merge` but in parallel fashion: sequences are being pulled at the same time, without waiting for any one to completion.
- `bind: ('a->AsyncSeq<'b>) -> AsyncSeq<'a> -> AsyncSeq<'b>` let's you create a new async sequence for each element pulled from upstream: sequences produced this way will be pulled until completion before a next element from upstream will be pulled.
- `skip: int64 -> AsyncSeq<'a> -> AsyncSeq<'a>`, `skipWhile: ('a->bool) -> AsyncSeq<'a> -> AsyncSeq<'a>` and `skipWithin: TimeSpan -> AsyncSeq<'a> -> AsyncSeq<'a>` all let you skip elements pulled from the upstream, either given number of them, until a predicate function is satisfied or for a given time window.
- `take: int64 -> AsyncSeq<'a> -> AsyncSeq<'a>`, `takeWhile: ('a->bool) -> AsyncSeq<'a> -> AsyncSeq<'a>` and `takeWithin: TimeSpan -> AsyncSeq<'a> -> AsyncSeq<'a>` all let you take elements pulled from the upstream, either given number of them, until a predicate function is satisfied or for a given time window. Once take requirement is met, async sequence will be completed.
- `split: ('a->bool) -> AsyncSeq<'a> -> AsyncSeq<'a>*AsyncSeq<'a>` lets you split upstream async sequence in two. First will be pulled until given predicate function is satisfied. Once that happens it will be completed and second sequence (with element that caused split) will be pulled for all other elements coming from upstream. That means, the predicate is checked only once.
- `delay: ('a->TimeSpan) -> AsyncSeq<'a> -> AsyncSeq<'a>` will delay all elements pulled from upstream by given time returned from function parameter. Function parameter can be used eg. for creating exponential backoff delays if necessary.
- `throttle: int -> TimeSpan -> AsyncSeq<'a> -> AsyncSeq<'a>` will apply rate limiting to upstream async sequence, limiting number of elements returned downstream to not reach over given number in specified time span.
- `keepAlive: TimeSpan -> ('a->'a) -> AsyncSeq<'a> -> AsyncSeq<'a>` will monitor rate in which elements from upstream are incoming. They will fall over specified time span, the last received element will be applied into generator function, that will produce a new element that will be generated instead to keep the rate of upstream in specified time bounds.
- `grouped: int -> AsyncSeq<'a> -> AsyncSeq<'a[]>` groups incoming elements into buckets of given size and pull them downstream as one. Final bucket (send right before sequence completion) can be smaller than requested size.
- `buffered: int -> AsyncSeq<'a> -> AsyncSeq<'a[]>` let's you to disassociate rate of elements pulled downstream from upstream, especially useful when downstream pulls elements at slower rate. Upstream will be continuously (and asynchronously) pulled for elements up until given buffer size is reached. If in the meantime downstream will pull this sequence, it will receive array with all elements pulled so far. 
- `zipWith: ('a->'b->'c) -> AsyncSeq<'a> -> AsyncSeq<'b> -> AsyncSeq<'c>` and `zip: AsyncSeq<'a> -> AsyncSeq<'b> -> AsyncSeq<'a*'b>` will merge together two async sequences, combining their elements pairwise before sending them downstream.
- `ofSeq: 'a seq -> AsyncSeq<'a>` creates async sequence out of the given sequence.
- `unfold: ('s->ValueTask<('s * 'a) option>) -> 's -> AsyncSeq<'a>` creates an async sequence out of the given generator function. That function takes initial state and continuously applies it producing new state and element to be emitted as result of async sequence. If None was returned, async sequence will be completed.
- `ofFunc: (int64->ValueTask<'a option>) -> AsyncSeq<'a>` creates an async sequence out of the given generator function. That function gets a counter value (incremented with each call) and produce element that will be emitted as sequence's output. If None was returned, the sequence will complete.  
- `ofAsync: Async<'a> -> AsyncSeq<'a>`, `ofTask: Task<'a> -> AsyncSeq<'a>` and `ofTaskCancellable: (CancellationToken->Task<'a>) -> AsyncSeq<'a>` all allow to transition F# Async or TPL Tasks into async enumerators. Cancellable version receives cancellation token passed once GetAsyncEnumerator is called and will be invoked on every such call.
- `empty: AsyncSeq<'a>` returns an async sequence that has no elements and will immediately finish once pulled.
- `repeat: 'a -> AsyncSeq<'a>` will produce infinite sequence of given element.
- `singleton: 'a -> AsyncSeq<'a>` wraps given value with async sequence, that will produce that value only once.
- `failed: exn -> AsyncSeq<'a>` returns a malfunctioning async sequence, that will fail with given exception when its enumerator will try to be pulled.
- `ofOption: 'a option -> AsyncSeq<'a>` wraps given option with async sequence, that will produce option's value or complete immediately if option was None.
- `ofChannel: ChannelReader<'a> -> AsyncSeq<'a>` modifies given channel into async sequence, that will complete once writer part of the channel will be explicitly completed.
- `onDispose: (unit->ValueTask) -> AsyncSeq<'a> -> AsyncSeq<'a>` attaches given function to upstream async sequence, that will be called together with that sequence's enumerator dispose call.
- `deduplicate: ('a->'a->bool) -> AsyncSeq<'a> -> AsyncSeq<'a>` will skip over consecutive elements of the upstream async sequence, if they are considered equal according to a given equality function. Unlike "distinct" operators, it doesn't produce globally unique values (as this would require buffering all values), it only deduplicates neighbor elements. 
- `interleave: ('a->'a->'a) -> AsyncSeq<'a> -> AsyncSeq<'a>` will produce an extra element (generated using specified function) in between two consecutive elements pulled from upstream.
- `groupBy: int -> int -> ('a->'b) -> AsyncSeq<'a> -> AsyncSeq<('b * AsyncSeq<'a>)>` will group elements of upstream async sequence by the given parameter function, producting async sequence of key and subsequence of grouped elements. Because of their nature, pull-based sequences are not ideal to work safely with grouping operators. For this reason, only up to specified number of subsequences can be active at any time. Additionally all active sequences must be periodically pulled in order to let upstream be called for other concurrent sequences to avoid deadlocking (which is amortized by every group having an internal buffer of a given capacity).

### Actors

This is simple actor implementation based on TPL and System.Threading.Channels. These actors don't offer location transparency mechanisms. By default actor's mailbox is unbounded, but this may be configured.

```fsharp
[<Sealed>
type Counter(init) =
    inherit UnboundedActor<int>()
    let mutable state = 0
    override this.Receive msg = uunitVTask {
        match msg with
        | Add v -> state <- state + v
        | Done -> this.Complete() // don't use `do! ctx.DisposeAsync(false)` in this context, as it may deadlock the actor
    }

use actor = new Counter(0)

// send a message to an actor
do! actor.Send (Add 1) // for unbouded actors you don't need to await as send will never block

// each actor contains CancellationToken that may be used to bound method execution to a lifecycle of an actor.
let! file = File.ReadAllAsync("file.txt", actor.CancellationToken)

// actor extends ChannelWriter class, so it can be used in this context
AsyncSeq.ofSeq [1;2;3] |> AsyncSeq.into false actor

// actor may be closed gracefully (letting it process all stashed messages first) or abrutly
do! actor.DisposeAsync(true)  // abrupt close
do! actor.DisposeAsync(false) // graceful close

// you can also await for actor's termination - this task will complete once actor gets disposed, it may also
// complete with failure as actor doesn't handle exceptions by itself (use try/with if that's necessary)
do! actor.Terminated
```

Current performance benchmarks as compared to F# MailboxProcessor - example of actor-based counter, processing 1 000 000 messages:

|                    Method |      Mean |    Error |   StdDev | Ratio | RatioSD |       Gen 0 |     Gen 1 |     Gen 2 |    Allocated |
|-------------------------- |----------:|---------:|---------:|------:|--------:|------------:|----------:|----------:|-------------:|
|          FSharpAsyncActor | 279.71 ms | 2.855 ms | 2.384 ms |  1.00 |    0.00 | 154000.0000 | 2000.0000 | 2000.0000 | 477856.16 KB |
|      FSharpActorUnbounded |  96.70 ms | 0.359 ms | 0.336 ms |  0.35 |    0.00 |           - |         - |         - |       2.6 KB |
| FSharpActorUnboundedAsync |  99.19 ms | 1.976 ms | 4.460 ms |  0.35 |    0.02 |           - |         - |         - |    371.57 KB |


* `FSharpAsyncActor` is F# `MailboxProcessor` implementation.
* `FSharpActorUnbounded` is Actor implementation from this lib using default (possibly synchronous) message passing.
* `FSharpActorUnboundedAsync` is Actor implementation from this lib using fully asynchronous message passing.

### Schedules 

While `Task.Delay()` is great way to introduce delays of various kind, sometimes we may need to introduce more advanced delay mechanisms: exponential backoffs, jittered (randomized) delays etc.
For these cases `Scheduler` module provides composable API, which can be used for multi-step time spacing mechanism:

```fsharp
// create policy that will execute up to 4 times, with exponential backoff starting from 1 second (1sec, 2sec, 4sec, 8sec)
// with each tick slightly randomized by 10% margin  
let policy =
    Schedule.exponential 2.0 (TimeSpan.FromSeconds 1.)
    |> Schedule.jittered 0.9 1.1
    |> Schedule.times 4

// now execute something up to 5 times, gathering result and all potential exceptions that happened along the way
let! (result, failures) = 
    policy
    |> Schedule.retry (fun _ -> uvtask {
        return! someHttpCall () 
    }) CancellationToken.None

// you can also execute some action N+1 times, with each time delayed from previous one
let sw = Stopwatch.StartNew()
let! results =
    Schedule.after (TimeSpan.FromMilliseconds 100.)
    |> Schedule.times 3
    |> Schedule.spaced (fun _ -> uvtask {
        printfn "Time passed: %ims" sw.ElapsedMilliseconds
        return 1
    }) CancellationToken.None

// Results should look more or less like:
// Time passed: 0ms
// Time passed: 100ms
// Time passed: 200ms
// Time passed: 300ms
```

Schedulers provide simple composable API and can be serialized/deserialized by any serializer able to handle discriminated unions (keep in mind that `Schedule.ofSeq` will most likely eagerly evaluate provided sequence in order to serialize it). 