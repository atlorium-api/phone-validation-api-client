<?php

/**
 * Клиент API валидации телефона Atlorium — разбор номера любой страны.
 *
 * Запуск (работает сразу, без регистрации — на демо-ключе):
 *   php main.php
 *   php main.php "+79161234567,8 495 785-63-00"
 *
 * Боевой ключ: получить на https://atlorium.com и положить в переменную окружения
 * ATLORIUM_API_KEY. Код при этом не меняется.
 *
 * ГРАНИЦА СЕРВИСА. Разбор идёт по встроенному справочнику мировой нумерации, локально,
 * без обращения в сеть оператора. Сервис отвечает на вопрос «может ли такой номер
 * существовать и как он правильно записывается», но НЕ проверяет, пользуется ли номером
 * живой абонент. «Валидный» не значит «рабочий».
 */

declare(strict_types=1);

/**
 * Публичный демо-ключ. С ним API отвечает правдоподобными МОКАМИ (не реальными
 * данными) — чтобы можно было встроить и протестировать интеграцию до оплаты.
 * Ответы детерминированы: один и тот же запрос всегда даёт один и тот же результат,
 * поэтому на них можно писать стабильные тесты.
 */
const SANDBOX_KEY = 'ak_sandbox_demo_mockdata_v1';

const TIMEOUT = 30;

/** Номера в разнобойных форматах — так и выглядит выгрузка из реальной CRM. */
const DEMO_NUMBERS = [
    '+79161234567',
    '+7 (916) 123-45-67',
    '8 916 123-45-67',
    '+7 495 785-63-00',
    '+7 809 123-45-67',
    '+1 650 253 0000',
    '12345',
];

// Решения по номеру.
const KEEP = 'KEEP';   // в рассылку
const FLAG = 'FLAG';   // в рассылку, но с пометкой риска
const DUP = 'DUP';     // дубль: тот же номер уже есть в списке
const VOICE = 'VOICE'; // SMS не примет, но годится для голосового обзвона
const DROP = 'DROP';   // выбросить

/** Ошибка API: HTTP-код разложен в человекочитаемую причину. */
final class AtloriumError extends RuntimeException
{
    private const REASONS = [
        400 => 'Строка не является телефонным номером (или не указана страна для номера без «+»)',
        401 => 'API-ключ отсутствует, просрочен или недействителен',
        402 => 'Недостаточно кредитов на балансе — пополните на https://atlorium.com',
        429 => 'Превышен лимит запросов — повторите позже',
        503 => 'Сервис временно недоступен (за сбой на своей стороне мы не списываем деньги)',
    ];

    public function __construct(public readonly int $status)
    {
        parent::__construct(sprintf(
            'HTTP %d: %s',
            $status,
            self::REASONS[$status] ?? 'Неизвестная ошибка'
        ));
    }
}

final class PhoneClient
{
    private string $apiKey;
    private string $baseUrl;

    public function __construct(?string $apiKey = null, ?string $baseUrl = null)
    {
        $this->apiKey = $apiKey ?? (getenv('ATLORIUM_API_KEY') ?: SANDBOX_KEY);
        $this->baseUrl = $baseUrl ?? (getenv('ATLORIUM_BASE_URL') ?: 'https://atlorium.com');
    }

    public function isSandbox(): bool
    {
        return $this->apiKey === SANDBOX_KEY;
    }

    /**
     * Карточка номера: валидность, форматы записи, страна, тип, оператор диапазона, часовые пояса.
     *
     * @param string      $number      Номер в международном ("+79161234567") или национальном
     *                                 ("8 916 123-45-67") формате. Пробелы, скобки, дефисы допустимы.
     * @param string|null $countryCode ISO-3166 alpha-2 ("RU") — в какой стране трактовать номер,
     *                                 записанный БЕЗ «+». Для номеров с «+» не нужен и игнорируется.
     *
     * @return array<string, mixed>
     */
    public function lookup(string $number, ?string $countryCode = null): array
    {
        // ВАЖНО: номер идёт в ПУТИ, поэтому кодируем его через rawurlencode (RFC 3986):
        // «+» превращается в %2B, пробел — в %20. Обычный urlencode закодировал бы пробел
        // как «+», а голый «+» часть веб-серверов и прокси читает как пробел — номер потерял бы
        // код страны и разбор развалился бы.
        $url = $this->baseUrl . '/api/Phone/' . rawurlencode($number);
        if ($countryCode !== null && $countryCode !== '') {
            $url .= '?' . http_build_query(['countryCode' => $countryCode]);
        }

        $curl = curl_init($url);
        curl_setopt_array($curl, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => TIMEOUT,
            CURLOPT_HTTPHEADER => [
                'Authorization: Bearer ' . $this->apiKey,
                'Accept: application/json',
            ],
        ]);

