using System.Net;
using System.Net.Http;
using System.Text;
using WinNotch;

namespace WinNotch.Tests;

public class GleitzeitTests
{
    static readonly TimeSpan Plus = new(2, 45, 0);

    [Fact]
    public void Closed_day_is_the_reported_saldo_and_already_neutral()
    {
        var blocks = new[]
        {
            new WorkBlock(Time(7, 47), Time(11, 30)),
            new WorkBlock(Time(12, 0), Time(17, 8)),
        };
        var now = Time(17, 30);
        Assert.Equal(Time(8, 51), Worked(blocks));
        Assert.Equal(Time(0, 30), GapBetween(blocks));
        var snap = Gleitzeit.At(Plus, blocks, now, ReadAt(17, 30), ReadAt(17, 30), reportedIncludesOpen: false);
        Assert.Equal(Plus, snap.Balance);
        Assert.Equal(TimeSpan.Zero, snap.UntilNeutral);
        Assert.Equal(WorkPresence.ClockedOut, snap.Presence);
        Assert.Equal("+2:45", Gleitzeit.Format(snap.Balance, seconds: false));
        Assert.Equal("0:00", Gleitzeit.Format(snap.UntilNeutral, seconds: false));
    }

    [Fact]
    public void A_short_gap_with_no_open_booking_is_a_break_and_the_saldo_stays_put()
    {
        var blocks = new[] { new WorkBlock(Time(7, 47), Time(11, 30)) };
        var snap = Gleitzeit.At(Plus, blocks, Time(11, 45), ReadAt(11, 45), ReadAt(11, 45), reportedIncludesOpen: false);
        Assert.Equal(WorkPresence.OnBreak, snap.Presence);
        Assert.Equal(Plus, snap.Balance);
        Assert.Equal(TimeSpan.FromMinutes(15), snap.BreakTaken);
    }

    [Fact]
    public void Thirty_minutes_after_a_single_block_counts_as_clocked_out()
    {
        var blocks = new[] { new WorkBlock(Time(7, 47), Time(11, 30)) };
        var snap = Gleitzeit.At(Plus, blocks, Time(12, 0), ReadAt(12, 0), ReadAt(12, 0), false);
        Assert.Equal(WorkPresence.ClockedOut, snap.Presence);
    }

    [Fact]
    public void An_open_booking_adds_time_since_it_started_until_the_extranet_is_seen_growing()
    {
        var blocks = new[] { new WorkBlock(Time(8, 0), null) };
        var saldo = TimeSpan.FromHours(-1);
        var snap = Gleitzeit.At(saldo, blocks, Time(8, 40), ReadAt(8, 40), ReadAt(8, 40), reportedIncludesOpen: false);
        Assert.Equal(WorkPresence.Working, snap.Presence);
        Assert.Equal(TimeSpan.FromMinutes(-20), snap.Balance);
        Assert.Equal(TimeSpan.FromMinutes(20), snap.UntilNeutral);
        Assert.Equal("\u22120:20", Gleitzeit.Format(snap.Balance, seconds: false));

        var asOf = ReadAt(8, 40);
        var growing = Gleitzeit.At(saldo, blocks, Time(8, 40), asOf.AddSeconds(-20), asOf, reportedIncludesOpen: true);
        Assert.Equal(saldo + TimeSpan.FromSeconds(20), growing.Balance);
    }

    [Fact]
    public void A_stale_reading_does_not_keep_counting()
    {
        var blocks = new[] { new WorkBlock(Time(8, 0), null) };
        var snap = Gleitzeit.At(TimeSpan.FromHours(-1), blocks, Time(9, 0), ReadAt(8, 0), ReadAt(8, 5), false);
        Assert.True(snap.Stale);
        Assert.Equal(TimeSpan.FromHours(-1), snap.Balance);
    }

