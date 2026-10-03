// Клиент API валидации телефона Atlorium — разбор номера любой страны.
//
// Запуск (работает сразу, без регистрации — на демо-ключе):
//     dotnet run
//     dotnet run -- "+79161234567,8 495 785-63-00"
//
// Боевой ключ: получить на https://atlorium.com и положить в переменную окружения
// ATLORIUM_API_KEY. Код при этом не меняется.
//
// ГРАНИЦА СЕРВИСА. Разбор идёт по встроенному справочнику мировой нумерации, локально,
// без обращения в сеть оператора. Сервис отвечает на вопрос «может ли такой номер
// существовать и как он правильно записывается», но НЕ проверяет, пользуется ли номером
// живой абонент. «Валидный» не значит «рабочий».

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

// Публичный демо-ключ. С ним API отвечает правдоподобными МОКАМИ (не реальными
// данными) — чтобы можно было встроить и протестировать интеграцию до оплаты.
// Ответы детерминированы: один и тот же запрос всегда даёт один и тот же результат,
// поэтому на них можно писать стабильные тесты.
const string SandboxKey = "ak_sandbox_demo_mockdata_v1";

// Номера в разнобойных форматах — так и выглядит выгрузка из реальной CRM.
string[] demoNumbers =
[
    "+79161234567",
    "+7 (916) 123-45-67",
    "8 916 123-45-67",
    "+7 495 785-63-00",
    "+7 809 123-45-67",
    "+1 650 253 0000",
    "12345",
];

var apiKey = Environment.GetEnvironmentVariable("ATLORIUM_API_KEY") ?? SandboxKey;
var baseUrl = Environment.GetEnvironmentVariable("ATLORIUM_BASE_URL") ?? "https://atlorium.com";

using var http = new HttpClient
{
    BaseAddress = new Uri(baseUrl),
    Timeout = TimeSpan.FromSeconds(30),
};
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

var client = new PhoneClient(http);

if (apiKey == SandboxKey)
{
    Console.WriteLine("Демо-ключ: ответы сгенерированы (моки), не реальные данные.\n");
}

var numbers = args.Length > 0 ? args[0].Split(',') : demoNumbers;

Summary summary;
try
{
    summary = await ContactListNormalizer.NormalizeAsync(client, numbers);
}
catch (AtloriumException error)
{
    Console.Error.WriteLine($"Ошибка: {error.Message}");
    return 1;
}

Console.WriteLine(Pad("вход", 22) + Pad("E.164", 16) + Pad("тип", 18) + "решение");
Console.WriteLine(new string('-', 78));
foreach (var contact in summary.Contacts)
{
    Console.WriteLine(
        Pad(contact.Raw, 22) +
        Pad(OrDash(contact.E164), 16) +
        Pad(OrDash(contact.NumberType), 18) +
        Pad(contact.Decision, 6) +
        contact.Reason);
}

Console.WriteLine();
foreach (var warning in summary.Warnings)
{
    Console.WriteLine($"  [!] {warning}");
}
if (summary.Warnings.Count > 0)
{
    Console.WriteLine();
}

Console.WriteLine($"К отправке SMS:        {summary.ToSend.Count}");
Console.WriteLine($"Только для обзвона:    {summary.VoiceOnly.Count}");
Console.WriteLine($"Отброшено:             {summary.Dropped.Count}");
Console.WriteLine($"Дублей схлопнулось:    {summary.DuplicatesRemoved}");

Console.WriteLine($"\nПо странам:  {Breakdown(summary.ByCountry)}");
Console.WriteLine($"По типам:    {Breakdown(summary.ByType)}");

// Часовые пояса нужны, чтобы не разбудить абонента SMS в три часа ночи по его времени.
Console.WriteLine("\nК отправке (E.164 — то, что принимают SMS-шлюзы):");
foreach (var contact in summary.ToSend)
{
    var zones = contact.TimeZones.Count > 0 ? string.Join(", ", contact.TimeZones) : "—";
    Console.WriteLine($"  {contact.E164}  {OrDash(contact.Carrier)}  [{zones}]");
}

