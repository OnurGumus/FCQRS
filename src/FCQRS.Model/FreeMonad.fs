/// A free monad: a program is a description made of instructions, and an interpreter that the
/// application supplies decides what each instruction does. The same program can run against a
/// database in production and against fixed answers in a test.
module FCQRS.Model.FreeMonad

// ============================================
// CORE FREE MONAD
// ============================================

#if !FABLE_COMPILER
/// A program whose result type is hidden, so that continuations of different types fit in one
/// queue.
type internal IProgram<'Instruction> =
    abstract IsPure: bool
    /// The boxed result of a `Pure` program.
    abstract Result: (obj | null)
    /// The instruction of an `Impure` program.
    abstract Instruction: 'Instruction
    /// What follows the instruction of an `Impure` program.
    abstract Next: Conts<'Instruction>

/// The continuations that follow an instruction, in order. A tree gives `bind` a constant-time
/// append, and `Queued` takes the first one off in amortized constant time.
and [<NoEquality; NoComparison>] internal Conts<'Instruction> =
    | One of ((obj | null) -> IProgram<'Instruction>)
    | Two of Conts<'Instruction> * Conts<'Instruction>

and internal IHasConts<'Instruction> =
    abstract Conts: Conts<'Instruction>
#endif

/// A program that performs instructions of type `'Instruction` and produces a `'Result`.
/// `Pure` is a finished program. `Impure` is an instruction and a function that takes the
/// interpreter's answer, boxed, and gives the rest of the program. An interpreter matches on
/// these two cases, or uses `Free.run`, `Free.runAsync`, or `Free.runTask`.
type Free<'Instruction, 'Result> =
    | Pure of 'Result
    | Impure of 'Instruction * ((obj | null) -> Free<'Instruction, 'Result>)

#if !FABLE_COMPILER
    interface IProgram<'Instruction> with
        member this.IsPure =
            match this with
            | Pure _ -> true
            | Impure _ -> false

        member this.Result =
            match this with
            | Pure result -> box result
            | Impure _ -> null

        member this.Instruction =
            match this with
            | Impure(instruction, _) -> instruction
            | Pure _ -> invalidOp "A finished program has no instruction."

        member this.Next =
            match this with
            | Impure(_, next) ->
                match box next with
                | :? IHasConts<'Instruction> as queued -> queued.Conts
                | _ -> One(fun answer -> next answer :> IProgram<'Instruction>)
            | Pure _ -> invalidOp "A finished program has no continuation."

/// The continuation that `Free.bind` builds. It keeps the bound functions as data and runs them
/// in a loop, so binds nested to any depth neither grow the stack nor slow each later
/// instruction. A closure per bind did both: a program that recursed `n` levels below a `let!`
/// called `n` closures for every instruction.
and [<Sealed>] internal Queued<'Instruction, 'Result>(conts: Conts<'Instruction>) =
    inherit FSharpFunc<(obj | null), Free<'Instruction, 'Result>>()

    interface IHasConts<'Instruction> with
        member _.Conts = conts

    /// This object as the function it is. The compiler takes a function type to have no
    /// subtypes, so the cast goes through `obj`.
    member this.AsFunction: (obj | null) -> Free<'Instruction, 'Result> =
        unbox<(obj | null) -> Free<'Instruction, 'Result>> (box this)

    override _.Invoke(answer: (obj | null)) : Free<'Instruction, 'Result> =
        let mutable pending = conts
        let mutable argument = answer
        let mutable result = Unchecked.defaultof<Free<'Instruction, 'Result>>
        let mutable finished = false

        while not finished do
            // Take the first continuation off the tree, leaving the others in order.
            let mutable head = pending
            let mutable rest: Conts<'Instruction> voption = ValueNone
            let mutable first = Unchecked.defaultof<(obj | null) -> IProgram<'Instruction>>
            let mutable found = false

            while not found do
                match head with
                | One continuation ->
                    first <- continuation
                    found <- true
                | Two(left, right) ->
                    head <- left

                    rest <-
                        match rest with
                        | ValueNone -> ValueSome right
                        | ValueSome later -> ValueSome(Two(right, later))

            let next = first argument

            match rest with
            | ValueNone ->
                // The last continuation gives the program of this queue's result type.
                result <- unbox<Free<'Instruction, 'Result>> next
                finished <- true
            | ValueSome later ->
                if next.IsPure then
                    argument <- next.Result
                    pending <- later
                else
                    let queued = Queued<'Instruction, 'Result>(Two(next.Next, later))
                    result <- Impure(next.Instruction, queued.AsFunction)
                    finished <- true

        result

/// The continuation of the common case: an instruction whose plain continuation `first` is
/// followed by one bound function. It costs what a closure costs, so a loop whose recursive call
/// comes last pays nothing for the queue. Bound again, it gives its two functions to a `Queued`.
and [<Sealed>] internal Then<'Instruction, 'A, 'Result>
    (first: (obj | null) -> Free<'Instruction, 'A>, next: 'A -> Free<'Instruction, 'Result>) =
    inherit FSharpFunc<(obj | null), Free<'Instruction, 'Result>>()

    static member private Erase(next: 'A -> Free<'Instruction, 'Result>) : Conts<'Instruction> =
        One(fun value -> next (unbox<'A> value) :> IProgram<'Instruction>)

    /// `program`, then `next` applied to its result.
    static member Bind
        (next: 'A -> Free<'Instruction, 'Result>, program: Free<'Instruction, 'A>)
        : Free<'Instruction, 'Result> =
        match program with
        | Pure value -> next value
        | Impure(instruction, rest) ->
            match box rest with
            | :? IHasConts<'Instruction> as queued ->
                let conts = Two(queued.Conts, Then<'Instruction, 'A, 'Result>.Erase next)
                Impure(instruction, Queued<'Instruction, 'Result>(conts).AsFunction)
            | _ -> Impure(instruction, Then<'Instruction, 'A, 'Result>(rest, next).AsFunction)

    interface IHasConts<'Instruction> with
        member _.Conts =
            Two(One(fun answer -> first answer :> IProgram<'Instruction>), Then<'Instruction, 'A, 'Result>.Erase next)

    member this.AsFunction: (obj | null) -> Free<'Instruction, 'Result> =
        unbox<(obj | null) -> Free<'Instruction, 'Result>> (box this)

    override _.Invoke(answer: (obj | null)) : Free<'Instruction, 'Result> =
        Then<'Instruction, 'A, 'Result>.Bind(next, first answer)
#endif

/// Functions that build, combine, and run programs.
module Free =
    /// `program`, then `next` applied to its result.
#if FABLE_COMPILER
    let rec bind (next: 'A -> Free<'I, 'B>) (program: Free<'I, 'A>) : Free<'I, 'B> =
        match program with
        | Pure value -> next value
        | Impure(instruction, rest) -> Impure(instruction, fun answer -> bind next (rest answer))
#else
    let bind (next: 'A -> Free<'I, 'B>) (program: Free<'I, 'A>) : Free<'I, 'B> =
        Then<'I, 'A, 'B>.Bind(next, program)
#endif

    /// A program that performs nothing and gives `x`.
    let return' x = Pure x

    /// `m`, with `f` applied to its result.
    let map f m = bind (f >> Pure) m

    /// Runs `body`, and gives an exception that the program's own code throws to `handler`. It
    /// sees exceptions from the code between instructions. What an interpreter does when it
    /// cannot carry out an instruction is the interpreter's choice.
    let rec tryWith (body: unit -> Free<'I, 'A>) (handler: exn -> Free<'I, 'A>) : Free<'I, 'A> =
        match (try Choice1Of2(body ()) with error -> Choice2Of2 error) with
        | Choice2Of2 error -> handler error
        | Choice1Of2(Pure value) -> Pure value
        | Choice1Of2(Impure(instruction, rest)) ->
            Impure(instruction, fun answer -> tryWith (fun () -> rest answer) handler)

    /// Runs `body`, then `compensation` once: when the program finishes, or when its own code
    /// throws. A program that an interpreter abandons at an instruction never runs it.
    let rec tryFinally (body: unit -> Free<'I, 'A>) (compensation: unit -> unit) : Free<'I, 'A> =
        let started =
            try
                body ()
            with _ ->
                compensation ()
                reraise ()

        match started with
        | Pure value ->
            compensation ()
            Pure value
        | Impure(instruction, rest) ->
            Impure(instruction, fun answer -> tryFinally (fun () -> rest answer) compensation)

    /// Runs `body` while `guard` holds. Rounds that perform no instruction run in a loop.
    let rec whileLoop (guard: unit -> bool) (body: unit -> Free<'I, unit>) : Free<'I, unit> =
        let mutable result = Pure()
        let mutable looping = true

        while looping do
            if guard () then
                match body () with
                | Pure() -> ()
                | waiting ->
                    result <- bind (fun () -> whileLoop guard body) waiting
                    looping <- false
            else
                looping <- false

        result

    /// Runs a program with an interpreter that answers each instruction at once. The answer is
    /// boxed, and must have the type the instruction's caller expects.
    let run (handle: 'I -> (obj | null)) (program: Free<'I, 'R>) : 'R =
        let mutable current = program
        let mutable result = Unchecked.defaultof<'R>
        let mutable finished = false

        while not finished do
            match current with
            | Pure value ->
                result <- value
                finished <- true
            | Impure(instruction, rest) -> current <- rest (handle instruction)

        result

    /// Runs a program with an interpreter that answers each instruction asynchronously.
    let rec runAsync (handle: 'I -> Async<(obj | null)>) (program: Free<'I, 'R>) : Async<'R> =
        async {
            match program with
            | Pure value -> return value
            | Impure(instruction, rest) ->
                let! answer = handle instruction
                return! runAsync handle (rest answer)
        }

#if !FABLE_COMPILER
    /// Runs a program with an interpreter that answers each instruction with a task.
    let runTask
        (handle: 'I -> System.Threading.Tasks.Task<(obj | null)>)
        (program: Free<'I, 'R>)
        : System.Threading.Tasks.Task<'R> =
        task {
            let mutable current = program
            let mutable result = Unchecked.defaultof<'R>
            let mutable finished = false

            while not finished do
                match current with
                | Pure value ->
                    result <- value
                    finished <- true
                | Impure(instruction, rest) ->
                    let! answer = handle instruction
                    current <- rest answer

            return result
        }
#endif

// ============================================
// FREE COMPUTATION EXPRESSION
// ============================================

type FreeBuilder() =
    member _.Return(x) = Pure x
    member _.Bind(m, f) = Free.bind f m
    member _.ReturnFrom(m) = m
    member _.Zero() = Pure()
    member _.Delay(f: unit -> Free<'I, 'A>) = f
    member _.Run(f: unit -> Free<'I, 'A>) = f ()
    member _.Combine(first: Free<'I, unit>, rest: unit -> Free<'I, 'A>) = Free.bind rest first
    member _.While(guard: unit -> bool, body: unit -> Free<'I, unit>) = Free.whileLoop guard body

    member _.For(items: seq<'T>, body: 'T -> Free<'I, unit>) : Free<'I, unit> =
        let enumerator = items.GetEnumerator()

        Free.tryFinally
            (fun () -> Free.whileLoop enumerator.MoveNext (fun () -> body enumerator.Current))
            enumerator.Dispose

    member _.TryWith(body: unit -> Free<'I, 'A>, handler: exn -> Free<'I, 'A>) = Free.tryWith body handler
    member _.TryFinally(body: unit -> Free<'I, 'A>, compensation: unit -> unit) = Free.tryFinally body compensation

/// Builds a `Free` program.
/// A block's code up to its first instruction runs when the block is built, not when the program
/// runs, so a program value shares what that code created between runs: a `let mutable` or a `for`
/// loop's position. Build a program that keeps such state in a function, such as
/// `let order id = free { ... }`, and call it for each run.
let free = FreeBuilder()

// ============================================
// FREE + RESULT
// ============================================

type FreeResultBuilder() =
    member _.Return(x: 'T) : Free<'I, Result<'T, 'E>> = Pure(Ok x)

    member _.ReturnFrom(x: Free<'I, Result<'T, 'E>>) : Free<'I, Result<'T, 'E>> = x

    // Bind for Free<I, Result<T, E>>: an Error ends the program.
    member _.Bind
        (m: Free<'I, Result<'T, 'E>>, f: 'T -> Free<'I, Result<'U, 'E>>)
        : Free<'I, Result<'U, 'E>> =
        Free.bind
            (fun result ->
                match result with
                | Ok value -> f value
                | Error e -> Pure(Error e))
            m

    // Bind for plain Result - allows let! on Result values
    member _.Bind(m: Result<'T, 'E>, f: 'T -> Free<'I, Result<'U, 'E>>) : Free<'I, Result<'U, 'E>> =
        match m with
        | Ok value -> f value
        | Error e -> Pure(Error e)

    member _.Zero() : Free<'I, Result<unit, 'E>> = Pure(Ok())

    member _.Delay(f: unit -> Free<'I, Result<'T, 'E>>) = f
    member _.Run(f: unit -> Free<'I, Result<'T, 'E>>) = f ()

    member this.Combine
        (first: Free<'I, Result<unit, 'E>>, rest: unit -> Free<'I, Result<'T, 'E>>)
        : Free<'I, Result<'T, 'E>> =
        this.Bind(first, rest)

    /// Runs `body` while `guard` holds and no round has given an `Error`.
    member _.While
        (guard: unit -> bool, body: unit -> Free<'I, Result<unit, 'E>>)
        : Free<'I, Result<unit, 'E>> =
        let rec loop () : Free<'I, Result<unit, 'E>> =
            let mutable result = Pure(Ok())
            let mutable looping = true

            while looping do
                if guard () then
                    match body () with
                    | Pure(Ok()) -> ()
                    | Pure(Error e) ->
                        result <- Pure(Error e)
                        looping <- false
                    | waiting ->
                        result <-
                            Free.bind
                                (fun outcome ->
                                    match outcome with
                                    | Ok() -> loop ()
                                    | Error e -> Pure(Error e))
                                waiting

                        looping <- false
                else
                    looping <- false

            result

        loop ()

    member this.For(items: seq<'T>, body: 'T -> Free<'I, Result<unit, 'E>>) : Free<'I, Result<unit, 'E>> =
        let enumerator = items.GetEnumerator()

        Free.tryFinally
            (fun () -> this.While(enumerator.MoveNext, (fun () -> body enumerator.Current)))
            enumerator.Dispose

    member _.TryWith(body: unit -> Free<'I, Result<'T, 'E>>, handler: exn -> Free<'I, Result<'T, 'E>>) =
        Free.tryWith body handler

    member _.TryFinally(body: unit -> Free<'I, Result<'T, 'E>>, compensation: unit -> unit) =
        Free.tryFinally body compensation

/// Builds a `Free` program that gives a `Result`: a `let!` on an `Error` ends it.
/// A block's code up to its first instruction runs when the block is built, not when the program
/// runs, so a program value shares what that code created between runs: a `let mutable` or a `for`
/// loop's position. Build a program that keeps such state in a function, such as
/// `let order id = free { ... }`, and call it for each run.
let freeResult = FreeResultBuilder()

// ============================================
// HELPERS: Lift into FreeResult context
// ============================================

/// A program that cannot fail as one that gives a `Result`, so `freeResult` can `let!` it.
let liftFree (m: Free<'I, 'T>) : Free<'I, Result<'T, 'E>> = Free.map Ok m

/// A `Result` as a program that performs nothing.
let liftResult (r: Result<'T, 'E>) : Free<'I, Result<'T, 'E>> = Pure r


/// Turns an instruction into a program.
module Eff =
    /// Reads an interpreter's answer as the type the program expects. A null answer is `()`,
    /// `None`, or a null reference. An answer of another type raises, naming both types.
    /// Under Fable, the answer is not checked: JavaScript keeps no generic type to test, and a
    /// type test of an option always fails there.
    let inline safeUnbox<'T> (o: obj | null) : 'T =
#if FABLE_COMPILER
        unbox<'T> o
#else
        match o with
        | :? 'T as t -> t
        | Null -> failwithf "Continuation received null, expected %s" typeof<'T>.Name
        | NonNull o ->
            failwithf "Continuation received %s, expected %s" (o.GetType().ToString()) (typeof<'T>.ToString())
#endif

    /// The instruction `i`, followed by `k` applied to the answer read as `'X`.
    let inline op<'I, 'X, 'A> (i: 'I) (k: 'X -> Free<'I, 'A>) : Free<'I, 'A> =
        Impure(i, fun o -> k (safeUnbox<'X> o))

    /// A program of one instruction, whose result is the interpreter's answer read as `'X`.
    let inline perform<'I, 'X> (i: 'I) : Free<'I, 'X> = op i Pure

    /// `perform` for a `freeResult` program: the answer, read as `'X`, wrapped in `Ok`.
    let inline performR<'I, 'X, 'E> (i: 'I) : Free<'I, Result<'X, 'E>> = perform i |> liftFree