    [Fact]
    public void Two_reads_show_whether_the_extranet_already_includes_the_open_booking()
    {
        Assert.False(Gleitzeit.DetectIncludesOpen(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(20), wasOpen: true));
        Assert.True(Gleitzeit.DetectIncludesOpen(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10 * 60 + 20), TimeSpan.FromSeconds(20), wasOpen: true));
        Assert.Null(Gleitzeit.DetectIncludesOpen(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(40), TimeSpan.FromSeconds(20), wasOpen: true));
        Assert.Null(Gleitzeit.DetectIncludesOpen(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(20), wasOpen: false));
    }

    [Fact]
    public void The_total_saldo_wins_over_the_month_close_and_over_holiday_balance()
    {
        const string today = """
            {
              "gleitzeitAktuell": "02:20",
              "gleitzeitSaldo": "02:45",
              "monatsabschluss": "-146:26",
              "ferienSaldo": 20.0,
              "records": [
                {"beginn": "07:47", "ende": "11:30"},
                {"beginn": "12:00", "ende": "17:08"}
              ]
            }
            """;
        Assert.True(GleitzeitParser.TryParse(today, null, null, null, out var parsed));
        Assert.Equal(Plus, parsed.Saldo);
        Assert.Equal(2, parsed.Today.Count);
        Assert.Equal(Time(7, 47), parsed.Today[0].Start);
        Assert.Equal(Time(17, 8), parsed.Today[1].End);
    }

    [Fact]
    public void The_open_month_close_is_not_used_as_the_balance()
    {
        const string json = """{"monatsabschluss":"-146:26","ferienSaldo":"20.00"}""";
        Assert.False(GleitzeitParser.TryParse(json, null, null, null, out _));
    }

    [Fact]
    public void An_open_end_and_the_legacy_page_label_are_read()
    {
        const string today = """{"entries":[{"from":"2026-11-18T08:00:00","to":null}]}""";
        const string html = "<div>Gleitzeit aktuell 02:20</div><div>Gleitzeit Saldo</div><b>\u22120:30</b>";
        Assert.True(GleitzeitParser.TryParse(today, null, null, html, out var parsed));
        Assert.Equal(TimeSpan.FromMinutes(-30), parsed.Saldo);
        Assert.Null(parsed.Today[0].End);
    }

    [Fact]
    public async Task A_read_only_signs_in_and_never_writes_a_booking()
    {
        var handler = new ScriptedHandler();
        using var extranet = new BbcExtranet(handler);
        string refresh = await extranet.LoginAsync("person@bbcag.ch", "not-stored", CancellationToken.None);
        Assert.Equal("refresh-1", refresh);
        var reading = await extranet.ReadAsync(refresh, CancellationToken.None);
        Assert.Equal(Plus, reading.Saldo);
        Assert.Equal("refresh-2", reading.RefreshToken);
        Assert.Equal(2, reading.Today.Count);
        Assert.DoesNotContain(handler.Calls, call => call.Method != HttpMethod.Get && !call.Path.EndsWith("/oauth/token", StringComparison.Ordinal));
        Assert.Contains(handler.Calls, call => call.Method == HttpMethod.Get && call.Path.EndsWith("/records/today", StringComparison.Ordinal));
    }

    static TimeSpan Time(int hour, int minute) => new(hour, minute, 0);

    static DateTimeOffset ReadAt(int hour, int minute) => new(2026, 11, 18, hour, minute, 0, TimeSpan.FromHours(1));

    static TimeSpan Worked(IReadOnlyList<WorkBlock> blocks)
        => blocks.Aggregate(TimeSpan.Zero, (sum, block) => sum + ((block.End ?? TimeSpan.Zero) - block.Start));

    static TimeSpan GapBetween(IReadOnlyList<WorkBlock> blocks)
        => blocks[1].Start - blocks[0].End!.Value;

    sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? "";
            Calls.Add((request.Method, path));
            if (request.Method != HttpMethod.Get && !path.EndsWith("/oauth/token", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            if (request.Method == HttpMethod.Post)
            {
                string body = request.Content == null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                string refresh = body.Contains("grant_type=refresh_token", StringComparison.Ordinal) ? "refresh-2" : "refresh-1";
                string json = $$"""{"access_token":"access","refresh_token":"{{refresh}}","expires_in":3600}""";
                return Task.FromResult(Json(json));
            }
            const string today = """
                {"gleitzeitSaldo":"02:45","records":[{"beginn":"07:47","ende":"11:30"},{"beginn":"12:00","ende":"17:08"}]}
                """;
            return Task.FromResult(Json(today));
        }

        static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}
