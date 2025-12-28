using Microsoft.Playwright;

public enum GolfDay
{
    Saturday,
    Sunday
}

public class Program
{
    // ----------------------------
    // Runtime configuration
    // ----------------------------
    private const bool Headless = true;
    private const int SlowMoMs = 0;

    // ----------------------------
    // City of Austin URLs
    // ----------------------------
    private const string LoginUrl = "https://txaustinweb.myvscloud.com/webtrac/web/login.html";
    private const string SearchBaseUrl = "https://txaustinweb.myvscloud.com/webtrac/web/search.html";

    public static async Task Main()
    {
        DotNetEnv.Env.Load();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = Headless,
            SlowMo = SlowMoMs
        });

        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(LoginUrl);
        await LoginAsync(page);

        foreach (var day in GetRequestedDays())
        {
            if (await TryBookDayAsync(page, day))
                break;
        }

        Console.WriteLine("Run complete.");
    }

    // -------------------------------------------------
    // Core booking flow
    // -------------------------------------------------

    static async Task<bool> TryBookDayAsync(IPage page, GolfDay day)
    {
        var date = GetTargetDate(day);
        var url = BuildSearchUrl(date);

        Console.WriteLine($"Attempting {day} ({date:MM/dd/yyyy})");
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.NetworkIdle });

        await WaitUntilBookingOpensAsync(page);

        Console.WriteLine("Adding tee time to cart...");
        await page.Locator(".cart-button:visible").First.ClickAsync();

        await FinalizeBookingAsync(page);

        Console.WriteLine($"{day}: flow completed");
        return true;
    }

    // -------------------------------------------------
    // Booking availability + finalization
    // -------------------------------------------------

    static async Task WaitUntilBookingOpensAsync(IPage page)
    {
        const int maxAttempts = 60;   // ~3 minutes
        const int delayMs = 3_000;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (await IsBookingUnlockedAsync(page))
            {
                Console.WriteLine("Booking unlocked");
                return;
            }

            Console.WriteLine($"Still locked (attempt {attempt}), refreshing...");
            await Task.Delay(delayMs);
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        }

        throw new TimeoutException("Booking never unlocked");
    }

    static async Task<bool> IsBookingUnlockedAsync(IPage page) =>
        await page.EvaluateAsync<bool>(
            @"() => {
                const btn = document.querySelector('.cart-button');
                if (!btn) return false;
                const tooltip = btn.getAttribute('data-tooltip') || '';
                return !tooltip.includes('Unavailable');
            }"
        );

    static async Task FinalizeBookingAsync(IPage page)
    {
        if (IsDryRun())
        {
            Console.WriteLine("DRY RUN enabled — skipping final booking commit");

            await page.ScreenshotAsync(new()
            {
                Path = $"dry-run-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png",
                FullPage = true
            });

            return;
        }

        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var finish = page.Locator("#golfmemberselection_buttononeclicktofinish");

            if (await finish.IsVisibleAsync())
            {
                Console.WriteLine("Finalizing booking...");
                await finish.ClickAsync();
                return;
            }

            Console.WriteLine($"Finalize button missing (attempt {attempt}), retrying...");
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            await WaitUntilBookingOpensAsync(page);
            await page.Locator(".cart-button:visible").First.ClickAsync();
        }

        throw new Exception("Failed to finalize booking");
    }

    // -------------------------------------------------
    // Helpers / configuration
    // -------------------------------------------------

    static async Task LoginAsync(IPage page)
    {
        var username = GetEnv("USERNAME");
        var password = GetEnv("PASSWORD");

        await page.WaitForSelectorAsync("#weblogin_username");

        await page.FillAsync("#weblogin_username", username);
        await page.FillAsync("#weblogin_password", password);

        await Task.WhenAll(
            page.WaitForNavigationAsync(),
            page.ClickAsync("#weblogin_buttonlogin")
        );
    }

    static IReadOnlyList<GolfDay> GetRequestedDays()
    {
        var value = Environment.GetEnvironmentVariable("GOLF_DAYS") ?? "Saturday";

        return value switch
        {
            "Saturday" => new[] { GolfDay.Saturday },
            "Sunday" => new[] { GolfDay.Sunday },
            "Both" => new[] { GolfDay.Saturday, GolfDay.Sunday },
            _ => throw new Exception($"Invalid GOLF_DAYS value: {value}")
        };
    }

    static DateTime GetTargetDate(GolfDay day)
    {
        var testDate = Environment.GetEnvironmentVariable("TEST_DATE");

        if (!string.IsNullOrWhiteSpace(testDate))
        {
            Console.WriteLine($"TEST_DATE override active: {testDate}");
            return DateTime.Parse(testDate);
        }

        return GetNextDay(day);
    }

    static DateTime GetNextDay(GolfDay day)
    {
        var today = DateTime.Today;
        var target = day == GolfDay.Saturday
            ? DayOfWeek.Saturday
            : DayOfWeek.Sunday;

        var delta = ((int)target - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(delta == 0 ? 7 : delta);
    }

    static string BuildSearchUrl(DateTime date)
    {
        var startTime = Environment.GetEnvironmentVariable("START_TIME") ?? "07:30 am";
        var players = Environment.GetEnvironmentVariable("PLAYERS") ?? "4";

        var query = new Dictionary<string, string>
        {
            ["Action"] = "Start",
            ["secondarycode"] = "2",
            ["begintime"] = startTime,
            ["begindate"] = date.ToString("MM/dd/yyyy"),
            ["numberofplayers"] = players,
            ["numberofholes"] = "18",
            ["display"] = "detail",
            ["search"] = "yes",
            ["page"] = "1",
            ["module"] = "GR",
            ["grwebsearch_buttonsearch"] = "yes"
        };

        var qs = string.Join("&",
            query.Select(kvp =>
                $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));

        return $"{SearchBaseUrl}?{qs}";
    }

    static bool IsDryRun() => Environment.GetEnvironmentVariable("DRY_RUN") == "true";

    static string GetEnv(string key) =>Environment.GetEnvironmentVariable(key) ?? throw new Exception($"{key} not set");
}
