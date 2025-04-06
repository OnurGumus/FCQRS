module Environments

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging

/// <summary>
/// Composition root for the application environment. While being a god object is an anti-pattern, it plays nicely with F# partial application.
/// Much better than DI magic with reflection. It wraps IConfiguration and ILoggerFactory.
/// </summary>
type AppEnv(config: IConfiguration, loggerFactory: ILoggerFactory) =
    interface ILoggerFactory with
        member this.AddProvider(provider: ILoggerProvider) : unit = loggerFactory.AddProvider(provider)

        member this.CreateLogger(categoryName: string) : ILogger =
            loggerFactory.CreateLogger(categoryName)

        member this.Dispose() : unit = loggerFactory.Dispose()

    interface IConfiguration with
        member _.Item
            with get (key: string) = config.[key]
            and set key v = config.[key] <- v

        member _.GetChildren() = config.GetChildren()
        member _.GetReloadToken() = config.GetReloadToken()
        member _.GetSection key = config.GetSection(key)