// snippet: 1
open OpenTelemetry.Trace
open OpenTelemetry.Logs
let configureFlush (tracerProvider: TracerProvider) (loggerProvider: LoggerProvider) =
    // snippet: 2
