
using System.Text;
using Microsoft.Playwright;
using Newtonsoft.Json;
using Flurl.Http;
using System.Text.RegularExpressions;
using Polly.Retry;
using Polly;

internal class Program
{
    public static SemaphoreSlim Semaphore = new SemaphoreSlim(5, 5);
    private static AsyncRetryPolicy _retryPolicy => Policy
    .Handle<Exception>()
    .WaitAndRetryAsync(5,
        retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
        (exception, timeSpan, retryCount, context) =>
        {
            System.Console.WriteLine($"retrying {retryCount}/5");
        });
    private static async Task Main(string[] args)
    {
        var downloadTasks = new List<Task>();
        var settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText("config.jsonc"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
        });
        var downloader = Task.Run(async () =>
        {
            while (downloadTasks.Count < 1)
            {
                await Task.Delay(500);
            }
            await Task.WhenAll(downloadTasks);
        });
        downloader.Wait();
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("https://filman.cc/logowanie");
        await page.GetByLabel("Nazwa użytkownika").FillAsync(settings.user);
        await page.GetByLabel("Hasło").FillAsync(settings.password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Zaloguj się" }).ClickAsync();
        await page.GotoAsync("https://filman.cc/serial-online/3912/laboratorium-dextera-dexters-laboratory");
        var episodes = (await page.Locator("ul#episode-list a").AllAsync()).ToList();
        foreach (var episode in episodes)
        {
            await _retryPolicy.ExecuteAsync(async () =>
            {
                var episodePage = await context.NewPageAsync();
                await episodePage.GotoAsync(await episode.GetAttributeAsync("href"));
                //await episode.ClickAsync();
                await episodePage.WaitForSelectorAsync("table#links");
                var title = await episodePage.Locator("#item-headline h3").InnerTextAsync();
                var links = await episodePage.Locator("table#links a").AllAsync();
                Uri vidPageUrl = null;
                try
                {
                    vidPageUrl = links
                        .Where(x => !string.IsNullOrEmpty(x.GetAttributeAsync("data-iframe").Result))
                        .Select(x => x.GetAttributeAsync("data-iframe").Result)
                        .Select(x => Encoding.UTF8.GetString(Convert.FromBase64String(x)))
                        .Select(x => JsonConvert.DeserializeObject<VidPage>(x))
                        .Select(x => x.src)
                        .First(x => x.Host.Equals("dood.yt"));
                }
                catch (InvalidOperationException)
                {
                    System.Console.WriteLine($"no dood.yt link found for {page.Url}");
                }
                var vidPage = await context.NewPageAsync();
                await vidPage.GotoAsync(vidPageUrl.ToString());
                await vidPage.WaitForSelectorAsync("video");
                var vidElem = vidPage.Locator("video");
                var vidUrl = await vidElem.GetAttributeAsync("src");
                var referer = new Uri(vidPage.Url).Host;
                System.Console.WriteLine(vidPageUrl);
                await vidPage.CloseAsync();
                await episodePage.CloseAsync();
                downloadTasks.Add(DownloadAsync(vidUrl, title, referer));
            });
        }
        downloader.Wait();
    }
    public static async Task DownloadAsync(string url, string name, string referer)
    {
        Program.Semaphore.Wait();
        try
        {
            string fileName = $"{Regex.Replace(name, @"[\\/:*?""<>|]", "-")}.mp4";
            fileName = System.IO.File.Exists(fileName) ? fileName + Path.GetRandomFileName() + ".mp4" : fileName;
            System.Console.WriteLine($"[{System.Environment.CurrentManagedThreadId}] downloading {fileName}...");
            //string url = $"https:{this.File}";
            await url
            .WithHeader("user-agent", "chromium")
            .WithHeader("referer", referer)
            .DownloadFileAsync(@".", fileName);
        }
        finally
        {
            Program.Semaphore.Release();
        }
    }
}
class Settings
{
    public string user;
    public string password;
}
class VidPage
{
    public Uri src;
    public string width;
    public string height;
}