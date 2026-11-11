/// Lets FCQRS.Workflows use the sharding helpers, the stop message, and journal type names.
module internal FCQRS.AssemblyInfo

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("FCQRS.Workflows")>]
do ()