return 0;

// Дополняет строку пробелами до нужной ширины (минимум один пробел-разделитель).
static string Pad(string value, int width) => value + new string(' ', Math.Max(width - value.Length, 1));

static string OrDash(string? value) => string.IsNullOrEmpty(value) ? "—" : value;

static string Breakdown(IReadOnlyDictionary<string, int> counts) =>
    string.Join(", ", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .Select(pair => $"{pair.Key} — {pair.Value}"));

// ── Клиент ───────────────────────────────────────────────────────────────────

/// <summary>Ошибка API: HTTP-код разложен в человекочитаемую причину.</summary>
public sealed class AtloriumException(HttpStatusCode status)
    : Exception($"HTTP {(int)status}: {Explain(status)}")
{
    public HttpStatusCode Status { get; } = status;

    private static string Explain(HttpStatusCode status) => (int)status switch
    {
        400 => "Строка не является телефонным номером (или не указана страна для номера без «+»)",
        401 => "API-ключ отсутствует, просрочен или недействителен",
        402 => "Недостаточно кредитов на балансе — пополните на https://atlorium.com",
        429 => "Превышен лимит запросов — повторите позже",
        503 => "Сервис временно недоступен (за сбой на своей стороне мы не списываем деньги)",
        _ => "Неизвестная ошибка",
    };
}

public sealed class PhoneClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Карточка номера: валидность, форматы записи, страна, тип, оператор диапазона, часовые пояса.
    /// </summary>
    /// <param name="number">
    /// Номер в международном ("+79161234567") или национальном ("8 916 123-45-67") формате.
    /// Пробелы, скобки и дефисы допустимы.
    /// </param>
    /// <param name="countryCode">
    /// ISO-3166 alpha-2 ("RU") — в какой стране трактовать номер, записанный БЕЗ «+».
    /// Для номеров с «+» не нужен и игнорируется.
    /// </param>
    public async Task<PhoneReport> LookupAsync(string number, string? countryCode = null)
    {
        // ВАЖНО: номер идёт в ПУТИ, поэтому кодируем его целиком. EscapeDataString превращает
        // «+» в %2B, а пробел — в %20. Голый «+» часть веб-серверов и прокси прочитала бы как
        // пробел: номер потерял бы код страны и разбор развалился бы.
        var path = $"/api/Phone/{Uri.EscapeDataString(number)}";
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            path += $"?countryCode={Uri.EscapeDataString(countryCode)}";
        }

        using var response = await http.GetAsync(path);
        if (!response.IsSuccessStatusCode)
        {
            throw new AtloriumException(response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<PhoneReport>(json, JsonOptions)
               ?? throw new InvalidOperationException("Пустой ответ API.");
    }
}

// ── Модель ответа ────────────────────────────────────────────────────────────

/// <summary>Тип номера, определённый по его диапазону в справочнике нумерации.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PhoneNumberKind>))]
public enum PhoneNumberKind
{
    Unknown,
    Mobile,
    FixedLine,

    /// <summary>Страна не разделяет мобильные и стационарные номера (например, США).</summary>
    FixedLineOrMobile,

    /// <summary>Бесплатный для звонящего номер (8-800 и мировые аналоги).</summary>
    TollFree,

    /// <summary>Номер с повышенной тарификацией.</summary>
    PremiumRate,
    SharedCost,
    Voip,
    PersonalNumber,
    Pager,
    Uan,
    Voicemail,
}

/// <summary>Результат офлайн-разбора номера.</summary>
public sealed record PhoneNumberInfo
{
    /// <summary>
    /// Номер попадает в реально выделенный диапазон своей страны.
    /// Это НЕ значит, что за номером есть живой абонент.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>Номер лишь похож на телефонный (подходящая длина). Мягче, чем <see cref="IsValid"/>.</summary>
    public bool IsPossible { get; init; }

    /// <summary>Канонический формат. Именно его стоит хранить в базе и отдавать SMS-шлюзу.</summary>
    public string? E164 { get; init; }

