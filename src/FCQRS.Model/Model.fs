module rec FCQRS.Model.Data


open System
open FCQRS.Model.Validation
open FCQRS.Model.Aether

/// Interface for messages that carry a Correlation ID (CID).
type IMessageWithCID =
    /// Gets the Correlation ID associated with the message.
    abstract member CID : CID

/// Helper types for ValueLens for static resolution
type ValueLensResultType<'Wrapped, 'Inner, 'Error
    when 'Wrapped: (static member Value_: (('Wrapped -> 'Inner) * ('Inner -> 'Wrapped -> Result<'Wrapped, 'Error>)))> =
    'Wrapped

/// Helper types for ValueLens for static resolution
type ValueLensType<'Wrapped, 'Inner
    when 'Wrapped: (static member Value_: (('Wrapped -> 'Inner) * ('Inner -> 'Wrapped -> 'Wrapped)))> = 'Wrapped

type ValueLens =
    /// Gets the inner value
    static member inline Value<'Wrapped, 'Inner, 'Error when ValueLensResultType<'Wrapped, 'Inner, 'Error>>
        (this: 'Wrapped)
        =
        fst 'Wrapped.Value_ this

    /// Gets the inner value
    static member inline Value<'Wrapped, 'Inner when ValueLensType<'Wrapped, 'Inner>>(this: 'Wrapped) =
        fst 'Wrapped.Value_ this

    /// Converts inner value ToString

    static member inline ToString<'Wrapped, 'Inner when ValueLensType<'Wrapped, 'Inner>>(this: 'Wrapped) =
        (ValueLens.Value this).ToString()

    /// Reapplies validation rules, typical use case is after deserialization when you cannot trust the data.
    static member inline IsValidValue<'Wrapped, 'Inner, 'Error when ValueLensResultType<'Wrapped, 'Inner, 'Error>>
        (this: 'Wrapped)
        =
        (Optic.set 'Wrapped.Value_ (ValueLens.Value this) this).IsOk

    /// Checks if the validation rules still hold. Typical use case is after deserialization when you cannot trust the data.
    static member inline Isvalid this =
        ValueLens.IsValidValue this && ValueLens.Value this|> ValueLens.IsValidValue

    /// Creates a Result type depending the outcome of validation.
    static member inline TryCreate<'Wrapped, 'Inner, 'Error when ValueLensResultType<'Wrapped, 'Inner, 'Error>>
        (innerValue: 'Inner)
        =
        snd 'Wrapped.Value_ innerValue Unchecked.defaultof<'Wrapped>

    /// Creates non validated parent type directly.

    static member inline Create<'Wrapped, 'Inner when ValueLensType<'Wrapped, 'Inner>>(innerValue: 'Inner) =
        snd 'Wrapped.Value_ innerValue Unchecked.defaultof<'Wrapped>

    /// Skips first level of validation.

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
            let errors = x |> List.map (fun x -> x |> string) |> String.concat ", "

            invalidOp errors

    let inline value e =
        match e with
        | Ok x -> x
        | Error x -> invalidOp (x |> string)

 /// Used to for queries
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
/// Aggregate Version
type Version =
    private
    | Version of int64

    static member Value_ =
        (fun (Version v) -> v), (fun v _ -> if v >= 0L then Ok(Version v) else Error MustBeNonNegative)

    static member Zero = Version 0L
    override this.ToString() = (ValueLens.Value this).ToString()

/// Validated string between 1,50 chars inclusive
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

/// Represents any string at least 1 chars
type LongString =
    private
    | LongString of string

    static member Value_ =
        (fun (LongString s) -> s),
        (fun (s: string) _ -> single (fun t -> t.TestOne s |> t.NotBlank EmptyString |> t.Map LongString |> t.End))

    member this.IsValid = ValueLens.IsValidValue this
    override this.ToString() = (ValueLens.Value this).ToString()

/// CorrelationID for commands and Sagas
type CID =
    private
    | CID of ShortString

    static member Value_ = (fun (CID v) -> v), (fun v _ -> CID v)
    member this.IsValid = (ValueLens.Value this).IsValid
    override this.ToString() = (ValueLens.Value this).ToString()

// Actor Id
type ActorId =
    private
    | ActorId of ShortString

    static member Value_ = (fun (ActorId v) -> v), (fun v _ -> ActorId v)
    member this.IsValid = (ValueLens.Value this).IsValid
    override this.ToString() = (ValueLens.Value this).ToString()

/// Message Id , generally not much use case.
type MessageId =
    private
    | MessageId of ShortString

    static member Value_ = (fun (MessageId v) -> v), (fun v _ -> MessageId v)
    member this.IsValid = (ValueLens.Value this).IsValid
    override this.ToString() = (ValueLens.Value this).ToString()
