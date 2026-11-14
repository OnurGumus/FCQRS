/// Lets FCQRS.Workflows use the sharding helpers, the stop message, journal type names, and
/// tracked projections, and lets the facade tests read a projection's journal read count.
module internal FCQRS.AssemblyInfo

open System.Runtime.CompilerServices

[<assembly: InternalsVisibleTo("FCQRS.Workflows")>]
[<assembly: InternalsVisibleTo("Facade.Tests")>]
do ()
