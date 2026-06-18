using Google.Authenticator;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(
        theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code,
        outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "ud_dns_record.log",
        outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31)
    .CreateLogger();

try
{
    // --- argument parsing -------------------------------------------------
    string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).Take(1).FirstOrDefault() ?? string.Empty;

    var mode = Arg("-mode").Trim().ToLowerInvariant();
    var mail = Arg("-mail");
    var password = Arg("-pw");
    var tfa = Arg("-tfa");
    var record = Arg("-record").Trim().Trim('.').ToLowerInvariant();
    var value = Arg("-value");

    if (mode is not ("create" or "delete") || string.IsNullOrEmpty(mail) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(record))
        throw new ArgumentException(
            "Usage: -mode <create|delete> -mail <mail> -pw <password> -record <_acme-challenge.example.com> [-value <txt-value>] [-tfa <secret>]");

    if (mode == "create" && string.IsNullOrEmpty(value))
        throw new ArgumentException("-value is required when -mode create");

    // --- login ------------------------------------------------------------
    var cookieContainer = new CookieContainer();
    var client = new HttpClient(new HttpClientHandler()
    {
        AllowAutoRedirect = true,
        UseCookies = true,
        CookieContainer = cookieContainer,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    });

    client.DefaultRequestHeaders.Add("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9");
    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/109.0.0.0 Safari/537.36");
    var login_page = await client.GetStringAsync("https://www.united-domains.de/login/");

    var csrf = Regex.Match(login_page, "(?<=<input type=\"hidden\" name=\"csrf\" value=\")[^\"]*(?=\"( /)?>)").Value;
    var csrf_script = Regex.Match(login_page, "(?<=\"CSRF_TOKEN\":\")[^\"]*(?=\")").Value;
    var session_id = Regex.Match(login_page, "(?<=\"SESSION_ID\":\")[^\"]*(?=\")").Value;

    client.DefaultRequestHeaders.Add("http-x-csrf-token", csrf_script);

    var language_response = await client.PostAsync($"https://www.united-domains.de/set-user-language?SESSID={session_id}", new FormUrlEncodedContent(new Dictionary<string, string>()
    {
        ["language"] = "en-US"
    }));

    if (!language_response.IsSuccessStatusCode)
        throw new InvalidOperationException($"setting language failed with error: {language_response.StatusCode}");

    var login_response = await client.PostAsync("https://www.united-domains.de/login/", new FormUrlEncodedContent(new Dictionary<string, string>()
    {
        ["csrf"] = csrf,
        ["email"] = mail,
        ["pwd"] = password,
        ["selector"] = "login",
        ["submit"] = "Login"
    }));

    if (!login_response.IsSuccessStatusCode)
        throw new InvalidOperationException($"login failed with error: {login_response.StatusCode}");

    if (!string.IsNullOrEmpty(tfa))
    {
        var authenticator = new TwoFactorAuthenticator();
        var current_pin = authenticator.GetCurrentPIN(tfa, true);

        var tfa_response = await client.PostAsync("https://www.united-domains.de/login/", new FormUrlEncodedContent(new Dictionary<string, string>()
        {
            ["csrf"] = csrf,
            ["totp"] = current_pin,
            ["submit"] = "Login",
        }));

        if (!tfa_response.IsSuccessStatusCode)
            throw new InvalidOperationException($"two factor authentication failed with error: {tfa_response.StatusCode}");
    }

    Log.Information("logged in as {Mail}", mail);

    client.DefaultRequestHeaders.Add("accept", "application/json, text/plain, */*");

    // --- resolve the registered domain for the requested record -----------
    var domain_list_response = await client.GetAsync("https://www.united-domains.de/pfapi/domain-list");
    if (!domain_list_response.IsSuccessStatusCode)
        throw new InvalidOperationException("getting domain list failed");

    var domain_list = JObject.Parse(await domain_list_response.Content.ReadAsStringAsync());

    // longest-suffix match: pick the registered domain that is the longest suffix of the record name
    JToken? matched = null;
    foreach (var entry in domain_list["data"]!)
    {
        var name = entry!["name"]!.ToString().ToLowerInvariant();
        if (record == name || record.EndsWith("." + name))
        {
            if (matched is null || name.Length > matched["name"]!.ToString().Length)
                matched = entry;
        }
    }

    if (matched is null)
        throw new InvalidOperationException($"no registered united-domains domain found for record {record}");

    var registered_domain = matched["name"]!.ToString().ToLowerInvariant();
    var domain_id = matched["id"]!.ToString();
    var sub_domain = record == registered_domain
        ? string.Empty
        : record[..^(registered_domain.Length + 1)];

    Log.Information("record {Record} -> domain {Domain} (id {Id}), sub_domain '{Sub}'", record, registered_domain, domain_id, sub_domain);

    // --- fetch current records --------------------------------------------
    var records_url = $"https://www.united-domains.de/pfapi/dns/domain/{domain_id}/records";
    var get_records_response = await client.GetAsync(records_url);
    if (!get_records_response.IsSuccessStatusCode)
        throw new InvalidOperationException($"getting records for {registered_domain} failed with error: {get_records_response.StatusCode}");

    var dns_records = JObject.Parse(await get_records_response.Content.ReadAsStringAsync());
    var txt_records = dns_records["data"]?["TXT"] as JArray ?? new JArray();

    // find any existing TXT record(s) matching this challenge name (and value if provided)
    var existing = txt_records
        .Where(e => string.Equals(e["filter_value"]?.ToString(), record, StringComparison.OrdinalIgnoreCase))
        .Where(e => string.IsNullOrEmpty(value) || string.Equals(e["content"]?.ToString()?.Trim(), value.Trim(), StringComparison.Ordinal))
        .ToList();

    if (mode == "create")
    {
        // already present with the same value -> nothing to do (idempotent)
        if (existing.Count > 0)
        {
            Log.Information("TXT record {Record} with the requested value already exists, nothing to do", record);
            return 0;
        }

        var payload = new Dictionary<string, object?>()
        {
            ["record"] = new Dictionary<string, object?>()
            {
                ["address"] = value,
                ["content"] = value,
                ["domain"] = registered_domain,
                ["filter_value"] = record,
                ["formId"] = "new",
                ["id"] = null,
                ["ssl"] = false,
                ["standard_value"] = false,
                ["sub_domain"] = sub_domain,
                ["ttl"] = 60,
                ["type"] = "TXT",
                ["udag_record_type"] = "TXT",
                ["webspace"] = false,
            },
            ["domain_lock_state"] = new Dictionary<string, object>()
            {
                ["domain_locked"] = false,
                ["email_locked"] = false,
                ["web_locked"] = false,
            },
        };

        var json = JsonConvert.SerializeObject(payload);
        // NOTE: create endpoint/verb modelled on the existing A-record update (POST to the records
        // collection with a record that has no id). Confirm against live traffic if this fails.
        var create = await client.PostAsync(records_url, new StringContent(json, Encoding.UTF8, "application/json"));
        if (!create.IsSuccessStatusCode)
        {
            var body = await create.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"creating TXT record {record} failed with error: {create.StatusCode} {body}");
        }

        Log.Information("created TXT record {Record} = '{Value}'", record, value);
        return 0;
    }
    else // delete
    {
        if (existing.Count == 0)
        {
            Log.Information("no matching TXT record {Record} to delete, nothing to do", record);
            return 0;
        }

        foreach (var rec in existing)
        {
            var record_id = rec["id"]!.ToString();
            var delete = await client.DeleteAsync($"{records_url}/{record_id}");
            if (!delete.IsSuccessStatusCode)
            {
                var body = await delete.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"deleting TXT record {record} (id {record_id}) failed with error: {delete.StatusCode} {body}");
            }

            Log.Information("deleted TXT record {Record} (id {Id})", record, record_id);
        }

        return 0;
    }
}
catch (Exception ex)
{
    Log.Error(ex, "{Message}", ex.Message);
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
