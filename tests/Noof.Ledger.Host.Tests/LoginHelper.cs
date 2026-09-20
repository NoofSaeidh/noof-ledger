using System.Text.RegularExpressions;

namespace Noof.Ledger.Host.Tests;

public static partial class LoginHelper
{
    public static async Task<HttpResponseMessage> PostWithTokenAsync(HttpClient client, string username, string password)
    {
        var html = await client.GetStringAsync("/account/login", TestContext.Current.CancellationToken);
        var token = TokenPattern().Match(html).Groups[1].Value;

        return await client.PostAsync("/account/login",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("username", username),
                new KeyValuePair<string, string>("password", password),
                new KeyValuePair<string, string>("__RequestVerificationToken", token)]),
            TestContext.Current.CancellationToken);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]*)\"")]
    private static partial Regex TokenPattern();
}
