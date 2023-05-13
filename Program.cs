// See https://aka.ms/new-console-template for more information
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StackExchange.Redis;

var quantity = 2;

var events = new Dictionary<string, (string Token, string EventId, string EventToken, int TicketId, string Captcha)>
{
     { "grape", new ("d40bb2cdf698f3b0fc788980b5a57c2c", "b8994dc3", "27cb944d11453e760ed506541f9a5d47f9c48081", 575410, "排灣族") }, // 4/8,
     //{ "guncat27", new ("5ef054356b7b5bdfd44010975077c2c9", "b8994dc3", "27cb944d11453e760ed506541f9a5d47f9c48081", 575410, "排灣族") }, // 4/9
};

// redis
var conn = ConnectionMultiplexer.Connect("127.0.0.1:6379");
var database = conn.GetDatabase();

var tasks = new List<Task>();
var rand = new Random();

do
{
    foreach (var item in events)
    {
        if (!database.KeyExists($"kktixFin:{item.Key}:{item.Value.EventId}"))
        {
            tasks.Add(OrderTicket(item.Key, item.Value.Token, item.Value.EventId, item.Value.EventToken, item.Value.TicketId, item.Value.Captcha));
        }
    }

    if (tasks.Any())
    {
        await Task.WhenAny(tasks);

        var count = tasks.RemoveAll(t => t.IsCompleted);
        Console.Write(count);

        await Task.Delay(rand.Next(1000, 2000));
    }
}
while (true);

async Task OrderTicket(string name, string kktixToken, string eventId, string eventToken, int ticketId, string custom_captcha)
{
    var handler = new HttpClientHandler() { };

    var client = new HttpClient(handler);
    client.BaseAddress = new Uri("https://kktix.com");

    // get XSRF-TOKEN
    var baseMessage = new HttpRequestMessage(HttpMethod.Get, $"/g/events/{eventId}/register_info");
    baseMessage.Headers.Add("cookie", $"kktix_session_token_v2={kktixToken};");

    var responseMessage = await client.SendAsync(baseMessage);

    var evnetResult = JsonSerializer.Deserialize<JsonObject>(await responseMessage.Content.ReadAsStringAsync());

    if (evnetResult != null && evnetResult.ContainsKey("ktx_captcha"))
        Console.WriteLine(evnetResult["ktx_captcha"]!["question"]);

    IEnumerable<Cookie> responseCookies = handler.CookieContainer.GetCookies(client.BaseAddress).Cast<Cookie>();

    foreach (Cookie cookie in responseCookies)
    {
        Console.WriteLine($"Key: {cookie.Name}, Value: {cookie.Value}");
        Console.WriteLine();
    }

    var authenticity_token = responseCookies?.FirstOrDefault(x => x.Name == "XSRF-TOKEN")?.Value;

    var values = new
    {
        tickets = new[] {
            new {
                id = ticketId,
                quantity = quantity,
                invitationCodes = new int[] { },
                member_code = "",
                use_qualification_id =  default(string),
            }
        },
        currency = "TWD",
        recaptcha = new
        {
            responseChallenge = "",
        },
        custom_captcha = custom_captcha,
        agreeTerm = true,
    };

    var content = new StringContent(JsonSerializer.Serialize(values), Encoding.UTF8, "application/json");

    var tokenResponse = default(TokenResponse);

    var rand = new Random();

    do
    {
        if (database.HashExists("kktixToken", authenticity_token))
        {
            break;
        }

        var message = new HttpRequestMessage(HttpMethod.Post, $"https://queue.kktix.com/queue/{eventId}?authenticity_token={authenticity_token}");
        message.Headers.Add("cookie", $"kktix_session_token_v2={kktixToken}; uvts=f7a1d62d-eda2-49b3-73de-48790f8fa4b5; pct-{eventId}={eventToken}");
        message.Content = content;

        var result = await client.SendAsync(message);
        var tokenReponseContext = await result.Content.ReadAsStringAsync();
        tokenResponse = JsonSerializer.Deserialize<TokenResponse>(await result.Content.ReadAsStringAsync());

        // Delay
        await Task.Delay(rand.Next(1000, 2000));

        Console.WriteLine($"{eventId} {authenticity_token.Substring(0, 6)} : {tokenReponseContext}");

        //tokenResponse.token = "4d27fb5e-b156-4627-af7f-6ee5c9d637b7";
        if (tokenResponse?.token != null)
        {
            database.HashSet("kktixToken", authenticity_token, tokenResponse?.token);

            CheckQueue(client, tokenResponse, name, eventId);
        }
    }
    while (tokenResponse?.token == null);
}

async Task CheckQueue(HttpClient client, TokenResponse tokenResponse, string name, string eventId)
{
    var rand = new Random();
    var registrationsResponse = default(RegistrationsResponse);

    for (int i = 0; i < 5; i++)
    {
        var baseInfo1 = await client.GetAsync($"https://queue.kktix.com/queue/token/{tokenResponse.token}");
        Console.WriteLine(await baseInfo1.Content.ReadAsStringAsync());

        registrationsResponse = JsonSerializer.Deserialize<RegistrationsResponse>(await baseInfo1.Content.ReadAsStringAsync());

        // Delay
        await Task.Delay(rand.Next(1000, 2000));

        registrationsResponse.name = name;
        registrationsResponse.token = tokenResponse.token;

        if (registrationsResponse.to_param != null)
        {
            registrationsResponse.url = $"https://kktix.com/events/{eventId}/registrations/{registrationsResponse?.to_param}#/";

            Console.WriteLine(registrationsResponse.name);
            Console.WriteLine(registrationsResponse.url);

            //database.HashSet("kktixFin", $"{name}_{eventId}", JsonSerializer.Serialize(registrationsResponse));

            database.StringSet($"kktixFin:{name}:{eventId}", JsonSerializer.Serialize(registrationsResponse), expiry: new TimeSpan(0, 10, 0));

            break;
        }
    }

    database.HashSet("kktix", tokenResponse.token, JsonSerializer.Serialize(registrationsResponse));
}

public class TokenResponse
{
    public string token { get; set; }

    public string result { get; set; }
}

public class RegistrationsResponse
{
    public string message { get; set; }

    public string to_param { get; set; }

    public string token { get; set; }

    public string name { get; set; }


    public string url { get; set; }
}