    public string? International { get; init; }
    public string? National { get; init; }
    public string? Rfc3966 { get; init; }
    public int CountryCallingCode { get; init; }
    public string? CountryCode { get; init; }
    public string? Country { get; init; }
    public PhoneNumberKind NumberType { get; init; }

    /// <summary>Оператор ДИАПАЗОНА, а не фактический: номер мог быть перенесён по MNP.</summary>
    public string? OriginalCarrier { get; init; }

    /// <summary>Привязка ДИАПАЗОНА, а не местоположение абонента. У мобильных обычно null.</summary>
    public string? Location { get; init; }

    public IReadOnlyList<string> TimeZones { get; init; } = [];
}

/// <summary>Карточка результата валидации телефона.</summary>
public sealed record PhoneReport
{
    public string Input { get; init; } = "";
    public PhoneNumberInfo Number { get; init; } = new();
    public long ElapsedMs { get; init; }
}

// ── Применение данных: подготовка базы контактов к SMS-рассылке ───────────────
// Карточка номера сама по себе — просто JSON. Ценность появляется, когда по ней
// принимают решение. Ниже — то, что реально делают перед рассылкой: отсев номеров,
// на которые SMS не дойдёт или дойдёт дорого, приведение к E.164 и дедупликация.

/// <summary>Решения по номеру.</summary>
public static class Decisions
{
    public const string Keep = "KEEP";   // в рассылку
    public const string Flag = "FLAG";   // в рассылку, но с пометкой риска
    public const string Dup = "DUP";     // дубль: тот же номер уже есть в списке
    public const string Voice = "VOICE"; // SMS не примет, но годится для голосового обзвона
    public const string Drop = "DROP";   // выбросить
}

/// <summary>Итог разбора одного номера из списка.</summary>
public sealed class Contact(string raw)
{
    /// <summary>Как номер был записан на входе.</summary>
    public string Raw { get; } = raw;

    /// <summary>Канонический формат — единственный, который принимают SMS-шлюзы.</summary>
    public string? E164 { get; set; }

    public string? NumberType { get; set; }
    public string? CountryCode { get; set; }

    /// <summary>Оператор ДИАПАЗОНА, а не фактический (см. MNP).</summary>
    public string? Carrier { get; set; }

    public IReadOnlyList<string> TimeZones { get; set; } = [];
    public string Decision { get; set; } = Decisions.Drop;
    public string Reason { get; set; } = "";
}

/// <summary>Сводка по всему списку.</summary>
public sealed class Summary
{
    public List<Contact> Contacts { get; } = [];
    public List<string> Warnings { get; } = [];
    public Dictionary<string, int> ByCountry { get; } = [];
    public Dictionary<string, int> ByType { get; } = [];
    public int DuplicatesRemoved { get; set; }

    public IReadOnlyList<Contact> ToSend =>
        Contacts.Where(c => c.Decision is Decisions.Keep or Decisions.Flag).ToList();

    public IReadOnlyList<Contact> VoiceOnly =>
        Contacts.Where(c => c.Decision == Decisions.Voice).ToList();

    public IReadOnlyList<Contact> Dropped =>
        Contacts.Where(c => c.Decision == Decisions.Drop).ToList();
}

