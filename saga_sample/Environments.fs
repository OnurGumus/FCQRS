module Environments

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FCQRS.Common
/// <summary>
/// Composition root for the application environment. While being a god object is an anti-pattern, it plays nicely with F# partial application.
/// Much better than DI magic with reflection. It wraps IConfiguration and ILoggerFactory.
/// </summary>
type AppEnv(config: IConfiguration, loggerFactory: ILoggerFactory) =
    interface ILoggerFactoryWrapper with
      member _.LoggerFactory = loggerFactory

    interface IConfigurationWrapper with
        member _.Configuration = config