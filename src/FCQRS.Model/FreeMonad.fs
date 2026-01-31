// The Free monad type
module FreeMonad
// ============================================
// CORE FREE MONAD
// ============================================


type Free<'Instruction, 'Result> =
    | Pure of 'Result
    | Impure of 'Instruction * (obj -> Free<'Instruction, 'Result>)

module Free =
    let rec bind (f: 'A -> Free<'I, 'B>) (m: Free<'I, 'A>) : Free<'I, 'B> =
        match m with
        | Pure a -> f a
        | Impure(instr, cont) -> Impure(instr, fun x -> bind f (cont x))
    
    let return' x = Pure x
    
    let map f m = bind (f >> Pure) m

// ============================================
// FREE COMPUTATION EXPRESSION
// ============================================

type FreeBuilder() =
    member _.Return(x) = Pure x
    member _.Bind(m, f) = Free.bind f m
    member _.ReturnFrom(m) = m
    member _.Zero() = Pure ()

let free = FreeBuilder()

// ============================================
// FREE + RESULT - NO OVERLOADING, SINGLE BIND
// ============================================

type FreeResultBuilder() =
    member _.Return(x : 'T) : Free<'I, Result<'T, 'E>> = 
        Pure (Ok x)
    
    member _.ReturnFrom(x: Free<'I, Result<'T, 'E>>) : Free<'I, Result<'T, 'E>> = 
        x
    
    // Single Bind - handles Free<I, Result<T, E>>
    member _.Bind(m: Free<'I, Result<'T, 'E>>, f: 'T -> Free<'I, Result<'U, 'E>>) 
        : Free<'I, Result<'U, 'E>> =
        Free.bind (fun result ->
            match result with
            | Ok value -> f value
            | Error e -> Pure (Error e)
        ) m
    
    member _.Zero() : Free<'I, Result<unit, 'E>> = 
        Pure (Ok ())

let freeResult = FreeResultBuilder()

// ============================================
// HELPER: Lift pure Free into FreeResult context
// ============================================

let liftFree (m: Free<'I, 'T>) : Free<'I, Result<'T, 'E>> =
    Free.map Ok m


module Eff =
  let inline safeUnbox<'T> (o: obj | null) : 'T =
      match o with
      | :? 'T as t -> t
      | Null -> failwithf "Continuation received null, expected %s" typeof<'T>.Name
      | NonNull o-> failwithf "Continuation received %s, expected %s" (o.GetType().ToString()) (typeof<'T>.ToString())
  let inline op<'I,'X,'A> (i:'I) (k:'X -> Free<'I,'A>) : Free<'I,'A> =
    Impure(i, fun o -> k (safeUnbox<'X> o))

  let inline perform<'I,'X> (i:'I) : Free<'I,'X> =
    op i Pure

  let inline performR<'I,'X,'E> (i:'I) : Free<'I, Result<'X,'E>> =
    perform i |> liftFree

    