public static class ContactListNormalizer
{
    /// <summary>
    /// Готовит список номеров к SMS-рассылке: решение по каждому + дедупликация по E.164.
    /// </summary>
    public static async Task<Summary> NormalizeAsync(
        PhoneClient client,
        IEnumerable<string> numbers,
        string defaultCountry = "RU")
    {
        var summary = new Summary();

        foreach (var input in numbers)
        {
            var raw = input.Trim();
            if (raw.Length == 0)
            {
                continue;
            }

            var contact = new Contact(raw);

            // countryCode нужен только номеру без «+»: иначе страну определить не из чего.
            var country = raw.StartsWith('+') ? null : defaultCountry;

            PhoneReport report;
            try
            {
                report = await client.LookupAsync(raw, country);
            }
            catch (AtloriumException error) when (error.Status == HttpStatusCode.BadRequest)
            {
                // 400 — строка вообще не является телефоном. Такой запрос не тарифицируется.
                contact.Reason = "не распознан как телефонный номер";
                summary.Contacts.Add(contact);
                continue;
            }

            var number = report.Number;
            contact.E164 = number.E164;
            contact.NumberType = number.NumberType.ToString();
            contact.CountryCode = number.CountryCode;
            contact.Carrier = number.OriginalCarrier;
            contact.TimeZones = number.TimeZones;

            // Невалиден — значит, номер не попадает в реально выделенный диапазон своей
            // страны. SMS уйдёт в никуда, а деньги за неё спишут.
            if (!number.IsValid)
            {
                contact.Reason = "номер не попадает в выделенный диапазон";
                summary.Contacts.Add(contact);
                continue;
            }

            switch (number.NumberType)
            {
                case PhoneNumberKind.PremiumRate:
                    // Премиум-номера тарифицируются по повышенной ставке. Случайная отправка
                    // на них бьёт по бюджету — отсеиваем и говорим об этом вслух.
                    contact.Reason = "премиум-номер: повышенная тарификация";
                    summary.Warnings.Add(
                        $"{raw} — премиум-номер ({contact.E164}). " +
                        "Отправка на такие номера тарифицируется по повышенной ставке.");
                    break;

                case PhoneNumberKind.TollFree:
                case PhoneNumberKind.SharedCost:
                    contact.Reason = "сервисный номер (8-800 и аналоги), SMS не принимает";
                    break;

                case PhoneNumberKind.FixedLine:
                    // Стационарный номер SMS не принимает — но это живой контакт для обзвона.
                    contact.Decision = Decisions.Voice;
                    contact.Reason = "стационарный: не для SMS, но годится для голосового обзвона";
                    break;

                case PhoneNumberKind.Voip:
                    // Виртуальные номера часто используют как одноразовые при верификации.
                    contact.Decision = Decisions.Flag;
                    contact.Reason = "VoIP: повышенный риск фрода / одноразового номера";
                    break;

                case PhoneNumberKind.Mobile:
                    contact.Decision = Decisions.Keep;
                    contact.Reason = "мобильный";
                    break;

                case PhoneNumberKind.FixedLineOrMobile:
                    contact.Decision = Decisions.Keep;
                    contact.Reason = "страна не разделяет мобильные и стационарные номера";
                    break;

                default:
                    contact.Reason = $"тип {number.NumberType}: SMS не отправляем";
                    break;
            }

            summary.Contacts.Add(contact);
        }

        // Дедупликация — только среди тех, кто дошёл до рассылки. Два по-разному записанных
        // номера после приведения к E.164 — один и тот же контакт. Это прямая экономия:
        // за каждую лишнюю SMS платит отправитель.
        var seen = new Dictionary<string, string>();
        foreach (var contact in summary.Contacts)
        {
            if (contact.E164 is not { Length: > 0 } e164)
            {
                continue;
            }
            if (contact.Decision is not (Decisions.Keep or Decisions.Flag or Decisions.Voice))
            {
                continue;
            }

            if (seen.TryGetValue(e164, out var first))
            {
                contact.Decision = Decisions.Dup;
                contact.Reason = $"дубль '{first}'";
                summary.DuplicatesRemoved++;
            }
            else
            {
                seen[e164] = contact.Raw;
            }
        }

        // Разбивка считается по уникальным разобранным номерам — дубли не удваивают статистику.
        var counted = new HashSet<string>();
        foreach (var contact in summary.Contacts)
        {
            if (contact.E164 is not { Length: > 0 } e164 || !counted.Add(e164))
            {
                continue;
            }

            var code = contact.CountryCode is { Length: > 0 } c ? c : "??";
            var kind = contact.NumberType is { Length: > 0 } k ? k : "Unknown";

            summary.ByCountry[code] = summary.ByCountry.GetValueOrDefault(code) + 1;
            summary.ByType[kind] = summary.ByType.GetValueOrDefault(kind) + 1;
        }

        return summary;
    }
}