        $body = curl_exec($curl);
        if ($body === false) {
            $error = curl_error($curl);
            curl_close($curl);
            throw new RuntimeException("Сетевая ошибка: {$error}");
        }

        $status = curl_getinfo($curl, CURLINFO_RESPONSE_CODE);
        curl_close($curl);

        if ($status !== 200) {
            throw new AtloriumError($status);
        }

        return json_decode((string) $body, true, 512, JSON_THROW_ON_ERROR);
    }
}

// ── Применение данных: подготовка базы контактов к SMS-рассылке ───────────────
// Карточка номера сама по себе — просто JSON. Ценность появляется, когда по ней
// принимают решение. Ниже — то, что реально делают перед рассылкой: отсев номеров,
// на которые SMS не дойдёт или дойдёт дорого, приведение к E.164 и дедупликация.

/**
 * Готовит список номеров к SMS-рассылке: решение по каждому + дедупликация по E.164.
 *
 * @param list<string> $numbers
 *
 * @return array{
 *     contacts: list<array<string, mixed>>,
 *     duplicatesRemoved: int,
 *     warnings: list<string>,
 *     byCountry: array<string, int>,
 *     byType: array<string, int>
 * }
 */
function normalizeContactList(PhoneClient $client, array $numbers, string $defaultCountry = 'RU'): array
{
    $contacts = [];
    $warnings = [];

    foreach ($numbers as $input) {
        $raw = trim($input);
        if ($raw === '') {
            continue;
        }

        $contact = [
            'raw' => $raw,          // как номер был записан на входе
            'e164' => null,         // канонический формат — его и принимают SMS-шлюзы
            'numberType' => null,
            'countryCode' => null,
            'carrier' => null,      // оператор ДИАПАЗОНА, а не фактический (см. MNP)
            'timeZones' => [],
            'decision' => DROP,
            'reason' => '',
        ];

        // countryCode нужен только номеру без «+»: иначе страну определить не из чего.
        $country = str_starts_with($raw, '+') ? null : $defaultCountry;

        try {
            $report = $client->lookup($raw, $country);
        } catch (AtloriumError $error) {
            // 400 — строка вообще не является телефоном. Такой запрос не тарифицируется.
            if ($error->status !== 400) {
                throw $error;
            }
            $contact['reason'] = 'не распознан как телефонный номер';
            $contacts[] = $contact;
            continue;
        }

        $number = $report['number'];
        $contact['e164'] = $number['e164'] ?? null;
        $contact['numberType'] = $number['numberType'] ?? null;
        $contact['countryCode'] = $number['countryCode'] ?? null;
        $contact['carrier'] = $number['originalCarrier'] ?? null;
        $contact['timeZones'] = $number['timeZones'] ?? [];

        // Невалиден — значит, номер не попадает в реально выделенный диапазон своей страны.
        // SMS уйдёт в никуда, а деньги за неё спишут.
        if (!($number['isValid'] ?? false)) {
            $contact['reason'] = 'номер не попадает в выделенный диапазон';
            $contacts[] = $contact;
            continue;
        }

        switch ($contact['numberType']) {
            case 'PremiumRate':
                // Премиум-номера тарифицируются по повышенной ставке. Случайная отправка
                // на них бьёт по бюджету — отсеиваем и говорим об этом вслух.
                $contact['reason'] = 'премиум-номер: повышенная тарификация';
                $warnings[] = sprintf(
                    '%s — премиум-номер (%s). Отправка на такие номера тарифицируется по повышенной ставке.',
                    $raw,
                    $contact['e164']
                );
                break;

            case 'TollFree':
            case 'SharedCost':
                $contact['reason'] = 'сервисный номер (8-800 и аналоги), SMS не принимает';
                break;

            case 'FixedLine':
                // Стационарный номер SMS не принимает — но это живой контакт для обзвона.
                $contact['decision'] = VOICE;
                $contact['reason'] = 'стационарный: не для SMS, но годится для голосового обзвона';
                break;

            case 'Voip':
                // Виртуальные номера часто используют как одноразовые при верификации.
                $contact['decision'] = FLAG;
                $contact['reason'] = 'VoIP: повышенный риск фрода / одноразового номера';
                break;

            case 'Mobile':
                $contact['decision'] = KEEP;
                $contact['reason'] = 'мобильный';
                break;

            case 'FixedLineOrMobile':
                $contact['decision'] = KEEP;
                $contact['reason'] = 'страна не разделяет мобильные и стационарные номера';
                break;

            default:
                $contact['reason'] = 'тип ' . ($contact['numberType'] ?? 'Unknown') . ': SMS не отправляем';
                break;
        }

        $contacts[] = $contact;
    }

    // Дедупликация — только среди тех, кто дошёл до рассылки. Два по-разному записанных
    // номера после приведения к E.164 — один и тот же контакт. Это прямая экономия:
    // за каждую лишнюю SMS платит отправитель.
    $seen = [];
    $duplicatesRemoved = 0;
    foreach ($contacts as &$contact) {
        if ($contact['e164'] === null || !in_array($contact['decision'], [KEEP, FLAG, VOICE], true)) {
            continue;
        }

        if (isset($seen[$contact['e164']])) {
            $contact['decision'] = DUP;
            $contact['reason'] = "дубль '{$seen[$contact['e164']]}'";
            $duplicatesRemoved++;
        } else {
            $seen[$contact['e164']] = $contact['raw'];
        }
    }
    unset($contact);

    // Разбивка считается по уникальным разобранным номерам — дубли не удваивают статистику.
    $byCountry = [];
    $byType = [];
    $counted = [];
    foreach ($contacts as $contact) {
        if ($contact['e164'] === null || isset($counted[$contact['e164']])) {
            continue;
        }
        $counted[$contact['e164']] = true;

        $code = $contact['countryCode'] ?? '??';
        $kind = $contact['numberType'] ?? 'Unknown';
        $byCountry[$code] = ($byCountry[$code] ?? 0) + 1;
        $byType[$kind] = ($byType[$kind] ?? 0) + 1;
    }
    ksort($byCountry);
    ksort($byType);

    return [
        'contacts' => $contacts,
        'duplicatesRemoved' => $duplicatesRemoved,
        'warnings' => $warnings,
        'byCountry' => $byCountry,
        'byType' => $byType,
    ];
}

