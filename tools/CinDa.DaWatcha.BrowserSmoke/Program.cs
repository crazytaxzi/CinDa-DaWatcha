using CinDa.DaWatcha.App;
using CinDa.DaWatcha.Core;

var profile = Path.Combine(
    Path.GetTempPath(), "CinDa-DaWatcha-BrowserSmoke-" + Guid.NewGuid());
var settings = new AppSettings
{
    FirefoxProfileDirectory = profile
};

try
{
    Console.WriteLine("Launching managed Firefox...");
    using var browser = new FirefoxChatController(() => settings);
    await browser.StartAsync();
    Console.WriteLine("PASS: Selenium launched Firefox with one managed tab.");
}
finally
{
    try
    {
        if (Directory.Exists(profile))
            Directory.Delete(profile, true);
    }
    catch
    {
        // Firefox may briefly retain a profile lock after shutdown.
    }
}
