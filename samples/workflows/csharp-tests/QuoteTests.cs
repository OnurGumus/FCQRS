using FCQRS.Workflows;
using Xunit;

public class QuoteTests
{
    // docs:setup
    // No activity runs in a test: the test gives each step's outcome. These
    // stand-ins fail the test if one runs anyway.
    private sealed class NoPrices : IPriceService
    {
        public Task<decimal> Fetch(string key, string symbol) =>
            throw new InvalidOperationException("a test runs no activity");
    }

    private sealed class NoMailer : IMailer
    {
        public Task<string> Send(string key, string customer, decimal total) =>
            throw new InvalidOperationException("a test runs no activity");
    }

    private static readonly QuoteWorkflow Quote =
        new(new NoPrices(), new NoMailer());
    private static readonly QuoteRequest Large = new("Ada", "ABC", 100);
    // docs:end

    // docs:approved
    [Fact]
    public void A_quote_over_1000_is_sent_once_a_person_approves_it()
    {
        var flow = WorkflowTest.Start(Quote, Large);
        flow.Complete("price", 12.5m);
        Assert.True(flow.Status.IsWaitingFor("approval"), "1250 needs approval");
        flow.Signal("approval", true);
        flow.Complete("send-quote", "receipt-1");
        Assert.Equal(new Quote(1250m, true, "receipt-1"), flow.Result);
        // A restart after any stored record must store the same journal.
        flow.CheckReplay();
    }
    // docs:end

    // docs:declined
    [Fact]
    public void A_quote_nobody_approves_within_a_day_is_declined()
    {
        var flow = WorkflowTest.Start(Quote, Large);
        flow.Complete("price", 12.5m);
        flow.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new Quote(1250m, false, ""), flow.Result);
        flow.CheckReplay();
    }
    // docs:end

    // docs:failed
    [Fact]
    public void A_mailer_that_stays_down_faults_the_quote()
    {
        var flow = WorkflowTest.Start(Quote, Large with { Quantity = 10 });
        flow.Complete("price", 12.5m);
        // As if the retry policy's five attempts had failed.
        flow.Fail("send-quote", new TimeoutException("the mailer is down"));
        var failed = Assert.IsType<WorkflowStatus.Failed>(flow.Status);
        Assert.Equal(ErrorKind.Exhausted, failed.Item.Kind);
    }
    // docs:end

    // docs:journal
    [Fact]
    public void A_quote_stores_the_step_names_that_running_quotes_depend_on()
    {
        var flow = WorkflowTest.Start(Quote, Large);
        flow.Complete("price", 12.5m);
        flow.Signal("approval", true);
        flow.Complete("send-quote", "receipt-1");
        string[] expected =
        [
            "started",
            "scheduled 0 price", "completed 0 price",
            "scheduled 1 approval", "signal approval", "completed 1 approval",
            "scheduled 2 send-quote", "completed 2 send-quote",
            "finished",
        ];
        Assert.Equal(expected, flow.Summary);
    }
    // docs:end
}
