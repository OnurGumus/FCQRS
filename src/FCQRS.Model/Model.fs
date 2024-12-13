module rec FCQRS.Model.Data


open System
open FCQRS.Model.Validation
open FCQRS.Model.Aether

type ValueLensResultType<'Wrapped, 'Inner, 'Error
    when 'Wrapped: (static member Value_: (('Wrapped -> 'Inner) * ('Inner -> 'Wrapped -> Result<'Wrapped, 'Error>)))> =
    'Wrapped

type ValueLensType<'Wrapped, 'Inner
    when 'Wrapped: (static member Value_: (('Wrapped -> 'Inner) * ('Inner -> 'Wrapped -> 'Wrapped)))> = 'Wrapped

type ValueLens =
    static member inline Value<'Wrapped, 'Inner, 'Error when ValueLensResultType<'Wrapped, 'Inner, 'Error>>
        (this: 'Wrapped)
        =
        fst 'Wrapped.Value_ this

    static member inline Value<'Wrapped, 'Inner when ValueLensType<'Wrapped, 'Inner>>(this: 'Wrapped) =
        fst 'Wrapped.Value_ this

    static member inline ToString<'Wrapped, 'Inner when ValueLensType<'Wrapped, 'Inner>>(this: 'Wrapped) =
        (ValueLens.Value this).ToString() |> Unchecked.nonNull

    static member inline IsValidValue<'Wrapped, 'Inner, 'Error when ValueLensResultType<'Wrapped, 'Inner, 'Error>>
        (this: 'Wrapped)
        =
        (Optic.set 'Wrapped.Value_ (ValueLens.Value this) this).IsOk

    static member inline Isvalid this =
        ValueLens.IsValidValue this && (ValueLens.Value this) |> ValueLens.IsValidValue

    static member inline TryCreate<'Wrapped, 'Inner, 'Error when ValueLensResultType<'Wrapped, 'Inner, 'Error>>
        (innerValue: 'Inner)
        =
        snd 'Wrapped.Value_ innerValue Unchecked.defaultof<'Wrapped>

    static member inline Create<'Wrapped, 'Inner when ValueLensType<'Wrapped, 'Inner>>(innerValue: 'Inner) =
        snd 'Wrapped.Value_ innerValue Unchecked.defaultof<'Wrapped>

    static member inline CreateAsResult v =
        v |> ValueLens.TryCreate |> Result.map ValueLens.Create
        

let inline (|ResultValue|) x = ValueLens.Value<_, _, _> x

let inline (|Value|) x = ValueLens.Value<_, _> x


[<RequireQualifiedAccessAttribute>]
module Result =
    let inline valueOrErrorList e =
        match e with
        | Ok x -> x
        | Error x ->
            let errors =
                x |> List.map (fun x -> x.ToString() |> Unchecked.nonNull) |> String.concat ", "

            invalidOp errors

    let inline value e =
        match e with
        | Ok x -> x
        | Error x -> invalidOp (x.ToString() |> Unchecked.nonNull)

type Predicate =
    | Greater of string * IComparable
    | GreaterOrEqual of string * IComparable
    | Smaller of string * IComparable
    | SmallerOrEqual of string * IComparable
    | Equal of string * obj
    | NotEqual of string * obj
    | And of Predicate * Predicate
    | Or of Predicate * Predicate
    | Not of Predicate

type ModelError =
    | EmptyString
    | TooLongString
    | InvalidEmail
    | InvalidUrl
    | InvalidGuid
    | MustBeNonNegative

type Version =
    private
    | Version of int64

    static member Value_ =
        (fun (Version v) -> v), (fun v _ -> if v >= 0L then Ok(Version v) else Error MustBeNonNegative)

    static member Zero = Version 0L
    override this.ToString() = (ValueLens.Value this).ToString()

type ShortString =
    private
    | ShortString of string

    static member Value_ =
        (fun (ShortString s) -> s),
        (fun (s: string) _ ->
            single (fun t ->
                t.TestOne s
                |> t.NotBlank EmptyString
                |> t.MaxLen 255 TooLongString
                |> t.Map ShortString
                |> t.End))

    member this.IsValid = ValueLens.IsValidValue this
    override this.ToString() = (ValueLens.Value this).ToString()

type LongString =
    private
    | LongString of string

    static member Value_ =
        (fun (LongString s) -> s),
        (fun (s: string) _ -> single (fun t -> t.TestOne s |> t.NotBlank EmptyString |> t.Map LongString |> t.End))
    member this.IsValid = ValueLens.IsValidValue this
    override this.ToString() = (ValueLens.Value this).ToString()

type CID =
    private
    | CID of ShortString

    static member Value_ = (fun (CID v) -> v), (fun v _ -> (CID v))
    member this.IsValid = (ValueLens.Value this).IsValid
    override this.ToString() = (ValueLens.Value this).ToString()