/** Дополняет строку пробелами до нужной ширины (минимум один пробел-разделитель). */
function pad(string $value, int $width): string
{
    // mb_strlen — считаем символы, а не байты: кириллица в UTF-8 занимает два байта.
    return $value . str_repeat(' ', max($width - mb_strlen($value), 1));
}

/** @param array<string, int> $counts */
function breakdown(array $counts): string
{
    $parts = [];
    foreach ($counts as $key => $count) {
        $parts[] = "{$key} — {$count}";
    }

    return implode(', ', $parts);
}

function orDash(?string $value): string
{
    return ($value === null || $value === '') ? '—' : $value;
}

/**
 * @param list<array<string, mixed>> $contacts
 * @param list<string>               $decisions
 *
 * @return list<array<string, mixed>>
 */
function withDecision(array $contacts, array $decisions): array
{
    return array_values(array_filter(
        $contacts,
        static fn(array $contact): bool => in_array($contact['decision'], $decisions, true)
    ));
}

// ── Демонстрация ─────────────────────────────────────────────────────────────

$client = new PhoneClient();

if ($client->isSandbox()) {
    echo "Демо-ключ: ответы сгенерированы (моки), не реальные данные.\n\n";
}

$numbers = isset($argv[1]) ? explode(',', $argv[1]) : DEMO_NUMBERS;

try {
    $summary = normalizeContactList($client, $numbers);
} catch (AtloriumError $error) {
    fwrite(STDERR, "Ошибка: {$error->getMessage()}\n");
    exit(1);
}

echo pad('вход', 22) . pad('E.164', 16) . pad('тип', 18) . "решение\n";
echo str_repeat('-', 78) . "\n";
foreach ($summary['contacts'] as $contact) {
    echo pad($contact['raw'], 22)
        . pad(orDash($contact['e164']), 16)
        . pad(orDash($contact['numberType']), 18)
        . pad($contact['decision'], 6)
        . $contact['reason'] . "\n";
}

echo "\n";
foreach ($summary['warnings'] as $warning) {
    echo "  [!] {$warning}\n";
}
if ($summary['warnings'] !== []) {
    echo "\n";
}

$toSend = withDecision($summary['contacts'], [KEEP, FLAG]);

echo 'К отправке SMS:        ' . count($toSend) . "\n";
echo 'Только для обзвона:    ' . count(withDecision($summary['contacts'], [VOICE])) . "\n";
echo 'Отброшено:             ' . count(withDecision($summary['contacts'], [DROP])) . "\n";
echo 'Дублей схлопнулось:    ' . $summary['duplicatesRemoved'] . "\n";

echo "\nПо странам:  " . breakdown($summary['byCountry']) . "\n";
echo 'По типам:    ' . breakdown($summary['byType']) . "\n";

// Часовые пояса нужны, чтобы не разбудить абонента SMS в три часа ночи по его времени.
echo "\nК отправке (E.164 — то, что принимают SMS-шлюзы):\n";
foreach ($toSend as $contact) {
    $zones = $contact['timeZones'] === [] ? '—' : implode(', ', $contact['timeZones']);
    echo "  {$contact['e164']}  " . orDash($contact['carrier']) . "  [{$zones}]\n";
}
